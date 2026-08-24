using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Syntax;

/// <summary>
/// Contract for a syntax parser backend: turns source text into a Core AST.
/// Implementations must be single-use per source: create one instance per Parse call.
/// </summary>
public interface ISyntaxParser
{
	DiagnosticBag Diagnostics { get; }

	/// <summary>
	/// Parses <paramref name="context"/>.Source into a CompilationUnitSyntax.
	/// Returns null when parsing produced diagnostics; all diagnostics are available via <see cref="Diagnostics"/>.
	/// Spans are absolute character offsets into context.Source.
	/// </summary>
	CompilationUnitSyntax? Parse(CompilationContext context);
}
