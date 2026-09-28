using Antlr4.Runtime;
using Cvolo.Core.AST.Base;
using Cvolo.Syntax.Antlr;

namespace Cvolo.Compiler.Tooling.Internal;

/// <summary>
/// Computes smart-selection chains from the parsed syntax tree. A chain starts at the token under
/// the caret and grows through every enclosing node that adds a distinct region, so a client can
/// step from an identifier to its type argument, call, receiver, block, declaration and unit.
/// </summary>
internal static class SelectionRangeService
{
	/// <summary>
	/// Returns one chain per requested position, in request order. A position outside the document
	/// or with no meaningful enclosing region yields null at that index.
	/// </summary>
	public static IReadOnlyList<ToolingSelectionRange?> GetSelectionRanges(ProjectSnapshot snapshot, DocumentSnapshot document, IReadOnlyList<int> positions)
	{
		ArgumentNullException.ThrowIfNull(snapshot);
		ArgumentNullException.ThrowIfNull(document);
		ArgumentNullException.ThrowIfNull(positions);

		var length = document.Text.Length;
		var tokens = SnapshotTokens(document.Text.ToString());
		SyntaxNode? root = null;

		if (snapshot.TryGetParsedUnit(document.Id, out var unit))
			root = unit;

		var chains = new ToolingSelectionRange?[positions.Count];

		for (var i = 0; i < positions.Count; i++)
		{
			var position = positions[i];

			if (position < 0 || position > length)
				continue;

			chains[i] = BuildChain(root, tokens, length, position);
		}

		return chains;
	}

	private static ToolingSelectionRange? BuildChain(SyntaxNode? root, IReadOnlyList<IToken> tokens, int length, int position)
	{
		// The walk descends from the unit, so the regions it produces run outermost first; they are
		// reversed before the token under the caret is put in front of them.
		List<TextSpan> chain = [];

		for (var node = root; node is not null;)
		{
			var span = Clamp(node.Span, length);

			if (!Contains(span, position))
				break;

			chain.Add(span);
			node = InnermostChild(node, span, position);
		}

		chain.Reverse();

		if (FindTokenAt(tokens, position) is { } token)
			chain.Insert(0, new TextSpan(token.StartIndex, (token.StopIndex - token.StartIndex) + 1));

		return chain.Count == 0 ? null : ToolingSelectionRange.CreateChain(chain);
	}

	/// <summary>
	/// Returns the child of <paramref name="parent"/> that contains <paramref name="position"/> and is
	/// the tightest such child, or null when the position is not inside any child.
	/// </summary>
	private static SyntaxNode? InnermostChild(SyntaxNode parent, TextSpan parentSpan, int position)
	{
		SyntaxNode? best = null;
		var bestLength = int.MaxValue;

		foreach (var child in parent.GetChildren())
		{
			var span = Clamp(child.Span, parentSpan.Start + parentSpan.Length);

			if (!Contains(span, position))
				continue;

			if (span.Length >= bestLength)
				continue;

			best = child;
			bestLength = span.Length;
		}

		return best;
	}

	/// <summary>
	/// Returns the narrowest token covering <paramref name="position"/>. Comment tokens are included
	/// so selecting inside a documentation comment starts from the comment itself.
	/// </summary>
	private static IToken? FindTokenAt(IReadOnlyList<IToken> tokens, int position)
	{
		IToken? best = null;
		var bestLength = int.MaxValue;

		foreach (var token in tokens)
		{
			var length = (token.StopIndex - token.StartIndex) + 1;

			if (token.StartIndex > position || token.StopIndex < position)
				continue;

			if (length >= bestLength)
				continue;

			best = token;
			bestLength = length;
		}

		return best;
	}

	private static IReadOnlyList<IToken> SnapshotTokens(string source)
	{
		return [.. new CvoloLexer(new AntlrInputStream(source)).GetAllTokens()];
	}

	private static bool Contains(TextSpan span, int position)
	{
		return position >= span.Start && position <= span.Start + span.Length;
	}

	/// <summary>
	/// Clamps a syntax span to the captured text. A recovered node can carry a span that runs past
	/// the end of the document, and a chain must never reach outside the captured source.
	/// </summary>
	private static TextSpan Clamp(Cvolo.Core.Diagnostics.TextSpan span, int limit)
	{
		var start = Math.Clamp(span.Start, 0, limit);
		var end = Math.Clamp(span.Start + span.Length, start, limit);

		return new TextSpan(start, end - start);
	}
}
