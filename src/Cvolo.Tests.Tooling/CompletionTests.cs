using Cvolo.Compiler.Tooling;
using Cvolo.Compiler.Tooling.Completion;
using Cvolo.Compiler.Tooling.Internal;
using AnalysisKind = Cvolo.Analysis.Completion.CompletionKind;

namespace Cvolo.Tests.Tooling;

public sealed class CompletionTests
{
	private static (string Source, int Position) SplitCursor(string sourceWithMarker)
	{
		var position = sourceWithMarker.IndexOf('|');
		Assert.True(position >= 0, "Test fixture must contain a '|' cursor marker.");
		return (sourceWithMarker.Remove(position, 1), position);
	}

	private static CompletionResult Complete(string sourceWithMarker)
	{
		var (source, position) = SplitCursor(sourceWithMarker);
		using var fixture = TempProject.Create(("Main.cvl", source));
		return CompleteCore(fixture, position);
	}

	private static CompletionResult Complete(string sourceWithMarker, params (string File, string Source)[] otherFiles)
	{
		var (source, position) = SplitCursor(sourceWithMarker);
		var docs = new (string File, string Source)[otherFiles.Length + 1];
		docs[0] = ("Main.cvl", source);
		Array.Copy(otherFiles, 0, docs, 1, otherFiles.Length);
		using var fixture = TempProject.Create(docs);
		return CompleteCore(fixture, position);
	}

	private static CompletionResult CompleteCore(TempProject fixture, int position)
	{
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		var document = project.InitialSnapshot.GetDocument(project.GetDocumentId("Main.cvl"));
		return document.GetCompletions(position);
	}

	private static bool Contains(CompletionResult result, string label, CompletionKind kind)
	{
		return result.Candidates.Any(c => c.Label == label && c.Kind == kind);
	}

	private static string ApplyCandidate(string sourceWithMarker, string label, CompletionKind kind)
	{
		var (source, position) = SplitCursor(sourceWithMarker);
		using var fixture = TempProject.Create(("Main.cvl", source));
		var result = CompleteCore(fixture, position);
		var candidate = Assert.Single(result.Candidates, c => c.Label == label && c.Kind == kind);

		return source.Remove(result.ReplacementRange.Start, result.ReplacementRange.Length)
			.Insert(result.ReplacementRange.Start, candidate.InsertText);
	}

	[Fact]
	public void Parameters_AreOfferedInFunctionBody()
	{
		var result = Complete("void foo(int a, int b) { | }\n");

		Assert.True(Contains(result, "a", CompletionKind.Parameter));
		Assert.True(Contains(result, "b", CompletionKind.Parameter));
		Assert.True(Contains(result, "return", CompletionKind.Keyword));
	}

	[Fact]
	public void Locals_DeclaredAfterCursor_AreNotOffered()
	{
		var result = Complete("int main() {\n    |\n    val int z = 1;\n}\n");

		Assert.DoesNotContain(result.Candidates, c => c.Label == "z");
	}

	[Fact]
	public void Local_IsNotOfferedInsideItsOwnInitializer()
	{
		var result = Complete("int foo() { return 0; }\nint main() {\n    val y = foo(|);\n    return 0;\n}\n");

		Assert.True(Contains(result, "foo", CompletionKind.Function));
		Assert.DoesNotContain(result.Candidates, c => c.Label == "y");
	}

	[Fact]
	public void ForEachItem_IsOfferedInsideBody()
	{
		var result = Complete("int main() {\n    val int[] items;\n    foreach (val item in items) {\n        |\n    }\n    return 0;\n}\n");

		Assert.True(Contains(result, "item", CompletionKind.Local));
	}

	[Fact]
	public void StructFields_AreOfferedWithoutKeywordCandidates()
	{
		var result = Complete("struct Point { int color; int count; }\nint main() {\n    val Point p;\n    return p.c|;\n}\n");

		Assert.True(Contains(result, "color", CompletionKind.StructField));
		Assert.True(Contains(result, "count", CompletionKind.StructField));
		Assert.DoesNotContain(result.Candidates, c => c.Kind == CompletionKind.Keyword);
		Assert.Equal(1, result.ReplacementRange.Length);
	}

	[Fact]
	public void PrivateFields_FromOtherFile_AreFiltered()
	{
		var result = Complete(
			"int main() {\n    val Hidden h;\n    return h.o|;\n}\n",
			("Hidden.cvl", "struct Hidden {\n    private int secret;\n    public int open;\n}\n"));

		Assert.True(Contains(result, "open", CompletionKind.StructField));
		Assert.DoesNotContain(result.Candidates, c => c.Label == "secret");
	}

	[Fact]
	public void UnionVariants_AreOffered()
	{
		var result = Complete("union Result { int Status; string Message; }\nint main() {\n    val Result r = default(Result);\n    return r.S|;\n}\n");

		Assert.True(Contains(result, "Status", CompletionKind.UnionVariant));
		Assert.DoesNotContain(result.Candidates, c => c.Label == "Message");
	}

	[Fact]
	public void EnumTypeNameReceiver_OffersVariants()
	{
		var result = Complete("enum Color { Red, Green, Blue }\nint main() {\n    return Color.R|;\n}\n");

		Assert.True(Contains(result, "Red", CompletionKind.EnumVariant));
		Assert.DoesNotContain(result.Candidates, c => c.Label == "Green");
		Assert.DoesNotContain(result.Candidates, c => c.Label == "Blue");
	}

	[Fact]
	public void EnumValueReceiver_OffersNothing()
	{
		var result = Complete("enum Color { Red, Green, Blue }\nint main() {\n    val Color c = Color.Red;\n    return c.fi|;\n}\n");

		Assert.Empty(result.Candidates);
	}

	[Fact]
	public void ExtensionMethods_AreOfferedOnStructReceiver()
	{
		var result = Complete("struct Point { int x; int y; }\nextension Point {\n    int Area() { return 0; }\n}\nint main() {\n    val Point p;\n    return p.A|;\n}\n");

		Assert.True(Contains(result, "Area", CompletionKind.Method));
	}

	[Fact]
	public void ExtensionMethods_AreOfferedOnEnumValueReceiver()
	{
		var result = Complete(
			"enum Color { Red, Green }\n" +
			"extension Color { int Value() { return 0; } }\n" +
			"int main() { val Color c = Color.Red; return c.Va|; }\n");

		Assert.True(Contains(result, "Value", CompletionKind.Method));
	}

	[Fact]
	public void SliceLength_IsOffered()
	{
		var result = Complete("int main() {\n    val int[] arr = { 1, 2, 3 };\n    return arr.L|;\n}\n");

		Assert.True(Contains(result, "Length", CompletionKind.ArrayLength));
	}

	[Fact]
	public void Globals_AreOffered()
	{
		var result = Complete("global int GLOBAL_COUNT = 10;\nint main() {\n    return GLOBAL_|;\n}\n");

		Assert.True(Contains(result, "GLOBAL_COUNT", CompletionKind.Global));
	}

	[Fact]
	public void ExpressionContext_OffersExpressionKeywordsOnly()
	{
		var result = Complete("int main() {\n    val int x = 1;\n    return x + foo(|);\n}\n");

		Assert.True(Contains(result, "true", CompletionKind.Keyword));
		Assert.True(Contains(result, "nameof", CompletionKind.Keyword));
		Assert.True(Contains(result, "default", CompletionKind.Keyword));
		Assert.DoesNotContain(result.Candidates, c => c.Label == "return");
		Assert.DoesNotContain(result.Candidates, c => c.Label == "val");
	}

	[Fact]
	public void TypeContext_IsNarrowedToTypesAndKeywords()
	{
		var result = Complete("struct Point { int x; }\nint Pointy() { return 0; }\nint main() {\n    val Po|int p;\n    return 0;\n}\n");

		Assert.True(Contains(result, "Point", CompletionKind.Type));
		Assert.DoesNotContain(result.Candidates, c => c.Label == "Pointy");
	}

	[Fact]
	public void GenericFunctionTemplate_IsOfferedAsFunction()
	{
		var result = Complete("int Max<T>(int a, int b) { return 0; }\nint main() { | }\n");

		Assert.True(Contains(result, "Max", CompletionKind.Function));
		Assert.DoesNotContain(result.Candidates, c => c.Label == "Max" && c.Kind == CompletionKind.Type);
	}

	[Fact]
	public void GenericFunctionTemplate_DoesNotSurviveTypeOnlyContext()
	{
		var result = Complete("int Max<T>(int a, int b) { return 0; }\nint main() {\n    val Max| p;\n    return 0;\n}\n");

		Assert.DoesNotContain(result.Candidates, c => c.Label == "Max");
	}

	[Fact]
	public void DeclarationContext_OffersNamespaces()
	{
		var result = Complete("namespace Foo { global int x; }\n|\n");

		Assert.True(Contains(result, "Foo", CompletionKind.Namespace));
		Assert.True(Contains(result, "struct", CompletionKind.Keyword));
		Assert.True(Contains(result, "global", CompletionKind.Keyword));
		Assert.True(Contains(result, "x", CompletionKind.Global));
	}

	[Fact]
	public void Comment_ProducesEmptyCandidatesWithPrefixSpan()
	{
		var result = Complete("int main() {\n    val int x = 1;\n    return x; // a|bc\n}\n");

		Assert.Empty(result.Candidates);
		Assert.Equal(1, result.ReplacementRange.Length);
	}

	[Fact]
	public void StringLiteral_ProducesEmptyCandidatesWithPrefixSpan()
	{
		var result = Complete("int main() {\n    val string s = \"hello wo|rld\";\n    return 0;\n}\n");

		Assert.Empty(result.Candidates);
		Assert.Equal(2, result.ReplacementRange.Length);
	}

	[Fact]
	public void MidTokenCursor_ReplacesWholeTokenAndFiltersByPrefix()
	{
		const string source = "int main() {\n    val int x = 1;\n    ret|urn x;\n}\n";
		var result = Complete(source);

		Assert.Equal(new TextSpan(source.IndexOf("ret", StringComparison.Ordinal), "return".Length), result.ReplacementRange);
		Assert.DoesNotContain(result.Candidates, c => c.Label == "val");
		Assert.True(Contains(result, "return", CompletionKind.Keyword));
		Assert.Equal(source.Replace("|", string.Empty, StringComparison.Ordinal), ApplyCandidate(source, "return", CompletionKind.Keyword));
	}

	[Fact]
	public void MidTokenTypeCompletion_ReplacesWholeIdentifier()
	{
		const string source = "struct Point { int x; }\nint main() { val Po|int p; return 0; }\n";
		var result = Complete(source);

		Assert.Equal(new TextSpan(source.IndexOf("Po|int", StringComparison.Ordinal), "Point".Length), result.ReplacementRange);
		Assert.True(Contains(result, "Point", CompletionKind.Type));
		Assert.Equal(source.Replace("|", string.Empty, StringComparison.Ordinal), ApplyCandidate(source, "Point", CompletionKind.Type));
	}

	[Fact]
	public void CursorAtTokenStart_UsesEmptyPrefixButReplacesWholeToken()
	{
		const string source = "struct Point { int x; }\nint main() { val |Point p; return 0; }\n";
		var result = Complete(source);

		Assert.Equal(new TextSpan(source.IndexOf("|Point", StringComparison.Ordinal), "Point".Length), result.ReplacementRange);
		Assert.True(Contains(result, "Point", CompletionKind.Type));
		Assert.Equal(source.Replace("|", string.Empty, StringComparison.Ordinal), ApplyCandidate(source, "Point", CompletionKind.Type));
	}

	[Fact]
	public void CursorSeparatedFromPreviousIdentifierByWhitespace_UsesEmptyInsertionSpan()
	{
		const string source = "int main() { val int value = 1; return value   |; }\n";
		var position = source.IndexOf('|');
		var result = Complete(source);

		Assert.Equal(new TextSpan(position, 0), result.ReplacementRange);
		Assert.True(Contains(result, "value", CompletionKind.Local));
	}

	[Fact]
	public void CursorAtEndOfFile_ReportsZeroLengthSpan()
	{
		var result = Complete("int main() { return 0; }|");

		Assert.Equal(0, result.ReplacementRange.Length);
		Assert.NotEmpty(result.Candidates);
	}

	[Fact]
	public void UnboundReceiver_ProducesEmptyCandidates()
	{
		var result = Complete("int main() {\n    value.|;\n}\n");

		Assert.Empty(result.Candidates);
		Assert.Equal(0, result.ReplacementRange.Length);
	}

	[Fact]
	public void ParseErrorInOtherDocument_DoesNotSuppressCompletion()
	{
		var result = Complete("int main() { | }\n", ("Lib.cvl", "int broken( { }\n"));

		Assert.NotEmpty(result.Candidates);
	}

	[Fact]
	public void StatementContext_OffersDeterministicOrderedCandidates()
	{
		const string source =
			"struct Point { int x; int y; }\n" +
			"global int g;\n" +
			"int helper() { return 0; }\n" +
			"int main() { val int x = 1; | }\n";

		var result = Complete(source);
		var labels = string.Join("|", result.Candidates.Select(c => c.Label));
		const string expected =
			"x|g|helper|main|Point|" +
			"return|val|var|ref|refvar|if|while|for|foreach|switch|defer|try|break|continue|unsafe|" +
			"asm|nameof|typeof|heap|true|false|null|void|default|panic";

		Assert.Equal(expected, labels);
		Assert.All(result.Candidates, c => Assert.Equal(c.Label, c.InsertText));
	}

	[Fact]
	public void GetCompletions_SnapshotIsImmutableAcrossDocuments()
	{
		const string source =
			"global int alpha;\n" +
			"global int beta;\n" +
			"int main() { | }\n";
		var positionA = source.IndexOf('|');
		var sourceA = source.Remove(positionA, 1);

		// Deriving a new snapshot B from A must not mutate A: rename a global so the candidate
		// surface differs while the cursor position stays valid (each doc uses its own marker).
		const string mutated =
			"global int alpha;\n" +
			"global int betaRenamed;\n" +
			"int main() { | }\n";
		var positionB = mutated.IndexOf('|');
		var mutatedSource = mutated.Remove(positionB, 1);

		using var fixture = TempProject.Create(("Main.cvl", sourceA));
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		var documentId = project.GetDocumentId("Main.cvl");

		var snapshotA = project.InitialSnapshot;
		var documentA = snapshotA.GetDocument(documentId);
		var resultA = documentA.GetCompletions(positionA);

		var snapshotB = snapshotA.WithDocument(documentId, SourceText.From(mutatedSource));
		var resultB = snapshotB.GetDocument(documentId).GetCompletions(positionB);

		Assert.Contains(resultB.Candidates, c => c.Label == "betaRenamed");
		Assert.DoesNotContain(resultB.Candidates, c => c.Label == "beta");

		var resultAReprobed = documentA.GetCompletions(positionA);
		Assert.Equal(resultA.ReplacementRange, resultAReprobed.ReplacementRange);
		Assert.True(resultA.Candidates.SequenceEqual(resultAReprobed.Candidates));
		Assert.DoesNotContain(resultA.Candidates, c => c.Label == "betaRenamed");
	}

	[Fact]
	public void GetCompletions_OutOfRangePosition_Throws()
	{
		using var fixture = TempProject.Create(("Main.cvl", "int main() { return 0; }\n"));
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		var document = project.InitialSnapshot.GetDocument(project.GetDocumentId("Main.cvl"));

		Assert.Throws<ArgumentOutOfRangeException>(() => document.GetCompletions(-1));
		Assert.Throws<ArgumentOutOfRangeException>(() => document.GetCompletions(document.Text.Length + 1));
	}

	[Fact]
	public void ChainedMemberReceiver_OffersNestedFields()
	{
		var result = Complete(
			"struct Point { int absc; int ord; }\n" +
			"struct Line { Point start; Point end; }\n" +
			"int main() {\n" +
			"    val Line l;\n" +
			"    return l.start.ab|;\n" +
			"}\n");

		Assert.True(Contains(result, "absc", CompletionKind.StructField));
		Assert.DoesNotContain(result.Candidates, c => c.Label == "ord");
	}

	[Fact]
	public void ExtensionMethod_FromUnreachableNamespace_NotOffered()
	{
		var result = Complete(
			"namespace App;\nusing ShapeLib;\nint main() { val ShapeLib.Shape s; return s.A|; }\n",
			("ShapeLib.cvl", "namespace ShapeLib;\nstruct Shape { int sides; }\n"),
			("ShapeExtLib.cvl", "namespace ShapeExtLib;\nusing ShapeLib;\nextension Shape { int Area() { return 0; } }\n"));

		Assert.DoesNotContain(result.Candidates, c => c.Label == "Area");
		Assert.DoesNotContain(result.Candidates, c => c.Kind == CompletionKind.Method);
	}

	[Fact]
	public void ExtensionMethod_IsOfferedAfterImportingNamespace()
	{
		var result = Complete(
			"namespace App;\nusing ShapeLib;\nusing ShapeExtLib;\nint main() { val ShapeLib.Shape s; return s.A|; }\n",
			("ShapeLib.cvl", "namespace ShapeLib;\nstruct Shape { int sides; }\n"),
			("ShapeExtLib.cvl", "namespace ShapeExtLib;\nusing ShapeLib;\nextension Shape { int Area() { return 0; } }\n"));

		Assert.True(Contains(result, "Area", CompletionKind.Method));
	}

	[Fact]
	public void ExtensionMethod_InCurrentNamespace_IsOfferedForImportedReceiverType()
	{
		var result = Complete(
			"namespace App;\nusing ShapeLib;\nint main() { val ShapeLib.Shape s; return s.L|; }\n",
			("ShapeLib.cvl", "namespace ShapeLib;\nstruct Shape { int sides; }\n"),
			("AppExtensions.cvl", "namespace App;\nusing ShapeLib;\nextension Shape { int LocalValue() { return 0; } }\n"));

		Assert.True(Contains(result, "LocalValue", CompletionKind.Method));
	}

	[Fact]
	public void CatchBindingVariable_IsOfferedInsideCatchBody()
	{
		var result = Complete(
			"[Error]\n" +
			"union Failure { int Code; }\n" +
			"int main() {\n" +
			"    try { return 0; } catch (Failure f) { |return 0; }\n" +
			"}\n");

		Assert.True(Contains(result, "f", CompletionKind.Local));
		Assert.True(Contains(result, "return", CompletionKind.Keyword));
	}

	[Fact]
	public void GenericStructTemplate_IsOfferedAsTypeOnlyOnce()
	{
		var result = Complete("struct Pair<T> { T item; }\nint main() { | }\n");

		Assert.Equal(1, result.Candidates.Count(c => c.Label == "Pair" && c.Kind == CompletionKind.Type));
	}

	[Fact]
	public void DeterministicOrdering_IsIndependentOfDeclarationOrder()
	{
		const string orderA =
			"struct StructA { int f; }\n" +
			"union Result { int Status; string Message; }\n" +
			"enum Color { Red, Green }\n" +
			"alias Alias = int;\n" +
			"int main() { val int x = 1; | }\n";
		const string orderB =
			"alias Alias = int;\n" +
			"enum Color { Red, Green }\n" +
			"union Result { int Status; string Message; }\n" +
			"struct StructA { int f; }\n" +
			"int main() { val int x = 1; | }\n";

		var labelsA = string.Join("|", Complete(orderA).Candidates.Select(c => c.Label));
		var labelsB = string.Join("|", Complete(orderB).Candidates.Select(c => c.Label));

		Assert.Equal(labelsA, labelsB);
		const string expected =
			"x|main|Alias|Color|Result|StructA|" +
			"return|val|var|ref|refvar|if|while|for|foreach|switch|defer|try|break|continue|unsafe|" +
			"asm|nameof|typeof|heap|true|false|null|void|default|panic";
		Assert.Equal(expected, labelsA);
	}

	[Fact]
	public void FreeFunction_IsNotSuppressedBySameLeafTypeInUnrelatedNamespace()
	{
		var result = Complete(
			"using FuncLib;\nint main() { return Wi|(); }\n",
			("Functions.cvl", "namespace FuncLib;\npublic int Widget() { return 1; }\n"),
			("Types.cvl", "namespace TypeLib;\npublic struct Widget { int value; }\n"));

		Assert.True(Contains(result, "Widget", CompletionKind.Function));
	}

	[Fact]
	public void GenericFreeFunction_IsNotSuppressedBySameLeafTypeInUnrelatedNamespace()
	{
		var result = Complete(
			"using FuncLib;\nint main() { return Ma|<int>(1); }\n",
			("Functions.cvl", "namespace FuncLib;\npublic int Map<T>(int value) { return value; }\n"),
			("Types.cvl", "namespace TypeLib;\npublic struct Map<T> { T value; }\n"));

		Assert.True(Contains(result, "Map", CompletionKind.Function));
	}

	[Fact]
	public void FunctionOverloadVisibility_DoesNotDependOnDeclarationOrder()
	{
		const string main = "using Lib;\nint main() { return Pi|(); }\n";
		const string privateFirst =
			"namespace Lib;\n" +
			"private int Pick(int value) { return value; }\n" +
			"public int Pick(string value) { return 1; }\n";
		const string publicFirst =
			"namespace Lib;\n" +
			"public int Pick(string value) { return 1; }\n" +
			"private int Pick(int value) { return value; }\n";

		var first = Complete(main, ("Lib.cvl", privateFirst));
		var second = Complete(main, ("Lib.cvl", publicFirst));

		Assert.True(Contains(first, "Pick", CompletionKind.Function));
		Assert.True(Contains(second, "Pick", CompletionKind.Function));
	}

	[Fact]
	public void ExtensionOverloadVisibility_DoesNotDependOnDeclarationOrder()
	{
		const string main =
			"using Models;\n" +
			"using Ext;\n" +
			"int main() { val Point p; return p.Mi|; }\n";
		const string model = "namespace Models;\npublic struct Point { int x; }\n";
		const string privateFirst =
			"namespace Ext;\nusing Models;\n" +
			"public extension Point {\n" +
			"    private int Mix(int value) { return value; }\n" +
			"    public int Mix(string value) { return 1; }\n" +
			"}\n";
		const string publicFirst =
			"namespace Ext;\nusing Models;\n" +
			"public extension Point {\n" +
			"    public int Mix(string value) { return 1; }\n" +
			"    private int Mix(int value) { return value; }\n" +
			"}\n";

		var first = Complete(main, ("Models.cvl", model), ("Ext.cvl", privateFirst));
		var second = Complete(main, ("Models.cvl", model), ("Ext.cvl", publicFirst));

		Assert.True(Contains(first, "Mix", CompletionKind.Method));
		Assert.True(Contains(second, "Mix", CompletionKind.Method));
	}

	[Fact]
	public void PrivateGenericType_IsFilteredAcrossFiles_WhileInternalTemplateRemainsVisible()
	{
		var result = Complete(
			"using Lib;\nint main() { val S| value; return 0; }\n",
			("Lib.cvl",
				"namespace Lib;\n" +
				"private struct Secret<T> { T value; }\n" +
				"internal struct Shared<T> { T value; }\n"));

		Assert.DoesNotContain(result.Candidates, c => c.Label == "Secret");
		Assert.True(Contains(result, "Shared", CompletionKind.Type));
	}

	[Fact]
	public void NamespaceCompletion_OffersResolvableRootAndRelativeSegmentsOnly()
	{
		var result = Complete(
			"namespace App;\n|",
			("Geometry.cvl", "namespace App.Geometry;\npublic struct Point { int x; }\n"),
			("Hidden.cvl", "namespace Other.Hidden;\npublic struct Secret { int x; }\n"));

		Assert.True(Contains(result, "App", CompletionKind.Namespace));
		Assert.True(Contains(result, "Geometry", CompletionKind.Namespace));
		Assert.True(Contains(result, "Other", CompletionKind.Namespace));
		Assert.DoesNotContain(result.Candidates, c => c.Label == "Hidden" && c.Kind == CompletionKind.Namespace);
	}

	[Fact]
	public void NamespaceCompletion_OffersNestedSegmentRelativeToActiveUsing()
	{
		var result = Complete(
			"namespace App;\nusing Other;\n|",
			("Hidden.cvl", "namespace Other.Hidden;\npublic struct Secret { int x; }\n"));

		Assert.True(Contains(result, "Hidden", CompletionKind.Namespace));
	}

	[Fact]
	public void CompletionKindMap_IsExhaustiveOverAllAnalysisKinds()
	{
		var analysisKinds = Enum.GetValues<AnalysisKind>();

		Assert.Equal(12, analysisKinds.Length);
		foreach (var kind in analysisKinds)
		{
			var mapped = CompletionService.MapKind(kind);
			Assert.NotEqual(CompletionKind.Keyword, mapped);
			Assert.Equal(kind.ToString(), mapped.ToString());
		}
	}

	[Fact]
	public void GetCompletions_CrossFileSnapshotPurity()
	{
		const string mainSource = "int main() { | }\n";
		var position = mainSource.IndexOf('|');
		var main = mainSource.Remove(position, 1);

		using var fixture = TempProject.Create(("Main.cvl", main), ("Lib.cvl", "global int LGL;\n"));
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		var mainId = project.GetDocumentId("Main.cvl");
		var libId = project.GetDocumentId("Lib.cvl");

		var snapshotA = project.InitialSnapshot;
		var documentA = snapshotA.GetDocument(mainId);
		var resultA = documentA.GetCompletions(position);

		Assert.Contains(resultA.Candidates, c => c.Label == "LGL");

		var snapshotB = snapshotA.WithDocument(libId, SourceText.From("global int LGLRenamed;\n"));
		var resultB = snapshotB.GetDocument(mainId).GetCompletions(position);

		Assert.Contains(resultB.Candidates, c => c.Label == "LGLRenamed");
		Assert.DoesNotContain(resultB.Candidates, c => c.Label == "LGL");

		var resultAReprobed = documentA.GetCompletions(position);
		Assert.Equal(resultA.ReplacementRange, resultAReprobed.ReplacementRange);
		Assert.True(resultA.Candidates.SequenceEqual(resultAReprobed.Candidates));
		Assert.DoesNotContain(resultAReprobed.Candidates, c => c.Label == "LGLRenamed");
	}

	[Fact]
	public void MemberAccess_BareDot_ReportsZeroLengthSpan()
	{
		var result = Complete("struct Point { int x; int y; }\nint main() { val Point p; return p.|; }\n");

		Assert.Equal(0, result.ReplacementRange.Length);
		Assert.True(Contains(result, "x", CompletionKind.StructField));
		Assert.True(Contains(result, "y", CompletionKind.StructField));
	}

	[Fact]
	public void BareDot_WithoutFollowingSemicolon_RecoversMemberContext()
	{
		var result = Complete(
			"struct Node { int value; int other; }\n" +
			"int main() {\n" +
			"    val Node n;\n" +
			"    n.|\n" +
			"}\n");

		Assert.True(Contains(result, "value", CompletionKind.StructField));
		Assert.True(Contains(result, "other", CompletionKind.StructField));
		Assert.DoesNotContain(result.Candidates, c => c.Kind == CompletionKind.Keyword);
		Assert.Equal(0, result.ReplacementRange.Length);
	}

	[Fact]
	public void BareDot_InIncompleteReturn_RecoversMemberContext()
	{
		var result = Complete(
			"struct Node { int value; }\n" +
			"int main() {\n" +
			"    val Node n;\n" +
			"    return n.|\n" +
			"}\n");

		Assert.True(Contains(result, "value", CompletionKind.StructField));
		Assert.DoesNotContain(result.Candidates, c => c.Kind == CompletionKind.Keyword);
	}

	[Fact]
	public void BareDot_WithWhitespaceBeforeCursor_RecoversMemberContext()
	{
		var result = Complete(
			"struct Node { int value; }\n" +
			"int main() {\n" +
			"    val Node n;\n" +
			"    n.   |\n" +
			"}\n");

		Assert.True(Contains(result, "value", CompletionKind.StructField));
		Assert.DoesNotContain(result.Candidates, c => c.Kind == CompletionKind.Keyword);
		Assert.Equal(0, result.ReplacementRange.Length);
	}

	[Fact]
	public void PartialMember_WithoutFollowingSemicolon_RecoversMemberContext()
	{
		var result = Complete(
			"struct Node { int value; int other; }\n" +
			"int main() {\n" +
			"    val Node n;\n" +
			"    n.v|\n" +
			"}\n");

		Assert.True(Contains(result, "value", CompletionKind.StructField));
		Assert.DoesNotContain(result.Candidates, c => c.Label == "other");
		Assert.DoesNotContain(result.Candidates, c => c.Kind == CompletionKind.Keyword);
		Assert.Equal(1, result.ReplacementRange.Length);
	}

	[Fact]
	public void UnknownReceiver_BareDot_DoesNotFallBackToGlobalsOrKeywords()
	{
		var result = Complete(
			"global int value = 1;\n" +
			"int main() {\n" +
			"    v.|\n" +
			"}\n");

		Assert.Empty(result.Candidates);
	}

	[Fact]
	public void UnknownReceiver_PartialMember_DoesNotSuggestValueOrVar()
	{
		var result = Complete(
			"global int value = 1;\n" +
			"int main() {\n" +
			"    v.v|\n" +
			"}\n");

		Assert.DoesNotContain(result.Candidates, c => c.Label == "value");
		Assert.DoesNotContain(result.Candidates, c => c.Label == "var");
		Assert.DoesNotContain(result.Candidates, c => c.Kind == CompletionKind.Keyword);
		Assert.Empty(result.Candidates);
	}

	[Fact]
	public void BareDot_NestedMemberReceiver_OffersFields()
	{
		var result = Complete(
			"struct Point { int absc; int ord; }\n" +
			"struct Line { Point start; Point end; }\n" +
			"int main() {\n" +
			"    val Line l;\n" +
			"    return l.start.|;\n" +
			"}\n");

		Assert.True(Contains(result, "absc", CompletionKind.StructField));
		Assert.True(Contains(result, "ord", CompletionKind.StructField));
		Assert.DoesNotContain(result.Candidates, c => c.Label == "end");
	}

	[Fact]
	public void BareDot_EnumTypeNameReceiver_OffersVariants()
	{
		var result = Complete("enum Color { Red, Green, Blue }\nint main() { return Color.|; }\n");

		Assert.True(Contains(result, "Red", CompletionKind.EnumVariant));
		Assert.True(Contains(result, "Green", CompletionKind.EnumVariant));
		Assert.True(Contains(result, "Blue", CompletionKind.EnumVariant));
	}

	[Fact]
	public void IncompleteKeywordToken_Ret_ShowsReturnKeyword()
	{
		var result = Complete("int main() {\n    ret|\n}\n");

		Assert.True(Contains(result, "return", CompletionKind.Keyword));
	}

	[Fact]
	public void TypeSlot_BareVal_OffersTypes()
	{
		var result = Complete(
			"struct Point { int x; }\n" +
			"int Pointy() { return 0; }\n" +
			"int main() {\n" +
			"    val Point p;\n" +
			"    val |;\n" +
			"}\n");

		Assert.True(Contains(result, "Point", CompletionKind.Type));
		Assert.DoesNotContain(result.Candidates, c => c.Label == "Pointy");
	}

	[Fact]
	public void IncompleteExpression_OffersCandidates()
	{
		var result = Complete("int main() {\n    val int vel = 2;\n    return vel + ve|\n}\n");

		Assert.True(Contains(result, "vel", CompletionKind.Local));
	}

	[Fact]
	public void Utf16SurrogatePair_PrefixSpanAndFiltering()
	{
		const string source = "int main() {\n    val string s = \"🤖\";\n    ret|urn 0;\n}\n";
		var result = Complete(source);

		Assert.Equal(new TextSpan(source.IndexOf("ret", StringComparison.Ordinal), "return".Length), result.ReplacementRange);
		Assert.True(Contains(result, "return", CompletionKind.Keyword));
		Assert.Equal(source.Replace("|", string.Empty, StringComparison.Ordinal), ApplyCandidate(source, "return", CompletionKind.Keyword));
	}

	[Fact]
	public void TopLevelPartialDeclarationKeyword_OffersDeclarationKeywords()
	{
		var result = Complete("struct A { int x; }\nali|");

		Assert.True(Contains(result, "alias", CompletionKind.Keyword));
	}

	[Fact]
	public void TopLevelPartialDeclarationKeyword_Struct()
	{
		var result = Complete("global int g;\nstr|");

		Assert.True(Contains(result, "struct", CompletionKind.Keyword));
	}

	[Fact]
	public void TopLevelPartialDeclarationKeyword_Namespace()
	{
		var result = Complete("struct A { int x; }\nname|");

		Assert.True(Contains(result, "namespace", CompletionKind.Keyword));
	}

	[Fact]
	public void TopLevelPartialDeclarationKeyword_DoesNotLeakStatementKeywords()
	{
		var result = Complete("struct A { int x; }\nali|");

		Assert.DoesNotContain(result.Candidates, c => c.Label == "return" && c.Kind == CompletionKind.Keyword);
	}

	[Fact]
	public void BareDot_BeforeFollowingStatement_RecoversMemberContext()
	{
		// The realistic editor buffer: `n.` on its own line followed by a later
		// statement. The probe must terminate the statement to bind the receiver.
		var result = Complete(
			"struct Name { int value; }\n" +
			"int Subtract(int left, int right) {\n" +
			"    val Name n;\n" +
			"    n.|\n" +
			"    return left - right;\n" +
			"}\n");

		Assert.True(Contains(result, "value", CompletionKind.StructField));
		Assert.DoesNotContain(result.Candidates, c => c.Kind == CompletionKind.Keyword);
		Assert.Equal(0, result.ReplacementRange.Length);
	}

	[Fact]
	public void BareDot_BeforeFollowingDeclaration_RecoversMemberContext()
	{
		var result = Complete(
			"struct Name { int value; }\n" +
			"int main() {\n" +
			"    val Name n;\n" +
			"    n.|\n" +
			"    val int later = 1;\n" +
			"}\n");

		Assert.True(Contains(result, "value", CompletionKind.StructField));
		Assert.DoesNotContain(result.Candidates, c => c.Kind == CompletionKind.Keyword);
	}

	[Fact]
	public void BareDot_AfterReturnKeyword_RecoversMemberContext()
	{
		var result = Complete(
			"struct Name { int value; }\n" +
			"int main() {\n" +
			"    val Name n;\n" +
			"    return n.|\n" +
			"}\n");

		Assert.True(Contains(result, "value", CompletionKind.StructField));
		Assert.DoesNotContain(result.Candidates, c => c.Kind == CompletionKind.Keyword);
	}

	[Fact]
	public void ExtensionDestructor_BareTilde_OffersSnippet()
	{
		var result = Complete("struct Name { int value; }\nextension Name {\n    ~|\n}\n");

		var candidate = Assert.Single(result.Candidates);
		Assert.Equal("~Name()", candidate.Label);
		Assert.True(candidate.IsSnippet);
		Assert.Contains("~Name()", candidate.InsertText, StringComparison.Ordinal);
		// The bare `~` is part of the replaced range so the snippet supplies it.
		Assert.Equal(new TextSpan("struct Name { int value; }\nextension Name {\n    ".Length, 1), result.ReplacementRange);
	}

	[Fact]
	public void ExtensionDestructor_PartialName_ReplacesTildeAndPrefix()
	{
		const string marked = "struct Name { int value; }\nextension Name {\n    ~Na|\n}\n";
		var (_, position) = SplitCursor(marked);
		var result = Complete(marked);

		var candidate = Assert.Single(result.Candidates);
		Assert.Equal("~Name()", candidate.Label);
		Assert.Equal(new TextSpan(position - 3, 3), result.ReplacementRange);
	}

	[Fact]
	public void ExtensionDestructor_GenericExtension_UsesExtendedTypeName()
	{
		var result = Complete("struct Box<T> { T item; }\nextension Box {\n    ~|\n}\n");

		var candidate = Assert.Single(result.Candidates);
		Assert.Equal("~Box()", candidate.Label);
	}

	[Fact]
	public void BitwiseNot_OutsideExtension_IsNotDestructor()
	{
		var result = Complete("int main() {\n    var int x = ~|\n}\n");

		Assert.DoesNotContain(result.Candidates, c => c.Label.StartsWith("~", StringComparison.Ordinal));
	}
}
