using Cvolo.Compiler.Tooling;

namespace Cvolo.Tests.Tooling;

/// <summary>
/// Verifies the document-batched CodeLens query: a zero count is a real result, the declaration
/// itself is never a reference, a member lens is opt-in, and a document with N declarations is
/// served by one project-wide index instead of N scans.
/// </summary>
public sealed class CodeLensServiceTests
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

	private static ToolingCodeLensInfo? ReferenceLensFor(IReadOnlyList<ToolingCodeLensInfo> lenses, DocumentSnapshot document, string name)
	{
		// The reference lens sits on the declaration's own name, so the span must select exactly it.
		return lenses.FirstOrDefault(lens => lens.Kind == ToolingCodeLensKind.References
			&& document.Text.GetText(lens.Range) == name);
	}

	private static ToolingCodeLensInfo LayoutLensFor(IReadOnlyList<ToolingCodeLensInfo> lenses, DocumentSnapshot document, string name) =>
		lenses.First(lens => lens.Kind == ToolingCodeLensKind.Layout && document.Text.GetText(lens.Range) == name);

	private const string Baseline =
		"public struct Unused\n" +
		"{\n" +
		"}\n" +
		"\n" +
		"/// Adds two values.\n" +
		"public nuint Add(nuint a, nuint b)\n" +
		"{\n" +
		"    return a + b;\n" +
		"}\n" +
		"\n" +
		"public struct Header\n" +
		"{\n" +
		"    public byte Kind;\n" +
		"    public int Length;\n" +
		"}\n" +
		"\n" +
		"int main()\n" +
		"{\n" +
		"    val nuint total = Add(1, 2);\n" +
		"    val nuint other = Add(3, 4);\n" +
		"    return (int)total;\n" +
		"}\n";

	[Fact]
	public void UnreferencedDeclaration_ReportsAZeroCount()
	{
		var x = Open(("Main.cvl", Baseline));
		using (x.Fixture)
		{
			var lenses = x.Document.GetCodeLenses();

			var lens = ReferenceLensFor(lenses, x.Document, "Unused");
			Assert.NotNull(lens);
			Assert.Equal("0 references", lens!.Title);
			Assert.NotNull(lens.ReferenceCount);
			Assert.Equal(0, lens.ReferenceCount!.Count);
		}
	}

	[Fact]
	public void Declaration_IsNotCountedAsItsOwnReference()
	{
		var x = Open(("Main.cvl", Baseline));
		using (x.Fixture)
		{
			var lens = ReferenceLensFor(x.Document.GetCodeLenses(), x.Document, "Add");
			Assert.NotNull(lens);
			Assert.Equal("2 references", lens!.Title);
		}
	}

	[Fact]
	public void MemberLenses_AreOffByDefaultAndAppearWhenEnabled()
	{
		var x = Open(("Main.cvl", Baseline));
		using (x.Fixture)
		{
			var byDefault = x.Document.GetCodeLenses();
			Assert.DoesNotContain(byDefault, lens => x.Document.Text.GetText(lens.Range) == "Length");

			var enabled = x.Document.GetCodeLenses(ToolingCodeLensOptions.Default with { Members = true });
			var field = enabled.FirstOrDefault(lens => lens.Kind == ToolingCodeLensKind.References && x.Document.Text.GetText(lens.Range) == "Length");
			Assert.NotNull(field);
			Assert.Equal("0 references", field!.Title);
		}
	}

	[Fact]
	public void ReferenceLenses_CanBeDisabledEntirely()
	{
		var x = Open(("Main.cvl", Baseline));
		using (x.Fixture)
		{
			var lenses = x.Document.GetCodeLenses(ToolingCodeLensOptions.Default with { References = false });
			Assert.DoesNotContain(lenses, lens => lens.Kind == ToolingCodeLensKind.References);
		}
	}

	[Fact]
	public void StructDeclaration_ReportsItsSizeAlignmentAndPadding()
	{
		var x = Open(("Main.cvl", Baseline));
		using (x.Fixture)
		{
			var lens = LayoutLensFor(x.Document.GetCodeLenses(), x.Document, "Header");
			Assert.Equal("size 8 B · align 4 B · padding 3 B", lens.Title);
			Assert.Equal(8, lens.Layout!.Size);
			Assert.Equal(4, lens.Layout.Alignment);
			Assert.Equal(3, lens.Layout.PaddingSize);
		}
	}

	[Fact]
	public void LayoutLens_CanBeDisabled()
	{
		var x = Open(("Main.cvl", Baseline));
		using (x.Fixture)
		{
			var lenses = x.Document.GetCodeLenses(ToolingCodeLensOptions.Default with { Layout = false });
			Assert.DoesNotContain(lenses, lens => lens.Kind == ToolingCodeLensKind.Layout);
		}
	}

	[Fact]
	public void GetTypeLayoutAtPosition_ResolvesTheTypeUnderTheCursor()
	{
		var x = Open(("Main.cvl", Baseline));
		using (x.Fixture)
		{
			var onName = x.Snapshot.GetTypeLayoutAtPosition(x.Document.Id, At(Baseline, "public struct Header") + "public struct ".Length);
			Assert.NotNull(onName);
			Assert.Equal("Header", onName!.TypeDisplay);
			Assert.Equal(8, onName.Size);
			Assert.Equal([(0L, 1L), (4L, 4L)], onName.Members.Select(member => (member.Offset, member.Size)));
			Assert.Contains(onName.Padding, padding => padding.Kind == ToolingPaddingKind.Internal && padding.Size == 3 && padding.Offset == 1);
			Assert.Equal(3, onName.PaddingSize);
			Assert.Equal(5, onName.PayloadSize);
			Assert.False(string.IsNullOrEmpty(onName.TargetDisplay));

			// A position that binds to no type contributes nothing rather than a host-layout guess.
			Assert.Null(x.Snapshot.GetTypeLayoutAtPosition(x.Document.Id, At(Baseline, "return a + b;")));
			Assert.Throws<ArgumentOutOfRangeException>(() => x.Document.GetTypeLayoutAtPosition(-1));
		}
	}

	[Fact]
	public void LayoutOfAnUnresolvedType_IsNotInvented()
	{
		const string generic = "public struct Pair<T>\n{\n    public T First;\n    public T Second;\n}\n";
		var x = Open(("Main.cvl", generic));
		using (x.Fixture)
		{
			// A generic template has no single layout, so neither a lens nor an inspection is offered.
			Assert.Null(x.Snapshot.GetTypeLayoutAtPosition(x.Document.Id, At(generic, "struct Pair") + "struct ".Length));
			Assert.DoesNotContain(x.Document.GetCodeLenses(), lens => lens.Kind == ToolingCodeLensKind.Layout);
		}
	}

	[Fact]
	public void CrossFileReferences_AreCountedFromTheWholeProject()
	{
		const string types = "public struct Counter\n{\n    public int Total;\n}\n";
		const string usesSource = "public int Bump(Counter counter)\n{\n    return counter.Total + 1;\n}\n";
		const string mainSource =
			"int main()\n" +
			"{\n" +
			"    val Counter counter = Counter { Total: 0 };\n" +
			"    return Bump(counter);\n" +
			"}\n";
		var x = Open(("Types.cvl", types), ("Uses.cvl", usesSource), ("Main.cvl", mainSource));
		using (x.Fixture)
		{
			// Counter is named once per other file, and the declaration itself is excluded, so the
			// count is the three cross-file mentions and nothing more.
			var lens = ReferenceLensFor(x.Document.GetCodeLenses(), x.Document, "Counter");
			Assert.NotNull(lens);
			Assert.Equal("3 references", lens!.Title);

			var uses = x.Snapshot.GetDocument(x.Project.GetDocumentId("Uses.cvl"));
			Assert.Equal("1 reference", ReferenceLensFor(uses.GetCodeLenses(), uses, "Bump")!.Title);

			// A lens belongs to the declaration it annotates, so a field declared in Types.cvl is
			// annotated there and not in every file that mentions it. Only the member access in
			// Uses.cvl is a resolved occurrence of the field.
			var typesDocument = x.Snapshot.GetDocument(x.Project.GetDocumentId("Types.cvl"));
			Assert.Equal("1 reference", ReferenceLensFor(typesDocument.GetCodeLenses(ToolingCodeLensOptions.Default with { Members = true }), typesDocument, "Total")!.Title);
		}
	}

	[Fact]
	public void Overloads_AreCountedIndependently()
	{
		const string source =
			"public int Scale(int value) { return value; }\n" +
			"public int Scale(float value) { return (int)value; }\n" +
			"int main()\n" +
			"{\n" +
			"    val int a = Scale(1);\n" +
			"    val int b = Scale(1.5f);\n" +
			"    return a + b;\n" +
			"}\n";
		var x = Open(("Main.cvl", source));
		using (x.Fixture)
		{
			var intOverload = At(source, "public int Scale");
			var floatOverload = At(source, "public int Scale", intOverload + 1);
			var lenses = x.Document.GetCodeLenses()
				.Where(lens => lens.Kind == ToolingCodeLensKind.References)
				.ToDictionary(lens => lens.Range.Start, lens => lens.Title);

			// Two declarations share the name, so the counts are distinguished by the declaration
			// range rather than by the name alone: each overload is counted on its own.
			Assert.Equal("1 reference", lenses[intOverload + "public int ".Length]);
			Assert.Equal("1 reference", lenses[floatOverload + "public int ".Length]);
			Assert.Equal("0 references", lenses[At(source, "int main") + "int ".Length]);
		}
	}

	[Fact]
	public void AssociatedFunctionAndOperator_AreCountedLikeAnyOtherDeclaration()
	{
		const string source =
			"public struct Vec2 { public int X; public int Y; }\n" +
			"public extension Vec2\n" +
			"{\n" +
			"    public Vec2 operator +(Vec2 left, Vec2 right) { return left; }\n" +
			"    public int Magnitude(Vec2 value) { return value.X; }\n" +
			"}\n" +
			"int main()\n" +
			"{\n" +
			"    val Vec2 a = Vec2 { X: 1, Y: 2 };\n" +
			"    val Vec2 b = a + a;\n" +
			"    return b.Magnitude();\n" +
			"}\n";
		var x = Open(("Main.cvl", source));
		using (x.Fixture)
		{
			var lenses = x.Document.GetCodeLenses();
			Assert.NotNull(ReferenceLensFor(lenses, x.Document, "Magnitude"));
			Assert.NotNull(ReferenceLensFor(lenses, x.Document, "+"));
		}
	}

	[Fact]
	public void ExtensionBlock_Itself_GetsNoReferenceLens()
	{
		const string source =
			"public struct Vec2 { public int X; }\n" +
			"public extension Vec2 { public int Magnitude(Vec2 value) { return value.X; } }\n";
		var x = Open(("Main.cvl", source));
		using (x.Fixture)
		{
			var lens = x.Document.GetCodeLenses()
				.FirstOrDefault(candidate => candidate.Kind == ToolingCodeLensKind.References
					&& x.Document.Text.GetText(candidate.Range) == "Vec2"
					&& candidate.Range.Start == At(source, "extension Vec2 {") + "extension ".Length);

			Assert.Null(lens);
		}
	}

	[Fact]
	public void SameSpellingOnAnotherSymbol_IsNotCounted()
	{
		const string source =
			"public struct Header { public int Length; }\n" +
			"public struct Frame { public int Length; }\n" +
			"int main()\n" +
			"{\n" +
			"    val Header header = Header { Length: 1 };\n" +
			"    return header.Length;\n" +
			"}\n";
		var x = Open(("Main.cvl", source));
		using (x.Fixture)
		{
			var lenses = x.Document.GetCodeLenses(ToolingCodeLensOptions.Default with { Members = true });
			var headerLength = lenses.Single(lens => lens.Kind == ToolingCodeLensKind.References && lens.Range.Start == At(source, "public int Length;") + "public int ".Length);
			var frameLength = lenses.Single(lens => lens.Kind == ToolingCodeLensKind.References && lens.Range.Start == At(source, "public int Length;", At(source, "public int Length;") + 1) + "public int ".Length);

			Assert.Equal("1 reference", headerLength.Title);
			Assert.Equal("0 references", frameLength.Title);
		}
	}

	[Fact]
	public void ConstructorAndGlobal_AreCounted()
	{
		const string source =
			"public struct Slot\n" +
			"{\n" +
			"    public int Value;\n" +
			"}\n" +
			"public extension Slot\n" +
			"{\n" +
			"    public Slot(int value) { Value = value; }\n" +
			"}\n" +
			"public global nuint Limit = 8;\n" +
			"int main()\n" +
			"{\n" +
			"    val Slot slot = Slot(1);\n" +
			"    val nuint limit = Limit;\n" +
			"    return slot.Value + (int)limit;\n" +
			"}\n";
		var x = Open(("Main.cvl", source));
		using (x.Fixture)
		{
			var lenses = x.Document.GetCodeLenses();
			Assert.Empty(x.Document.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error));
			Assert.Equal("1 reference", ReferenceLensFor(lenses, x.Document, "Limit")!.Title);

			// The struct and its constructor share the name, so the constructor lens is selected by
			// its declaration position rather than by the name alone. Constructing the value names
			// the type, so the constructor itself has no reference at its use site.
			var byPosition = lenses
				.Where(lens => lens.Kind == ToolingCodeLensKind.References)
				.ToDictionary(lens => lens.Range.Start, lens => lens.Title);
			Assert.Equal("3 references", byPosition[At(source, "public struct Slot") + "public struct ".Length]);
			Assert.Equal("0 references", byPosition[At(source, "public Slot(int value)") + "public ".Length]);
		}
	}

	[Fact]
	public void UnsavedOverlay_UpdatesTheDerivedSnapshotAndLeavesTheBaselineIntact()
	{
		var x = Open(("Main.cvl", Baseline));
		using (x.Fixture)
		{
			var baseline = x.Document.GetCodeLenses();
			Assert.Equal("2 references", ReferenceLensFor(baseline, x.Document, "Add")!.Title);

			var edited = Baseline + "int Extra() { return (int)Add(5, 6); }\n";
			var advanced = x.Project.Advance(new Dictionary<string, string> { [x.Document.FilePath] = edited });
			var document = advanced.InitialSnapshot.GetDocument(x.Document.Id);
			var lenses = document.GetCodeLenses();

			Assert.Equal("3 references", ReferenceLensFor(lenses, document, "Add")!.Title);
			Assert.Equal("2 references", ReferenceLensFor(x.Document.GetCodeLenses(), x.Document, "Add")!.Title);
		}
	}

	[Fact]
	public void OneIndexServesEveryDeclarationInTheDocument()
	{
		var many = string.Concat(Enumerable.Range(0, 12).Select(i => $"public int F{i}() {{ return {i}; }}\n"));
		var x = Open(("Main.cvl", many));
		using (x.Fixture)
		{
			var lenses = x.Document.GetCodeLenses();
			Assert.Equal(12, lenses.Count(lens => lens.Kind == ToolingCodeLensKind.References));

			// The occurrence index is the single per-snapshot batch, so every declaration reuses it
			// instead of triggering its own project scan.
			Assert.Same(x.Snapshot.GetOccurrenceIndex(), x.Snapshot.GetOccurrenceIndex());
		}
	}

	[Fact]
	public void GetCodeLenses_RejectsAnUnknownDocument()
	{
		var x = Open(("Main.cvl", Baseline));
		using (x.Fixture)
		{
			Assert.Throws<KeyNotFoundException>(() => x.Snapshot.GetCodeLenses(default(DocumentId)));
		}
	}

	[Fact]
	public void ExportedFunction_GetsANativeInteropLens()
	{
		const string source =
			"expose extern \"C\"\n" +
			"{\n" +
			"    public int Render(int handle) { return handle; }\n" +
			"}\n" +
			"int main()\n" +
			"{\n" +
			"    return Render(0);\n" +
			"}\n";
		var x = Open(("Main.cvl", source));
		using (x.Fixture)
		{
			var lens = x.Document.GetCodeLenses().Single(candidate => candidate.Kind == ToolingCodeLensKind.NativeInterop);
			Assert.Equal(NativeLinkageDirection.Export, lens.NativeLinkage!.Direction);
			Assert.Equal("C", lens.NativeLinkage.CallingConvention);
			Assert.Equal("Render", lens.NativeLinkage.ExternalName);
		}
	}

	[Fact]
	public void Lenses_AreOrderedByPositionThenKind()
	{
		var x = Open(("Main.cvl", Baseline));
		using (x.Fixture)
		{
			var lenses = x.Document.GetCodeLenses();
			var ordered = lenses.OrderBy(lens => lens.Range.Start).ThenBy(lens => lens.Kind).ToList();
			Assert.Equal(ordered.Select(lens => (lens.Range, lens.Kind)), lenses.Select(lens => (lens.Range, lens.Kind)));
		}
	}
}
