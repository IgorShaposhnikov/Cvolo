using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Collections;
using Cvolo.Analysis.Symbols.Structs;

namespace Cvolo.Compiler.Tooling.Internal;

/// <summary>
/// Resolves the source declaration that defines the semantic type of the value or type named at a
/// position - the target of Go to Type Definition (§2-5).
/// <para>
/// Ordinary go-to-definition answers "where was this symbol declared". This answers the different
/// question "what semantic type does this value have": a local <c>layout</c> still navigates to its own
/// declaration with F12, while Go to Type Definition sends the reader to <c>struct Layout</c>. The type
/// is the compiler's own resolved type of the position, mapped back to the declaration it was named
/// after; when the compiler has no such type - a primitive, an unresolved name, or a value whose type
/// is not named by any source declaration - there is no target rather than a guessed one (§5, §54).
/// </para>
/// </summary>
internal static class TypeNavigation
{
	/// <summary>
	/// The declarations that define the semantic type of the position. A type declaration or type use
	/// answers with the type's own declaration; a value answers with the declaration of its resolved
	/// type; anything else answers with nothing.
	/// </summary>
	internal static IReadOnlyList<SymbolDefinition> GetTypeDefinitions(
		ProjectSnapshot snapshot,
		DocumentSnapshot document,
		int position)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		ArgumentNullException.ThrowIfNull(document);
		ArgumentOutOfRangeException.ThrowIfNegative(position);
		ArgumentOutOfRangeException.ThrowIfGreaterThan(position, document.Text.Length);

		var navigation = snapshot.GetNavigationIndex();
		if (navigation.Lookup(document.Id, position) is not { } resolved)
			return [];

		// A type declaration or a type use already names the type, so its own definitions are the
		// answer and no separate type lookup is needed.
		if (IsTypeKind(resolved.Kind))
			return navigation.Definitions(resolved.SymbolId);

		// Otherwise the position names a value - a local, parameter, field or global - and the
		// compiler-resolved type of that value is exactly what the reader is asking for.
		if (navigation.TypeAt(document.Id, position) is not { } type)
			return [];

		type = Unwrap(type);
		var leaf = TypeLayoutView.LeafType(type.Name);
		if (leaf.Length == 0)
			return [];

		return navigation.FindTypeSymbol(leaf, document.Id) is { } typeSymbol
			? navigation.Definitions(typeSymbol)
			: [];
	}

	private static bool IsTypeKind(ToolingSymbolKind kind) => kind is
		ToolingSymbolKind.Struct or ToolingSymbolKind.Union or ToolingSymbolKind.Enum
		or ToolingSymbolKind.Interface or ToolingSymbolKind.Protocol or ToolingSymbolKind.Delegate
		or ToolingSymbolKind.TypeAlias or ToolingSymbolKind.OtherType;

	/// <summary>
	/// The named type a value's type refers to through a wrapper: a <c>ref</c>/<c>refvar</c> reference,
	/// a raw pointer, or a fixed array or slice names the type it points at or holds. An aggregate is
	/// its own answer.
	/// </summary>
	private static TypeSymbol Unwrap(TypeSymbol type)
	{
		while (true)
		{
			switch (type)
			{
				case PointerTypeSymbol pointer:
					type = pointer.ReferencedType;
					continue;
				case RawPointerTypeSymbol rawPointer:
					type = rawPointer.ElementType;
					continue;
				case ArrayTypeSymbol array:
					type = array.ElementType;
					continue;
				case SliceTypeSymbol slice:
					type = slice.ElementType;
					continue;
				default:
					return type;
			}
		}
	}
}
