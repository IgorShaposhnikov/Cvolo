namespace Cvolo.Core.AST.Declarations;

/// <summary>
/// How an extension method declares its receiver parameter ('this').
/// Markers are parsed as the first parameter of the extension method:
/// 'refvar this' requests a mutable reference, 'ref this' a read-only one.
/// 'None' leaves mutability to body auto-inference (fallback contract).
/// </summary>
public enum ReceiverContract
{
	/// <summary>No explicit receiver marker; body auto-inference decides 'this' mutability.</summary>
	None,

	/// <summary>Read-only 'ref this' receiver.</summary>
	Ref,

	/// <summary>Mutable 'refvar this' receiver.</summary>
	Refvar
}