namespace Cvolo.Core.Diagnostics;

/// <summary>
/// Stable identifiers for compiler diagnostics. Reserved prefix families
/// (modeled on the .NET CS/SYSLIB/CA convention):
///
///   CVLxxxx    - core compiler errors/warnings (syntax, semantics, memory)
///   SYSLIBxxxx - deprecations inside the standard library
///   CVLSxxxx   - Cvolo system-library diagnostics (reserved)
///   CVLAxxxx   - code analyzers / linters: quality, style, performance (reserved)
///   CVLDxxxx   - documentation and comment checks (reserved)
///   CVLXxxxx   - extensions / macros / generators (reserved)
///   CVLFxxxx   - FFI and C-ABI interop (reserved)
/// </summary>
public static class DiagnosticIds
{
	/// <summary>'[UnsafeBody]' applied to a body without any unsafe operations.</summary>
	public const string UnsafeBodyNoEffect = "CVL1001";

	/// <summary>Attribute name is not a known intrinsic; accepted and erased, but flagged for likely typos.</summary>
	public const string UnknownAttribute = "CVL1002";

	/// <summary>Large struct (&gt;16 bytes) passed by value; payload is duplicated. Consider passing by ref.</summary>
	public const string LargeCopyWarning = "CVL1003";

	/// <summary>Attribute cannot be applied in the current safety tier.</summary>
	public const string AttributeWrongTier = "CVL1004";

	/// <summary>Raw pointer T* used outside an unsafe context.</summary>
	public const string RawPointerOutsideUnsafe = "CVL1005";

	/// <summary>Dereference '*' used outside an unsafe context.</summary>
	public const string DereferenceOutsideUnsafe = "CVL1006";

	/// <summary>Address-of '&' used outside an unsafe context.</summary>
	public const string AddressOfOutsideUnsafe = "CVL1007";

	/// <summary>Reference cannot escape an unbound scope.</summary>
	public const string RefEscapesUnboundScope = "CVL1008";

	/// <summary>'unbound' modifier on a function with no ref/refvar parameters.</summary>
	public const string UnboundNoRefParams = "CVL1010";
	/// <summary>Auto-inference chose mutability for an unmarked extension method.</summary>
	public const string AutoInferMutationWarning = "CVL1011";
}
