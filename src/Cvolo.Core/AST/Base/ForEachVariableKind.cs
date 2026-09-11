namespace Cvolo.Core.AST.Base;

/// <summary>
/// The binding kind of a <c>foreach</c> loop variable, per the iteration Binding Matrix
/// (see 'Inc - foreach Loop (Hardened Structural Iteration)' §2.A).
/// </summary>
public enum ForEachVariableKind
{
	/// <summary>Read-only binding: a value copy when 'Current' returns by value, or an
	/// immutable reference binding when 'Current' returns <c>ref T</c>/<c>refvar T</c>.
	/// Also used for an explicit item type (non-refvar) form.</summary>
	Val,

	/// <summary>A detached mutable local copy of the current element; never propagates
	/// back to the collection.</summary>
	Var,

	/// <summary>A mutable reference binding pointing directly into the collection's
	/// backing memory. Requires 'Current' to return <c>refvar T</c> (or an array/slice
	/// element slot); cannot be combined with an explicit item type.</summary>
	RefVar,
}
