using Cvolo.Analysis;
using Cvolo.Analysis.Contracts;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.AST;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using CoreTextSpan = Cvolo.Core.Diagnostics.TextSpan;

namespace Cvolo.Compiler.Tooling.Internal;

/// <summary>
/// The concrete places that satisfy a contract. An interface is implemented nominally: only an
/// <c>extension X : I</c> conforms. A protocol is conformed to structurally: any concrete type
/// whose members satisfy the contract does, wherever the declaring block happens to live. The
/// structural half is answered by the compiler's own <see cref="ContractService"/> rather than by
/// matching member names here, so the editor and the compiler agree on what conforms.
/// </summary>
internal static class ImplementationNavigation
{
	internal static IReadOnlyList<ToolingImplementation> GetImplementations(ProjectSnapshot snapshot, SymbolId symbol)
	{
		ArgumentNullException.ThrowIfNull(snapshot);

		var navigation = snapshot.GetNavigationIndex();
		if (!navigation.Owns(symbol))
		{
			return [];
		}

		var declaration = navigation.Declaration(symbol);
		var definitions = navigation.Definitions(symbol);
		if (declaration is null || definitions.Count == 0)
		{
			return [];
		}

		var context = snapshot.GetAnalysis().BinderContext;
		if (context is null)
		{
			return [];
		}

		return declaration switch
		{
			InterfaceDeclarationSyntax iface => NominalConformers(snapshot, navigation, context, iface.Name),
			ProtocolDeclarationSyntax protocol => StructuralConformers(snapshot, navigation, context, protocol.Name),
			InterfaceMethodDeclarationSyntax member => InterfaceMemberImplementations(snapshot, navigation, context, member),
			ProtocolMethodDeclarationSyntax member => ProtocolMemberImplementations(snapshot, navigation, context, member),
			_ => [],
		};
	}

	/// <summary>
	/// The extension blocks that name a conforming type. A type may inherit an interface through a
	/// base interface, so the test is the compiler's conformance set, not the literal <c>: I</c>.
	/// </summary>
	private static IReadOnlyList<ToolingImplementation> NominalConformers(
		ProjectSnapshot snapshot,
		NavigationIndex navigation,
		BindingContext context,
		string interfaceName)
	{
		var analysis = snapshot.GetAnalysis();
		var results = new List<ToolingImplementation>();
		var seen = new HashSet<(DocumentId, int)>();

		lock (context)
		{
			if (ResolveInterface(context, analysis, interfaceName) is not { } interfaceSymbol)
			{
				return [];
			}

			foreach (var (documentId, unit) in analysis.UnitsByDocument)
			{
				if (unit is null)
				{
					continue;
				}

				UnderUnit(context, unit, () =>
				{
					foreach (var extension in Descendants(unit).OfType<ExtensionDeclarationSyntax>())
					{
						if (string.IsNullOrEmpty(extension.ConformsTo))
						{
							continue;
						}

						var concrete = context.ResolveType(context.NormalizeGenericName(extension.ExtendedTypeName));
						if (concrete is null
							|| !context.Conformance.TryGetValue(concrete.Name, out var interfaces)
							|| !interfaces.Contains(interfaceSymbol.Name))
						{
							continue;
						}

						if (seen.Add((documentId, extension.NameSpan.Start)))
						{
							results.Add(new ToolingImplementation(
								ToolingImplementationKind.Implements,
								new SymbolDefinition(
									documentId,
									new TextSpan(extension.Span.Start, extension.Span.Length),
									new TextSpan(extension.NameSpan.Start, extension.NameSpan.Length)),
								extension.ExtendedTypeName));
						}
					}
				});
			}
		}

		return results;
	}

	private static IReadOnlyList<ToolingImplementation> InterfaceMemberImplementations(
		ProjectSnapshot snapshot,
		NavigationIndex navigation,
		BindingContext context,
		InterfaceMethodDeclarationSyntax member)
	{
		var analysis = snapshot.GetAnalysis();
		if (FindOwningInterface(analysis, member) is not var (documentId, unit, iface))
		{
			return [];
		}

		var results = new List<ToolingImplementation>();
		var seen = new HashSet<(DocumentId, int)>();

		lock (context)
		{
			if (ResolveInterface(context, analysis, iface.Name) is not { } interfaceSymbol)
			{
				return [];
			}

			foreach (var extension in ConformingExtensions(context, analysis, interfaceSymbol))
			{
				foreach (var method in extension.Extension.Methods)
				{
					if (!Implements(method, member))
					{
						continue;
					}

					if (seen.Add((extension.DocumentId, method.NameSpan.Start)))
					{
						results.Add(new ToolingImplementation(
							ToolingImplementationKind.Implements,
							new SymbolDefinition(
								extension.DocumentId,
								new TextSpan(method.Span.Start, method.Span.Length),
								new TextSpan(method.NameSpan.Start, method.NameSpan.Length)),
							$"{extension.Extension.ExtendedTypeName}.{method.Name}"));
					}
				}
			}
		}

		return results;
	}

	private static IReadOnlyList<ToolingImplementation> StructuralConformers(
		ProjectSnapshot snapshot,
		NavigationIndex navigation,
		BindingContext context,
		string protocolName)
	{
		var analysis = snapshot.GetAnalysis();
		var contracts = new ContractService(context);
		var results = new List<ToolingImplementation>();
		var seen = new HashSet<(DocumentId, int)>();

		lock (context)
		{
			if (ResolveProtocol(context, analysis, protocolName) is not { } protocolSymbol)
			{
				return [];
			}

			foreach (var type in ConcreteTypes(context))
			{
				if (!ConformsAnywhere(context, analysis, contracts, type, protocolSymbol))
				{
					continue;
				}

				if (DeclarationOf(navigation, type) is { } definition
					&& seen.Add((definition.DocumentId, definition.SelectionSpan.Start)))
				{
					results.Add(new ToolingImplementation(
						ToolingImplementationKind.ConformsTo,
						definition,
						TypeLayoutView.LeafType(type.Name)));
				}
			}
		}

		return results;
	}

	private static IReadOnlyList<ToolingImplementation> ProtocolMemberImplementations(
		ProjectSnapshot snapshot,
		NavigationIndex navigation,
		BindingContext context,
		ProtocolMethodDeclarationSyntax member)
	{
		var analysis = snapshot.GetAnalysis();
		if (FindOwningProtocol(analysis, member) is not var (documentId, unit, protocol))
		{
			return [];
		}

		var contracts = new ContractService(context);
		var results = new List<ToolingImplementation>();
		var seen = new HashSet<(DocumentId, int)>();

		lock (context)
		{
			if (ResolveProtocol(context, analysis, protocol.Name) is not { } protocolSymbol)
			{
				return [];
			}

			foreach (var type in ConcreteTypes(context))
			{
				if (!ConformsAnywhere(context, analysis, contracts, type, protocolSymbol))
				{
					continue;
				}

				var leaf = TypeLayoutView.LeafType(type.Name);
				var ambiguous = contracts.TryFindAmbiguousProtocolMember(type, protocolSymbol, out _);
				foreach (var extension in ExtensionsOf(context, analysis, leaf))
				{
					foreach (var method in extension.Extension.Methods)
					{
						if (!MemberMatches(method, member))
						{
							continue;
						}

						if (seen.Add((extension.DocumentId, method.NameSpan.Start)))
						{
							results.Add(new ToolingImplementation(
								ToolingImplementationKind.ConformsTo,
								new SymbolDefinition(
									extension.DocumentId,
									new TextSpan(method.Span.Start, method.Span.Length),
									new TextSpan(method.NameSpan.Start, method.NameSpan.Length)),
								$"{extension.Extension.ExtendedTypeName}.{method.Name}",
								ambiguous));
						}
					}
				}
			}
		}

		return results;
	}

	private static IEnumerable<(DocumentId DocumentId, CompilationUnitSyntax Unit, ExtensionDeclarationSyntax Extension)> ConformingExtensions(
		BindingContext context,
		AnalyzedProject analysis,
		InterfaceTypeSymbol interfaceSymbol)
	{
		foreach (var (documentId, unit) in analysis.UnitsByDocument)
		{
			if (unit is null)
			{
				continue;
			}

			foreach (var extension in Descendants(unit).OfType<ExtensionDeclarationSyntax>())
			{
				if (string.IsNullOrEmpty(extension.ConformsTo))
				{
					continue;
				}

				var concrete = context.ResolveType(context.NormalizeGenericName(extension.ExtendedTypeName));
				if (concrete is not null
					&& context.Conformance.TryGetValue(concrete.Name, out var interfaces)
					&& interfaces.Contains(interfaceSymbol.Name))
				{
					yield return (documentId, unit, extension);
				}
			}
		}
	}

	private static IEnumerable<(DocumentId DocumentId, ExtensionDeclarationSyntax Extension)> ExtensionsOf(
		BindingContext context,
		AnalyzedProject analysis,
		string leafName)
	{
		foreach (var (documentId, unit) in analysis.UnitsByDocument)
		{
			if (unit is null)
			{
				continue;
			}

			foreach (var extension in Descendants(unit).OfType<ExtensionDeclarationSyntax>())
			{
				var concrete = context.ResolveType(context.NormalizeGenericName(extension.ExtendedTypeName));
				if (concrete is not null && TypeLayoutView.LeafType(concrete.Name) == leafName)
				{
					yield return (documentId, extension);
				}
			}
		}
	}

	private static bool ConformsAnywhere(
		BindingContext context,
		AnalyzedProject analysis,
		ContractService contracts,
		TypeSymbol type,
		ProtocolTypeSymbol protocol)
	{
		foreach (var (_, unit) in analysis.UnitsByDocument)
		{
			if (unit is null)
			{
				continue;
			}

			var conforms = false;
			UnderUnit(context, unit, () => conforms = contracts.ConformsToProtocol(type, protocol));
			if (conforms)
			{
				return true;
			}
		}

		return false;
	}

	private static IEnumerable<TypeSymbol> ConcreteTypes(BindingContext context)
	{
		var seen = new HashSet<string>(StringComparer.Ordinal);
		foreach (var type in context.StructTypes.Values)
		{
			if (seen.Add(type.Name))
			{
				yield return type;
			}
		}

		foreach (var type in context.UnionTypes.Values)
		{
			if (seen.Add(type.Name))
			{
				yield return type;
			}
		}
	}

	private static InterfaceTypeSymbol? ResolveInterface(BindingContext context, AnalyzedProject analysis, string name)
	{
		var declared = FindDeclaringUnit(analysis, name);
		if (declared is null)
		{
			return null;
		}

		InterfaceTypeSymbol? symbol = null;
		UnderUnit(context, declared, () => symbol = context.ResolveType(context.NormalizeGenericName(name)) as InterfaceTypeSymbol);
		return symbol;
	}

	private static ProtocolTypeSymbol? ResolveProtocol(BindingContext context, AnalyzedProject analysis, string name)
	{
		var declared = FindDeclaringUnit(analysis, name);
		if (declared is null)
		{
			return null;
		}

		ProtocolTypeSymbol? symbol = null;
		UnderUnit(context, declared, () => symbol = context.ResolveType(context.NormalizeGenericName(name)) as ProtocolTypeSymbol);
		return symbol;
	}

	private static CompilationUnitSyntax? FindDeclaringUnit(AnalyzedProject analysis, string name)
	{
		foreach (var unit in analysis.UnitsByDocument.Values)
		{
			if (unit is not null && DeclarationExists(unit, name))
			{
				return unit;
			}
		}

		return analysis.UnitsByDocument.Values.FirstOrDefault(static unit => unit is not null);
	}

	private static bool DeclarationExists(CompilationUnitSyntax unit, string name)
		=> Descendants(unit).Any(node => node switch
		{
			InterfaceDeclarationSyntax iface => iface.Name == name,
			ProtocolDeclarationSyntax protocol => protocol.Name == name,
			_ => false,
		});

	private static (DocumentId DocumentId, CompilationUnitSyntax Unit, InterfaceDeclarationSyntax Interface)? FindOwningInterface(
		AnalyzedProject analysis,
		InterfaceMethodDeclarationSyntax member)
	{
		foreach (var (documentId, unit) in analysis.UnitsByDocument)
		{
			if (unit is null)
			{
				continue;
			}

			foreach (var iface in Descendants(unit).OfType<InterfaceDeclarationSyntax>())
			{
				if (iface.Members.Any(candidate => ReferenceEquals(candidate, member)))
				{
					return (documentId, unit, iface);
				}
			}
		}

		return null;
	}

	private static (DocumentId DocumentId, CompilationUnitSyntax Unit, ProtocolDeclarationSyntax Protocol)? FindOwningProtocol(
		AnalyzedProject analysis,
		ProtocolMethodDeclarationSyntax member)
	{
		foreach (var (documentId, unit) in analysis.UnitsByDocument)
		{
			if (unit is null)
			{
				continue;
			}

			foreach (var protocol in Descendants(unit).OfType<ProtocolDeclarationSyntax>())
			{
				if (protocol.Members.Any(candidate => ReferenceEquals(candidate, member)))
				{
					return (documentId, unit, protocol);
				}
			}
		}

		return null;
	}

	private static SymbolDefinition? DeclarationOf(NavigationIndex navigation, TypeSymbol type)
	{
		var leaf = TypeLayoutView.LeafType(type.Name);
		if (leaf.Length == 0)
		{
			return null;
		}

		return navigation.FindTypeSymbol(leaf) is { } symbol && navigation.Definitions(symbol) is { Count: > 0 } found
			? found[0]
			: null;
	}

	private static void UnderUnit(BindingContext context, CompilationUnitSyntax unit, Action action)
	{
		var previousUnit = context.CurrentUnit;
		var previousNamespace = context.CurrentNamespace;
		context.CurrentUnit = unit;
		context.CurrentNamespace = unit.NamespaceDeclaration?.Name;
		try
		{
			action();
		}
		finally
		{
			context.CurrentUnit = previousUnit;
			context.CurrentNamespace = previousNamespace;
		}
	}

	private static bool Implements(FunctionDeclarationSyntax method, InterfaceMethodDeclarationSyntax member)
		=> method.Name == member.Name
			&& method.ReturnType == member.ReturnType
			&& TypesMatch(method.Parameters, member.Parameters);

	private static bool MemberMatches(FunctionDeclarationSyntax method, ProtocolMethodDeclarationSyntax member)
		=> method.Name == member.Name
			&& method.ReturnType == member.ReturnType
			&& TypesMatch(method.Parameters, member.Parameters);

	private static bool TypesMatch(IReadOnlyList<ParameterSyntax> method, IReadOnlyList<ParameterSyntax> member)
	{
		if (method.Count != member.Count)
		{
			return false;
		}

		for (var i = 0; i < member.Count; i++)
		{
			if (method[i].Type != member[i].Type)
			{
				return false;
			}
		}

		return true;
	}

	private static IEnumerable<SyntaxNode> Descendants(SyntaxNode node)
	{
		foreach (var child in node.GetChildren())
		{
			yield return child;
			foreach (var nested in Descendants(child))
			{
				yield return nested;
			}
		}
	}
}
