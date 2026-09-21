using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Collections;
using Cvolo.Analysis.Symbols.Structs;

namespace Cvolo.Analysis.Symbols;

/// <summary>
/// Static helpers for the ''Safe Delegates &amp; Borrowed Closures'' type rules.
/// </summary>
public static class DelegateTypeHelpers
{
	/// <summary>
	/// §3.2 provenance-independent return rule. A delegate return type may not bear
	/// reference/refvar provenance, slice provenance (regardless of element), safe/native
	/// delegate provenance, or carry them transitively through value aggregates.
	/// Raw pointers (T*) are not provenance-bearing and are permitted.
	/// A generic parameter is undecidable at template level and is decided
	/// per-instantiation.
	/// </summary>
	public static bool IsProvenanceIndependentReturn(TypeSymbol type) => type switch
	{
		PointerTypeSymbol => false,
		SliceTypeSymbol => false,
		DelegateTypeSymbol => false,
		TypeParameterSymbol => true,
		StructTypeSymbol s => s.Fields.All(f => IsProvenanceIndependentReturn(f.Type)),
		UnionTypeSymbol u => u.Fields.All(f => IsProvenanceIndependentReturn(f.Type)),
		ArrayTypeSymbol a => IsProvenanceIndependentReturn(a.ElementType),
		_ => true,
	};

	/// <summary>
	/// §8.9 ContainsMutableBorrowCapability(T). True when the value itself is a mutable-borrow
	/// value (refvar T or a slice, regardless of element) or transitively contains one inside a
	/// value aggregate reachable by value. Deliberately ignores read-only 'ref T' inside aggregates.
	/// </summary>
	public static bool ContainsMutableBorrowCapability(TypeSymbol type) => type switch
	{
		PointerTypeSymbol p => p.IsMutable,
		SliceTypeSymbol => true,
		TypeParameterSymbol => true,
		StructTypeSymbol s => s.Fields.Any(f => ContainsMutableBorrowCapability(f.Type)),
		UnionTypeSymbol u => u.Fields.Any(f => ContainsMutableBorrowCapability(f.Type)),
		ArrayTypeSymbol a => ContainsMutableBorrowCapability(a.ElementType),
		DelegateTypeSymbol => false,
		_ => false,
	};

	/// <summary>
	/// §13.0.1 ContainsSafeDelegate(T). True when the value physically contains a visible safe
	/// delegate somewhere inside it, transitively through value aggregates, fixed arrays, Option
	/// payloads, and instantiated generics — but stops at indirection (raw pointers, and slice
	/// redirection when the element cannot be derived as a value aggregate).
	/// </summary>
	public static bool ContainsSafeDelegate(TypeSymbol type) => type switch
	{
		DelegateTypeSymbol => true,
		TypeParameterSymbol => false,
		StructTypeSymbol s => s.Fields.Any(f => ContainsSafeDelegate(f.Type)),
		UnionTypeSymbol u => u.Fields.Any(f => !f.IsVoidVariant && ContainsSafeDelegate(f.Type)),
		ArrayTypeSymbol a => ContainsSafeDelegate(a.ElementType),
		SliceTypeSymbol sl => ContainsSafeDelegate(sl.ElementType),
		_ => false,
	};

	/// <summary>
	/// §13.0.2 MayExposeSafeDelegate(T). Reader-side capability: true when reading a value of this
	/// type may surface a safe delegate (a reader treats any such surfaced delegate as non-escaping
	/// when obtained from parameter-origin storage). Recurse through value aggregates, ref/refvar,
	/// slices, Option payloads, and instantiated generics. Raw pointers are excluded.
	/// </summary>
	public static bool MayExposeSafeDelegate(TypeSymbol type) => type switch
	{
		DelegateTypeSymbol => true,
		TypeParameterSymbol => false,
		StructTypeSymbol s => s.Fields.Any(f => MayExposeSafeDelegate(f.Type)),
		UnionTypeSymbol u => u.Fields.Any(f => !f.IsVoidVariant && MayExposeSafeDelegate(f.Type)),
		ArrayTypeSymbol a => MayExposeSafeDelegate(a.ElementType),
		SliceTypeSymbol sl => MayExposeSafeDelegate(sl.ElementType),
		PointerTypeSymbol => false,
		_ => false,
	};

	/// <summary>
	/// §13.0.3 IsDelegateEscapingStoreDestination. A store destination rooted in a refvar parameter,
	/// a mutable slice/view parameter, an optional reference parameter, or a writable projection of
	/// any of those is an escaping destination. Store allowed only when the stored delegate's
	/// context is null/static/provenance-free.
	/// </summary>
	public static bool IsDelegateEscapingStoreDestination(TypeSymbol destinationType) => destinationType switch
	{
		PointerTypeSymbol p => p.IsMutable,
		SliceTypeSymbol => true,
		TypeParameterSymbol => true,
		StructTypeSymbol s => s.Fields.Any(f => IsDelegateEscapingStoreDestination(f.Type)),
		UnionTypeSymbol u => u.Fields.Any(f => !f.IsVoidVariant && IsDelegateEscapingStoreDestination(f.Type)),
		ArrayTypeSymbol a => IsDelegateEscapingStoreDestination(a.ElementType),
		_ => false,
	};

	/// <summary>
	/// Order of evaluation used for the escape/borrow model: a safe delegate is a plain two-word
	/// value copy (its hidden context is shared, never owned). Distinct delegate declarations are
	/// distinct nominal types and never implicitly convert.
	/// </summary>
	public static bool IsSafeDelegateDuplicateable(TypeSymbol type) => type is DelegateTypeSymbol;
}