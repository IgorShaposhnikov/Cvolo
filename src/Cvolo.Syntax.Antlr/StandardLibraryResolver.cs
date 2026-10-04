using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Directives;
using Cvolo.Core.AST.Expressions;
using Cvolo.Core.Diagnostics;
using Cvolo.Syntax.Rewriters;

namespace Cvolo.Syntax.Antlr;

internal enum SdkTier
{
	Base,
	System
}

public sealed record SdkNamespaceMismatch(string FilePath, string ExpectedNamespace, string ActualNamespace);

internal sealed record SdkResolution(
	IReadOnlyList<string> Sources,
	IReadOnlyList<string> BaseSources,
	IReadOnlyList<string> SystemSources,
	IReadOnlyList<string> RequiredSystemNamespaces,
	IReadOnlyList<SdkNamespaceMismatch> NamespaceMismatches);

internal static class SdkLibraryResolver
{
	private sealed class Entry
	{
		public string FilePath = "";
		public string RelativePath = "";
		public SdkTier Tier;
		public string NamespaceName = "";
		public CompilationUnitSyntax? Unit;
		public List<string> Usings = [];
		public List<string> ReExports = [];
		public Dictionary<string, string> Aliases = new(StringComparer.Ordinal);
		public List<string> ReferencedNamespaces = [];
	}

	public static SdkResolution Resolve(
		string libraryRoot,
		IReadOnlyList<string> projectSources,
		bool includeSystem,
		IReadOnlyDictionary<string, string>? sourceOverrides = null,
		IReadOnlyList<string>? extraSystemNamespaces = null)
	{
		var files = Directory.GetFiles(libraryRoot, "*.cvl", SearchOption.AllDirectories)
			.Concat(Directory.GetFiles(libraryRoot, "*.cv", SearchOption.AllDirectories))
			.ToList();

		var pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
		var entries = new List<Entry>(files.Count);
		var byPath = new Dictionary<string, Entry>(pathComparer);
		var mismatches = new List<SdkNamespaceMismatch>();

		foreach (var path in files)
		{
			if (!TryClassifyTier(libraryRoot, path, out var tier, out var expectedNamespace))
			{
				continue;
			}

			var entry = new Entry
			{
				FilePath = path,
				RelativePath = NormalizeRelative(libraryRoot, path),
				Tier = tier,
				Unit = ParseUnit(path, sourceOverrides)
			};

			if (entry.Unit is not null)
			{
				entry.NamespaceName = entry.Unit.NamespaceDeclaration?.Name ?? "";
				if (!string.Equals(entry.NamespaceName, expectedNamespace, StringComparison.Ordinal))
				{
					mismatches.Add(new SdkNamespaceMismatch(path, expectedNamespace, entry.NamespaceName));
				}

				CollectUsings(entry);
			}

			entries.Add(entry);
			byPath[path] = entry;
		}

		var catalog = new Dictionary<string, List<Entry>>(StringComparer.Ordinal);
		foreach (var entry in entries)
		{
			if (entry.Tier != SdkTier.System || entry.NamespaceName.Length == 0)
			{
				continue;
			}

			if (!catalog.TryGetValue(entry.NamespaceName, out var list))
			{
				list = [];
				catalog[entry.NamespaceName] = list;
			}

			list.Add(entry);
		}

		var catalogNamespaces = new HashSet<string>(catalog.Keys, StringComparer.Ordinal);
		foreach (var entry in entries)
		{
			if (entry.Unit is null)
			{
				continue;
			}

			entry.ReferencedNamespaces.AddRange(ReferencedCandidates(entry.Unit, entry.Usings, entry.Aliases, catalogNamespaces));
		}

		var required = new HashSet<string>(StringComparer.Ordinal);
		void Require(string namespaceName)
		{
			if (IsSystemRooted(namespaceName))
			{
				required.Add(namespaceName);
			}
		}

		var selected = new List<Entry>();
		var selectedPaths = new HashSet<string>(pathComparer);
		var processed = new HashSet<string>(StringComparer.Ordinal);
		var pending = new Queue<string>();

		void Enqueue(string namespaceName)
		{
			if (namespaceName.Length > 0 && processed.Add(namespaceName))
			{
				pending.Enqueue(namespaceName);
			}
		}

		void Select(Entry entry)
		{
			if (!selectedPaths.Add(entry.FilePath))
			{
				return;
			}

			selected.Add(entry);

			foreach (var usingName in entry.Usings)
			{
				Require(usingName);
				Enqueue(usingName);
			}

			foreach (var reExport in entry.ReExports)
			{
				Require(reExport);
				Enqueue(reExport);
			}

			foreach (var aliasTarget in entry.Aliases.Values)
			{
				Require(aliasTarget);
				Enqueue(aliasTarget);
			}

			foreach (var referenced in entry.ReferencedNamespaces)
			{
				Require(referenced);
				Enqueue(referenced);
			}
		}

		foreach (var entry in entries)
		{
			if (entry.Tier == SdkTier.Base)
			{
				Select(entry);
			}
		}

		foreach (var source in projectSources)
		{
			var fullPath = Path.GetFullPath(source);
			if (byPath.ContainsKey(fullPath))
			{
				continue;
			}

			var unit = ParseUnit(fullPath, sourceOverrides);
			if (unit is null)
			{
				continue;
			}

			var active = ActiveUsings(unit);
			var aliases = ActiveAliases(unit);
			foreach (var usingName in active)
			{
				Require(usingName);
				Enqueue(usingName);
			}

			foreach (var aliasTarget in aliases.Values)
			{
				Require(aliasTarget);
				Enqueue(aliasTarget);
			}

			foreach (var referenced in ReferencedCandidates(unit, active, aliases, catalogNamespaces))
			{
				Require(referenced);
				Enqueue(referenced);
			}
		}

		if (extraSystemNamespaces is not null)
		{
			foreach (var namespaceName in extraSystemNamespaces)
			{
				Require(namespaceName);
				Enqueue(namespaceName);
			}
		}

		if (includeSystem)
		{
			while (pending.Count > 0)
			{
				var namespaceName = pending.Dequeue();
				if (!catalog.TryGetValue(namespaceName, out var matches))
				{
					continue;
				}

				foreach (var entry in matches)
				{
					Select(entry);
				}
			}
		}

		selected.Sort((left, right) => string.CompareOrdinal(left.RelativePath, right.RelativePath));
		var sources = selected.Select(entry => entry.FilePath).ToList();
		var baseSources = selected.Where(entry => entry.Tier == SdkTier.Base).Select(entry => entry.FilePath).ToList();
		var systemSources = selected.Where(entry => entry.Tier == SdkTier.System).Select(entry => entry.FilePath).ToList();

		var requiredList = required.ToList();
		requiredList.Sort(StringComparer.Ordinal);

		return new SdkResolution(sources, baseSources, systemSources, requiredList, mismatches);
	}

	private static bool TryClassifyTier(string libraryRoot, string filePath, out SdkTier tier, out string expectedNamespace)
	{
		// The module directory (the file's directory relative to the library root) names the
		// namespace; the file name itself never contributes. The first directory segment
		// selects the tier so nested directories (System/Math/Int.cvl) and dotted module
		// directories (System.Memory/Heap.cvl) classify identically and deterministically.
		// No tier is inferred from a file name and no module is skipped for being nested.
		var relative = Path.GetRelativePath(libraryRoot, filePath).Replace('\\', '/');
		var segments = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
		if (segments.Length < 2)
		{
			// A file directly in the library root belongs to no module.
			tier = SdkTier.System;
			expectedNamespace = "";
			return false;
		}

		var directorySegments = segments[..^1];
		var first = directorySegments[0];

		if (first == "Base" || first.StartsWith("Base.", StringComparison.Ordinal))
		{
			// Base is the always-available tier and carries the global namespace.
			tier = SdkTier.Base;
			expectedNamespace = "";
			return true;
		}

		if (first == "System" || first.StartsWith("System.", StringComparison.Ordinal))
		{
			tier = SdkTier.System;
			expectedNamespace = string.Join('.', directorySegments);
			return true;
		}

		tier = SdkTier.System;
		expectedNamespace = "";
		return false;
	}

	private static string NormalizeRelative(string libraryRoot, string filePath) =>
		Path.GetRelativePath(libraryRoot, filePath).Replace('\\', '/');

	private static bool IsSystemRooted(string namespaceName) =>
		namespaceName.Equals("System", StringComparison.Ordinal)
		|| namespaceName.StartsWith("System.", StringComparison.Ordinal);

	private static CompilationUnitSyntax? ParseUnit(string filePath, IReadOnlyDictionary<string, string>? sourceOverrides)
	{
		try
		{
			var text = sourceOverrides is not null && sourceOverrides.TryGetValue(filePath, out var overrideText)
				? overrideText
				: File.ReadAllText(filePath);
			var parser = new AntlrSyntaxParser();
			return parser.Parse(new CompilationContext(text, filePath));
		}
		catch (IOException)
		{
			return null;
		}
	}

	private static void CollectUsings(Entry entry)
	{
		var unit = entry.Unit!;
		foreach (var directive in unit.Usings)
		{
			// Alias targets still select SDK modules even though aliases do not import members.
			if (directive.IsAlias)
			{
				entry.Aliases[directive.AliasName!] = directive.NamespaceName;
				continue;
			}

			if (directive.IsExposed)
			{
				entry.ReExports.Add(directive.NamespaceName);
			}
			else
			{
				entry.Usings.Add(directive.NamespaceName);
			}
		}

		if (unit.NamespaceDeclaration is not null)
		{
			foreach (var directive in unit.NamespaceDeclaration.Usings)
			{
				if (directive.IsAlias)
				{
					entry.Aliases[directive.AliasName!] = directive.NamespaceName;
					continue;
				}

				if (directive.IsExposed)
				{
					entry.ReExports.Add(directive.NamespaceName);
				}
				else
				{
					entry.Usings.Add(directive.NamespaceName);
				}
			}
		}
	}

	private static List<string> ActiveUsings(CompilationUnitSyntax unit)
	{
		var active = new List<string>();
		foreach (var directive in unit.Usings)
		{
			if (!directive.IsExposed && !directive.IsAlias)
			{
				active.Add(directive.NamespaceName);
			}
		}

		if (unit.NamespaceDeclaration is not null)
		{
			foreach (var directive in unit.NamespaceDeclaration.Usings)
			{
				if (!directive.IsExposed && !directive.IsAlias)
				{
					active.Add(directive.NamespaceName);
				}
			}
		}

		return active;
	}

	private static IReadOnlyDictionary<string, string> ActiveAliases(CompilationUnitSyntax unit)
	{
		var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
		foreach (var directive in unit.Usings)
		{
			if (directive.IsAlias)
				aliases[directive.AliasName!] = directive.NamespaceName;
		}

		if (unit.NamespaceDeclaration is not null)
		{
			foreach (var directive in unit.NamespaceDeclaration.Usings)
			{
				if (directive.IsAlias)
					aliases[directive.AliasName!] = directive.NamespaceName;
			}
		}

		return aliases;
	}

	private static IEnumerable<string> ReferencedCandidates(CompilationUnitSyntax unit, IReadOnlyList<string> activeUsings, IReadOnlyDictionary<string, string> aliases, IReadOnlyCollection<string> namespaces)
	{
		var results = new HashSet<string>(StringComparer.Ordinal);

		void Add(string dotted)
		{
			if (namespaces.Contains(dotted))
			{
				results.Add(dotted);
			}

			foreach (var usingName in activeUsings)
			{
				var candidate = usingName + "." + dotted;
				if (namespaces.Contains(candidate))
				{
					results.Add(candidate);
				}
			}

			var separator = dotted.IndexOf('.');
			var root = separator < 0 ? dotted : dotted[..separator];
			if (aliases.TryGetValue(root, out var aliasTarget))
			{
				var expanded = separator < 0 ? aliasTarget : aliasTarget + dotted[separator..];
				if (namespaces.Contains(expanded))
					results.Add(expanded);
			}
		}

		foreach (var node in Descendants(RewriteForReferences(unit)))
		{
			switch (node)
			{
				case CallExpressionSyntax call:
					foreach (var prefix in Prefixes(call.FunctionName))
					{
						Add(prefix);
					}

					break;
				case MemberAccessExpressionSyntax memberAccess:
					var dotted = DottedName(memberAccess.Expression);
					if (dotted is not null)
					{
						Add(dotted);
					}

					break;
			}
		}

		return results;
	}

	private static IEnumerable<string> Prefixes(string dotted)
	{
		var index = dotted.IndexOf('.', StringComparison.Ordinal);
		while (index >= 0)
		{
			yield return dotted[..index];
			index = dotted.IndexOf('.', index + 1);
		}

		yield return dotted;
	}

	private static string? DottedName(ExpressionSyntax expression)
	{
		return expression switch
		{
			IdentifierExpressionSyntax identifier => identifier.Name,
			MemberAccessExpressionSyntax memberAccess => DottedName(memberAccess.Expression) is { } left
				? left + "." + memberAccess.MemberName
				: null,
			_ => null
		};
	}

	private static SyntaxNode RewriteForReferences(CompilationUnitSyntax unit)
	{
		try
		{
			return new StringInterpolationRewriter(new AntlrSyntaxParser()).Rewrite(unit);
		}
		catch (InvalidOperationException)
		{
			return unit;
		}
	}

	private static IEnumerable<SyntaxNode> Descendants(SyntaxNode node)
	{
		foreach (var child in node.GetChildren())
		{
			yield return child;
			foreach (var descendant in Descendants(child))
			{
				yield return descendant;
			}
		}
	}
}