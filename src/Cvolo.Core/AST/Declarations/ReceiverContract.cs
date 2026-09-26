namespace Cvolo.Core.AST.Declarations;

/// <summary>
/// How an extension method declares its receiver parameter ('this').
/// Markers are parsed as the first parameter of the extension method:
/// 'refvar this' requests a mutable reference, 'ref this' a read-only one.
/// 'None' leaves mutability to body auto-inference (fallback contract).
/// </summary>
public enum ReceiverContract
{
	/// <summary>
	/// No explicit receiver marker; body auto-inference decides 'this' mutability.
	/// </summary>
	None,

	/// <summary>
	/// Read-only 'ref this' receiver.</summary>
	Ref,

	/// <summary>
	/// Mutable 'refvar this' receiver.
	/// </summary>
	Refvar
}

/// <summary>
/// How a function declaration binds to a receiver.
/// </summary>
/// <remarks>
/// This is deliberately separate from <see cref="ReceiverContract"/>: that type only describes
/// the mutability of an instance receiver that is actually present, while this type records
/// whether a receiver exists at all. An associated function declared as '.Name' has no receiver
/// and therefore can never carry a <see cref="ReceiverContract"/>.
/// </remarks>
public enum FunctionBindingKind
{
	/// <summary>
	/// A free function, or an extension member written without a leading dot. Extension members in
	/// this form are instance extension methods and receive a synthetic 'this' parameter.
	/// </summary>
	Default,
	/// <summary>
	/// An associated function written with a leading dot ('.Name'). Called through the owner type
	/// name (<c>Type.Name(...)</c>), has no synthetic 'this', and has no implicit instance scope.
	/// </summary>
	Associated
}
