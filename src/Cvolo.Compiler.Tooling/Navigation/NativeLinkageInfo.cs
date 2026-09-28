namespace Cvolo.Compiler.Tooling;

/// <summary>
/// The direction of a native linkage fact resolved by the compiler.
/// </summary>
public enum NativeLinkageDirection
{
	/// <summary>
	/// The symbol has no resolved native linkage.
	/// </summary>
	None,
	/// <summary>
	/// The symbol is imported from a foreign library.
	/// </summary>
	Import,
	/// <summary>
	/// The symbol is exported to foreign code.
	/// </summary>
	Export,
}

/// <summary>
/// Already-resolved native linkage identity for one declaration. Every value here is taken from
/// compiler binding; nothing is inferred from call-site syntax, and no ABI register assignment is
/// represented.
/// </summary>
public sealed record NativeLinkageInfo(
	NativeLinkageDirection Direction,
	string? Library = null,
	string? CallingConvention = null,
	string? ExternalName = null)
{
	/// <summary>
	/// True when the compiler resolved a direction for this symbol.
	/// </summary>
	public bool HasLinkage => Direction != NativeLinkageDirection.None;
}
