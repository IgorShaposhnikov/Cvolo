namespace Cvolo.Compiler.Tooling;

/// <summary>
/// One level of a smart-selection chain: the smallest meaningful source range containing the
/// requested position, plus the next larger meaningful range around it. Equivalent duplicate
/// parents are omitted, so the chain contains distinct regions only.
/// </summary>
/// <param name="Range">The source range of this level.</param>
/// <param name="Parent">The next larger range, or null for the outermost level.</param>
public sealed record ToolingSelectionRange(TextSpan Range, ToolingSelectionRange? Parent)
{
	/// <summary>
	/// Builds a chain from the given innermost-to-outermost ranges and returns the innermost level,
	/// whose <see cref="Parent"/> links grow outwards. Any level whose range equals the range of the
	/// level around it is omitted, so the chain contains distinct regions only.
	/// </summary>
	public static ToolingSelectionRange? CreateChain(IEnumerable<TextSpan> innermostToOutermost)
	{
		ArgumentNullException.ThrowIfNull(innermostToOutermost);

		ToolingSelectionRange? parent = null;

		// The chain is built from the outside in, so the level returned is the innermost one and
		// every parent link points at a strictly larger region.
		foreach (var range in innermostToOutermost.Reverse())
		{
			if (parent is not null && parent.Range == range)
				continue;

			parent = new ToolingSelectionRange(range, parent);
		}

		return parent;
	}
}
