using Cvolo.Analysis.Symbols.Base;
using Cvolo.Core.AST.Declarations;

namespace Cvolo.Analysis.Symbols.Structs;

/// <summary>
/// A structural (implicit) protocol type. Like an interface, a protocol has no
/// value representation and is never allocated; it is recognized so a
/// protocol-name annotation (e.g. a value/ref parameter) can be lowered to the
/// concrete conforming type at each call site. Unlike an interface, conformance
/// is structural: a concrete type satisfies a protocol iff it provides every
/// required member with a matching signature (duck typing), with no explicit
/// conformance declaration.
/// </summary>
public sealed class ProtocolTypeSymbol(
	string name,
	IReadOnlyList<ProtocolMethodDeclarationSyntax> members,
	IReadOnlyList<string> genericParameters,
	string? constraint,
	IReadOnlySet<string> canonicalMembers) : TypeSymbol(name)
{
	/// <summary>The required member signatures declared by this protocol.</summary>
	public IReadOnlyList<ProtocolMethodDeclarationSyntax> Members { get; } = members;

	/// <summary>The protocol's generic type parameters (e.g. ["T"] for IContainer&lt;T&gt;).</summary>
	public IReadOnlyList<string> GenericParameters { get; } = genericParameters;

	/// <summary>
	/// The optional `for ...` requires-clause contract a conforming type must
	/// itself satisfy, mapped from the protocol header (null when absent).
	/// </summary>
	public string? Constraint { get; } = constraint;

	/// <summary>
	/// Canonical structural member tokens for O(1) pre-matching: each is
	/// "{Return}:{Name}({Param1},{Param2},...)" with fully-qualified type names
	/// resolved in the protocol's namespace. Generic type parameters are
	/// normalized to positional $-placeholders ($T0, $T1, ...) so structural
	/// matching is topological and independent of local parameter naming.
	/// </summary>
	public IReadOnlySet<string> CanonicalMembers { get; } = canonicalMembers;
}
