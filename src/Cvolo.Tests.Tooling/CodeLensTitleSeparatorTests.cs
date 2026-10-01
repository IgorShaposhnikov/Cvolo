using System;
using System.Linq;
using Cvolo.Compiler.Tooling;
using Xunit;

namespace Cvolo.Tests.Tooling;

/// <summary>
/// An editor joins the several CodeLenses of one line with its own <c>|</c>. A title that contains a
/// pipe therefore reads as two lenses, so a struct declared on a single line looks like its lenses
/// were duplicated. These tests pin the title convention that makes one lens read as one lens.
/// </summary>
public sealed class CodeLensTitleSeparatorTests
{
	/// <summary>
	/// The settings a default VS Code install sends: reference counts on, type layout on, per-field
	/// layout on, per-field reference counts off.
	/// </summary>
	private static readonly ToolingCodeLensOptions EditorDefaults = new()
	{
		References = true,
		Layout = true,
		Members = false,
		FieldLayout = true,
		NativeInterop = true,
	};

	private const string SingleLineStructs = """
		public struct Point { public int X; public int Y; }

		public struct Pair { public int First; public int Second; }
		""";

	[Fact]
	public void NoLensTitleContainsTheEditorsOwnSeparator()
	{
		using var fixture = TempProject.Create(("Main.cvl", SingleLineStructs));
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		Assert.True(project.TryGetDocumentId(fixture.GetPath("Main.cvl"), out var id));
		var document = project.InitialSnapshot.GetDocument(id);

		var lenses = document.GetCodeLenses(EditorDefaults).ToList();

		Assert.NotEmpty(lenses);
		foreach (var lens in lenses)
		{
			Assert.DoesNotContain("|", lens.Title);
		}
	}

	[Fact]
	public void EveryLensTitleOfAOneLineStructRowIsUniquelyIdentifiable()
	{
		using var fixture = TempProject.Create(("Main.cvl", SingleLineStructs));
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		Assert.True(project.TryGetDocumentId(fixture.GetPath("Main.cvl"), out var id));
		var document = project.InitialSnapshot.GetDocument(id);
		var text = document.Text.ToString()!.Replace("\r\n", "\n");

		// VS Code renders every lens whose range is on one line as one pipe-joined row, so a repeated
		// title inside that row is what a reader sees as a duplicated lens. The same title on another
		// line is just two declarations of equal shape and is left alone.
		var rows = document.GetCodeLenses(EditorDefaults)
			.GroupBy(lens => LineOf(text, lens.Range.Start))
			.ToList();

		Assert.NotEmpty(rows);
		foreach (var row in rows)
		{
			var titles = row.Select(lens => lens.Title).ToList();
			Assert.Equal(titles.Count, titles.Distinct().Count());
		}
	}

	private static int LineOf(string text, int position) =>
		text[..Math.Min(position, text.Length)].Count(c => c == '\n');

	[Fact]
	public void OneDeclarationYieldsAtMostOneLensOfEachKind()
	{
		using var fixture = TempProject.Create(("Main.cvl", SingleLineStructs));
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		Assert.True(project.TryGetDocumentId(fixture.GetPath("Main.cvl"), out var id));
		var document = project.InitialSnapshot.GetDocument(id);

		var keys = document.GetCodeLenses(EditorDefaults)
			.Select(lens => (lens.SymbolId, lens.Kind))
			.ToList();

		Assert.Equal(keys.Count, keys.Distinct().Count());
	}
}