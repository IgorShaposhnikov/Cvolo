using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Expressions;

namespace Cvolo.Analysis.Ownership;

/// <summary>
/// Builds canonical <see cref="MovePath"/> identities from l-value expressions. Only
/// statically stable roots, field projections, and constant fixed-index projections produce a
/// path; dynamic indices, raw-pointer dereferences, and call results have no stable identity and
/// yield <c>null</c> (they are handled through candidate regions or the raw-pointer boundary).
/// </summary>
public static class MovePathBuilder
{
	/// <summary>
	/// Attempts to build the statically stable move path designated by an expression, or
	/// <c>null</c> when the expression has no tracked ownership identity.
	/// </summary>
	public static MovePath? TryBuild(ExpressionSyntax expression)
	{
		switch (expression)
		{
			case IdentifierExpressionSyntax identifier:
				return new MovePath(identifier.Name);

			case BorrowExpressionSyntax borrow:
				return TryBuild(borrow.Expression);

			case MemberAccessExpressionSyntax member:
				return TryBuild(member.Expression)?.Append(ProjectionStep.Field(member.MemberName));

			case IndexExpressionSyntax index:
				var basePath = TryBuild(index.Left);
				if (basePath is null)
					return null;
				return TryGetConstantIndex(index.Index, out var constantIndex)
					? basePath.Append(ProjectionStep.FixedIndex(constantIndex))
					: null;

			default:
				return null;
		}
	}

	/// <summary>
	/// Extracts a compile-time constant fixed index from an integer literal or a negated integer
	/// literal. Other expression forms are considered non-constant (dynamic).
	/// </summary>
	public static bool TryGetConstantIndex(ExpressionSyntax expression, out int value)
	{
		switch (expression)
		{
			case IntegerLiteralExpressionSyntax literal when literal.Value <= int.MaxValue:
				value = (int)literal.Value;
				return true;

			case UnaryExpressionSyntax { Operator: "-" } negation
				when negation.Operand is IntegerLiteralExpressionSyntax operand
					&& operand.Value <= (ulong)int.MaxValue + 1:
				value = unchecked(-(int)operand.Value);
				return true;

			default:
				value = 0;
				return false;
		}
	}
}
