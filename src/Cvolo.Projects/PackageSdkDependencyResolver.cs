using Cvolo.Packaging;

namespace Cvolo.Projects;

/// <summary>
/// Collects System SDK namespaces required by package implementation artifacts.
/// </summary>
/// <remarks>
/// Package bitcode intentionally omits SDK bodies. The executable consumer therefore has to select
/// every System SDK module referenced by that bitcode even when the executable's own sources never
/// import those modules. Sector 5 source metadata is the implementation-level dependency source;
/// API/template units are also consulted so source-fallback and source-backed generic paths behave
/// identically.
/// </remarks>
internal static class PackageSdkDependencyResolver
{
	public static IReadOnlyList<string> Collect(IEnumerable<ResolvedPackageArtifacts> artifacts)
	{
		var namespaces = new HashSet<string>(StringComparer.Ordinal);

		foreach (var artifact in artifacts)
		{
			foreach (var unit in artifact.ApiMetadata.Units)
			{
				AddSystemNamespaces(namespaces, unit.FileUsings);
				AddSystemNamespaces(namespaces, unit.NamespaceUsings);
			}

			foreach (var unit in artifact.TemplateUnits.Concat(artifact.SourceFallbackUnits))
			{
				// Alias directives are deliberately included here. They do not import members into the
				// package scope, but an alias whose target is System.* still means the implementation
				// bitcode depends on that SDK namespace.
				AddSystemNamespaces(namespaces, unit.Usings.Select(usingDirective => usingDirective.NamespaceName));
				if (unit.NamespaceDeclaration is not null)
					AddSystemNamespaces(namespaces, unit.NamespaceDeclaration.Usings.Select(usingDirective => usingDirective.NamespaceName));
			}

			if (artifact.SourceFiles is null)
				continue;

			foreach (var sourceFile in artifact.SourceFiles)
				AddSystemNamespaces(namespaces, ExtractSystemNamespaces(sourceFile.Source));
		}

		var result = namespaces.ToList();
		result.Sort(StringComparer.Ordinal);
		return result;
	}

	/// <summary>
	/// Extracts System-rooted dependencies from one source file. Both ordinary imports, namespace
	/// aliases and fully-qualified references are recognized. The resolver that consumes the result
	/// intersects these candidates with the real SDK namespace catalog, so emitting every dotted
	/// prefix is intentional: it finds the namespace boundary without reimplementing semantic lookup.
	/// </summary>
	internal static IReadOnlyList<string> ExtractSystemNamespaces(string source)
	{
		var namespaces = new HashSet<string>(StringComparer.Ordinal);
		var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
		var lines = source.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

		// First collect every using target so alias-qualified references can be expanded even if the
		// implementation is scanned independently of semantic binding.
		foreach (var line in lines)
		{
			if (!TryGetUsingDirective(line.Trim(), out var aliasName, out var usingTarget))
				continue;

			AddSystemNamespace(namespaces, usingTarget);
			if (aliasName is not null && IsSystemRooted(usingTarget))
				aliases[aliasName] = usingTarget;
		}

		foreach (var line in lines)
		{
			foreach (var namespaceName in ExtractQualifiedSystemPrefixes(line))
				AddSystemNamespace(namespaces, namespaceName);

			foreach (var (aliasName, aliasTarget) in aliases)
			{
				foreach (var namespaceName in ExtractAliasQualifiedPrefixes(line, aliasName, aliasTarget))
					AddSystemNamespace(namespaces, namespaceName);
			}
		}

		var result = namespaces.ToList();
		result.Sort(StringComparer.Ordinal);
		return result;
	}

	private static bool TryGetUsingDirective(string trimmed, out string? aliasName, out string namespaceName)
	{
		const string usingPrefix = "using ";
		const string exposeUsingPrefix = "expose using ";

		string body;
		if (trimmed.StartsWith(exposeUsingPrefix, StringComparison.Ordinal))
			body = trimmed[exposeUsingPrefix.Length..];
		else if (trimmed.StartsWith(usingPrefix, StringComparison.Ordinal))
			body = trimmed[usingPrefix.Length..];
		else
		{
			aliasName = null;
			namespaceName = string.Empty;
			return false;
		}

		if (!body.EndsWith(';'))
		{
			aliasName = null;
			namespaceName = string.Empty;
			return false;
		}

		body = body[..^1].Trim();
		var equals = body.IndexOf('=');
		if (equals >= 0)
		{
			aliasName = body[..equals].Trim();
			namespaceName = body[(equals + 1)..].Trim();
			return aliasName.Length > 0 && namespaceName.Length > 0;
		}

		aliasName = null;
		namespaceName = body;
		return namespaceName.Length > 0;
	}

	private static IEnumerable<string> ExtractAliasQualifiedPrefixes(string line, string aliasName, string aliasTarget)
	{
		var needle = aliasName + ".";
		var index = 0;
		while ((index = line.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
		{
			if (index > 0)
			{
				var previous = line[index - 1];
				if (char.IsLetterOrDigit(previous) || previous is '_' or '.')
				{
					index += needle.Length;
					continue;
				}
			}

			var end = index + aliasName.Length;
			while (end < line.Length && (char.IsLetterOrDigit(line[end]) || line[end] is '_' or '.'))
				end++;

			var suffix = line[(index + aliasName.Length)..end].TrimEnd('.');
			var expanded = aliasTarget + suffix;
			var parts = expanded.Split('.', StringSplitOptions.RemoveEmptyEntries);
			for (var length = 1; length <= parts.Length; length++)
				yield return string.Join('.', parts.Take(length));

			index = end;
		}
	}

	private static bool IsSystemRooted(string namespaceName) =>
		namespaceName == "System" || namespaceName.StartsWith("System.", StringComparison.Ordinal);

	private static void AddSystemNamespaces(HashSet<string> target, IEnumerable<string> namespaces)
	{
		foreach (var namespaceName in namespaces)
			AddSystemNamespace(target, namespaceName);
	}

	private static void AddSystemNamespace(HashSet<string> target, string namespaceName)
	{
		if (namespaceName == "System" || namespaceName.StartsWith("System.", StringComparison.Ordinal))
			target.Add(namespaceName);
	}

	private static IEnumerable<string> ExtractQualifiedSystemPrefixes(string line)
	{
		var index = 0;
		while ((index = line.IndexOf("System.", index, StringComparison.Ordinal)) >= 0)
		{
			// Do not treat the tail of an identifier such as MySystem.Math as a rooted System path.
			if (index > 0)
			{
				var previous = line[index - 1];
				if (char.IsLetterOrDigit(previous) || previous is '_' or '.')
				{
					index += "System.".Length;
					continue;
				}
			}

			var end = index + "System".Length;
			while (end < line.Length && (char.IsLetterOrDigit(line[end]) || line[end] is '_' or '.'))
				end++;

			var qualifiedName = line[index..end].TrimEnd('.');
			var parts = qualifiedName.Split('.', StringSplitOptions.RemoveEmptyEntries);
			for (var length = 1; length <= parts.Length; length++)
				yield return string.Join('.', parts.Take(length));

			index = end;
		}
	}
}
