using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Directives;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Analysis.Passes.Declaration;

/// <summary>
/// Registers and validates Cvolo namespace aliases declared by <c>using Alias = Namespace.Path;</c>
/// during declaration analysis.
/// </summary>
/// <remarks>
/// An alias is a lexical, compile-time namespace-prefix binding and nothing more: it is registered per
/// physical compilation unit, never enters local or global storage tables, and is never serialized
/// into package metadata. This service owns the declaration-phase rules only — duplicate names,
/// target existence, and the rejection of <c>expose using Alias = ...</c>. Expansion of an alias
/// prefix during name resolution is owned by <see cref="BindingContext"/>.
/// </remarks>
internal sealed class NamespaceAliasValidator(BindingContext context)
{
	/// <summary>
	/// Records every alias declared by a compilation unit and reports duplicate-name and
	/// <c>expose using</c> errors. Runs right after <see cref="ExposeUsingValidator"/> so alias
	/// registration precedes every name lookup that may expand an alias prefix.
	/// </summary>
	public void Register(IEnumerable<CompilationUnitSyntax> units)
	{
		context.NamespaceAliases.Clear();

		foreach (var unit in units)
		{
			context.CurrentUnit = unit;
			var fileContext = context.FileContexts[unit];

			Register(fileContext, unit.Usings);
			Register(fileContext, unit.NamespaceDeclaration?.Usings);
		}
	}

	/// <summary>
	/// Reports every alias whose target is not a namespace of this compilation.
	/// Must run after the struct, union, enum, contract, delegate, function, and global registries
	/// are populated, because an imported namespace is often implied only by the symbols beneath it.
	/// </summary>
	public void ValidateTargets(IEnumerable<CompilationUnitSyntax> units)
	{
		foreach (var unit in units)
		{
			context.CurrentUnit = unit;
			var fileContext = context.FileContexts[unit];

			foreach (var directive in EnumerateAliases(unit))
			{
				if (IsKnownNamespace(directive.NamespaceName))
					continue;

				context.Diagnostics.Report(
					fileContext,
					directive.Span,
					$"namespace alias target '{directive.NamespaceName}' does not exist",
					DiagnosticIds.NamespaceAliasTargetDoesNotExist);
			}
		}
	}

	/// <summary>Yields the unit's alias directives in declaration order, file scope before namespace scope.</summary>
	private static IEnumerable<UsingDirectiveSyntax> EnumerateAliases(CompilationUnitSyntax unit)
	{
		foreach (var directive in unit.Usings)
		{
			if (directive.IsAlias)
				yield return directive;
		}

		if (unit.NamespaceDeclaration is null)
			yield break;

		foreach (var directive in unit.NamespaceDeclaration.Usings)
		{
			if (directive.IsAlias)
				yield return directive;
		}
	}

	/// <summary>
	/// Adds one using scope's aliases to the unit's table, rejecting duplicates and re-exports.
	/// </summary>
	private void Register(CompilationContext fileContext, IReadOnlyList<UsingDirectiveSyntax>? directives)
	{
		if (directives is null || directives.Count == 0)
			return;

		var unit = context.CurrentUnit!;
		var key = unit.Context;

		if (!context.NamespaceAliases.TryGetValue(key, out var aliases))
		{
			aliases = new Dictionary<string, UsingDirectiveSyntax>(StringComparer.Ordinal);
			context.NamespaceAliases[key] = aliases;
		}

		foreach (var directive in directives)
		{
			if (!directive.IsAlias)
				continue;

			var alias = directive.AliasName!;

			// Aliases are local conveniences, never namespace re-export edges.
			if (directive.IsExposed)
			{
				context.Diagnostics.Report(
					fileContext,
					directive.Span,
					$"'expose using' cannot declare the namespace alias '{alias}'. Aliases are local and are not re-exported.",
					DiagnosticIds.ExposeUsingCannotDeclareNamespaceAlias);
				continue;
			}

			if (!aliases.TryAdd(alias, directive))
			{
				context.Diagnostics.Report(
					fileContext,
					directive.AliasSpan ?? directive.Span,
					$"Duplicate namespace alias '{alias}' in the same scope.",
					DiagnosticIds.DuplicateNamespaceAlias);
			}
		}
	}

	/// <summary>
	/// Whether a name is a namespace of this compilation, either declared outright or implied by any
	/// registered symbol living beneath it.
	/// </summary>
	private bool IsKnownNamespace(string candidate)
	{
		if (candidate.Length == 0)
			return false;

		if (context.DeclaredNamespaces.Contains(candidate))
			return true;

		var prefix = candidate + ".";
		return HasSymbolUnder(prefix);
	}

	private bool HasSymbolUnder(string prefix) =>
		context.StructTypes.Keys.Any(k => k.StartsWith(prefix, StringComparison.Ordinal))
		|| context.UnionTypes.Keys.Any(k => k.StartsWith(prefix, StringComparison.Ordinal))
		|| context.EnumTypes.Keys.Any(k => k.StartsWith(prefix, StringComparison.Ordinal))
		|| context.InterfaceTypes.Keys.Any(k => k.StartsWith(prefix, StringComparison.Ordinal))
		|| context.ProtocolTypes.Keys.Any(k => k.StartsWith(prefix, StringComparison.Ordinal))
		|| context.DelegateTypes.Keys.Any(k => k.StartsWith(prefix, StringComparison.Ordinal))
		|| context.OverloadedFunctions.Keys.Any(k => k.StartsWith(prefix, StringComparison.Ordinal))
		|| context.AssociatedFunctions.Keys.Any(k => k.StartsWith(prefix, StringComparison.Ordinal))
		|| context.GlobalsByQualifiedName.Keys.Any(k => k.StartsWith(prefix, StringComparison.Ordinal));
}