using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Expressions;

namespace Cvolo.Analysis.Passes;

/// <summary>
/// Detects constructs that only make sense under an unsafe context. Grows as the
/// unbound/unsafe tier lands; today heap allocations, pointer dereference, address-of,
/// and unsafe casts are the raw-memory operations.
/// </summary>
internal static class UnsafeOperationScanner
{
	private static readonly HashSet<SyntaxKind> UnsafeKinds =
	[
		SyntaxKind.HeapAllocationExpression,
		SyntaxKind.HeapArrayAllocationExpression
	];

	public static bool ContainsUnsafeOperations(SyntaxNode root)
	{
		foreach (var child in root.GetChildren())
		{
			if (UnsafeKinds.Contains(child.Kind))
				return true;

			// Dereference *ptr and address-of &expr are modeled as UnaryExpression with string operators
			if (child is UnaryExpressionSyntax unary && unary.Operator is "*" or "&")
				return true;

			// Cast to pointer type: UnaryExpression with operator like "(int*)"
			if (child is UnaryExpressionSyntax cast && cast.Operator.StartsWith("(") && cast.Operator.EndsWith("*)"))
				return true;

			// The null literal is unsafe-only: in safe/unbound code it is a compile error
			// (CVL1104), so a body that uses it genuinely requires an unsafe context.
			if (child is NullLiteralExpressionSyntax)
				return true;

			if (ContainsUnsafeOperations(child))
				return true;
		}

		return false;
	}
}
