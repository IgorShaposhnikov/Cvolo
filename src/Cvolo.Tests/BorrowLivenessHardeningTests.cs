using Cvolo.Analysis.Passes.Safety;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Expressions;
using Cvolo.Core.AST.Statements;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Tests;

/// <summary>
/// INC A / A4 regression tests for non-lexical borrow liveness.
/// Receiver-based calls, nested expressions, and inner block scans must not
/// make the compiler release a still-live borrow.
/// </summary>
public sealed class BorrowLivenessHardeningTests
{
    private static readonly TextSpan Span = new(0, 0);

    /// <summary>
    /// A call receiver counts as a use only when the reference name matches a
    /// complete identifier segment; a name prefix is not sufficient.
    /// </summary>
    [Theory]
    [InlineData("r.Read", "r", true)]
    [InlineData("r.Read.Field", "r", true)]
    [InlineData("r", "r", true)]
    [InlineData("other.Read", "r", false)]
    [InlineData("result.Read", "r", false)]
    [InlineData("Reader.Read", "r", false)]
    public void CallReceiverCountsAsReferenceUse(string name, string referenceName, bool expected)
    {
        var call = new CallExpressionSyntax(Span, name, [], []);

        Assert.Equal(
            expected,
            BorrowLivenessAnalyzer.ExpressionContainsRefUse(call, referenceName));
    }

    /// <summary>
    /// Last-use analysis must retain the borrow before a receiver-only call
    /// and may release it after that call has completed.
    /// </summary>
    [Fact]
    public void ReceiverOnlyUseDoesNotReleaseBorrowBeforeTheCall()
    {
        var active = new Dictionary<string, (string BorrowedName, bool IsMutable, int LastUseEnd, TextSpan DeclSpan)>
        {
            ["r"] = ("owner", false, 0, Span)
        };
        var removed = new List<string>();
        var scanner = new BorrowLivenessAnalyzer(active, removed.Add);
        var call = new CallExpressionSyntax(Span, "r.Read", [], []);
        var block = new BlockStatementSyntax(Span, [new ExpressionStatementSyntax(Span, call)]);

        // The call itself is the last use, not an opportunity to release early.
        scanner.ReleaseExpiredBorrows(0, block, 0);
        Assert.Empty(removed);

        // After the call there are no remaining uses of 'r'.
        scanner.ReleaseExpiredBorrows(0, block, 1);
        Assert.Single(removed);
        Assert.Equal("r", removed[0]);
    }

    /// <summary>
    /// An inner block is not entitled to release a borrow that originated
    /// outside it; the enclosing control flow may still use that reference.
    /// </summary>
    [Fact]
    public void EnclosingReferenceIsNotReleasedByAnInnerBlockScan()
    {
        var active = new Dictionary<string, (string BorrowedName, bool IsMutable, int LastUseEnd, TextSpan DeclSpan)>
        {
            ["r"] = ("owner", false, 0, Span)
        };
        var removed = new List<string>();
        var scanner = new BorrowLivenessAnalyzer(active, removed.Add, name => name == "r");
        var innerBlock = new BlockStatementSyntax(Span, [
            new ExpressionStatementSyntax(Span, new IntegerLiteralExpressionSyntax(Span, 3UL))
        ]);

        scanner.ReleaseExpiredBorrows(0, innerBlock, 0);

        Assert.Empty(removed);
    }

    /// <summary>
    /// Uses hidden in conditional, coalescing, or optional access expressions
    /// must be visible to the same liveness scanner as direct identifier uses.
    /// </summary>
    [Fact]
    public void NestedExpressionVariantsDoNotSilentlyHideReferenceUse()
    {
        var reference = new IdentifierExpressionSyntax(Span, "r");
        var nested = new TernaryExpressionSyntax(
            Span,
            new BooleanLiteralExpressionSyntax(Span, true),
            new CoalesceExpressionSyntax(Span, reference, new IdentifierExpressionSyntax(Span, "fallback")),
            new OptionalChainExpressionSyntax(Span, reference, "Touch", []));

        Assert.True(BorrowLivenessAnalyzer.ExpressionContainsRefUse(nested, "r"));
    }
}
