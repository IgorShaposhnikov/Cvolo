using Cvolo.Compiler.Tooling;

namespace Cvolo.Tests.Tooling;

/// <summary>
/// Verifies smart selection: the chain starts at the token under the caret and grows outwards
/// through every enclosing region that adds something, one chain per requested position.
/// </summary>
public sealed class SelectionRangeServiceTests
{
	private const string Source = """
public struct Header
{
    public byte Kind;
}

public struct Layout
{
    public nuint Size;
}

public extension Layout
{
    public Layout FromType<T>()
    {
        return Layout { Size: 0 };
    }

    public nuint Repeat(nuint count)
    {
        return count;
    }
}

int main()
{
    /// A documentation line.
    var layout = Layout.FromType<Header>().Repeat(3);
    return 0;
}
""";

	[Fact]
	public void SelectionGrowsFromTheIdentifierOutwards()
	{
		var (_, document) = Open(("Main.cvl", Source));

		var chain = LevelsOf(document, At(Source, "<Header>") + 2);

		Assert.Equal("Header", chain[0]);
		Assert.Contains("Layout.FromType<Header>()", chain);
		Assert.Contains("Layout.FromType<Header>().Repeat", chain);
		Assert.Contains(chain, text => text.StartsWith("var layout =", StringComparison.Ordinal));
		Assert.StartsWith("public struct Header", chain[^1], StringComparison.Ordinal);
		Assert.EndsWith("}", chain[^1], StringComparison.Ordinal);
	}

	[Fact]
	public void EveryLevelContainsTheLevelBeforeIt()
	{
		var (_, document) = Open(("Main.cvl", Source));

		var ranges = RangesOf(document, At(Source, "<Header>") + 2);

		for (var i = 1; i < ranges.Count; i++)
		{
			Assert.True(ranges[i].Range.Start <= ranges[i - 1].Range.Start, "a parent must start no later than its child");
			Assert.True(ranges[i].Range.End >= ranges[i - 1].Range.End, "a parent must end no earlier than its child");
			Assert.NotEqual(ranges[i].Range, ranges[i - 1].Range);
		}
	}

	[Fact]
	public void OneChainIsReturnedPerRequestedPositionInRequestOrder()
	{
		var (_, document) = Open(("Main.cvl", Source));

		var chains = document.GetSelectionRanges([At(Source, "<Header>") + 2, At(Source, "Repeat(3)") + 2]);

		Assert.Equal(2, chains.Count);
		Assert.Equal("Header", document.Text.GetText(chains[0]!.Range));

		var second = TextsOf(document, chains[1]);
		Assert.Equal("Repeat", second[0]);
		Assert.Contains("Layout.FromType<Header>().Repeat", second);
	}

	[Fact]
	public void APositionInsideADocumentationCommentStartsAtTheComment()
	{
		var (_, document) = Open(("Main.cvl", Source));

		var chain = LevelsOf(document, At(Source, "documentation line") + 3);

		Assert.StartsWith("///", chain[0], StringComparison.Ordinal);
	}

	[Fact]
	public void ACaretInWhitespaceStillReachesTheEnclosingDeclaration()
	{
		var (_, document) = Open(("Main.cvl", Source));

		var chain = LevelsOf(document, At(Source, "    var layout") + 4);

		Assert.Contains(chain, text => text.StartsWith("var layout =", StringComparison.Ordinal));
	}

	[Fact]
	public void APositionOutsideTheDocumentYieldsNoChain()
	{
		var (_, document) = Open(("Main.cvl", Source));

		var chains = document.GetSelectionRanges([document.Text.Length + 1]);

		Assert.Equal([null], chains);
	}

	[Fact]
	public void SelectionWorksOnAnIncompleteDocument()
	{
		const string incomplete = """
int main()
{
    var layout = Layout.FromType<Header>(
""";
		var (_, document) = Open(("Main.cvl", incomplete));

		var chain = LevelsOf(document, At(incomplete, "Header") + 1);

		Assert.Equal("Header", chain[0]);
		Assert.True(chain.Count > 1);
		Assert.All(chain, text => Assert.False(string.IsNullOrEmpty(text)));
	}

	[Fact]
	public void ANegativeRequestedPositionIsRejected()
	{
		var (_, document) = Open(("Main.cvl", Source));

		Assert.Throws<ArgumentOutOfRangeException>(() => document.GetSelectionRanges([-1]));
	}

	private static IReadOnlyList<ToolingSelectionRange> RangesOf(DocumentSnapshot document, int position)
	{
		var chain = document.GetSelectionRanges([position])[0];

		Assert.NotNull(chain);

		List<ToolingSelectionRange> ranges = [];

		for (var level = chain; level is not null; level = level.Parent)
			ranges.Add(level);

		return ranges;
	}

	private static IReadOnlyList<string> LevelsOf(DocumentSnapshot document, int position)
	{
		return TextsOf(document, document.GetSelectionRanges([position])[0]);
	}

	private static IReadOnlyList<string> TextsOf(DocumentSnapshot document, ToolingSelectionRange? chain)
	{
		List<string> texts = [];

		for (var level = chain; level is not null; level = level.Parent)
			texts.Add(document.Text.GetText(level.Range));

		return texts;
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
