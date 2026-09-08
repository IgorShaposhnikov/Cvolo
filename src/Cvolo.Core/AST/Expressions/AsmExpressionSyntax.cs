using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Core.AST.Expressions;

public sealed class AsmExpressionSyntax(
	TextSpan span,
	string template,
	List<AsmOperandSyntax> operands,
	List<string> clobbers,
	AsmOptions options,
	string? resultType) : ExpressionSyntax(span)
{
	public override SyntaxKind Kind => SyntaxKind.AsmExpression;

	public string Template { get; } = template;

	public List<AsmOperandSyntax> Operands { get; } = operands;

	public List<string> Clobbers { get; } = clobbers;

	public AsmOptions Options { get; } = options;

	public string? ResultType { get; } = resultType;

	public override IEnumerable<SyntaxNode> GetChildren()
	{
		foreach (var operand in Operands)
		{
			yield return operand;
		}
	}
}
