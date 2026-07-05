using Cvolo.Core.Diagnostics;

namespace Cvolo.Core.AST.Base;

public abstract class ExpressionSyntax(TextSpan span) : SyntaxNode(span)
{
}
