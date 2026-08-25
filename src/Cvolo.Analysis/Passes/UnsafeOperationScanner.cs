using Cvolo.Core.AST.Base;

namespace Cvolo.Analysis.Passes;

/// <summary>
/// Detects constructs that only make sense under an unsafe context. Grows as the
/// unbound/unsafe tier lands; today heap allocations are the raw-memory operations.
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
			if (UnsafeKinds.Contains(child.Kind) || ContainsUnsafeOperations(child))
				return true;
		}

		return false;
	}
}
