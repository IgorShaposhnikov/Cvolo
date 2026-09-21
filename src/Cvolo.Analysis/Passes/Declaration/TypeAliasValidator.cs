using Cvolo.Analysis.Symbols.Base;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Analysis.Passes.Declaration;

/// <summary>
/// Registers and validates Cvolo type aliases during declaration analysis.
/// </summary>
/// <remarks>
/// Alias names are registered before ordinary type declarations are resolved, then validated after
/// real type symbols have been registered. Resolution and cycle tracking remain owned by
/// <see cref="BindingContext"/>; this service owns declaration-phase alias rules and diagnostics.
/// </remarks>
internal sealed class TypeAliasValidator(BindingContext context)
{
	/// <summary>
	/// Registers a type alias under its namespace-aware name during declaration Pass 0a-pre.
	/// </summary>
	/// <remarks>
	/// Underlying-type validation is intentionally deferred until <see cref="Validate"/>, after raw
	/// struct, union, enum, interface, protocol, and delegate symbols have been registered.
	/// </remarks>
	public void Declare(TypeAliasDeclarationSyntax aliasDeclaration)
	{
		var mangledName = context.GetMangledName(aliasDeclaration.Name, context.CurrentNamespace);

		if (!context.TypeAliases.TryAdd(mangledName, aliasDeclaration))
		{
			Report(aliasDeclaration, $"Duplicate type alias '{aliasDeclaration.Name}'");
			return;
		}

		context.SymbolUnits[mangledName] = context.CurrentUnit!;
	}

	/// <summary>
	/// Validates every registered alias after real type names are available during Pass 0a-post.
	/// </summary>
	/// <remarks>
	/// The existing rules reject aliases that shadow real types, unknown or cyclic alias targets,
	/// and aliases used as generic-parameter constraints. Generic aliases are resolved through the
	/// validation-specific BindingContext path so template state is not polluted by type parameters.
	/// </remarks>
	public void Validate(IEnumerable<CompilationUnitSyntax> units)
	{
		foreach (var unit in units)
		{
			context.CurrentUnit = unit;
			context.CurrentNamespace = unit.NamespaceDeclaration?.Name;

			var members = context.CurrentNamespace != null ? unit.NamespaceDeclaration!.Members : unit.Members;

			ValidateConstraintAliases(members);

			foreach (var member in members)
			{
				if (member is not TypeAliasDeclarationSyntax aliasDeclaration)
					continue;

				var mangledName = context.GetMangledName(aliasDeclaration.Name, context.CurrentNamespace);

				if (ResolvesToRealType(aliasDeclaration.Name))
				{
					Report(aliasDeclaration, $"Type alias '{aliasDeclaration.Name}' conflicts with an existing type name.");
					continue;
				}

				// Generic aliases are validated with bare generic parameters substituted to a concrete
				// placeholder so validation does not instantiate templates with TypeParameterSymbol values.
				if (context.ResolveAliasForValidation(mangledName, aliasDeclaration) is null
					&& !context.ReportedAliasCycles.Contains(mangledName))
				{
					Report(
						aliasDeclaration,
						$"Cannot resolve type alias '{aliasDeclaration.Name}'. Referenced type does not exist.",
						DiagnosticIds.UnknownTypeAlias);
				}
			}
		}
	}

	/// <summary>
	/// Reports aliases used in generic-parameter constraints, which are forbidden by CVL1202.
	/// </summary>
	private void ValidateConstraintAliases(IReadOnlyList<SyntaxNode> members)
	{
		// Only structs retain constraint types in the AST today; preserve the existing declaration
		// pass behavior until other declaration forms expose equivalent constraint data.
		foreach (var member in members)
		{
			if (member is not StructDeclarationSyntax structDeclaration)
				continue;

			foreach (var constraint in structDeclaration.GenericParameterConstraints.Values.SelectMany(list => list))
			{
				if (!context.IsAliasReference(constraint))
					continue;

				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(
					currentFileContext,
					structDeclaration.Span,
					$"Type alias '{constraint}' cannot be used as a generic parameter constraint.",
					DiagnosticIds.AliasAsConstraint);
			}
		}
	}

	/// <summary>
	/// Returns whether a primitive or registered real type already claims the alias's source name.
	/// </summary>
	private bool ResolvesToRealType(string name)
	{
		if (TypeSymbol.FromName(name) is not null)
			return true;

		var currentUnit = context.CurrentUnit;
		if (context.CurrentNamespace is not null)
		{
			var localMangled = context.GetMangledName(name, context.CurrentNamespace);
			if (IsRegisteredTypeName(localMangled))
				return true;
		}

		if (IsRegisteredTypeName(name))
			return true;

		if (currentUnit is null)
			return false;

		foreach (var namespaceName in context.GetActiveUsings(currentUnit))
		{
			if (IsRegisteredTypeName(context.GetMangledName(name, namespaceName)))
				return true;
		}

		return false;
	}

	/// <summary>
	/// Returns whether a fully resolved name belongs to a non-alias semantic type registry.
	/// </summary>
	private bool IsRegisteredTypeName(string name) =>
		context.StructTypes.ContainsKey(name)
		|| context.UnionTypes.ContainsKey(name)
		|| context.EnumTypes.ContainsKey(name)
		|| context.InterfaceTypes.ContainsKey(name)
		|| context.ProtocolTypes.ContainsKey(name)
		|| context.DelegateTypes.ContainsKey(name);

	/// <summary>
	/// Reports an alias diagnostic without a dedicated diagnostic identifier.
	/// </summary>
	private void Report(SyntaxNode node, string message)
	{
		var currentFileContext = context.FileContexts[context.CurrentUnit!];
		context.Diagnostics.Report(currentFileContext, node.Span, message);
	}

	/// <summary>
	/// Reports an alias diagnostic with its stable diagnostic identifier.
	/// </summary>
	private void Report(SyntaxNode node, string message, string diagnosticId)
	{
		var currentFileContext = context.FileContexts[context.CurrentUnit!];
		context.Diagnostics.Report(currentFileContext, node.Span, message, diagnosticId);
	}
}
