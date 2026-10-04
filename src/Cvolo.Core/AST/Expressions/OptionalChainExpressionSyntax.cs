using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Core.AST.Expressions;

/// <summary>
/// Optional chaining expression <c>receiver?.member</c> or <c>receiver?.Method(args)</c>.
/// </summary>
/// <remarks>
/// <para>
/// The receiver must be an <c>Option&lt;T&gt;</c>. When it is <c>Some</c> the member is projected
/// through the payload; when it is <c>None</c> the whole expression collapses to the absence value
/// (or to a no-op when the member returns <c>void</c>). The member and its arguments are resolved
/// against the payload type <c>T</c> at compile time but are only evaluated on the
/// <c>Some</c> path.
/// </para>
/// <para>
/// Result lifting adds at most one synthetic <c>Option</c> layer: a member returning <c>U</c>
/// yields <c>Option&lt;U&gt;</c>, a member already returning <c>Option&lt;U&gt;</c> yields
/// <c>Option&lt;U&gt;</c> unchanged, and a member returning <c>void</c> yields <c>void</c>.
/// </para>
/// <para>
/// <see cref="Arguments"/> is <see langword="null"/> for field access and a (possibly empty) list for
/// a method or delegate invocation. The node is deliberately not an lvalue: conditional writeback
/// (<c>x?.field = v</c>) is rejected.
/// </para>
/// </remarks>
public sealed class OptionalChainExpressionSyntax(
	TextSpan span,
	ExpressionSyntax receiver,
	string memberName,
	IReadOnlyList<ExpressionSyntax>? arguments) : ExpressionSyntax(span)
{
	public override SyntaxKind Kind => SyntaxKind.OptionalChainExpression;
	public ExpressionSyntax Receiver { get; } = receiver;
	public string MemberName { get; } = memberName;
	public IReadOnlyList<ExpressionSyntax>? Arguments { get; } = arguments;
	public bool IsInvocation => Arguments is not null;

	public override IEnumerable<SyntaxNode> GetChildren()
	{
		yield return Receiver;
		if (Arguments is not null)
		{
			foreach (var argument in Arguments)
				yield return argument;
		}
	}
}
