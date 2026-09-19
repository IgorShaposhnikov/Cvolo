using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Core.AST.Expressions;

public sealed class CallExpressionSyntax(
	TextSpan span,
	string functionName,
	IReadOnlyList<string> typeArguments,
	IReadOnlyList<ExpressionSyntax> arguments,
	TextSpan? argumentListSpan = null) : ExpressionSyntax(span)
{
	public override SyntaxKind Kind => SyntaxKind.CallExpression;

	public string FunctionName { get; } = functionName;
	public IReadOnlyList<string> TypeArguments { get; } = typeArguments;
	public IReadOnlyList<ExpressionSyntax> Arguments { get; } = arguments;

	/// <summary>
	/// Span of the call's argument list (the parentheses and everything between them), for
	/// diagnostics that should highlight the arguments rather than the whole call. Falls back to
	/// the whole call span.
	/// </summary>
	public TextSpan ArgumentListSpan { get; } = argumentListSpan ?? span;

	public override IEnumerable<SyntaxNode> GetChildren() => Arguments;
}
