using Cvolo.Analysis;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;

namespace Cvolo.Compiler.Tooling.Internal;

/// <summary>
/// Answers the declared contract hierarchy for <c>textDocument/prepareTypeHierarchy</c> and the
/// <c>typeHierarchy/supertypes|subtypes</c> follow-ups. Only the <c>:</c> base clauses of interface
/// and protocol declarations are edges; a concrete type that structurally satisfies a protocol is
/// deliberately absent, because that relation belongs to Implementation, not Hierarchy (§13, §15).
/// </summary>
internal static class TypeHierarchyNavigation
{
	internal static ToolingHierarchyItem? At(ProjectSnapshot snapshot, DocumentSnapshot document, int position)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		ArgumentNullException.ThrowIfNull(document);

		var navigation = snapshot.GetNavigationIndex();
		SymbolLookupResult? resolved = navigation.Lookup(document.Id, position);
		if (resolved is null)
		{
			return null;
		}

		ToolingSymbolKind? kind = KindOf(navigation.Declaration(resolved.SymbolId));
		return kind is null ? null : Item(navigation, resolved.SymbolId, resolved.Name, kind.Value);
	}

	internal static ToolingHierarchyItem? ByName(ProjectSnapshot snapshot, DocumentSnapshot anchor, string name)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		ArgumentNullException.ThrowIfNull(anchor);

		if (string.IsNullOrWhiteSpace(name))
		{
			return null;
		}

		var navigation = snapshot.GetNavigationIndex();
		string leaf = TypeLayoutView.LeafType(name);
		if (leaf.Length == 0)
		{
			return null;
		}

		SymbolId? symbol = navigation.FindTypeSymbol(leaf, anchor.Id);
		if (symbol is null)
		{
			return null;
		}

		ToolingSymbolKind? kind = KindOf(navigation.Declaration(symbol.Value));
		return kind is null ? null : Item(navigation, symbol.Value, leaf, kind.Value);
	}

	internal static IReadOnlyList<ToolingHierarchyItem> Supertypes(ProjectSnapshot snapshot, SymbolId symbol)
	{
		ArgumentNullException.ThrowIfNull(snapshot);

		var navigation = snapshot.GetNavigationIndex();
		SyntaxNode? declaration = navigation.Declaration(symbol);
		if (KindOf(declaration) is null)
		{
			return [];
		}

		AnalyzedProject analysis = snapshot.GetAnalysis();
		BindingContext? context = analysis.BinderContext;
		CompilationUnitSyntax? unit = FindUnit(analysis, declaration!);
		if (context is null || unit is null)
		{
			return [];
		}

		var items = new List<ToolingHierarchyItem>();
		foreach (string baseName in BasesOf(declaration!))
		{
			(string Name, ToolingSymbolKind Kind)? contract = ResolveContract(context, unit, baseName);
			if (contract is null)
			{
				continue;
			}

			SymbolId? baseSymbol = navigation.FindTypeSymbol(contract.Value.Name, null);
			if (baseSymbol is null)
			{
				continue;
			}

			ToolingHierarchyItem? item = Item(navigation, baseSymbol.Value, contract.Value.Name, contract.Value.Kind);
			if (item is not null && !Contains(items, item))
			{
				items.Add(item);
			}
		}

		return items;
	}

	internal static IReadOnlyList<ToolingHierarchyItem> Subtypes(ProjectSnapshot snapshot, SymbolId symbol)
	{
		ArgumentNullException.ThrowIfNull(snapshot);

		var navigation = snapshot.GetNavigationIndex();
		SyntaxNode? target = navigation.Declaration(symbol);
		string? targetName = KindOf(target) is null ? null : DeclaredName(target!);
		if (targetName is null)
		{
			return [];
		}

		AnalyzedProject analysis = snapshot.GetAnalysis();
		BindingContext? context = analysis.BinderContext;
		if (context is null)
		{
			return [];
		}

		var items = new List<ToolingHierarchyItem>();
		foreach ((DocumentId documentId, CompilationUnitSyntax? unit) in analysis.UnitsByDocument)
		{
			if (unit is null)
			{
				continue;
			}

			foreach (SyntaxNode declaration in Descendants(unit))
			{
				if (KindOf(declaration) is not { } childKind || !BasesOf(declaration).Any())
				{
					continue;
				}

				foreach (string baseName in BasesOf(declaration))
				{
					(string Name, ToolingSymbolKind Kind)? baseContract = ResolveContract(context, unit, baseName);
					if (baseContract is null || !string.Equals(baseContract.Value.Name, targetName, StringComparison.Ordinal))
					{
						continue;
					}

					string childName = DeclaredName(declaration) ?? baseContract.Value.Name;
					if (navigation.FindTypeSymbol(childName, documentId) is not { } childSymbol)
					{
						continue;
					}

					ToolingHierarchyItem? item = Item(navigation, childSymbol, childName, childKind);
					if (item is not null && !Contains(items, item))
					{
						items.Add(item);
					}

					break;
				}
			}
		}

		return items;
	}

	private static ToolingHierarchyItem? Item(NavigationIndex navigation, SymbolId symbol, string name, ToolingSymbolKind kind)
	{
		IReadOnlyList<SymbolDefinition> definitions = navigation.Definitions(symbol);
		return definitions.Count == 0 ? null : new ToolingHierarchyItem(symbol, kind, name, definitions[0]);
	}

	private static bool Contains(IReadOnlyList<ToolingHierarchyItem> items, ToolingHierarchyItem item)
	{
		foreach (ToolingHierarchyItem existing in items)
		{
			if (existing.Definition.DocumentId == item.Definition.DocumentId
				&& existing.Definition.SelectionSpan.Start == item.Definition.SelectionSpan.Start)
			{
				return true;
			}
		}

		return false;
	}

	private static string? DeclaredName(SyntaxNode declaration) => declaration switch
	{
		InterfaceDeclarationSyntax iface => iface.Name,
		ProtocolDeclarationSyntax protocol => protocol.Name,
		_ => null,
	};

	private static ToolingSymbolKind? KindOf(SyntaxNode? declaration) => declaration switch
	{
		InterfaceDeclarationSyntax => ToolingSymbolKind.Interface,
		ProtocolDeclarationSyntax => ToolingSymbolKind.Protocol,
		_ => null,
	};

	private static IReadOnlyList<string> BasesOf(SyntaxNode declaration) => declaration switch
	{
		InterfaceDeclarationSyntax iface => iface.Bases,
		ProtocolDeclarationSyntax protocol => protocol.Bases,
		_ => [],
	};

	private static (string Name, ToolingSymbolKind Kind)? ResolveContract(
		BindingContext context,
		CompilationUnitSyntax unit,
		string baseName)
	{
		TypeSymbol? type = UnderUnit(context, unit, () => context.ResolveType(context.NormalizeGenericName(baseName)));
		return type switch
		{
			InterfaceTypeSymbol => (TypeLayoutView.LeafType(type.Name), ToolingSymbolKind.Interface),
			ProtocolTypeSymbol => (TypeLayoutView.LeafType(type.Name), ToolingSymbolKind.Protocol),
			_ => null,
		};
	}

	private static CompilationUnitSyntax? FindUnit(AnalyzedProject analysis, SyntaxNode declaration)
	{
		foreach ((_, CompilationUnitSyntax? unit) in analysis.UnitsByDocument)
		{
			if (unit is not null && Contains(unit, declaration))
			{
				return unit;
			}
		}

		return null;
	}

	private static bool Contains(SyntaxNode root, SyntaxNode node)
	{
		if (ReferenceEquals(root, node))
		{
			return true;
		}

		foreach (SyntaxNode child in Descendants(root))
		{
			if (ReferenceEquals(child, node))
			{
				return true;
			}
		}

		return false;
	}

	private static IEnumerable<SyntaxNode> Descendants(SyntaxNode node)
	{
		foreach (SyntaxNode child in node.GetChildren())
		{
			yield return child;

			foreach (SyntaxNode nested in Descendants(child))
			{
				yield return nested;
			}
		}
	}

	private static T UnderUnit<T>(BindingContext context, CompilationUnitSyntax unit, Func<T> action)
	{
		lock (context)
		{
			CompilationUnitSyntax? previousUnit = context.CurrentUnit;
			string? previousNamespace = context.CurrentNamespace;
			context.CurrentUnit = unit;
			context.CurrentNamespace = unit.NamespaceDeclaration?.Name;
			try
			{
				return action();
			}
			finally
			{
				context.CurrentUnit = previousUnit;
				context.CurrentNamespace = previousNamespace;
			}
		}
	}
}
