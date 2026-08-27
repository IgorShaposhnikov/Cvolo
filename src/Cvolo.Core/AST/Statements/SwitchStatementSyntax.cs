using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Core.AST.Statements;

public sealed class SwitchCaseSyntax(TextSpan span, string variantName, string? variableName, bool isDefault, IReadOnlyList<SyntaxNode> body) : SyntaxNode(span)
{
	public override SyntaxKind Kind => SyntaxKind.SwitchCase;

	public string VariantName { get; } = variantName;
	public string? VariableName { get; } = variableName;
	public bool IsDefault { get; } = isDefault;
	public IReadOnlyList<SyntaxNode> Body { get; } = body;

	public override IEnumerable<SyntaxNode> GetChildren() => Body;
}

public sealed class SwitchStatementSyntax(TextSpan span, ExpressionSyntax expression, IReadOnlyList<SwitchCaseSyntax> cases) : SyntaxNode(span)
{
	public override SyntaxKind Kind => SyntaxKind.SwitchStatement;

	public ExpressionSyntax Expression { get; } = expression;
	public IReadOnlyList<SwitchCaseSyntax> Cases { get; } = cases;

	public override IEnumerable<SyntaxNode> GetChildren() => [Expression, .. Cases];
}
