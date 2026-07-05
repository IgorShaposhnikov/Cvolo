using Cvolo.Core.Diagnostics;

namespace Cvolo.Analysis.Symbols.Borrowing;

public sealed class BorrowSymbol(string borrowerName, string borrowedName, bool isMutable, TextSpan span)
{
	public string BorrowerName { get; } = borrowerName;
	public string BorrowedName { get; } = borrowedName;
	public bool IsMutable { get; } = isMutable;
	public TextSpan Span { get; } = span;
}
