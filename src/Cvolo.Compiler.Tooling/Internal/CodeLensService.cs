using Cvolo.Analysis;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;

namespace Cvolo.Compiler.Tooling.Internal;

/// <summary>
/// Computes the editor CodeLens entries for a whole document in one batched pass. Reference counts
/// come from the snapshot's single project-wide occurrence index, and layout comes from the
/// compiler's authoritative layout service, so the numbers shown in the editor are the same numbers
/// <c>sizeof</c>, <c>alignof</c> and <c>offsetof</c> report.
/// </summary>
internal static class CodeLensService
{
	/// <summary>The separator between the facts of one lens title, e.g. the layout facts.</summary>
	private const char Separator = '|';

	internal static IReadOnlyList<ToolingCodeLensInfo> GetCodeLenses(ProjectSnapshot snapshot, DocumentId document, ToolingCodeLensOptions options)
	{
		return GetCodeLenses(snapshot, snapshot.GetDocument(document), options);
	}

	internal static IReadOnlyList<ToolingCodeLensInfo> GetCodeLenses(ProjectSnapshot snapshot, DocumentSnapshot document, ToolingCodeLensOptions options)
	{
		ArgumentNullException.ThrowIfNull(document);

		var navigation = snapshot.GetNavigationIndex();
		var context = snapshot.GetAnalysis().BinderContext;
		var occurrences = context is null ? null : snapshot.GetOccurrenceIndex();
		var result = new List<ToolingCodeLensInfo>();

		foreach (var (symbol, owner) in Flatten(navigation.DocumentSymbols(document.Id)))
		{
			var declaration = navigation.Declaration(symbol.SymbolId);

			if (options.References && occurrences is not null && ShouldShowReferences(options, symbol.Kind, declaration))
			{
				var count = occurrences.ReferenceCount(symbol.SymbolId);
				result.Add(new ToolingCodeLensInfo(
					symbol.SelectionSpan,
					ToolingCodeLensKind.References,
					$"{count} reference{(count == 1 ? string.Empty : "s")}",
					symbol.SymbolId,
					null,
					null,
					new ToolingReferenceCount(symbol.SymbolId, count)));
			}

			if (options.Layout && TryInspect(declaration, context, out var inspection))
			{
				result.Add(new ToolingCodeLensInfo(
					symbol.SelectionSpan,
					ToolingCodeLensKind.Layout,
					LayoutTitle(inspection),
					symbol.SymbolId,
					inspection,
					null));
			}
			else if (options.FieldLayout && TryFieldLayout(symbol, owner, navigation, context, out var fieldLayout))
			{
				result.Add(new ToolingCodeLensInfo(
					symbol.SelectionSpan,
					ToolingCodeLensKind.Layout,
					FieldLayoutTitle(fieldLayout),
					symbol.SymbolId,
					null,
					null,
					null,
					fieldLayout));
			}

			if (options.NativeInterop && TryLinkage(navigation, symbol, out var linkage))
			{
				result.Add(new ToolingCodeLensInfo(
					symbol.SelectionSpan,
					ToolingCodeLensKind.NativeInterop,
					LinkageTitle(linkage),
					symbol.SymbolId,
					null,
					linkage));
			}
		}

		return [.. result.OrderBy(lens => lens.Range.Start).ThenBy(lens => lens.Kind)];
	}

	internal static TypeLayoutInspection? GetTypeLayoutAtPosition(ProjectSnapshot snapshot, DocumentSnapshot document, int position)
	{
		ArgumentNullException.ThrowIfNull(document);
		ArgumentOutOfRangeException.ThrowIfNegative(position);
		ArgumentOutOfRangeException.ThrowIfGreaterThan(position, document.Text.Length);

		var navigation = snapshot.GetNavigationIndex();
		if (navigation.Lookup(document.Id, position) is not { } resolved)
			return null;

		var context = snapshot.GetAnalysis().BinderContext;

		// A field is not itself a type: the layout the editor shows for a field is the layout of the
		// type that stores it, which is what a field lens click and "Show Type Layout" on a field
		// both ask for. The owner comes from the outline rather than from a name match, so a field
		// and a same-spelled type never cross over.
		if (resolved.Kind is ToolingSymbolKind.Field or ToolingSymbolKind.EnumMember
			&& TryOwningDeclaration(navigation, document.Id, position) is { } owner)
		{
			return TryInspect(owner, context, out var ownerLayout) ? ownerLayout : null;
		}

		return TryInspect(navigation.Declaration(resolved.SymbolId), context, out var inspection)
			? inspection
			: null;
	}

	/// <summary>
	/// Finds the innermost aggregate declaration that contains <paramref name="position"/>, so a
	/// field position resolves to the type that stores it.
	/// </summary>
	private static SyntaxNode? TryOwningDeclaration(NavigationIndex navigation, DocumentId document, int position)
	{
		foreach (var (symbol, owner) in Flatten(navigation.DocumentSymbols(document)))
		{
			if (symbol.Kind is not (ToolingSymbolKind.Field or ToolingSymbolKind.EnumMember))
				continue;

			if (position < symbol.Range.Start || position > symbol.Range.End)
				continue;

			return owner is null ? null : navigation.Declaration(owner.SymbolId);
		}

		return null;
	}

	/// <summary>
	/// Flattens the document outline, keeping each declaration's immediate owner. The outline is the
	/// compiler's own declaration tree for this document, so a CodeLens is never attached to a
	/// declaration the compiler does not model, and a field is always attributed to the type the
	/// compiler bound it to.
	/// </summary>
	private static IEnumerable<(DocumentSymbolInfo Symbol, DocumentSymbolInfo? Owner)> Flatten(IReadOnlyList<DocumentSymbolInfo> roots)
	{
		foreach (var root in roots)
		{
			yield return (root, null);

			foreach (var nested in Flatten(root.Children))
				yield return (nested.Symbol, nested.Owner ?? root);
		}
	}

	/// <summary>
	/// Produces the compiler-computed storage facts for one field, or false when the field has no
	/// reportable layout. A field is eligible when it is a struct or union field, its containing type
	/// has an authoritative layout, and the field appears in that layout. Everything else - an
	/// unresolved generic template, an enum variant (whose storage is its backing integer, not a
	/// member list), a compiler-generated declaration with no source field, and a type whose layout
	/// the compiler cannot describe numerically - simply produces no lens rather than a guess.
	/// </summary>
	private static bool TryFieldLayout(
		DocumentSymbolInfo symbol,
		DocumentSymbolInfo? owner,
		NavigationIndex navigation,
		BindingContext? context,
		out ToolingFieldLayoutInfo fieldLayout)
	{
		fieldLayout = null!;
		if (context is null
			|| owner is null
			|| symbol.Kind is not ToolingSymbolKind.Field
			|| navigation.Declaration(owner.SymbolId) is not (StructDeclarationSyntax or UnionDeclarationSyntax)
			|| !TryInspect(navigation.Declaration(owner.SymbolId), context, out var layout))
		{
			return false;
		}

		if (layout.Members.FirstOrDefault(member => string.Equals(member.Name, symbol.Name, StringComparison.Ordinal)) is not { } member)
			return false;

		var paddingBefore = layout.Padding.FirstOrDefault(region =>
				region.Kind == ToolingPaddingKind.Internal
				&& region.Offset + region.Size == member.Offset) is { } pad
			? pad.Size
			: 0L;

		fieldLayout = new ToolingFieldLayoutInfo(
			layout.TypeDisplay,
			member.Name,
			member.Offset,
			member.Size,
			member.Alignment,
			paddingBefore);

		return true;
	}

	/// <summary>
	/// Decides whether a declaration gets a reference lens. A member lens is opt-in because a large
	/// struct or enum would otherwise get one line per field, and an extension block is not a
	/// first-class referenceable symbol, so no artificial "references to extension T" lens is
	/// invented for it; its members are separate symbols.
	/// </summary>
	private static bool ShouldShowReferences(ToolingCodeLensOptions options, ToolingSymbolKind kind, SyntaxNode? declaration)
	{
		if (declaration is ExtensionDeclarationSyntax)
			return false;

		if (kind is ToolingSymbolKind.Field or ToolingSymbolKind.EnumMember)
			return options.Members;

		return kind switch
		{
			ToolingSymbolKind.Struct
				or ToolingSymbolKind.Union
				or ToolingSymbolKind.Enum
				or ToolingSymbolKind.Interface
				or ToolingSymbolKind.Protocol
				or ToolingSymbolKind.Delegate
				or ToolingSymbolKind.TypeAlias
				or ToolingSymbolKind.Function
				or ToolingSymbolKind.Method
				or ToolingSymbolKind.ExtensionMethod
				or ToolingSymbolKind.AssociatedFunction
				or ToolingSymbolKind.Operator
				or ToolingSymbolKind.Constructor
				or ToolingSymbolKind.Destructor
				or ToolingSymbolKind.Global
				or ToolingSymbolKind.Constant => true,
			_ => false,
		};
	}

	/// <summary>
	/// Produces the authoritative layout of the type declared by <paramref name="declaration"/>, or
	/// false when there is no concrete storage type to describe. An unresolved generic template has
	/// no numeric layout: reporting one would be a guess, so the layout lens is simply omitted.
	/// </summary>
	private static bool TryInspect(SyntaxNode? declaration, BindingContext? context, out TypeLayoutInspection inspection)
	{
		inspection = null!;
		if (context is null || declaration is null)
			return false;

		string? name = declaration switch
		{
			StructDeclarationSyntax { GenericParameters.Count: 0 } structDeclaration => structDeclaration.Name,
			UnionDeclarationSyntax { GenericParameters.Count: 0 } unionDeclaration => unionDeclaration.Name,
			EnumDeclarationSyntax enumDeclaration => enumDeclaration.Name,
			DelegateDeclarationSyntax delegateDeclaration => delegateDeclaration.Name,
			_ => null,
		};

		if (name is null)
			return false;

		TypeSymbol? type;
		lock (context)
		{
			type = context.ResolveType(name);
		}

		if (type is null)
			return false;

		inspection = TypeLayoutAdapter.ToTooling(context.LayoutService.Inspect(type));
		return true;
	}

	private static string LayoutTitle(TypeLayoutInspection layout) =>
		$"size {layout.Size}B {Separator} align {layout.Alignment}B {Separator} padding {layout.PaddingSize}B";

	/// <summary>
	/// Where one field is stored. The offset comes first because that is the question a field layout
	/// lens answers; the padding that precedes the field is appended so a gap in the offsets is
	/// explained where it appears, and tail padding stays with the type summary.
	/// </summary>
	private static string FieldLayoutTitle(ToolingFieldLayoutInfo field) =>
		$"offset {field.Offset}B {Separator} size {field.Size}B {Separator} align {field.Alignment}B"
		+ (field.PaddingBefore > 0 ? $" {Separator} pad {field.PaddingBefore}B before" : string.Empty);

	private static string LinkageTitle(NativeLinkageInfo linkage)
	{
		var direction = linkage.Direction == NativeLinkageDirection.Import ? "import" : "export";
		var convention = string.IsNullOrEmpty(linkage.CallingConvention) ? string.Empty : $"{linkage.CallingConvention} {Separator} ";
		var library = string.IsNullOrEmpty(linkage.Library) ? string.Empty : $"{linkage.Library} {Separator} ";
		return $"{direction} {convention}{library}{linkage.ExternalName}";
	}

	/// <summary>
	/// Maps already-resolved interop metadata onto a linkage fact. Nothing is inferred from a call
	/// site: in particular no register assignment or ABI classification is ever reported.
	/// </summary>
	private static bool TryLinkage(NavigationIndex navigation, DocumentSymbolInfo symbol, out NativeLinkageInfo linkage)
	{
		linkage = null!;
		if (navigation.NativeInteropFor(symbol.SymbolId) is not { } interop)
			return false;

		linkage = interop.Kind switch
		{
			NativeInteropKind.ForeignGlobal => new NativeLinkageInfo(
				NativeLinkageDirection.Import,
				interop.LibraryName,
				interop.CallingConvention,
				interop.ImportName ?? symbol.Name),
			NativeInteropKind.Export => new NativeLinkageInfo(
				NativeLinkageDirection.Export,
				null,
				interop.CallingConvention,
				symbol.Name),
			NativeInteropKind.NativeDelegate => new NativeLinkageInfo(
				NativeLinkageDirection.Import,
				interop.LibraryName,
				interop.CallingConvention,
				symbol.Name),
			_ => null,
		};

		return linkage is not null;
	}
}
