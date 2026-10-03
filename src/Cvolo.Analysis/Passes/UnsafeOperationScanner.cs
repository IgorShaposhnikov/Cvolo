using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Expressions;

namespace Cvolo.Analysis.Passes;

/// <summary>
/// Detects constructs that only make sense under an unsafe context. This is an implementation aid
/// for syntax-level unsafe operations whose semantic validation site does not need to inspect a
/// fully-bound type; semantic unsafe operations (native delegate/ABI calls, raw-pointer indexing)
/// are recorded by validation through the canonical unsafe-usage registry. It deliberately does not
/// descend into nested callable bodies: usage inside a lambda belongs to that nested body, not to
/// the syntactically enclosing function.
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
			// A nested callable body owns its own unsafe usage; do not attribute it to the outer body.
			if (child is LambdaExpressionSyntax)
				continue;

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

			// Inline assembly is only allowed inside unsafe contexts (CVL1600).
			if (child is AsmExpressionSyntax)
				return true;

			if (ContainsUnsafeOperations(child))
				return true;
		}

		return false;
	}
}
