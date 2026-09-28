using Cvolo.Analysis;
using Cvolo.Analysis.Symbols;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using Cvolo.Core.AST.Expressions;

namespace Cvolo.Compiler.Tooling.Internal;

/// <summary>
/// Computes the editor inlay hints for one document from the compiler's own binding. Every hint is
/// derived from a resolved compiler fact (a bound variable type, a resolved call target, a final
/// receiver classification, the authoritative type layout, a compiler-selected enum value), so an
/// unresolvable or incomplete construct simply produces no hint rather than a guess.
/// </summary>
/// <remarks>
/// Generic argument hints are deliberately absent: <see cref="FunctionSymbol"/> exposes no resolved
/// type-argument substitution, and the increment requires one authoritative substitution instead of
/// an LSP-side reconstruction of the type name. The option is still honoured so enabling it is a
/// no-op rather than an error.
/// </remarks>
internal static class InlayHintService
{
	private const char MiddleDot = '·';

	internal static IReadOnlyList<ToolingInlayHint> GetInlayHints(
		ProjectSnapshot snapshot,
		DocumentSnapshot document,
		TextSpan requestedRange,
		ToolingInlayHintOptions options)
	{
		var analysis = snapshot.GetAnalysis();
		if (analysis.BinderContext is null
			|| !analysis.UnitsByDocument.TryGetValue(document.Id, out var unit)
			|| unit is null)
		{
			return [];
		}

		var context = analysis.BinderContext;
		var source = document.Text.ToString();
		var hints = new List<ToolingInlayHint>();
		var types = new Dictionary<SyntaxNode, TypeSymbol?>();
		var layouts = new Dictionary<SyntaxNode, TypeLayoutInspection?>();

		lock (context)
		{
			foreach (var (node, parent) in InRange(unit, requestedRange))
			{
				switch (node)
				{
					case VariableDeclarationSyntax variable when options.Types && variable.Type is null:
						AddTypeHint(hints, context, source, variable);
						break;
					case CallExpressionSyntax call when options.Parameters:
						AddParameterHints(hints, context, call);
						break;
					case FunctionDeclarationSyntax method when options.ReceiverMutability
						&& method.Receiver == ReceiverContract.None
						&& !method.IsAssociated
						&& parent is ExtensionDeclarationSyntax owner:
						AddReceiverHint(hints, context, source, unit, owner, method);
						break;
					case StructFieldSyntax field when options.Layout && parent is StructDeclarationSyntax structDeclaration:
						AddFieldLayoutHint(hints, field.Name, field.Span, LayoutOf(context, layouts, structDeclaration));
						break;
					case UnionFieldSyntax field when options.Layout && parent is UnionDeclarationSyntax unionDeclaration:
						AddFieldLayoutHint(hints, field.Name, field.Span, LayoutOf(context, layouts, unionDeclaration));
						break;
					case EnumVariantDeclarationSyntax variant when options.EnumValues && parent is EnumDeclarationSyntax enumDeclaration:
						AddEnumValueHint(hints, context, types, variant, enumDeclaration);
						break;
				}
			}
		}

		return [.. hints.OrderBy(hint => hint.Position).ThenBy(hint => hint.Kind)];
	}

	/// <summary>
	/// §28: an inferred local type, shown only where the source omitted the type. The text is the
	/// bound variable's own type, never a reconstruction of the type name from the initializer.
	/// </summary>
	private static void AddTypeHint(List<ToolingInlayHint> hints, BindingContext context, string source, VariableDeclarationSyntax variable)
	{
		if (!context.VariableSymbols.TryGetValue(variable, out var symbol))
			return;

		var typeName = symbol.Type.Name;
		if (string.IsNullOrEmpty(typeName))
			return;

		if (!TryIndexOf(source, variable.Span, variable.Name, out var nameStart))
			return;

		hints.Add(new ToolingInlayHint(
			nameStart + variable.Name.Length,
			ToolingInlayHintKind.Type,
			$": {typeName}",
			PaddingLeft: false));
	}

	/// <summary>
	/// §29-32: the resolved callable's parameter name in front of each argument. A receiver is never
	/// hinted (the synthetic parameter-0 "this" is an ABI parameter, not a source argument), and an
	/// argument that already spells the parameter name is left alone.
	/// </summary>
	private static void AddParameterHints(List<ToolingInlayHint> hints, BindingContext context, CallExpressionSyntax call)
	{
		if (!context.ResolvedCalls.TryGetValue(call, out var target) || target.Parameters.Count == 0)
			return;

		var first = target.IsInstanceExtension ? 1 : 0;

		for (var i = 0; i < call.Arguments.Count; i++)
		{
			var index = first + i;
			if (index >= target.Parameters.Count)
				break;

			var name = target.Parameters[index].Name;
			if (string.IsNullOrEmpty(name) || name == "this")
				continue;

			var argument = call.Arguments[i];
			if (argument is IdentifierExpressionSyntax identifier && identifier.Name == name)
				continue;

			hints.Add(new ToolingInlayHint(
				argument.Span.Start,
				ToolingInlayHintKind.Parameter,
				$"{name}: ",
				PaddingLeft: false));
		}
	}

	/// <summary>
	/// §33: the receiver's final mutability, shown only where the source omitted the contract. The
	/// classification is the one the validation pass wrote onto the registered symbol, so a hint never
	/// contradicts the body the compiler accepted.
	/// </summary>
	private static void AddReceiverHint(
		List<ToolingInlayHint> hints,
		BindingContext context,
		string source,
		CompilationUnitSyntax unit,
		ExtensionDeclarationSyntax owner,
		FunctionDeclarationSyntax method)
	{
		if (context.ResolveType(owner.ExtendedTypeName) is not { } ownerType)
			return;

		var baseName = context.GetMangledName($"{owner.ExtendedTypeName}.{method.Name}", unit.NamespaceDeclaration?.Name);
		var signature = new List<TypeSymbol> { new PointerTypeSymbol(ownerType, isMutable: false) };

		foreach (var parameter in method.Parameters)
		{
			if (context.ResolveType(parameter.Type) is not { } parameterType)
				return;

			signature.Add(parameterType);
		}

		if (context.Globals.Lookup(context.GetOverloadedMangledName(baseName, signature)) is not FunctionSymbol target)
			return;

		if (target.Parameters.Count == 0 || target.Parameters[0].Type is not PointerTypeSymbol receiver)
			return;

		if (AfterParameterList(source, method) is not { } position)
			return;

		hints.Add(new ToolingInlayHint(
			position,
			ToolingInlayHintKind.ReceiverMutability,
			receiver.IsMutable ? "refvar this" : "ref this",
			PaddingLeft: true));
	}

	/// <summary>
	/// §34: where a field sits in its type. Tail padding is intentionally not repeated here; it stays
	/// in the layout CodeLens and in the layout inspection so it is reported exactly once.
	/// </summary>
	private static void AddFieldLayoutHint(List<ToolingInlayHint> hints, string fieldName, Cvolo.Core.Diagnostics.TextSpan span, TypeLayoutInspection? layout)
	{
		if (layout is null)
			return;

		if (layout.Members.FirstOrDefault(candidate => candidate.Name == fieldName) is not { } member)
			return;

		var suffix = layout.Padding.FirstOrDefault(candidate =>
				candidate.Kind == ToolingPaddingKind.Internal
				&& candidate.Offset + candidate.Size == member.Offset) is { Size: > 0 } pad
			? $" {MiddleDot} pad {pad.Size} before"
			: string.Empty;

		hints.Add(new ToolingInlayHint(
			span.End,
			ToolingInlayHintKind.Layout,
			$"offset {member.Offset} {MiddleDot} size {member.Size}{suffix}",
			PaddingLeft: true));
	}

	/// <summary>
	/// §35: the compiler-selected value of an enum variant, or nothing when the source already spells
	/// one out or the variant never resolved.
	/// </summary>
	private static void AddEnumValueHint(
		List<ToolingInlayHint> hints,
		BindingContext context,
		Dictionary<SyntaxNode, TypeSymbol?> types,
		EnumVariantDeclarationSyntax variant,
		EnumDeclarationSyntax declaration)
	{
		if (variant.Value is not null)
			return;

		if (TypeOf(context, types, declaration) is not EnumTypeSymbol enumType)
			return;

		if (enumType.FindVariant(variant.Name) is not { } resolved)
			return;

		hints.Add(new ToolingInlayHint(variant.Span.End, ToolingInlayHintKind.EnumValue, $"= {resolved.Value}", PaddingLeft: true));
	}

	/// <summary>
	/// Resolves the position just past the closing parenthesis of a declaration's parameter list. The
	/// search is bounded by the declaration itself, and starts after the last parameter so a nested
	/// parenthesis inside an earlier parameter type cannot be mistaken for the end of the list.
	/// </summary>
	private static int? AfterParameterList(string source, FunctionDeclarationSyntax method)
	{
		var start = method.Parameters.Count > 0 ? method.Parameters[^1].Span.End : method.NameSpan.End;
		var limit = method.Span.End;

		if (start < 0 || limit > source.Length || start > limit)
			return null;

		var index = source.IndexOf(')', start, limit - start);
		return index < 0 ? null : index + 1;
	}

	private static TypeSymbol? TypeOf(BindingContext context, Dictionary<SyntaxNode, TypeSymbol?> cache, SyntaxNode declaration)
	{
		if (cache.TryGetValue(declaration, out var cached))
			return cached;

		var name = declaration switch
		{
			StructDeclarationSyntax structDeclaration => structDeclaration.Name,
			UnionDeclarationSyntax unionDeclaration => unionDeclaration.Name,
			EnumDeclarationSyntax enumDeclaration => enumDeclaration.Name,
			_ => null,
		};

		var resolved = name is null ? null : context.ResolveType(name);
		cache[declaration] = resolved;
		return resolved;
	}

	/// <summary>
	/// The authoritative layout of the declared type, or null when there is no concrete storage type
	/// to describe: an unresolved generic template has no numeric layout and is never guessed at.
	/// </summary>
	private static TypeLayoutInspection? LayoutOf(BindingContext context, Dictionary<SyntaxNode, TypeLayoutInspection?> cache, SyntaxNode declaration)
	{
		if (cache.TryGetValue(declaration, out var cached))
			return cached;

		var inspected = TypeOf(context, new Dictionary<SyntaxNode, TypeSymbol?>(), declaration) is { } type
			? TypeLayoutAdapter.ToTooling(context.LayoutService.Inspect(type))
			: null;

		cache[declaration] = inspected;
		return inspected;
	}

	/// <summary>
	/// Walks the document's own syntax tree, restricted to the requested range. A node outside the
	/// request is neither reported nor descended into, so a viewport-sized request never costs a
	/// whole-file semantic walk. A zero-length range is the caret, so only nodes containing it match.
	/// </summary>
	private static IEnumerable<(SyntaxNode Node, SyntaxNode? Parent)> InRange(SyntaxNode root, TextSpan range)
	{
		if (!Contains(root.Span, range))
			yield break;

		yield return (root, null);

		foreach (var child in root.GetChildren())
		{
			// The recursion already knows each node's own parent; only a direct child, which the
			// recursion reported without one, is attributed to this frame.
			foreach (var entry in InRange(child, range))
				yield return (entry.Node, entry.Parent ?? root);
		}
	}

	private static bool Contains(Cvolo.Core.Diagnostics.TextSpan span, TextSpan range)
	{
		if (span.Length <= 0)
			return true;

		if (span.End < range.Start)
			return false;

		return range.Length == 0 ? span.Start <= range.Start : span.Start < range.Start + range.Length;
	}

	private static bool TryIndexOf(string source, Cvolo.Core.Diagnostics.TextSpan span, string name, out int position)
	{
		if (name.Length == 0 || span.Start < 0 || span.End > source.Length)
		{
			position = 0;
			return false;
		}

		var index = source.IndexOf(name, span.Start, StringComparison.Ordinal);
		if (index < 0 || index + name.Length > span.End)
		{
			position = 0;
			return false;
		}

		position = index;
		return true;
	}
}
