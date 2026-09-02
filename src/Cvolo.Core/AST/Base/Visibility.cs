namespace Cvolo.Core.AST.Base;

/// <summary>
/// The three-tier visibility model (Visibility &amp; Access Control Specification):
/// <c>private</c> = file scope, <c>internal</c> = module scope, <c>public</c> = universal ABI scope.
/// </summary>
public enum Visibility
{
	Private,
	Internal,
	Public
}