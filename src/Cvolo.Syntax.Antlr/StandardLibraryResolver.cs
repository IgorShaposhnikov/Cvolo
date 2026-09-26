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
		public List<string> ReferencedNamespaces = [];
	}

	public static SdkResolution Resolve(
		string libraryRoot,
		IReadOnlyList<string> projectSources,
		bool includeSystem,
		IReadOnlyDictionary<string, string>? sourceOverrides = null)
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

			entry.ReferencedNamespaces.AddRange(ReferencedCandidates(entry.Unit, entry.Usings, catalogNamespaces));
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
			foreach (var usingName in active)
			{
				Require(usingName);
				Enqueue(usingName);
			}

			foreach (var referenced in ReferencedCandidates(unit, active, catalogNamespaces))
			{
				Require(referenced);
				Enqueue(referenced);
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

		var requiredList = required.ToList();
		requiredList.Sort(StringComparer.Ordinal);

		return new SdkResolution(sources, requiredList, mismatches);
	}

	private static bool TryClassifyTier(string libraryRoot, string filePath, out SdkTier tier, out string expectedNamespace)
	{
		var relative = Path.GetRelativePath(libraryRoot, filePath).Replace('\\', '/');
		var segments = relative.Split('/', StringSplitOptions.RemoveEmptyEntries);
		if (segments.Length == 0)
		{
			tier = SdkTier.System;
			expectedNamespace = "";
			return false;
		}

		switch (segments[0])
		{
			case "Base":
				tier = SdkTier.Base;
				expectedNamespace = "";
				return true;
			case "System":
				tier = SdkTier.System;
				var parts = new List<string> { "System" };
				parts.AddRange(segments.Skip(1).Take(segments.Length - 2));
				expectedNamespace = string.Join('.', parts);
				return true;
			default:
				tier = SdkTier.System;
				expectedNamespace = "";
				return false;
		}
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
			if (!directive.IsExposed)
			{
				active.Add(directive.NamespaceName);
			}
		}

		if (unit.NamespaceDeclaration is not null)
		{
			foreach (var directive in unit.NamespaceDeclaration.Usings)
			{
				if (!directive.IsExposed)
				{
					active.Add(directive.NamespaceName);
				}
			}
		}

		return active;
	}

	private static IEnumerable<string> ReferencedCandidates(CompilationUnitSyntax unit, IReadOnlyList<string> activeUsings, IReadOnlyCollection<string> namespaces)
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
