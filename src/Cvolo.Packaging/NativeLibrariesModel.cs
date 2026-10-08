using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Cvolo.Packaging;

/// <summary>
/// The compilation target OS a &lt;NativeLibraries&gt; group applies to.
/// Parse-time canonical values: windows, linux, macos.
/// </summary>
public enum NativeLibraryTarget
{
	Windows,
	Linux,
	MacOS
}

/// <summary>
/// One &lt;NativeLibraries&gt; block: a target-specific group of explicit native-library mappings
/// declared by the final application's .cvlproj.
/// </summary>
/// <param name="Target">The target OS the group applies to (required).</param>
/// <param name="Arch">Optional requested-target architecture: "x64", "arm64", or "x86". Null matches any.</param>
/// <param name="Configuration">Optional requested-target configuration: "Debug" or "Release". Null matches any.</param>
/// <param name="Libraries">The &lt;NativeLibrary&gt; mappings inside the block.</param>
public sealed record NativeLibrariesGroup(
	NativeLibraryTarget Target,
	string? Arch,
	string? Configuration,
	IReadOnlyList<NativeLibraryDeclaration> Libraries);

/// <summary>
/// One &lt;NativeLibrary&gt; element inside a &lt;NativeLibraries&gt; block.
/// </summary>
/// <param name="For">The opaque logical library id (compared ordinal, case-sensitive, complete string).</param>
/// <param name="Path">The link-time input, resolved relative to the application .cvlproj directory.</param>
/// <param name="RuntimeFiles">Explicit runtime artifacts copied next to the output after a successful link.</param>
public sealed record NativeLibraryDeclaration(
	string For,
	string Path,
	IReadOnlyList<string> RuntimeFiles);

/// <summary>How native-library resolution ended for one logical library.</summary>
public enum NativeLibraryMatchKind
{
	/// <summary>No group matched the requested target/arch/configuration.</summary>
	NotFound,

	/// <summary>A unique most-specific group was selected.</summary>
	Selected,

	/// <summary>Two matching groups are incomparable; neither dominates the other.</summary>
	Ambiguous
}

/// <summary>
/// The outcome of selecting a &lt;NativeLibraries&gt; mapping for a logical library on one request.
/// </summary>
/// <param name="Kind">The outcome.</param>
/// <param name="Group">The selected group when <see cref="Kind"/> is <see cref="NativeLibraryMatchKind.Selected"/>.</param>
/// <param name="Declaration">The selected declaration when <see cref="Kind"/> is <see cref="NativeLibraryMatchKind.Selected"/>.</param>
/// <param name="Conflict">One of the conflicting groups when <see cref="Kind"/> is <see cref="NativeLibraryMatchKind.Ambiguous"/>.</param>
public sealed record NativeLibrarySelection(
	NativeLibraryMatchKind Kind,
	NativeLibrariesGroup? Group = null,
	NativeLibraryDeclaration? Declaration = null,
	NativeLibrariesGroup? Conflict = null);

/// <summary>
/// Parses and validates the .cvlproj &lt;NativeLibraries&gt; model (Application-Native-Library-Resolution INC).
/// Validation is strict at project load: unknown targets/architectures/configurations, unexpected
/// attributes or elements, blank values, unsupported path expressions, duplicate selectors, and
/// ambiguous matching candidates are all hard errors. No Condition/MSBuild-expression language exists.
/// </summary>
public static partial class NativeLibrariesParser
{
	private static readonly Regex EnvironmentExpression = EnvironmentExpressionRegex();

	/// <summary>
	/// Parses all &lt;NativeLibraries&gt; direct children of the project root.
	/// </summary>
	/// <param name="projectRoot">The &lt;Project&gt; root element of the .cvlproj.</param>
	/// <param name="warnings">Collects non-fatal advisories (e.g. absolute-path portability).</param>
	public static IReadOnlyList<NativeLibrariesGroup> Parse(XElement projectRoot, List<(string Code, string Message)> warnings)
	{
		var groups = new List<NativeLibrariesGroup>();

		foreach (var block in projectRoot.Elements("NativeLibraries"))
		{
			var target = ParseTarget(block);
			var arch = ParseArchitecture(block);
			var configuration = ParseConfiguration(block);

			foreach (var attribute in block.Attributes())
			{
				if (attribute.Name.LocalName is not ("Target" or "Arch" or "Configuration"))
					throw StructureError(block, $"Unexpected attribute '{attribute.Name.LocalName}' on <NativeLibraries>.");
			}

			var libraries = new List<NativeLibraryDeclaration>();
			foreach (var element in block.Elements())
			{
				if (element.Name.LocalName != "NativeLibrary")
					throw StructureError(block, $"Unexpected element <{element.Name.LocalName}> inside <NativeLibraries>.");

				libraries.Add(ParseLibrary(element, warnings));
			}

			groups.Add(new NativeLibrariesGroup(target, arch, configuration, libraries));
		}

		ValidateNoDuplicates(groups);
		ValidateNoAmbiguity(groups);

		return groups;
	}

	private static NativeLibraryTarget ParseTarget(XElement block)
	{
		var target = (string?)block.Attribute("Target");
		if (string.IsNullOrWhiteSpace(target))
			throw StructureError(block, "<NativeLibraries> requires a <Target> attribute (windows, linux, or macos).");

		return target.Trim().ToLowerInvariant() switch
		{
			"windows" => NativeLibraryTarget.Windows,
			"linux" => NativeLibraryTarget.Linux,
			"macos" => NativeLibraryTarget.MacOS,
			_ => throw new PackageException(
				PackageDiagnosticIds.NativeLibrariesUnknownTarget,
				$"<NativeLibraries Target=\"{target.Trim()}\"> is not a supported target. Expected 'windows', 'linux', or 'macos'.")
		};
	}

	private static string? ParseArchitecture(XElement block)
	{
		var arch = (string?)block.Attribute("Arch");
		if (string.IsNullOrWhiteSpace(arch))
			return null;

		var canonical = arch.Trim().ToLowerInvariant();
		return canonical switch
		{
			"x64" or "arm64" or "x86" => canonical,
			_ => throw new PackageException(
				PackageDiagnosticIds.NativeLibrariesUnknownArchitecture,
				$"<NativeLibraries Arch=\"{arch.Trim()}\"> is not a supported architecture. Expected 'x64', 'arm64', or 'x86'.")
		};
	}

	private static string? ParseConfiguration(XElement block)
	{
		var configuration = (string?)block.Attribute("Configuration");
		if (string.IsNullOrWhiteSpace(configuration))
			return null;

		var canonical = configuration.Trim().ToLowerInvariant();
		return canonical switch
		{
			"debug" or "release" => canonical == "debug" ? "Debug" : "Release",
			_ => throw new PackageException(
				PackageDiagnosticIds.NativeLibrariesUnknownConfiguration,
				$"<NativeLibraries Configuration=\"{configuration.Trim()}\"> is not a supported configuration. Expected 'Debug' or 'Release'.")
		};
	}

	private static NativeLibraryDeclaration ParseLibrary(XElement element, List<(string Code, string Message)> warnings)
	{
		foreach (var attribute in element.Attributes())
		{
			if (attribute.Name.LocalName is not ("For" or "Path"))
				throw StructureError(element, $"Unexpected attribute '{attribute.Name.LocalName}' on <NativeLibrary>.");
		}

		var forName = ((string?)element.Attribute("For"))?.Trim();
		if (string.IsNullOrEmpty(forName))
			throw StructureError(element, "<NativeLibrary> requires a non-empty 'For' attribute.");

		var path = ValidatePath((string?)element.Attribute("Path"), element, "Path");
		if (path is not null)
		{
			try
			{
				if (Path.IsPathRooted(path) && !string.IsNullOrWhiteSpace(path))
				{
					warnings.Add((
						PackageDiagnosticIds.NativeLibrariesAbsolutePath,
						$"<NativeLibrary For=\"{forName}\" Path=\"{path}\"> uses an absolute path; this is not portable across machines/projects. Prefer a path relative to the application .cvlproj."));
				}
			}
			catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException)
			{
				throw StructureError(element, $"Invalid path value: {path}");
			}
		}

		var runtimeFiles = new List<string>();
		foreach (var child in element.Elements())
		{
			if (child.Name.LocalName != "RuntimeFile")
				throw StructureError(element, $"Unexpected element <{child.Name.LocalName}> inside <NativeLibrary>.");

			foreach (var attribute in child.Attributes())
			{
				if (attribute.Name.LocalName != "Path")
					throw StructureError(child, $"Unexpected attribute '{attribute.Name.LocalName}' on <RuntimeFile>.");
			}

			if (child.Elements().Any())
				throw StructureError(child, "<RuntimeFile> must not contain nested elements.");

			if (ValidatePath((string?)child.Attribute("Path"), child, "Path") is { } runtimePath)
				runtimeFiles.Add(runtimePath);
		}

		return new NativeLibraryDeclaration(forName, path!, runtimeFiles);
	}

	private static string? ValidatePath(string? value, XElement element, string attributeName)
	{
		var path = value?.Trim();
		if (string.IsNullOrEmpty(path))
			throw StructureError(element, $"<{element.Name.LocalName}> requires a non-empty '{attributeName}' attribute.");

		if (path.Contains('$') || EnvironmentExpression.IsMatch(path))
		{
			throw new PackageException(
				PackageDiagnosticIds.NativeLibrariesInvalidPath,
				$"Path '{path}' contains an unsupported environment/MSBuild expression ($(...), ${{...}}, %NAME%). Native library paths must be literal filesystem paths.");
		}

		return path;
	}

	private static void ValidateNoDuplicates(IReadOnlyList<NativeLibrariesGroup> groups)
	{
		var seen = new Dictionary<(string, NativeLibraryTarget, string?, string?), NativeLibraryDeclaration>(DuplicateSelectorComparer);
		foreach (var group in groups)
		{
			foreach (var declaration in group.Libraries)
			{
				var key = (declaration.For, group.Target, group.Arch, group.Configuration);
				if (seen.TryGetValue(key, out var previous))
				{
					throw new PackageException(
						PackageDiagnosticIds.NativeLibrariesDuplicateSelector,
						$"Duplicate native-library mapping for '{declaration.For}' on {group.Target}/{group.Arch ?? "*"}/{group.Configuration ?? "*"} " +
						$"declared more than once (first declared at '{previous.Path}', again at '{declaration.Path}').");
				}

				seen.Add(key, declaration);
			}
		}
	}

	private static void ValidateNoAmbiguity(IReadOnlyList<NativeLibrariesGroup> groups)
	{
		foreach (var forName in groups.SelectMany(g => g.Libraries.Select(l => l.For)).Distinct(StringComparer.Ordinal))
		{
			var own = groups.Where(g => g.Libraries.Any(l => l.For == forName)).ToList();
			var targets = own.Select(g => g.Target).Distinct().ToList();
			var arches = own.Select(g => g.Arch).Append(null).Distinct().ToList();
			var configurations = own.Select(g => g.Configuration).Append(null).Distinct().ToList();

			foreach (var target in targets)
			{
				foreach (var arch in arches)
				{
					foreach (var configuration in configurations)
					{
						var selection = NativeLibrarySelector.Select(groups, forName, target, arch, configuration);
						if (selection.Kind == NativeLibraryMatchKind.Ambiguous && selection.Group is not null && selection.Conflict is not null)
						{
							throw new PackageException(
								PackageDiagnosticIds.NativeLibrariesAmbiguous,
								$"Native-library mappings for '{forName}' are ambiguous for {target}/{arch ?? "*"}/{configuration ?? "*"}: " +
								$"'{selection.Group.Arch ?? "*"}/{selection.Group.Configuration ?? "*"}' and '{selection.Conflict.Arch ?? "*"}/{selection.Conflict.Configuration ?? "*"}' " +
								"both match and neither is more specific. Add an explicit mapping that dominates both.");
						}
					}
				}
			}
		}
	}

	private static PackageException StructureError(XElement element, string message)
	{
		return new(PackageDiagnosticIds.NativeLibrariesInvalidStructure, message);
	}

	private static readonly IEqualityComparer<(string, NativeLibraryTarget, string?, string?)> DuplicateSelectorComparer =
		new ValueTupleComparer();

	private sealed class ValueTupleComparer : IEqualityComparer<(string, NativeLibraryTarget, string?, string?)>
	{
		public bool Equals((string, NativeLibraryTarget, string?, string?) x, (string, NativeLibraryTarget, string?, string?) y)
		{
			return x.Item1 == y.Item1 && x.Item2 == y.Item2 && x.Item3 == y.Item3 && x.Item4 == y.Item4;
		}

		public int GetHashCode((string, NativeLibraryTarget, string?, string?) value)
		{
			var hash = new HashCode();
			hash.Add(value.Item1, StringComparer.Ordinal);
			hash.Add(value.Item2);
			hash.Add(value.Item3);
			hash.Add(value.Item4);
			return hash.ToHashCode();
		}
	}

	[GeneratedRegex("%\\w+%", RegexOptions.Compiled)]
	private static partial Regex EnvironmentExpressionRegex();
}

/// <summary>
/// Pure selection of the most-specific matching &lt;NativeLibraries&gt; mapping for one logical library.
/// A group without an optional selector matches any value of that dimension; among matched groups the
/// unique most-specific one (selector-set inclusion) wins.
/// </summary>
public static class NativeLibrarySelector
{
	/// <summary>
	/// Selects the native-library mapping for <paramref name="forName"/> on the requested target,
	/// architecture (canonical x64/arm64/x86 or null), and configuration (Debug/Release or null).
	/// </summary>
	public static NativeLibrarySelection Select(
		IReadOnlyList<NativeLibrariesGroup> groups,
		string forName,
		NativeLibraryTarget target,
		string? arch,
		string? configuration)
	{
		var matched = groups
			.Where(g => g.Libraries.Any(l => l.For == forName)
				&& g.Target == target
				&& (g.Arch is null || g.Arch == arch)
				&& (g.Configuration is null || g.Configuration == configuration))
			.ToList();

		var declaration = matched
			.SelectMany(g => g.Libraries.Where(l => l.For == forName).Select(l => (Group: g, Declaration: l)))
			.ToList();

		if (declaration.Count == 1)
		{
			var single = declaration[0];
			return new NativeLibrarySelection(NativeLibraryMatchKind.Selected, single.Group, single.Declaration);
		}

		if (declaration.Count == 0)
			return new NativeLibrarySelection(NativeLibraryMatchKind.NotFound);

		// Keep only maximal groups: no other matched group is strictly more specific.
		var maximal = declaration.Where(candidate =>
			!declaration.Any(other => !ReferenceEquals(other.Declaration, candidate.Declaration)
				&& StrictlyMoreSpecific(other.Group!, candidate.Group!)))
			.ToList();

		if (maximal.Count == 1)
		{
			var winner = maximal[0];
			return new NativeLibrarySelection(NativeLibraryMatchKind.Selected, winner.Group, winner.Declaration);
		}

		NativeLibrariesGroup? conflictGroup = null;
		for (var i = 1; i < maximal.Count; i++)
		{
			conflictGroup = maximal[i].Group;
			break;
		}

		return new NativeLibrarySelection(NativeLibraryMatchKind.Ambiguous, maximal[0].Group, maximal[0].Declaration, conflictGroup);
	}

	/// <summary>
	/// True when <paramref name="a"/> is strictly more specific than <paramref name="b"/>: it includes
	/// every selector <paramref name="b"/> declares (on the matching target) and declares at least one
	/// extra selector. A group without an optional selector cannot dominate a group that sets it.
	/// </summary>
	private static bool StrictlyMoreSpecific(NativeLibrariesGroup a, NativeLibrariesGroup b)
	{
		var coversArch = b.Arch is null || a.Arch == b.Arch;
		var coversConfiguration = b.Configuration is null || a.Configuration == b.Configuration;
		var strictlyNarrower = a.Arch != b.Arch || a.Configuration != b.Configuration;
		return coversArch && coversConfiguration && strictlyNarrower;
	}
}
