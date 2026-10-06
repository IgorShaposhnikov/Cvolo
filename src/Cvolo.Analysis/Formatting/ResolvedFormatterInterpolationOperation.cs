using Cvolo.Core.AST.Base;

namespace Cvolo.Analysis.Formatting;

/// <summary>
/// One parsed segment of an interpolated string literal.
/// </summary>
public sealed record FormatterInterpolationSegment(
	bool IsExpression,
	string Text,
	ExpressionSyntax? Expression,
	ResolvedFormattingOperation? Operation);

/// <summary>
/// The already-bound formatter-directed interpolation for a <c>formatter.Write($"...")</c> call,
/// keyed in <see cref="BindingContext.ResolvedFormatterInterpolations"/>. Literal segments are
/// written straight to the target formatter's sink and expression segments re-use the exact
/// canonical operations bound for direct formatting, so the interpolation is never materialized as
/// an intermediate string.
/// </summary>
public sealed record ResolvedFormatterInterpolationOperation
{
	/// <summary>
	/// Left-to-right segments; each expression is evaluated exactly once by the emitter.
	/// </summary>
	public required IReadOnlyList<FormatterInterpolationSegment> Segments { get; init; }
}
