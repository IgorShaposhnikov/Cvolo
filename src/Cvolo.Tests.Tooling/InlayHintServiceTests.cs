using Cvolo.Compiler.Tooling;

namespace Cvolo.Tests.Tooling;

/// <summary>
/// Verifies the inlay-hint query: every label comes from a resolved compiler fact, an argument that
/// already spells its parameter is not annotated, the synthetic receiver never becomes a hint, and
/// the dense categories stay off unless they are asked for.
/// </summary>
public sealed class InlayHintServiceTests
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

	private static IReadOnlyList<ToolingInlayHint> HintsIn(
		DocumentSnapshot document,
		string source,
		string needle,
		ToolingInlayHintOptions? options = null)
	{
		var start = At(source, needle);

		Assert.True(start >= 0, $"the source does not contain '{needle}'");

		return document.GetInlayHints(start, 0, options);
	}

	private static IReadOnlyList<ToolingInlayHint> OfKind(IReadOnlyList<ToolingInlayHint> hints, ToolingInlayHintKind kind) =>
		[.. hints.Where(hint => hint.Kind == kind)];

	private static ToolingInlayHint SingleOfKind(IReadOnlyList<ToolingInlayHint> hints, ToolingInlayHintKind kind) =>
		Assert.Single(OfKind(hints, kind));

	private const string TypesAndParameters =
		"public struct Vec\n" +
		"{\n" +
		"    public int X;\n" +
		"    public int Y;\n" +
		"}\n" +
		"\n" +
		"public extension Vec\n" +
		"{\n" +
		"    public int .Sum(int left, int right)\n" +
		"    {\n" +
		"        return left + right;\n" +
		"    }\n" +
		"\n" +
		"    public int Scaled(int factor)\n" +
		"    {\n" +
		"        return X * factor;\n" +
		"    }\n" +
		"\n" +
		"    public int Peek(ref this)\n" +
		"    {\n" +
		"        return this.X;\n" +
		"    }\n" +
		"}\n" +
		"\n" +
		"int main()\n" +
		"{\n" +
		"    val Vec v = Vec { X: 1, Y: 2 };\n" +
		"    val int a = 2;\n" +
		"    val b = 3;\n" +
		"    val int total = Vec.Sum(a, b);\n" +
		"    val int scaled = v.Scaled(4);\n" +
		"    val int peeked = v.Peek();\n" +
		"    return total + scaled + peeked;\n" +
		"}\n";

	[Fact]
	public void InferredLocalType_ComesFromTheCompilerNotTheSource()
	{
		var x = Open(("Main.cvl", TypesAndParameters));
		using (x.Fixture)
		{
			var hint = SingleOfKind(HintsIn(x.Document, TypesAndParameters, "val b = 3;"), ToolingInlayHintKind.Type);

			// The label sits immediately after the declared name, so `val b: int = 3;` reads naturally.
			Assert.Equal(": int", hint.Label);
			Assert.Equal(At(TypesAndParameters, "val b") + "val b".Length, hint.Position);
			Assert.False(hint.PaddingLeft);
		}
	}

	[Fact]
	public void ExplicitlyTypedLocal_GetsNoTypeHint()
	{
		var x = Open(("Main.cvl", TypesAndParameters));
		using (x.Fixture)
		{
			Assert.Empty(OfKind(HintsIn(x.Document, TypesAndParameters, "val int a = 2;"), ToolingInlayHintKind.Type));
		}
	}

	[Fact]
	public void ArgumentPositions_GetTheParameterNameTheyBindTo()
	{
		var x = Open(("Main.cvl", TypesAndParameters));
		using (x.Fixture)
		{
			var hints = OfKind(HintsIn(x.Document, TypesAndParameters, "Vec.Sum(a, b)"), ToolingInlayHintKind.Parameter);

			Assert.Equal(2, hints.Count);
			Assert.Equal("left: ", hints[0].Label);
			Assert.Equal("right: ", hints[1].Label);
			Assert.Equal(At(TypesAndParameters, "Sum(a, b)") + "Sum(".Length, hints[0].Position);
			Assert.Equal(At(TypesAndParameters, "Sum(a, b)") + "Sum(a, ".Length, hints[1].Position);
		}
	}

	[Fact]
	public void ArgumentThatAlreadySpellsItsParameter_IsNotHinted()
	{
		const string Source =
			"public struct Vec\n" +
			"{\n" +
			"    public int X;\n" +
			"}\n" +
			"\n" +
			"public extension Vec\n" +
			"{\n" +
			"    public int .Sum(int left, int right)\n" +
			"    {\n" +
			"        return left + right;\n" +
			"    }\n" +
			"}\n" +
			"\n" +
			"int main()\n" +
			"{\n" +
			"    val int left = 2;\n" +
			"    val int right = 3;\n" +
			"    val int total = Vec.Sum(left, right);\n" +
			"    return total;\n" +
			"}\n";

		var x = Open(("Main.cvl", Source));
		using (x.Fixture)
		{
			// `left` and `right` are the parameter names, so a label would only add noise.
			Assert.Empty(OfKind(HintsIn(x.Document, Source, "Vec.Sum(left, right)"), ToolingInlayHintKind.Parameter));
		}
	}

	[Fact]
	public void InstanceCall_AnnotatesArgumentsWithoutInventingAReceiver()
	{
		var x = Open(("Main.cvl", TypesAndParameters));
		using (x.Fixture)
		{
			var hints = OfKind(HintsIn(x.Document, TypesAndParameters, "v.Scaled(4)"), ToolingInlayHintKind.Parameter);

			// The synthetic `this` is parameter zero of the registered symbol, and it must not
			// shift the argument onto it or produce a label of its own.
			var hint = Assert.Single(hints);
			Assert.Equal("factor: ", hint.Label);
			Assert.Equal(At(TypesAndParameters, "Scaled(4)") + "Scaled(".Length, hint.Position);
		}
	}

	[Fact]
	public void ReceiverMutability_UsesTheFinalCompilerClassification()
	{
		var x = Open(("Main.cvl", TypesAndParameters));
		using (x.Fixture)
		{
			// `Scaled` omits the contract and never writes through the receiver, so the compiler
			// settled on an immutable receiver.
			var scaled = SingleOfKind(HintsIn(x.Document, TypesAndParameters, "public int Scaled(int factor)"), ToolingInlayHintKind.ReceiverMutability);

			Assert.Equal("ref this", scaled.Label);
			Assert.Equal(At(TypesAndParameters, "public int Scaled(int factor)") + "public int Scaled(int factor)".Length, scaled.Position);
			Assert.True(scaled.PaddingLeft);
		}
	}

	[Fact]
	public void ReceiverMutability_AnExplicitContractIsNeverAnnotated()
	{
		var x = Open(("Main.cvl", TypesAndParameters));
		using (x.Fixture)
		{
			Assert.Empty(OfKind(HintsIn(x.Document, TypesAndParameters, "public int Peek(ref this)"), ToolingInlayHintKind.ReceiverMutability));
		}
	}

	[Fact]
	public void ReceiverMutability_NeverAppliesToAnAssociatedFunction()
	{
		var x = Open(("Main.cvl", TypesAndParameters));
		using (x.Fixture)
		{
			Assert.Empty(OfKind(HintsIn(x.Document, TypesAndParameters, "public int .Sum(int left, int right)"), ToolingInlayHintKind.ReceiverMutability));
		}
	}

	[Fact]
	public void Hints_AreRestrictedToTheRequestedRange()
	{
		var x = Open(("Main.cvl", TypesAndParameters));
		using (x.Fixture)
		{
			var callStart = At(TypesAndParameters, "Vec.Sum(a, b)");
			var callEnd = At(TypesAndParameters, "val int scaled");

			var hints = x.Document.GetInlayHints(callStart, callEnd - callStart);

			// Only the two argument labels live in the call; the local type hints before it and the
			// receiver hints in the extension block are outside the range.
			Assert.NotEmpty(hints);
			Assert.All(hints, hint => Assert.InRange(hint.Position, callStart, callEnd));
			Assert.All(hints, hint => Assert.Equal(ToolingInlayHintKind.Parameter, hint.Kind));
		}
	}

	[Fact]
	public void DenseCategories_AreOffByDefault()
	{
		const string Source =
			"public enum Mode\n" +
			"{\n" +
			"    Fast,\n" +
			"    Slow\n" +
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
			"    return 0;\n" +
			"}\n";

		var x = Open(("Main.cvl", Source));
		using (x.Fixture)
		{
			var hints = x.Document.GetInlayHints(0, Source.Length);

			Assert.DoesNotContain(hints, hint => hint.Kind == ToolingInlayHintKind.Layout);
			Assert.DoesNotContain(hints, hint => hint.Kind == ToolingInlayHintKind.EnumValue);
			Assert.DoesNotContain(hints, hint => hint.Kind == ToolingInlayHintKind.GenericArgument);
		}
	}

	[Fact]
	public void FieldLayout_HasOffsetSizeAndOnlyPrecedingPadding()
	{
		const string Source =
			"public struct Header\n" +
			"{\n" +
			"    public byte Kind;\n" +
			"    public int Length;\n" +
			"}\n" +
			"\n" +
			"int main()\n" +
			"{\n" +
			"    return 0;\n" +
			"}\n";

		var x = Open(("Main.cvl", Source));
		using (x.Fixture)
		{
			var options = ToolingInlayHintOptions.Default with { Layout = true };
			var hints = x.Document.GetInlayHints(0, Source.Length, options)
				.Where(hint => hint.Kind == ToolingInlayHintKind.Layout)
				.ToArray();

			Assert.Equal(2, hints.Length);
			Assert.Equal("offset 0 | size 1 | align 1", hints[0].Label);
			Assert.Equal(At(Source, "public byte Kind;") + "public byte Kind;".Length, hints[0].Position);

			// The three padding bytes sit *before* `Length`; the trailing padding after the last
			// field belongs to the layout lens, not to every field.
			Assert.Equal("offset 4 | size 4 | align 4 | pad 3 before", hints[1].Label);
			Assert.Equal(At(Source, "public int Length;") + "public int Length;".Length, hints[1].Position);

			// The label is text, but the numbers behind it are also carried structurally so a client
			// can render them in another notation without parsing the label.
			Assert.NotNull(hints[1].FieldLayout);
			Assert.Equal("Header", hints[1].FieldLayout!.ContainingTypeDisplay);
			Assert.Equal("Length", hints[1].FieldLayout.FieldName);
			Assert.Equal((4L, 4L, 4L, 3L), (hints[1].FieldLayout.Offset, hints[1].FieldLayout.Size, hints[1].FieldLayout.Alignment, hints[1].FieldLayout.PaddingBefore));

			// The first field starts the type, so it has no padding before it and its label says so
			// by omission.
			Assert.Equal(0, hints[0].FieldLayout!.PaddingBefore);
		}
	}

	[Fact]
	public void EnumValue_ShowsTheCompilerSelectedNumber()
	{
		const string Source =
			"public enum Mode\n" +
			"{\n" +
			"    Fast,\n" +
			"    Slow\n" +
			"}\n" +
			"\n" +
			"int main()\n" +
			"{\n" +
			"    return 0;\n" +
			"}\n";

		var x = Open(("Main.cvl", Source));
		using (x.Fixture)
		{
			var options = ToolingInlayHintOptions.Default with { EnumValues = true };
			var hints = x.Document.GetInlayHints(0, Source.Length, options)
				.Where(hint => hint.Kind == ToolingInlayHintKind.EnumValue)
				.ToArray();

			Assert.Equal(2, hints.Length);
			Assert.Equal("= 0", hints[0].Label);
			Assert.Equal("= 1", hints[1].Label);
		}
	}

	[Fact]
	public void GenericArguments_AreNeverGuessed()
	{
		const string Source =
			"public struct Pair<T>\n" +
			"{\n" +
			"    public T First;\n" +
			"}\n" +
			"\n" +
			"int main()\n" +
			"{\n" +
			"    return 0;\n" +
			"}\n";

		var x = Open(("Main.cvl", Source));
		using (x.Fixture)
		{
			// The compiler does not publish an authoritative resolved substitution, so the option is
			// honoured as a no-op rather than satisfied with a guess.
			var options = ToolingInlayHintOptions.Default with { GenericArguments = true };
			var hints = x.Document.GetInlayHints(0, Source.Length, options);

			Assert.DoesNotContain(hints, hint => hint.Kind == ToolingInlayHintKind.GenericArgument);
		}
	}

	[Fact]
	public void UnresolvedCall_ContributesNoParameterHints()
	{
		const string Source =
			"int main()\n" +
			"{\n" +
			"    val int value = Missing(1, 2);\n" +
			"    return value;\n" +
			"}\n";

		var x = Open(("Main.cvl", Source));
		using (x.Fixture)
		{
			Assert.Empty(OfKind(HintsIn(x.Document, Source, "Missing(1, 2)"), ToolingInlayHintKind.Parameter));
		}
	}

	[Fact]
	public void Hints_AreOrderedByPosition()
	{
		var x = Open(("Main.cvl", TypesAndParameters));
		using (x.Fixture)
		{
			var hints = x.Document.GetInlayHints(0, TypesAndParameters.Length);
			var positions = hints.Select(hint => hint.Position).ToArray();

			Assert.NotEmpty(positions);
			Assert.Equal([.. positions.OrderBy(position => position)], positions);
		}
	}
}
