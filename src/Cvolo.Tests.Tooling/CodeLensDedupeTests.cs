using Cvolo.Compiler.Tooling;

namespace Cvolo.Tests.Tooling;

/// <summary>
/// One declaration gets one reference lens, and only on the declaration itself. A reference, a
/// call, an assignment or any other use of a name is a use, not a declaration, so it never carries
/// a lens. These tests pin that invariant by counting lenses per declaration identity rather than
/// by line, so an attribute in front of a declaration, a recomputation, or a broken edit can never
/// turn one declaration into two lenses or move a lens onto a use.
/// </summary>
public sealed class CodeLensDedupeTests
{
	private static (ProjectSnapshot Snapshot, DocumentSnapshot Document, TempProject Fixture) Open(
		string source,
		string file = "Main.cvl")
	{
		var fixture = TempProject.Create((file, source));
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		var snapshot = project.InitialSnapshot;
		return (snapshot, snapshot.GetDocument(project.GetDocumentId(file)), fixture);
	}

	private static IReadOnlyList<ToolingCodeLensInfo> Lenses(DocumentSnapshot document) => document.GetCodeLenses();

	private static IReadOnlyList<ToolingCodeLensInfo> ReferenceLenses(DocumentSnapshot document) =>
		[.. Lenses(document).Where(lens => lens.Kind == ToolingCodeLensKind.References)];

	/// <summary>Every reference lens in the document, described by the exact text it selects.</summary>
	private static string[] Describe(DocumentSnapshot document) =>
		[.. ReferenceLenses(document)
			.Select(lens => $"{document.Text.GetText(lens.Range)}@{lens.Range.Start}:{document.Text.GetText(lens.Range).Trim()}")
			.OrderBy(text => text, StringComparer.Ordinal)];

	/// <summary>The user-reported shape: an attributed constructor whose body calls and assigns names.</summary>
	private const string WindowExtension = """
		public struct WindowParameters
		{
		    public int Width;
		    public int Height;
		}

		public struct Window
		{
		    public nuint Handle;
		}

		public extension Window
		{
		    [UnsafeBody]
		    public Window(WindowParameters args)
		    {
		        Handle = CreateWindow(args.Width, args.Height);
		        MakeContextCurrent(Handle);
		        ClearColor(Handle);
		    }
		}
		""";

	private const string WindowPreamble = """
		extern nuint CreateWindow(int width, int height);
		extern void MakeContextCurrent(nuint handle);
		extern void ClearColor(nuint handle);

		""";

	/// <summary>
	/// The invariant itself: no two lenses in a document may describe the same declaration with the
	/// same kind. Two different declarations may legitimately share a name - a struct and its
	/// constructor are both called <c>Window</c> - so identity, not the selected text, is the key.
	/// </summary>
	private static void AssertNoDuplicateLenses(DocumentSnapshot document)
	{
		var keys = Lenses(document)
			.Select(lens => (lens.SymbolId, lens.Kind))
			.ToList();

		Assert.Equal(keys.Count, keys.Distinct().Count());
	}

	/// <summary>The number of reference lenses anchored to one declaration identity.</summary>
	private static int ReferenceLensesFor(DocumentSnapshot document, ToolingCodeLensInfo anchor) =>
		ReferenceLenses(document).Count(lens => lens.SymbolId == anchor.SymbolId);

	[Fact]
	public void AttributedConstructor_GetsExactlyOneReferenceLens()
	{
		var x = Open(WindowPreamble + WindowExtension);
		using (x.Fixture)
		{
			AssertNoDuplicateLenses(x.Document);

			var windowLenses = ReferenceLenses(x.Document)
				.Where(lens => x.Document.Text.GetText(lens.Range) == "Window")
				.ToList();

			// The struct and its constructor are two declarations that happen to share a name.
			Assert.Equal(2, windowLenses.Count);
			Assert.Equal(2, windowLenses.Select(lens => lens.SymbolId).Distinct().Count());
			Assert.All(windowLenses, lens => Assert.Equal(1, ReferenceLensesFor(x.Document, lens)));
		}
	}

	[Fact]
	public void AttributeAheadOfDeclaration_DoesNotCreateASecondLens()
	{
		var x = Open(WindowPreamble + WindowExtension);
		using (x.Fixture)
		{
			var lenses = ReferenceLenses(x.Document);

			// [UnsafeBody] sits on the line above the constructor; no lens may land on the attribute.
			foreach (var lens in lenses)
			{
				var text = x.Document.Text.GetText(lens.Range);
				Assert.DoesNotContain("UnsafeBody", text, StringComparison.Ordinal);
			}

			// No lens may start inside the constructor body.
			var source = WindowPreamble + WindowExtension;
			var ctorBody = source.IndexOf("Handle = CreateWindow", StringComparison.Ordinal);
			var bodyEnd = source.IndexOf("}", ctorBody, StringComparison.Ordinal);
			Assert.DoesNotContain(lenses, lens => lens.Range.Start > ctorBody && lens.Range.Start < bodyEnd);
		}
	}

	[Fact]
	public void UsesInBodies_ReceiveNoLens()
	{
		var x = Open(WindowPreamble + WindowExtension);
		using (x.Fixture)
		{
			var uses = new[] { "CreateWindow", "MakeContextCurrent", "ClearColor" };
			var selected = ReferenceLenses(x.Document).Select(lens => x.Document.Text.GetText(lens.Range)).ToList();

			foreach (var use in uses)
			{
				// Each name has exactly one lens and it is the declaration, never a use.
				Assert.Equal(1, selected.Count(name => name == use));
			}

			AssertNoDuplicateLenses(x.Document);
		}
	}

	[Fact]
	public void SingleMethodDeclaration_GetsExactlyOneReferenceLens()
	{
		const string source = """
			public nuint Add(nuint a, nuint b)
			{
			    return a + b;
			}

			int main()
			{
			    val nuint total = Add(1, 2);
			    return (int)total;
			}
			""";

		var x = Open(source);
		using (x.Fixture)
		{
			var add = ReferenceLenses(x.Document).Where(lens => x.Document.Text.GetText(lens.Range) == "Add").ToList();

			Assert.Equal(1, add.Count);
			AssertNoDuplicateLenses(x.Document);
		}
	}

	[Fact]
	public void AdjacentDeclarations_GetRangesThatDoNotOverlap()
	{
		const string source = """
			public nuint First()
			{
			    return 1;
			}

			public nuint Second()
			{
			    return 2;
			}
			""";

		var x = Open(source);
		using (x.Fixture)
		{
			var ranges = ReferenceLenses(x.Document).Select(lens => lens.Range).OrderBy(range => range.Start).ToList();

			for (var i = 1; i < ranges.Count; i++)
			{
				Assert.True(ranges[i].Start >= ranges[i - 1].End, $"lens {i} at {ranges[i].Start} overlaps the previous lens ending at {ranges[i - 1].End}");
			}
		}
	}

	[Fact]
	public void Recomputing_ProducesTheSameLensSet()
	{
		var x = Open(WindowPreamble + WindowExtension);
		using (x.Fixture)
		{
			var first = Describe(x.Document);
			var second = Describe(x.Document);
			var third = Describe(x.Document);

			Assert.Equal(first, second);
			Assert.Equal(first, third);
			AssertNoDuplicateLenses(x.Document);
		}
	}

	[Fact]
	public void TransientSyntaxError_ProducesNoDuplicateLenses()
	{
		var fixture = TempProject.Create(("Main.cvl", WindowPreamble + WindowExtension));
		using (fixture)
		{
			var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
			Assert.True(project.TryGetDocumentId(fixture.GetPath("Main.cvl"), out var documentId));
			var healthy = Describe(project.InitialSnapshot.GetDocument(documentId));

			// One stray character breaks the file; the document still has to answer coherently.
			var brokenSource = SourceText.From(WindowPreamble + WindowExtension.Replace(
				"MakeContextCurrent(Handle);",
				"MakeContextCurrent(Handle;",
				StringComparison.Ordinal));
			var broken = project.InitialSnapshot.WithDocument(documentId, brokenSource).GetDocument(documentId);

			var lenses = Describe(broken);
			Assert.Equal(lenses.Length, lenses.Distinct(StringComparer.Ordinal).Count());
			Assert.Equal(healthy.Length, healthy.Distinct(StringComparer.Ordinal).Count());
			AssertNoDuplicateLenses(broken);
		}
	}
}