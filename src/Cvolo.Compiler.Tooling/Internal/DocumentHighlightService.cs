using Cvolo.Analysis;
using Cvolo.Analysis.Completion;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using Cvolo.Core.AST.Expressions;

namespace Cvolo.Compiler.Tooling.Internal;

/// <summary>
/// Computes the semantic document highlights for one position: every occurrence in the same
/// document that binds to the same declaration, each classified as a read, a write, both, or a
/// declaration.
/// </summary>
/// <remarks>
/// There is deliberately no textual same-spelling fallback. A name that the compiler does not bind
/// at the requested position yields no highlights at all, because a highlight the compiler cannot
/// vouch for is worse than none. The classification is a best effort from the resolved syntax: an
/// occurrence the compiler models but the editor cannot place confidently is reported as a plain
/// declaration highlight, which the editor renders without a read/write colour.
/// </remarks>
internal static class DocumentHighlightService
{
	internal static IReadOnlyList<ToolingDocumentHighlight> GetDocumentHighlights(ProjectSnapshot snapshot, DocumentSnapshot document, int position)
	{
		var analysis = snapshot.GetAnalysis();
		if (analysis.BinderContext is null
			|| !analysis.UnitsByDocument.TryGetValue(document.Id, out var unit)
			|| unit is null)
		{
			return [];
		}

		var context = analysis.BinderContext;
		SyntaxNode? declaration;

		lock (context)
		{
			declaration = ResolveDeclaration(snapshot, context, document.Id, unit, position);
		}

		if (declaration is null)
			return [];

		var occurrences = snapshot.GetOccurrenceIndex().OccurrencesOfDeclaration(declaration, document.Id);
		if (occurrences.Count == 0)
			return [];

		var parents = ParentMap(unit);
		var result = new List<ToolingDocumentHighlight>(occurrences.Count);

		foreach (var occurrence in occurrences)
		{
			result.Add(new ToolingDocumentHighlight(
				occurrence.Span,
				occurrence.IsDeclaration
					? ReferenceAccessKind.Declaration
					: Classify(parents, occurrence.Span)));
		}

		return result;
	}

	private static SyntaxNode? ResolveDeclaration(
		ProjectSnapshot snapshot,
		BindingContext context,
		DocumentId documentId,
		CompilationUnitSyntax unit,
		int position)
	{
		// A navigable symbol comes first so that a function name shared with a local still resolves to
		// the declaration the navigation index agrees with.
		if (snapshot.GetNavigationIndex().Lookup(documentId, position) is { } resolved)
			return snapshot.GetNavigationIndex().Declaration(resolved.SymbolId);

		return CompletionQuery.ResolveSymbol(context, unit, position)?.Declaration;
	}

	/// <summary>
	/// The access kind of a non-declaration occurrence, decided by the node that holds the occurrence
	/// and the expression it sits in. A plain assignment target is a write; a compound assignment or
	/// an increment reads before it writes, so it is both; anything else is a read.
	/// </summary>
	private static ReferenceAccessKind Classify(Dictionary<SyntaxNode, SyntaxNode?> parents, TextSpan span)
	{
		SyntaxNode? innermost = null;
		foreach (var (node, _) in parents)
		{
			if (node.Span.Length <= 0 || node.Span.Start > span.Start || node.Span.End < span.Start + span.Length)
				continue;

			if (innermost is null || node.Span.Length < innermost.Span.Length)
				innermost = node;
		}

		if (innermost is null || !parents.TryGetValue(innermost, out var parent) || parent is null)
			return ReferenceAccessKind.Declaration;

		switch (parent)
		{
			case BinaryExpressionSyntax binary when ReferenceEquals(binary.Left, innermost):
				return binary.Operator switch
				{
					"=" => ReferenceAccessKind.Write,
					"" => ReferenceAccessKind.Read,
					_ => ReferenceAccessKind.ReadWrite,
				};
			case UnaryExpressionSyntax { Operator: "++_postfix" or "--_postfix" or "++_prefix" or "--_prefix" } unary when ReferenceEquals(unary.Operand, innermost):
				return ReferenceAccessKind.ReadWrite;
			case MemberAccessExpressionSyntax or IdentifierExpressionSyntax or CallExpressionSyntax:
				return ReferenceAccessKind.Read;
			case StructFieldSyntax or UnionFieldSyntax or EnumVariantDeclarationSyntax or ParameterSyntax or VariableDeclarationSyntax:
				// A declared name in a position the expression forms do not model: report it without a
				// read/write colour rather than claiming an access the compiler never described.
				return ReferenceAccessKind.Declaration;
			default:
				return ReferenceAccessKind.Read;
		}
	}

	private static Dictionary<SyntaxNode, SyntaxNode?> ParentMap(SyntaxNode root)
	{
		var map = new Dictionary<SyntaxNode, SyntaxNode?>(ReferenceEqualityComparer.Instance);
		Collect(root, null, map);
		return map;
	}

	private static void Collect(SyntaxNode node, SyntaxNode? parent, Dictionary<SyntaxNode, SyntaxNode?> map)
	{
		map[node] = parent;

		foreach (var child in node.GetChildren())
			Collect(child, node, map);
	}
}
