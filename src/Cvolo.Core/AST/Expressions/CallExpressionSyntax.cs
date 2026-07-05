using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Core.AST.Expressions;

public sealed class CallExpressionSyntax(
	TextSpan span,
	string functionName,
	IReadOnlyList<ExpressionSyntax> arguments) : ExpressionSyntax(span)
{
	public override SyntaxKind Kind => SyntaxKind.CallExpression;

	public string FunctionName { get; } = functionName;
	public IReadOnlyList<ExpressionSyntax> Arguments { get; } = arguments;

	public override IEnumerable<SyntaxNode> GetChildren() => Arguments;
}
