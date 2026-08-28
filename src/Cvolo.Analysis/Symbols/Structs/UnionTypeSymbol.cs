using Cvolo.Analysis.Symbols.Base;

namespace Cvolo.Analysis.Symbols.Structs;

public sealed class UnionTypeSymbol(string name, IReadOnlyList<UnionFieldSymbol> fields) : TypeSymbol(name)
{
	public IReadOnlyList<UnionFieldSymbol> Fields { get; } = fields;

	public UnionFieldSymbol? FindField(string name) => Fields.FirstOrDefault(f => f.Name == name);

	/// <summary>
	/// True when this union has the Option shape: exactly one empty (void) variant and
	/// exactly one payload variant. This is the structural stand-in for the stdlib
	/// <c>Option&lt;T&gt;</c> (replaces the old name-based <c>Contains("Option")</c> matching).
	/// </summary>
	public bool IsOption
	{
		get
		{
			var payload = PayloadVariant;
			var none = NoneVariant;
			return payload is not null && none is not null;
		}
	}

	/// <summary>
	/// The non-void payload variant (e.g. <c>Some</c>) when the union has the Option shape.
	/// </summary>
	public UnionFieldSymbol? PayloadVariant
	{
		get
		{
			var nonVoid = Fields.Where(f => !f.IsVoidVariant).ToList();
			if (nonVoid.Count != 1) return null;
			return Fields.Count(f => f.IsVoidVariant) == 1 ? nonVoid[0] : null;
		}
	}

	/// <summary>
	/// The void (empty) variant (e.g. <c>None</c>) when the union has the Option shape.
	/// </summary>
	public UnionFieldSymbol? NoneVariant
	{
		get
		{
			var voidVariants = Fields.Where(f => f.IsVoidVariant).ToList();
			if (voidVariants.Count != 1) return null;
			return Fields.Count(f => !f.IsVoidVariant) == 1 ? voidVariants[0] : null;
		}
	}

	/// <summary>
	/// True when this union qualifies for Null-Pointer Optimization (NPO): an Option whose
	/// payload variant is a reference (<c>ref</c>/<c>refvar</c>, i.e. <c>PointerTypeSymbol</c>).
	/// Such options compile to a single flat 8-byte pointer (Some = non-zero, None = zero).
	/// </summary>
	public bool IsNpoEligible
	{
		get
		{
			var payload = PayloadVariant;
			return payload is not null && payload.Type is PointerTypeSymbol;
		}
	}
}
