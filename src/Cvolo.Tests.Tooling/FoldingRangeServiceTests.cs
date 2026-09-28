using Cvolo.Compiler.Tooling;

namespace Cvolo.Tests.Tooling;

/// <summary>
/// Verifies that folding regions come from the parsed syntax and the comment tokens. A region never
/// ends on the line it starts on, never leaves the document, and a single-line declaration never
/// produces a region at all.
/// </summary>
public sealed class FoldingRangeServiceTests
{
	private const string Source = """
namespace Sample
{
    /// Documentation line one.
    /// Documentation line two.
    public struct Header
    {
        public byte Kind;
        public int Length;
    }

    /*
     * Block comment line one.
     * Block comment line two.
     */

    // A plain source comment
    // spanning two lines.

    public nuint Measure(Header header)
    {
        if (header.Length > 0)
        {
            while (header.Length > 0)
            {
                return 1;
            }
        }

        try
        {
            return 2;
        }
        catch
        {
            return 3;
        }
        finally
        {
            return 4;
        }
    }

    public nuint OneLiner() { return 0; }
}
""";

	[Fact]
	public void EveryDeclarationWithABodyContributesARegion()
	{
		var (_, document) = Open(("Main.cvl", Source));

		var folds = document.GetFoldingRanges();

		foreach (var needle in new[]
		{
			"public byte Kind;",
			"while (header.Length > 0)",
			"return 1;",
			"return 2;",
			"return 3;",
			"return 4;",
			"public nuint Measure",
		})
		{
			Assert.Contains(folds, fold => document.Text.GetText(fold.Range).Contains(needle, StringComparison.Ordinal));
		}
	}

	[Fact]
	public void RegionsAreDistinctAndADeclarationFoldsOnce()
	{
		var (_, document) = Open(("Main.cvl", Source));

		var folds = document.GetFoldingRanges();
		var bodyOpen = Source.IndexOf('{', At(Source, "public nuint Measure"));

		// A callable and its body block describe the same brace pair, so both would begin right after
		// the callable's opening brace; they must collapse into one region instead of two.
		Assert.Equal(folds.Count, folds.Select(fold => fold.Range).Distinct().Count());
		Assert.Single(folds.Where(fold => fold.Range.Start == bodyOpen + 1));
	}

	[Fact]
	public void ASingleLineDeclarationDoesNotFold()
	{
		var (_, document) = Open(("Main.cvl", Source));

		var folds = document.GetFoldingRanges();
		var oneLiner = document.Text.GetLinePosition(At(Source, "OneLiner")).Line;

		Assert.DoesNotContain(folds, fold => document.Text.GetLinePosition(fold.Range.Start).Line == oneLiner);
	}

	[Fact]
	public void ADocumentationBlockFoldsAsOneDocumentationRegion()
	{
		var (_, document) = Open(("Main.cvl", Source));

		var folds = document.GetFoldingRanges();
		var documentation = folds.Where(fold => fold.Kind == ToolingFoldingKind.Documentation).ToArray();

		var fold = Assert.Single(documentation);
		var text = document.Text.GetText(fold.Range);
		Assert.Contains("Documentation line one.", text, StringComparison.Ordinal);
		Assert.Contains("Documentation line two.", text, StringComparison.Ordinal);
	}

	[Fact]
	public void ABlockCommentFoldsAsACommentRegion()
	{
		var (_, document) = Open(("Main.cvl", Source));

		var folds = document.GetFoldingRanges();
		var fold = Assert.Single(folds.Where(candidate => candidate.Kind == ToolingFoldingKind.Comment
			&& document.Text.GetText(candidate.Range).Contains("Block comment line one.", StringComparison.Ordinal)));

		Assert.Contains("Block comment line two.", document.Text.GetText(fold.Range), StringComparison.Ordinal);
	}

	[Fact]
	public void ConsecutiveSourceCommentLinesFoldAsOneCommentRegion()
	{
		var (_, document) = Open(("Main.cvl", Source));

		var folds = document.GetFoldingRanges();
		var fold = Assert.Single(folds.Where(candidate => candidate.Kind == ToolingFoldingKind.Comment
			&& document.Text.GetText(candidate.Range).Contains("A plain source comment", StringComparison.Ordinal)));

		Assert.Contains("spanning two lines.", document.Text.GetText(fold.Range), StringComparison.Ordinal);
	}

	[Fact]
	public void EveryRegionSpansMoreThanOneLineAndStaysInsideTheDocument()
	{
		var (_, document) = Open(("Main.cvl", Source));

		foreach (var fold in document.GetFoldingRanges())
		{
			var start = document.Text.GetLinePosition(fold.Range.Start).Line;
			var end = document.Text.GetLinePosition(fold.Range.End).Line;

			Assert.True(end > start, $"region at {fold.Range.Start}..{fold.Range.End} folded within a single line");
			Assert.InRange(fold.Range.Start, 0, document.Text.Length);
			Assert.InRange(fold.Range.End, 0, document.Text.Length);
		}
	}

	[Fact]
	public void RegionsAreOrderedByStartAndThenByDecreasingLength()
	{
		var (_, document) = Open(("Main.cvl", Source));

		var regions = document.GetFoldingRanges().Select(fold => (fold.Range.Start, -fold.Range.Length)).ToArray();

		Assert.Equal([.. regions.Order()], regions);
	}

	[Fact]
	public void AnIncompleteDocumentStillFoldsWhatWasRecovered()
	{
		const string incomplete = """
namespace Broken
{
    public struct Header
    {
        public int Length;
    }

    public nuint Measure()
    {
        return 1;
""";
		var (_, document) = Open(("Main.cvl", incomplete));

		var folds = document.GetFoldingRanges();

		Assert.Contains(folds, fold => document.Text.GetText(fold.Range).Contains("public int Length;", StringComparison.Ordinal));
		Assert.All(folds, fold => Assert.True(fold.Range.End <= document.Text.Length));
	}

	[Fact]
	public void AnEmptyDocumentHasNoRegions()
	{
		var (_, document) = Open(("Main.cvl", "\n\n"));

		Assert.Empty(document.GetFoldingRanges());
	}

	private static (ProjectSnapshot Snapshot, DocumentSnapshot Document) Open(params (string File, string Source)[] files)
	{
		using var fixture = TempProject.Create(files);
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		var snapshot = project.InitialSnapshot;

		return (snapshot, snapshot.GetDocument(project.GetDocumentId(files[0].File)));
	}

	private static int At(string source, string needle, int start = 0) => source.IndexOf(needle, start, StringComparison.Ordinal);
}
