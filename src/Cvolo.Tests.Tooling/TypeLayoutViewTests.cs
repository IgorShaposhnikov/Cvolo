using Cvolo.Compiler.Tooling;

namespace Cvolo.Tests.Tooling;

/// <summary>
/// Show Type Layout resolves the same way the compiler does: a type declaration, a type use, a
/// variable or parameter whose type is written down, and a local whose type the compiler inferred
/// from its initializer. A position the compiler cannot type is unavailable rather than guessed
/// (§25, §54).
/// </summary>
public sealed class TypeLayoutViewTests
{
	private static (CvoloProject Project, ProjectSnapshot Snapshot, DocumentSnapshot Document, TempProject Fixture) Open(
		params (string File, string Source)[] files)
	{
		var fixture = TempProject.Create(files);
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		var snapshot = project.InitialSnapshot;
		return (project, snapshot, snapshot.GetDocument(project.GetDocumentId(files[0].File)), fixture);
	}

	private static int At(string source, string needle, int start = 0) =>
		source.IndexOf(needle, start, StringComparison.Ordinal);

	private const string Source =
		"public struct Header\n" +
		"{\n" +
		"    public byte Kind;\n" +
		"    public int Length;\n" +
		"}\n" +
		"\n" +
		"Header GetHeader()\n" +
		"{\n" +
		"    return Header { Kind: 0, Length: 0 };\n" +
		"}\n" +
		"\n" +
		"T Identity<T>(T item)\n" +
		"{\n" +
		"    return item;\n" +
		"}\n" +
		"\n" +
		"int main()\n" +
		"{\n" +
		"    val Header value = GetHeader();\n" +
		"    var inferred = GetHeader();\n" +
		"    val int total = value.Length;\n" +
		"    return total;\n" +
		"}\n";

	[Fact]
	public void ATypeDeclaration_ResolvesTheDeclaredType()
	{
		var x = Open(("Main.cvl", Source));
		using (x.Fixture)
		{
			var layout = x.Snapshot.GetTypeLayoutAtPosition(
				x.Document.Id, At(Source, "public struct Header") + "public struct ".Length);

			Assert.NotNull(layout);
			Assert.Equal("Header", layout!.TypeDisplay);
			Assert.Equal(8, layout.Size);
		}
	}

	[Fact]
	public void ATypeUse_ResolvesTheNamedType()
	{
		var x = Open(("Main.cvl", Source));
		using (x.Fixture)
		{
			var layout = x.Snapshot.GetTypeLayoutAtPosition(
				x.Document.Id, At(Source, "val Header value") + "val ".Length);

			Assert.NotNull(layout);
			Assert.Equal("Header", layout!.TypeDisplay);
			Assert.Equal(new[] { "Kind", "Length" }, layout.Members.Select(member => member.Name));
		}
	}

	[Fact]
	public void AKnownVariable_ResolvesItsDeclaredType()
	{
		var x = Open(("Main.cvl", Source));
		using (x.Fixture)
		{
			var layout = x.Snapshot.GetTypeLayoutAtPosition(
				x.Document.Id, At(Source, "val Header value") + "val Header ".Length);

			Assert.NotNull(layout);
			Assert.Equal("Header", layout!.TypeDisplay);
			Assert.Equal(8, layout.Size);
			Assert.Equal(4, layout.Alignment);
		}
	}

	[Fact]
	public void AVariableWhoseNameAlsoAppearsInItsDeclaration_ResolvesItsType()
	{
		// The name `v` also appears in the keyword `val` and as a prefix of `Vec`, so a resolver that
		// matched any substring would find the name span in the wrong place and give up on the value.
		const string source =
			"public struct Vec\n" +
			"{\n" +
			"    public int X;\n" +
			"    public int Y;\n" +
			"}\n" +
			"\n" +
			"int main()\n" +
			"{\n" +
			"    val Vec v = Vec { X: 1, Y: 2 };\n" +
			"    return v.X;\n" +
			"}\n";

		var x = Open(("Main.cvl", source));
		using (x.Fixture)
		{
			var layout = x.Snapshot.GetTypeLayoutAtPosition(
				x.Document.Id, At(source, "val Vec v") + "val Vec ".Length);

			Assert.NotNull(layout);
			Assert.Equal("Vec", layout!.TypeDisplay);
			Assert.Equal(new[] { "X", "Y" }, layout.Members.Select(member => member.Name));
		}
	}

	[Fact]
	public void AnInferredLocal_ResolvesTheTypeTheCompilerInferred()
	{
		var x = Open(("Main.cvl", Source));
		using (x.Fixture)
		{
			var layout = x.Snapshot.GetTypeLayoutAtPosition(
				x.Document.Id, At(Source, "var inferred") + "var ".Length);

			Assert.NotNull(layout);
			Assert.Equal("Header", layout!.TypeDisplay);

			// The value symbol has no source of its own to navigate to, so the layout points at the
			// type the compiler inferred, not at the editor guessing from the initializer.
			Assert.NotNull(layout.Definition);
			Assert.Equal(
				At(Source, "public struct Header") + "public struct ".Length,
				layout.Definition!.SelectionSpan.Start);
		}
	}

	[Fact]
	public void AFieldUse_ResolvesTheFieldsOwnType()
	{
		var x = Open(("Main.cvl", Source));
		using (x.Fixture)
		{
			var layout = x.Snapshot.GetTypeLayoutAtPosition(
				x.Document.Id, At(Source, "value.Length") + "value.".Length);

			Assert.NotNull(layout);
			Assert.Equal("int", layout!.TypeDisplay);
			Assert.Equal(4, layout.Size);
		}
	}

	[Fact]
	public void AValueWhoseTypeTheCompilerCannotResolve_IsUnavailableRatherThanGuessed()
	{
		var x = Open(("Main.cvl", Source));
		using (x.Fixture)
		{
			// A type parameter is not a concrete layout, so there is nothing honest to show.
			var parameter = x.Snapshot.GetTypeLayoutAtPosition(
				x.Document.Id, At(Source, "T item") + "T ".Length);
			Assert.Null(parameter);

			// A position with no symbol at all is unavailable for the same reason.
			Assert.Null(x.Snapshot.GetTypeLayoutAtPosition(x.Document.Id, At(Source, "return total;")));
		}
	}

	[Fact]
	public void ALayoutCarriesASubjectTheClientCanAskForAgain()
	{
		var x = Open(("Main.cvl", Source));
		using (x.Fixture)
		{
			var atSource = x.Snapshot.GetTypeLayoutAtPosition(
				x.Document.Id, At(Source, "public struct Header") + "public struct ".Length);

			Assert.NotNull(atSource);
			Assert.Equal("Header", atSource!.Subject);

			// The subject is what a client stores. Asking again with it re-resolves against the
			// current snapshot and yields the same compiler facts, including the navigation.
			var bySubject = x.Snapshot.GetTypeLayoutBySubject(x.Document.Id, atSource.Subject!);

			Assert.NotNull(bySubject);
			Assert.Equal(atSource.TypeDisplay, bySubject!.TypeDisplay);
			Assert.Equal(atSource.Size, bySubject.Size);
			Assert.Equal(atSource.Alignment, bySubject.Alignment);
			Assert.Equal(
				atSource.Members.Select(member => member.Name),
				bySubject.Members.Select(member => member.Name));
			Assert.Equal("Header", bySubject.Subject);
			Assert.Equal(
				At(Source, "public struct Header") + "public struct ".Length,
				bySubject.Definition!.SelectionSpan.Start);
		}
	}

	[Fact]
	public void ASubjectThatNoLongerNamesAType_IsUnavailableRatherThanStale()
	{
		var x = Open(("Main.cvl", Source));
		using (x.Fixture)
		{
			// A type that was renamed or removed stops resolving, so the open view can say so
			// instead of keeping the numbers it last saw.
			Assert.Null(x.Snapshot.GetTypeLayoutBySubject(x.Document.Id, "Renamed"));

			// A blank subject is the same kind of absence, not an error.
			Assert.Null(x.Snapshot.GetTypeLayoutBySubject(x.Document.Id, "   "));
		}
	}
}
