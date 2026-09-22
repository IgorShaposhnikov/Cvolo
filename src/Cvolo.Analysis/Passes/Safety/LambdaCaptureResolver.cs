using Cvolo.Analysis.Symbols;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Expressions;

namespace Cvolo.Analysis.Passes.Safety;

/// <summary>
/// Computes lambda capture sets from the existing syntax shape so capture policy and delegate
/// provenance share the same capture-discovery rules without introducing another traversal strategy.
/// </summary>
internal sealed class LambdaCaptureResolver
{
	/// <summary>
	/// Computes the set of by-value outer locals and parameters referenced by a lambda body.
	/// References to globals or to identities shadowed inside the body are not captures.
	/// </summary>
	public HashSet<string> ComputeCapturedNames(LambdaExpressionSyntax lambda, SymbolTable scope)
	{
		var captured = new HashSet<string>();
		var body = lambda.BlockBody is not null ? (SyntaxNode)lambda.BlockBody : lambda.ExpressionBody!;
		if (body is null)
			return captured;

		foreach (var id in EnumerateNodes<IdentifierExpressionSyntax>(body))
		{
			if (captured.Contains(id.Name))
				continue;
			if (scope.Lookup(id.Name) is VariableSymbol v && !v.IsGlobal)
				captured.Add(id.Name);
		}

		return captured;
	}

	/// <summary>Enumerates syntax nodes of the requested type using the existing recursive shape.</summary>
	private static IEnumerable<TSyntax> EnumerateNodes<TSyntax>(SyntaxNode root) where TSyntax : SyntaxNode
	{
		if (root is TSyntax match)
			yield return match;
		foreach (var child in root.GetChildren())
		{
			foreach (var nested in EnumerateNodes<TSyntax>(child))
			{
				yield return nested;
			}
		}
	}
}
