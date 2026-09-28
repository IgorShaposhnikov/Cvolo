using Cvolo.Compiler.Tooling;

namespace Cvolo.Tests.Tooling;

/// <summary>
/// Verifies that document highlights are the semantic occurrences of one binding, each classified by
/// how it accesses it. There is deliberately no same-spelling fallback: a different binding that
/// happens to share a name is never highlighted.
/// </summary>
public sealed class DocumentHighlightServiceTests
{
	private const string Counter = """
public struct Counter {
    public int Total;
}

int main() {
    var counter = Counter { Total: 0 };
    counter.Total = 1;
    counter.Total += 2;
    return counter.Total;
}
""";

	[Fact]
	public void ALocal_HighlightsItsDeclarationAndEveryUse()
	{
		var (_, document) = Open(("Main.cvl", Counter));

		var highlights = document.GetDocumentHighlights(At(Counter, "counter"));

		Assert.Equal(4, highlights.Count);
		Assert.All(highlights, highlight => Assert.Equal("counter", document.Text.GetText(highlight.Range)));
		Assert.Equal(ReferenceAccessKind.Declaration, highlights[0].AccessKind);
		Assert.All(highlights.Skip(1), highlight => Assert.Equal(ReferenceAccessKind.Read, highlight.AccessKind));
	}

	[Fact]
	public void AField_IsClassifiedByHowTheAssignmentUsesIt()
	{
		var (_, document) = Open(("Main.cvl", Counter));

		var highlights = document.GetDocumentHighlights(At(Counter, "counter.Total = 1") + "counter.".Length);

		// The struct literal, the assignment, the compound assignment and the read. The struct literal
		// has no read/write shape of its own, so it is reported without one rather than guessed at.
		Assert.Equal(4, highlights.Count);
		Assert.Equal(1, highlights.Count(highlight => highlight.AccessKind == ReferenceAccessKind.Write));
		Assert.Equal(1, highlights.Count(highlight => highlight.AccessKind == ReferenceAccessKind.ReadWrite));
		Assert.Equal(1, highlights.Count(highlight => highlight.AccessKind == ReferenceAccessKind.Read));
		Assert.Equal(1, highlights.Count(highlight => highlight.AccessKind == ReferenceAccessKind.Declaration));
		Assert.All(highlights, highlight => Assert.Equal("Total", document.Text.GetText(highlight.Range)));
	}

	[Fact]
	public void AnIncrementOperator_IsAReadWriteAccess()
	{
		const string source = """
int main() {
    var count = 0;
    count++;
    return count;
}
""";
		var (_, document) = Open(("Main.cvl", source));

		var highlights = document.GetDocumentHighlights(At(source, "count++"));

		Assert.Equal(3, highlights.Count);
		Assert.Equal(
			[ReferenceAccessKind.Declaration, ReferenceAccessKind.ReadWrite, ReferenceAccessKind.Read],
			[.. highlights.Select(highlight => highlight.AccessKind)]);
	}

	[Fact]
	public void AParameter_KeepsItsDeclarationAndItsUsesTogether()
	{
		const string source = """
int Twice(int value) { return value + value; }
int main() { return Twice(5); }
""";
		var (_, document) = Open(("Main.cvl", source));

		var highlights = document.GetDocumentHighlights(At(source, "int value") + "int ".Length);

		Assert.Equal(3, highlights.Count);
		Assert.All(highlights, highlight => Assert.Equal("value", document.Text.GetText(highlight.Range)));
		Assert.Equal(ReferenceAccessKind.Declaration, highlights[0].AccessKind);
	}

	[Fact]
	public void ADifferentBindingWithTheSameSpellingIsNeverHighlighted()
	{
		const string source = """
int Doubled(int value) { return value + value; }
int Tripled(int value) { return value + value + value; }
int main() { return Doubled(2) + Tripled(3); }
""";
		var (_, document) = Open(("Main.cvl", source));

		var doubled = document.GetDocumentHighlights(At(source, "int value") + "int ".Length);
		var tripled = document.GetDocumentHighlights(At(source, "int value", At(source, "Tripled") - "int ".Length) + "int ".Length);

		Assert.Equal(3, doubled.Count);
		Assert.Equal(4, tripled.Count);
		Assert.DoesNotContain(tripled, highlight => doubled.Contains(highlight));
	}

	[Fact]
	public void APositionOnNothingInParticularYieldsNoHighlights()
	{
		var (_, document) = Open(("Main.cvl", Counter));

		var highlights = document.GetDocumentHighlights(At(Counter, "int main") - 1);

		Assert.Empty(highlights);
	}

	[Fact]
	public void Highlights_AreOrderedByPosition()
	{
		var (_, document) = Open(("Main.cvl", Counter));

		var highlights = document.GetDocumentHighlights(At(Counter, "counter"));
		var starts = highlights.Select(highlight => highlight.Range.Start).ToArray();

		Assert.Equal([.. starts.Order()], starts);
	}

	[Fact]
	public void Highlights_StayInsideTheDocumentThatOwnsTheBinding()
	{
		const string main = """
int main() {
    var counter = Counter { Total: 0 };
    counter.Total = 1;
    return counter.Total;
}
""";
		var (_, document) = Open(
			("Main.cvl", main),
			("Types.cvl", "public struct Counter {\n    public int Total;\n}\n"));

		var highlights = document.GetDocumentHighlights(At(main, "counter.Total = 1") + "counter.".Length);

		// The field is declared in Types.cvl, so only the two uses inside this document are returned.
		Assert.Equal(2, highlights.Count);
		Assert.All(highlights, highlight => Assert.True(highlight.Range.End <= document.Text.Length));
		Assert.All(highlights, highlight => Assert.Equal("Total", document.Text.GetText(highlight.Range)));
	}

	[Fact]
	public void APositionOutsideTheDocumentIsRejected()
	{
		var (_, document) = Open(("Main.cvl", Counter));

		Assert.Throws<ArgumentOutOfRangeException>(() => document.GetDocumentHighlights(-1));
		Assert.Throws<ArgumentOutOfRangeException>(() => document.GetDocumentHighlights(document.Text.Length + 1));
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
