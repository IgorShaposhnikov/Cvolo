using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using Cvolo.Core.AST.Directives;
using Cvolo.Core.AST.Expressions;
using Cvolo.Core.Diagnostics;
using Cvolo.Syntax.Rewriters;

namespace Cvolo.Syntax.Antlr;

internal enum LibraryTier
{
	Core,
	Std
}

internal static class StandardLibraryResolver
{
	private sealed class Entry
	{
		public string FilePath = "";
		public LibraryTier Tier;
		public string NamespaceName = "";
		public CompilationUnitSyntax? Unit;
		public List<string> Usings = [];
		public List<string> ReExports = [];
		public List<string> ReferencedNamespaces = [];
	}

	public static IReadOnlyList<string> Resolve(
		string libraryRoot,
		IReadOnlyList<string> projectSources,
		bool includeStd,
		IReadOnlyDictionary<string, string>? sourceOverrides = null)
	{
		var files = Directory.GetFiles(libraryRoot, "*.cvl", SearchOption.AllDirectories)
			.Concat(Directory.GetFiles(libraryRoot, "*.cv", SearchOption.AllDirectories))
			.OrderBy(path => path, StringComparer.Ordinal)
			.ToList();

		if (files.Count == 0)
		{
			return [];
		}

		var pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
		var entries = new List<Entry>(files.Count);
		var byPath = new Dictionary<string, Entry>(pathComparer);
		var namespaces = new HashSet<string>(StringComparer.Ordinal);

		foreach (var path in files)
		{
			var entry = new Entry
			{
				FilePath = path,
				Tier = ClassifyTier(libraryRoot, path),
				Unit = ParseUnit(path, sourceOverrides)
			};

			if (entry.Unit is not null)
			{
				entry.NamespaceName = entry.Unit.NamespaceDeclaration?.Name ?? "";
				if (entry.NamespaceName.Length > 0)
				{
					namespaces.Add(entry.NamespaceName);
				}

				CollectUsings(entry);
			}

			entries.Add(entry);
			byPath[path] = entry;
		}

		foreach (var entry in entries)
		{
			if (entry.Unit is null)
			{
				continue;
			}

			entry.ReferencedNamespaces.AddRange(ReferencedCandidates(entry.Unit, entry.Usings, namespaces));
		}

		var candidates = includeStd ? entries : entries.Where(entry => entry.Tier == LibraryTier.Core).ToList();
		var selected = new List<string>();
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

			selected.Add(entry.FilePath);
			foreach (var usingName in entry.Usings)
			{
				Enqueue(usingName);
			}

			foreach (var reExport in entry.ReExports)
			{
				Enqueue(reExport);
			}

			foreach (var referenced in entry.ReferencedNamespaces)
			{
				Enqueue(referenced);
			}
		}

		foreach (var entry in candidates)
		{
			if (entry.Tier == LibraryTier.Core)
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
				Enqueue(usingName);
			}

			foreach (var referenced in ReferencedCandidates(unit, active, namespaces))
			{
				Enqueue(referenced);
			}
		}

		while (pending.Count > 0)
		{
			var namespaceName = pending.Dequeue();
			foreach (var entry in candidates)
			{
				if (string.Equals(entry.NamespaceName, namespaceName, StringComparison.Ordinal))
				{
					Select(entry);
				}
			}
		}

		selected.Sort(StringComparer.Ordinal);
		return selected;
	}

	private static LibraryTier ClassifyTier(string libraryRoot, string filePath)
	{
		var relative = Path.GetRelativePath(libraryRoot, filePath);
		var separator = relative.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
		var firstSegment = separator < 0 ? relative : relative[..separator];
		return string.Equals(firstSegment, "Core", StringComparison.Ordinal) ? LibraryTier.Core : LibraryTier.Std;
	}

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
