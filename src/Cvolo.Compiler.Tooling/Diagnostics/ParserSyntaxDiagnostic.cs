using CoreDiagnosticIds = Cvolo.Core.Diagnostics.DiagnosticIds;

namespace Cvolo.Compiler.Tooling;

/// <summary>
/// Identifies the parser's syntax diagnostics so hosts can tell a document that did not parse from
/// one that parsed and failed to type-check.
/// </summary>
/// <remarks>
/// The ids come from the compiler's catalog rather than from literals here, so this predicate can
/// never drift from <c>DiagnosticIds</c>. <c>AntlrSyntaxParser</c> is the only producer of these
/// three ids, which is what makes the set closed.
/// </remarks>
public static class ParserSyntaxDiagnostic
{
	/// <summary>
	/// True when <paramref name="diagnosticId"/> is a parser syntax error, meaning the document's
	/// declarations are incomplete or absent rather than merely wrong.
	/// </summary>
	public static bool IsSyntaxError(string diagnosticId) =>
		diagnosticId == CoreDiagnosticIds.UnexpectedToken
		|| diagnosticId == CoreDiagnosticIds.ExpectedToken
		|| diagnosticId == CoreDiagnosticIds.UnexpectedEndOfFile;
}