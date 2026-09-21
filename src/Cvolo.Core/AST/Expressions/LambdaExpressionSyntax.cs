using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Statements;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Core.AST.Expressions;

public sealed class LambdaExpressionSyntax(
	TextSpan span,
	LambdaCaptureMode captureMode,
	IReadOnlyList<LambdaParameterSyntax> parameters,
	LambdaBodyKind bodyKind,
	ExpressionSyntax? expressionBody,
	BlockStatementSyntax? blockBody) : ExpressionSyntax(span)
{
	public override SyntaxKind Kind => SyntaxKind.LambdaExpression;

	public LambdaCaptureMode CaptureMode { get; } = captureMode;
	public IReadOnlyList<LambdaParameterSyntax> Parameters { get; } = parameters;
	public LambdaBodyKind BodyKind { get; } = bodyKind;
	public ExpressionSyntax? ExpressionBody { get; } = expressionBody;
	public BlockStatementSyntax? BlockBody { get; } = blockBody;

	public override IEnumerable<SyntaxNode> GetChildren()
	{
		foreach (var p in Parameters)
			yield return p;

		if (ExpressionBody is not null)
			yield return ExpressionBody;

		if (BlockBody is not null)
			yield return BlockBody;
	}
}