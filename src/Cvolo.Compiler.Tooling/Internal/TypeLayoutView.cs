using Cvolo.Analysis;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using CoreTextSpan = Cvolo.Core.Diagnostics.TextSpan;

namespace Cvolo.Compiler.Tooling.Internal;

/// <summary>
/// Produces the compiler's authoritative storage layout of the type at a source position, and attaches
/// to that layout everything a viewer may navigate to: where the type name is declared, where each
/// field name and each field's type name are declared, the field's <c>///</c> documentation, and whether
/// the field's own type is an aggregate whose layout can be requested on its own.
/// <para>
/// Every target is a declaration the compiler already indexed for go-to-definition, reached by asking
/// the navigation index about a real source position, and every target is the one F12 would open from
/// the same position. Nothing is derived from the text a viewer displays, so a client never has to
/// recognize a name to follow a link, and a link that exists is a link the compiler agrees with.
/// </para>
/// </summary>
internal static class TypeLayoutView
{
	/// <summary>
	/// The layout of the type a position names, or null when the position binds to no type that has a
	/// numeric layout. A field is not itself a type, so a position on a field resolves to the type that
	/// stores it: that is the layout a field lens and "Show Type Layout" on a field both ask for.
	/// </summary>
	internal static TypeLayoutInspection? At(ProjectSnapshot snapshot, DocumentSnapshot document, int position)
	{
		ArgumentNullException.ThrowIfNull(document);
		ArgumentOutOfRangeException.ThrowIfNegative(position);
		ArgumentOutOfRangeException.ThrowIfGreaterThan(position, document.Text.Length);

		var navigation = snapshot.GetNavigationIndex();
		if (navigation.Lookup(document.Id, position) is not { } resolved)
			return null;

		var context = snapshot.GetAnalysis().BinderContext;
		var roots = navigation.DocumentSymbols(document.Id);

		DocumentSymbolInfo? type;
		if (resolved.Kind is ToolingSymbolKind.Field or ToolingSymbolKind.EnumMember)
		{
			// The owner comes from the outline rather than from a name match, so a field and a
			// same-spelled type never cross over. Only a position inside the field's own declaration
			// resolves to the storing type; a field *use* falls through to the value path below.
			if (OwningSymbol(roots, position) is not { } owner)
				return ValueLayout(snapshot, navigation, document, context, roots, position);
			type = owner;
			if (!TryInspect(navigation.Declaration(owner.SymbolId), context, out var ownerLayout))
				return null;

			return Enrich(snapshot, navigation, document, owner, ownerLayout, resolved: null);
		}

		// A type declaration or a type use names the type directly.
		if (TryInspect(navigation.Declaration(resolved.SymbolId), context, out var inspection))
			return Enrich(snapshot, navigation, document, Find(roots, resolved.SymbolId), inspection, resolved);

		// Otherwise the position names a value - a variable, parameter, global or field - whose
		// compiler-resolved type can be laid out (§25). A type the compiler cannot resolve yields
		// nothing rather than a guessed layout (§54).
		return ValueLayout(snapshot, navigation, document, context, roots, position);
	}

	/// <summary>
	/// The layout of the compiler-resolved type of the value at <paramref name="position"/>, used when
	/// the position names a variable, parameter, global or field rather than a type. The type's own
	/// outline entry supplies the field facts and the declaration the layout's type name navigates to;
	/// a type declared in another document has no outline entry here, so it lays out with no field
	/// targets.
	/// </summary>
	private static TypeLayoutInspection? ValueLayout(
		ProjectSnapshot snapshot,
		NavigationIndex navigation,
		DocumentSnapshot document,
		BindingContext? context,
		IReadOnlyList<DocumentSymbolInfo> roots,
		int position)
	{
		if (navigation.TypeAt(document.Id, position) is not { } resolvedType)
			return null;

		if (!TryInspect(resolvedType, context, out var inspection))
			return null;

		return Enrich(snapshot, navigation, document, FindType(roots, inspection.TypeDisplay), inspection, resolved: null);
	}

	/// <summary>
	/// The layout of the type a stored subject names, re-resolved against the current snapshot. This is
	/// the refresh path: a client that once showed a type keeps only the canonical name and asks again
	/// after the project changes, so the numbers it shows are always the compiler's latest. A type that
	/// no longer resolves - renamed, removed, or no longer concrete - yields null, which the editor
	/// presents as the unavailable state rather than leaving stale facts on screen.
	/// </summary>
	internal static TypeLayoutInspection? BySubject(ProjectSnapshot snapshot, DocumentSnapshot document, string typeName)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		ArgumentNullException.ThrowIfNull(document);
		if (string.IsNullOrWhiteSpace(typeName))
			return null;

		var context = snapshot.GetAnalysis().BinderContext;
		if (context is null)
			return null;

		TypeSymbol? type;
		lock (context)
			type = context.ResolveType(typeName);

		if (type is null || !TryInspect(type, context, out var inspection))
			return null;

		var navigation = snapshot.GetNavigationIndex();
		var roots = navigation.DocumentSymbols(document.Id);
		return Enrich(snapshot, navigation, document, FindType(roots, inspection.TypeDisplay), inspection, resolved: null);
	}

	/// <summary>
	/// Produces the authoritative layout of the type declared by <paramref name="declaration"/>, or
	/// false when there is no concrete storage type to describe. An unresolved generic template has
	/// no numeric layout: reporting one would be a guess, so the layout lens is simply omitted.
	/// </summary>
	internal static bool TryInspect(SyntaxNode? declaration, BindingContext? context, out TypeLayoutInspection inspection)
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

	/// <summary>
	/// Produces the authoritative layout of an already-resolved type, or false when there is no concrete
	/// storage type to describe. The caller resolved the type through the compiler's own binding, so this
	/// never guesses one.
	/// </summary>
	internal static bool TryInspect(TypeSymbol? type, BindingContext? context, out TypeLayoutInspection inspection)
	{
		inspection = null!;
		if (context is null || type is null)
			return false;

		var layout = context.LayoutService.Inspect(type);
		if (layout is null)
			return false;

		inspection = TypeLayoutAdapter.ToTooling(layout);
		return true;
	}

	/// <summary>
	/// Flattens the document outline, keeping each declaration's immediate owner. The outline is the
	/// compiler's own declaration tree for this document, so a CodeLens or a navigation target is never
	/// attached to a declaration the compiler does not model, and a field is always attributed to the
	/// type the compiler bound it to.
	/// </summary>
	internal static IEnumerable<(DocumentSymbolInfo Symbol, DocumentSymbolInfo? Owner)> Flatten(IReadOnlyList<DocumentSymbolInfo> roots)
	{
		foreach (var root in roots)
		{
			yield return (root, null);

			foreach (var nested in Flatten(root.Children))
				yield return (nested.Symbol, nested.Owner ?? root);
		}
	}

	/// <summary>
	/// The innermost aggregate declaration that contains <paramref name="position"/>, so a field
	/// position resolves to the type that stores it.
	/// </summary>
	private static DocumentSymbolInfo? OwningSymbol(IReadOnlyList<DocumentSymbolInfo> roots, int position)
	{
		foreach (var (symbol, owner) in Flatten(roots))
		{
			if (symbol.Kind is not (ToolingSymbolKind.Field or ToolingSymbolKind.EnumMember))
				continue;

			if (position < symbol.Range.Start || position > symbol.Range.End)
				continue;

			return owner;
		}

		return null;
	}

	private static DocumentSymbolInfo? Find(IReadOnlyList<DocumentSymbolInfo> roots, SymbolId symbol)
	{
		foreach (var (candidate, _) in Flatten(roots))
		{
			if (candidate.SymbolId == symbol)
				return candidate;
		}

		return null;
	}

	/// <summary>
	/// The outline entry of the aggregate type named by <paramref name="display"/>, used when the
	/// position named a value rather than the type itself and only the compiler-resolved type name is
	/// available. The join is by kind and leaf name inside this document, mirroring how a member's own
	/// field is joined by name.
	/// </summary>
	private static DocumentSymbolInfo? FindType(IReadOnlyList<DocumentSymbolInfo> roots, string display)
	{
		var leaf = LeafType(display);
		foreach (var (candidate, _) in Flatten(roots))
		{
			if (candidate.Kind is ToolingSymbolKind.Struct or ToolingSymbolKind.Union or ToolingSymbolKind.Enum
				&& string.Equals(candidate.Name, leaf, StringComparison.Ordinal))
			{
				return candidate;
			}
		}

		return null;
	}

	/// <summary>
	/// Attaches the declaration the type name navigates to, and per member the field's own declaration,
	/// its documentation, and the field's type declaration. The member order and every number remain the
	/// layout service's; this only adds what the navigation index already knows, and every member fact
	/// is optional, so a type whose fields are not in this document still lays out normally.
	/// </summary>
	private static TypeLayoutInspection Enrich(
		ProjectSnapshot snapshot,
		NavigationIndex navigation,
		DocumentSnapshot document,
		DocumentSymbolInfo? type,
		TypeLayoutInspection inspection,
		SymbolLookupResult? resolved)
	{
		var definition = resolved is not null
			? Definition(navigation, document.Id, resolved.SymbolId)
			: type is null ? null : Definition(navigation, document.Id, type.SymbolId);

		// A type declared in another document has no field entry here, so its rows carry no targets; the
		// type name still navigates to the declaration the compiler indexed.
		if (type is null)
			return inspection with { Definition = definition, Subject = LeafType(inspection.TypeDisplay) };

		var source = document.Text.ToString();
		TypeLayoutMemberInspection[] members = [.. inspection.Members.Select(member =>
			Member(snapshot, navigation, document.Id, source, inspection, Field(type, member.Name), member))];

		return inspection with { Members = members, Definition = definition, Subject = LeafType(inspection.TypeDisplay) };
	}

	/// <summary>
	/// The outline entry of the stored field named <paramref name="name"/>. The member list comes from
	/// the layout service and the field list from the outline, so the two are joined by name inside the
	/// one type both of them belong to.
	/// </summary>
	private static DocumentSymbolInfo? Field(DocumentSymbolInfo? type, string name)
	{
		if (type is null)
			return null;

		foreach (var child in type.Children)
		{
			if (child.Kind is ToolingSymbolKind.Field && string.Equals(child.Name, name, StringComparison.Ordinal))
				return child;
		}

		return null;
	}

	/// <summary>
	/// Attaches what one member row may navigate to. A field name resolves to the field's own
	/// declaration and documentation; the field's type name resolves to that type's declaration, and to
	/// a nested layout request only when the compiler can lay that type out as a type of its own.
	/// </summary>
	private static TypeLayoutMemberInspection Member(
		ProjectSnapshot snapshot,
		NavigationIndex navigation,
		DocumentId document,
		string source,
		TypeLayoutInspection inspection,
		DocumentSymbolInfo? field,
		TypeLayoutMemberInspection member)
	{
		if (field is null)
			return member;

		// The documentation comes from the compiler's own resolution of the field's name span, accepted
		// only when it resolves to this very field, so a field that shadows nothing is never described by
		// another declaration's comment.
		var documentation = navigation.Lookup(document, field.SelectionSpan.Start) is { } name
			&& name.SymbolId == field.SymbolId
			? name.Documentation
			: null;

		var typeTarget = TypeTarget(navigation, document, source, field);
		var signature = $"{member.TypeDisplay} {inspection.TypeDisplay}.{member.Name}";

		return member with
		{
			Navigation = new TypeLayoutMemberNavigation(
				signature,
				documentation,
				Definition(navigation, document, field.SymbolId),
				typeTarget?.Definition,
				Nested(navigation, snapshot, document, source, typeTarget?.Lookup)),
		};
	}

	/// <summary>
	/// The type a field's own type name resolves to, asked at the position the compiler's resolver would
	/// treat as a type reference. A field whose type is not a name the compiler can resolve - an array
	/// suffix, an unresolved generic argument - simply contributes no type target, exactly as F12 on
	/// that same source position contributes nothing.
	/// </summary>
	private static (SymbolLookupResult Lookup, SymbolDefinition Definition)? TypeTarget(
		NavigationIndex navigation,
		DocumentId document,
		string source,
		DocumentSymbolInfo symbol)
	{
		var declaration = navigation.Declaration(symbol.SymbolId);
		var typeText = FieldTypeText(declaration);
		if (typeText is null)
			return null;

		var leaf = LeafType(typeText);
		if (leaf.Length == 0)
			return null;

		if (navigation.Lookup(document, NameSpan(source, declaration.Span, leaf).Start) is not { } resolved)
			return null;

		if (!string.Equals(resolved.Name, leaf, StringComparison.Ordinal))
			return null;

		return Definition(navigation, document, resolved.SymbolId) is { } definition
			? (resolved, definition)
			: null;
	}

	/// <summary>
	/// Where the layout of a member's own type can be requested, or null when the member's type is not an
	/// aggregate the compiler can lay out as a type of its own. The test is the same concreteness test
	/// the layout lens uses, so a generic template never offers a nested layout it cannot describe.
	/// </summary>
	private static SymbolDefinition? Nested(
		NavigationIndex navigation,
		ProjectSnapshot snapshot,
		DocumentId document,
		string source,
		SymbolLookupResult? type)
	{
		if (type is null || type.Kind is not (ToolingSymbolKind.Struct or ToolingSymbolKind.Union))
			return null;

		var context = snapshot.GetAnalysis().BinderContext;
		if (context is null)
			return null;

		// A type declared in this project is laid out only when it is not a generic template; a type
		// that comes from package metadata has no template parameters to reject.
		if (navigation.Declaration(type.SymbolId) is { } declaration
			&& declaration is not (StructDeclarationSyntax { GenericParameters.Count: 0 } or UnionDeclarationSyntax { GenericParameters.Count: 0 }))
		{
			return null;
		}

		TypeSymbol? resolved;
		lock (context)
		{
			resolved = context.ResolveType(type.Name);
		}

		if (resolved is null || context.LayoutService.Inspect(resolved) is null)
			return null;

		return Definition(navigation, document, type.SymbolId);
	}

	/// <summary>
	/// The source declaration of a symbol, preferring the document the request came from so a viewer
	/// links to the declaration the reader is looking at.
	/// </summary>
	private static SymbolDefinition? Definition(NavigationIndex navigation, DocumentId document, SymbolId symbol)
	{
		SymbolDefinition? fallback = null;
		foreach (var definition in navigation.Definitions(symbol))
		{
			if (definition.DocumentId == document)
				return definition;

			fallback ??= definition;
		}

		return fallback;
	}

	/// <summary>
	/// The declared type text of a stored field, or null when the declaration is not a field at all.
	/// </summary>
	private static string? FieldTypeText(SyntaxNode? declaration) => declaration switch
	{
		StructFieldSyntax field => field.Type,
		UnionFieldSyntax field => field.Type,
		_ => null,
	};

	/// <summary>
	/// The type leaf of a type reference as the compiler's own resolver reads it: the last segment
	/// before any generic arguments, which is the name a type reference contributes to navigation.
	/// </summary>
	internal static string LeafType(string typeName)
	{
		var text = typeName.Trim();
		if (text.StartsWith("refvar ", StringComparison.Ordinal))
			text = text[7..];
		else if (text.StartsWith("ref ", StringComparison.Ordinal))
			text = text[4..];

		var generic = text.IndexOf('<');
		if (generic >= 0)
			text = text[..generic];

		var dot = text.LastIndexOf('.');
		return dot >= 0 ? text[(dot + 1)..] : text;
	}

	/// <summary>
	/// The span of the first whole-word occurrence of <paramref name="name"/> inside a declaration,
	/// matching the rule the compiler's own symbol resolver uses to decide which name a source position
	/// names. An occurrence that is only part of a longer identifier does not name anything.
	/// </summary>
	private static TextSpan NameSpan(string source, CoreTextSpan range, string name)
	{
		var start = Math.Clamp(range.Start, 0, source.Length);
		var end = Math.Clamp(range.End, start, source.Length);
		if (end <= start || name.Length == 0)
			return new TextSpan(start, 0);

		var limit = Math.Min(end, source.Length);
		for (var index = source.IndexOf(name, start, StringComparison.Ordinal);
			index >= start && index + name.Length <= limit;
			index = source.IndexOf(name, index + 1, StringComparison.Ordinal))
		{
			var before = index > 0 ? source[index - 1] : ' ';
			var after = index + name.Length < source.Length ? source[index + name.Length] : ' ';
			if (!char.IsLetterOrDigit(before) && before != '_' && !char.IsLetterOrDigit(after) && after != '_')
				return new TextSpan(index, name.Length);
		}

		return new TextSpan(start, Math.Min(name.Length, end - start));
	}
}
