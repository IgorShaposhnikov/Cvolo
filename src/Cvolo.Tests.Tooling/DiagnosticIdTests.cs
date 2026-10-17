using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cvolo.Compiler.Tooling;
using Cvolo.Packaging;

namespace Cvolo.Tests.Tooling;

/// <summary>
/// Guards the diagnostic-id contract: every diagnostic the analysis pipeline
/// emits must carry an explicit, stable catalog id. The placeholder
/// <c>CVL0000</c> must never appear in emitted diagnostics or production code.
/// </summary>
public sealed class DiagnosticIdTests
{
	private const string PlaceholderId = "CVL0000";

	private static DocumentSnapshot Open(string source)
	{
		using var fixture = TempProject.Create(("Main.cvl", source));
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		var documentId = project.GetDocumentId("Main.cvl");
		return project.InitialSnapshot.GetDocument(documentId);
	}

	private const string SemanticErrors =
		"namespace Smoke;\n\n" +
		"public struct Point\n{\n    public int x;\n    public int y;\n}\n\n" +
		"public struct Broken\n{\n    public int ok;\n    public TotallyUnknownType bad;\n}\n\n" +
		"int Compute()\n{\n    int uninit;\n    return uninit;\n}\n";

	[Fact]
	public void EmittedDiagnostics_NeverUseThePlaceholderId()
	{
		var document = Open(SemanticErrors);

		var diagnostics = document.GetDiagnostics();
		Assert.NotEmpty(diagnostics);
		Assert.DoesNotContain(diagnostics, d => d.Id == PlaceholderId);
	}

	[Fact]
	public void EmittedDiagnostics_AlwaysCarryANonEmptyId()
	{
		var document = Open(SemanticErrors);

		Assert.All(document.GetDiagnostics(), d => Assert.False(string.IsNullOrWhiteSpace(d.Id)));
	}

	[Fact]
	public void TheSameDiagnosticKind_AtDifferentLocations_SharesOneId()
	{
		const string source =
			"namespace Smoke;\n\n" +
			"public struct A\n{\n    public NopeField first;\n}\n\n" +
			"public struct B\n{\n    public NopeField second;\n}\n";

		var document = Open(source);

		var unknownType = document.GetDiagnostics()
			.Where(d => d.Message.Contains("Unknown type", StringComparison.Ordinal))
			.ToList();

		Assert.Equal(2, unknownType.Count);
		var id = unknownType[0].Id;
		Assert.False(string.IsNullOrWhiteSpace(id));
		Assert.All(unknownType, d => Assert.Equal(id, d.Id));
	}

	[Fact]
	public void SyntaxError_UnexpectedToken_GetsItsOwnStableId()
	{
		// A stray top-level '}' is extraneous input -> "unexpected token".
		var document = Open("int main()\n{\n}\n}\n");

		var diagnostics = document.GetDiagnostics();
		Assert.NotEmpty(diagnostics);
		Assert.DoesNotContain(diagnostics, d => d.Id == PlaceholderId);
		Assert.Contains(diagnostics, d => d.Id == "CVL4173");
	}

	[Fact]
	public void StrayIdentifierBeforeFunction_ReportsStrayWordNotReturnType()
	{
		const string source =
			"int Helper() { return 1; }\n" +
			"asdasd\n\n" +
			"int Main() { return Helper(); }\n";
		var document = Open(source);
		var diagnostics = document.GetDiagnostics();
		var badStart = source.IndexOf("asdasd", StringComparison.Ordinal);
		Assert.Contains(diagnostics, d => d.Id == "CVL4173"
			&& d.Location.Span.Start == badStart && d.Location.Span.Length == 6);
		Assert.DoesNotContain(diagnostics, d => d.Id == "CVL4173"
			&& d.Location.Span.Start == source.IndexOf("int Main", StringComparison.Ordinal));
		Assert.Contains(document.GetDocumentSymbols(), symbol => symbol.Name == "Main");
	}

	[Fact]
	public void BarePrimitiveBeforeVar_ReportsIncompleteTypeWithoutConsumingNextStatement()
	{
		const string source =
			"int Main() {\n" +
			"    var first = 1;\n" +
			"    double\n\n" +
			"    var window = first;\n" +
			"    return window;\n" +
			"}\n";
		var document = Open(source);
		var diagnostics = document.GetDiagnostics();
		var bareStart = source.IndexOf("double", StringComparison.Ordinal);
		Assert.Contains(diagnostics, d => d.Id == "CVL4173"
			&& d.Location.Span.Start == bareStart && d.Location.Span.Length == "double".Length);
		Assert.DoesNotContain(diagnostics, d => d.Message.Contains("doublevar", StringComparison.Ordinal));
		Assert.DoesNotContain(diagnostics, d => d.Location.Span.Start == source.IndexOf("var window", StringComparison.Ordinal));
		Assert.Contains(document.GetDocumentSymbols(), symbol => symbol.Name == "Main");
	}

	[Theory]
	[InlineData("d0o", "var")]
	[InlineData("asdasd", "val")]
	public void StandaloneUnknownIdentifier_PreservesSubsequentInferredVariable(string badName, string declarationKeyword)
	{
		var source = $"int Main() {{\n    {badName}\n\n    {declarationKeyword} window = 1;\n    return window;\n}}\n";
		var document = Open(source);
		var diagnostics = document.GetDiagnostics();
		var badStart = source.IndexOf(badName, StringComparison.Ordinal);
		Assert.Contains(diagnostics, d => d.Id == "CVL4173"
			&& d.Location.Span.Start == badStart
			&& d.Location.Span.Length == badName.Length);
		Assert.DoesNotContain(diagnostics, d => d.Message.Contains(badName + declarationKeyword, StringComparison.Ordinal)
			|| d.Id == "CVL4004");
		Assert.Contains(document.GetDocumentSymbols(), symbol => symbol.Name == "Main");

		var declarationStart = source.IndexOf("window = 1", StringComparison.Ordinal);
		var useStart = source.LastIndexOf("window", StringComparison.Ordinal);
		var windowTokens = document.GetSemanticTokens()
			.Where(token => document.Text.GetText(token.Span) == "window").ToArray();
		Assert.Contains(windowTokens, token => token.Kind == ToolingSymbolKind.Local
			&& token.Span.Start == declarationStart);
		Assert.Contains(windowTokens, token => token.Kind == ToolingSymbolKind.Local
			&& token.Span.Start == useStart);
	}

	[Theory]
	[InlineData("if (true) { return window; }", "if")]
	[InlineData("while (false) { return window; }", "while")]
	[InlineData("return window;", "return")]
	[InlineData("Console.WriteLine(window);", "Console")]
	public void StandaloneIdentifierBeforeStatement_DoesNotConsumeNextStatement(string statement, string nextWord)
	{
		var source = "int Main() {\n"
			+ "    var window = 1;\n"
			+ "    i\n"
			+ "    " + statement + "\n"
			+ "    return window;\n"
			+ "}\n";
		var document = Open(source);
		var diagnostics = document.GetDiagnostics();
		var badStart = source.IndexOf("    i\n", StringComparison.Ordinal) + 4;
		Assert.Contains(diagnostics, d => d.Id == "CVL4173"
			&& d.Location.Span.Start == badStart && d.Location.Span.Length == 1);
		Assert.DoesNotContain(diagnostics, d => d.Message.Contains("i" + nextWord, StringComparison.Ordinal)
			|| d.Id == "CVL4004" || d.Id == "CVL1045");
		Assert.Contains(document.GetDocumentSymbols(), symbol => symbol.Name == "Main");
		var returnStart = source.LastIndexOf("window", StringComparison.Ordinal);
		Assert.Contains(document.GetSemanticTokens(), token => token.Kind == ToolingSymbolKind.Local
			&& token.Span.Start == returnStart
			&& document.Text.GetText(token.Span) == "window");
	}

	[Fact]
	public void StrayIdentifiersBetweenNamespaceTypesAndExtensionMembers_PreserveDeclarations()
	{
		const string source =
			"using System;\n" +
			"a\n" +
			"namespace App;\n" +
			"d\n" +
			"public interface ITest {}\n" +
			"d\n" +
			"struct WindowParameters { int width; int height; string title; }\n" +
			"a\n" +
			"struct Window { int glfwWindow; }\n" +
			"public extension Window {\n" +
			"    [UnsafeBody]\n" +
			"    public void Start(ref this) { return; }\n" +
			"c\n" +
			"    [UnsafeBody]\n" +
			"    public ~Window() {}\n" +
			"d\n" +
			"    public void Swap(ref this) { return; }\n" +
			"a\n" +
			"    [UnsafeBody]\n" +
			"    public bool IsInit(ref this) { return true; }\n" +
			"}\n" +
			"int Main() { return 0; }\n";

		var document = Open(source);
		var diagnostics = document.GetDiagnostics();
		var strayPositions = new[]
		{
			source.IndexOf("a\nnamespace", StringComparison.Ordinal),
			source.IndexOf("d\npublic interface", StringComparison.Ordinal),
			source.IndexOf("d\nstruct WindowParameters", StringComparison.Ordinal),
			source.IndexOf("a\nstruct Window", StringComparison.Ordinal),
			source.IndexOf("c\n    [UnsafeBody]", StringComparison.Ordinal),
			source.IndexOf("d\n    public void Swap", StringComparison.Ordinal),
			source.IndexOf("a\n    [UnsafeBody]", StringComparison.Ordinal)
		};
		Assert.All(strayPositions, position => Assert.True(position >= 0));
		foreach (var position in strayPositions)
			Assert.Contains(diagnostics, d => d.Id == "CVL4173"
				&& d.Location.Span.Start == position && d.Location.Span.Length == 1);
		Assert.Equal(strayPositions.Length, diagnostics.Count(d => d.Id == "CVL4173"));
		Assert.DoesNotContain(diagnostics, d => d.Id is "CVL4004" or "CVL1045" or "CVL4174");

		var outline = document.GetDocumentSymbols();
		foreach (var name in new[] { "ITest", "WindowParameters", "Window", "Main" })
			Assert.Contains(outline, symbol => symbol.Name == name);
		Assert.Contains(outline.SelectMany(symbol => symbol.Children), symbol => symbol.Name == "Swap");
		Assert.Contains(outline.SelectMany(symbol => symbol.Children), symbol => symbol.Name == "IsInit");
	}

	[Theory]
	[InlineData("struct")]
	[InlineData("union")]
	public void StrayIdentifierBeforePrimitiveField_PreservesTheField(string containerKind)
	{
		var source = containerKind + " Window {\n"
			+ "    a\n"
			+ "    void* glfwWindow;\n"
			+ "    int width;\n"
			+ "}\n";
		var document = Open(source);
		var diagnostics = document.GetDiagnostics();
		var badStart = source.IndexOf("    a\n", StringComparison.Ordinal) + 4;
		Assert.Contains(diagnostics, d => d.Id == "CVL4173"
			&& d.Location.Span.Start == badStart && d.Location.Span.Length == 1);
		Assert.DoesNotContain(diagnostics, d => d.Id is "CVL4004" or "CVL4174"
			|| d.Location.Span.Start == source.IndexOf("void*", StringComparison.Ordinal));
		var container = Assert.Single(document.GetDocumentSymbols(), symbol => symbol.Name == "Window");
		Assert.Contains(container.Children, field => field.Name == "glfwWindow");
		Assert.Contains(container.Children, field => field.Name == "width");
	}

	[Fact]
	public void CustomTypeAndFieldNameMaySpanLinesWithoutRecovery()
	{
		const string source = "struct Item { int value; }\n"
			+ "struct Holder {\n"
			+ "    Item\n"
			+ "    item;\n"
			+ "}\n";
		var document = Open(source);
		Assert.DoesNotContain(document.GetDiagnostics(), d => d.Id is "CVL4173" or "CVL4004");
		var holder = Assert.Single(document.GetDocumentSymbols(), symbol => symbol.Name == "Holder");
		Assert.Contains(holder.Children, field => field.Name == "item");
	}

	[Fact]
	public void NamespaceBracesAndMultilineCustomReturnType_AreNotRecoveredAsStrayTokens()
	{
		const string source =
			"namespace App {\n" +
			"    struct Item { int value; }\n" +
			"    Item\n" +
			"    Create() { return default; }\n" +
			"    public extension Item {\n" +
			"        public int Get(ref this) { return value; }\n" +
			"    }\n" +
			"}\n";
		var document = Open(source);
		Assert.DoesNotContain(document.GetDiagnostics(), d => d.Id is "CVL4173" or "CVL4004");
		Assert.Contains(document.GetDocumentSymbols(), symbol => symbol.Name == "Create");
	}

	[Fact]
	public void MultilineCustomTypeDeclaration_IsNotTreatedAsStandaloneIdentifier()
	{
		const string source =
			"struct Window { int Width; }\n" +
			"int Main() {\n    Window\n    window = Window { Width: 1 };\n    return window.Width;\n}\n";
		var document = Open(source);
		Assert.DoesNotContain(document.GetDiagnostics(), d => d.Id == "CVL4173");
		Assert.Contains(document.GetSemanticTokens(), token =>
			document.Text.GetText(token.Span) == "window" && token.Kind == ToolingSymbolKind.Local);
	}

	[Theory]
	[InlineData("if")]
	[InlineData("while")]
	public void IncompleteControlBeforeQualifiedCall_DoesNotConsumeCall(string keyword)
	{
		var source = "using System;\nint Main() {\n"
			+ "    " + keyword + "\n"
			+ "    Console.WriteLine(\"ok\");\n"
			+ "    return 0;\n}\n";
		var document = Open(source);
		var diagnostics = document.GetDiagnostics();
		var tokenStart = source.IndexOf("    " + keyword + "\n", StringComparison.Ordinal) + 4;
		Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "CVL4173"
			&& diagnostic.Location.Span.Start == tokenStart
			&& diagnostic.Location.Span.Length == keyword.Length);
		Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id is "CVL4004" or "CVL1045"
			|| diagnostic.Message.Contains("missing '('", StringComparison.Ordinal)
			|| diagnostic.Location.Span.Start == source.IndexOf("Console.WriteLine", StringComparison.Ordinal));
		Assert.Contains(document.GetDocumentSymbols(), symbol => symbol.Name == "Main");
	}

	[Fact]
	public void ConsecutiveIncompleteControls_BothRecoverBeforeQualifiedCall()
	{
		const string source = "using System;\nint Main() {\n"
			+ "    while\n"
			+ "    if\n"
			+ "    Console.WriteLine(\"ok\");\n"
			+ "    return 0;\n}\n";
		var document = Open(source);
		var diagnostics = document.GetDiagnostics();
		foreach (var keyword in new[] { "while", "if" })
		{
			var offset = source.IndexOf("    " + keyword + "\n", StringComparison.Ordinal) + 4;
			Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "CVL4173"
				&& diagnostic.Location.Span.Start == offset && diagnostic.Location.Span.Length == keyword.Length);
		}
		Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id is "CVL4004" or "CVL1045");
		Assert.Contains(document.GetDocumentSymbols(), symbol => symbol.Name == "Main");
	}

	[Fact]
	public void ControlConditionMayStartOnFollowingLine()
	{
		const string source = "int Main() {\n"
			+ "    if\n        (true) { return 1; }\n"
			+ "    while\n        (false) { return 2; }\n"
			+ "    return 0;\n}\n";
		var diagnostics = Open(source).GetDiagnostics();
		Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id is "CVL4173" or "CVL4004");
	}

	[Theory]
	[InlineData("Console.WriteLine(\"ok\");")]
	[InlineData("if (true) { return 1; }")]
	[InlineData("while (false) { return 1; }")]
	public void IncompleteQualifiedCall_DoesNotConsumeFollowingStatement(string following)
	{
		var source = "int Main() {\n"
			+ "    Console.Wr\n"
			+ "    " + following + "\n"
			+ "    return 0;\n}\n";
		var document = Open(source);
		var diagnostics = document.GetDiagnostics();
		var start = source.IndexOf("Console.Wr", StringComparison.Ordinal);
		Assert.Contains(diagnostics, d => d.Id == "CVL4173"
			&& d.Location.Span.Start == start && d.Location.Span.Length == "Console.Wr".Length);
		Assert.DoesNotContain(diagnostics, d => d.Id is "CVL4004" or "CVL1045");
		Assert.Contains(document.GetDocumentSymbols(), symbol => symbol.Name == "Main");
	}

	[Fact]
	public void QualifiedCallMayContinueOnNextLine()
	{
		const string source = "int Main() {\n"
			+ "    Console.WriteLine\n"
			+ "    (\"ok\");\n"
			+ "    return 0;\n}\n";
		Assert.DoesNotContain(Open(source).GetDiagnostics(), d => d.Id == "CVL4173"
			&& d.Message.Contains("Incomplete member access", StringComparison.Ordinal));
	}

	[Fact]
	public void DestructorWithParameters_ReportsOneRootErrorAndPreservesBody()
	{
		const string source = "struct Window { int value; }\n"
			+ "extension Window {\n"
			+ "    [UnsafeBody]\n"
			+ "    public ~Window(int[] a) {\n"
			+ "        value = 1;\n"
			+ "    }\n"
			+ "    public int Get(ref this) { return value; }\n"
			+ "}\n";
		var document = Open(source);
		var diagnostics = document.GetDiagnostics();
		var start = source.IndexOf("int[] a", StringComparison.Ordinal);
		Assert.Contains(diagnostics, d => d.Id == "CVL4173"
			&& d.Location.Span.Start == start && d.Location.Span.Length == "int[] a".Length);
		Assert.DoesNotContain(diagnostics, d => d.Id is "CVL4004" or "CVL4174"
			|| d.Location.Span.Start == source.IndexOf("value = 1", StringComparison.Ordinal));
		Assert.Contains(document.GetDocumentSymbols(), symbol => symbol.Name == "Window");
	}

	[Fact]
	public void ParameterlessDestructor_RemainsValid()
	{
		const string source = "struct Window { int value; }\n"
			+ "extension Window { public ~Window() {} }\n";
		Assert.DoesNotContain(Open(source).GetDiagnostics(), d => d.Id == "CVL4173");
	}

	[Fact]
	public void UnfinishedRepeatedCall_DoesNotReclassifyNextCallAsVariable()
	{
		const string source =
			"void Clear(int value) {}\n"
			+ "void Begin(int value) {}\n"
			+ "int Main() {\n"
			+ "    Clear(1);\n"
			+ "    Clear\n"
			+ "    Begin(2);\n"
			+ "    return 0;\n"
			+ "}\n";
		var document = Open(source);
		var diagnostics = document.GetDiagnostics();
		var start = source.IndexOf("    Clear\n", StringComparison.Ordinal) + 4;
		Assert.Contains(diagnostics, d => d.Id == "CVL4173"
			&& d.Location.Span.Start == start && d.Location.Span.Length == "Clear".Length);
		Assert.DoesNotContain(diagnostics, d => d.Id == "CVL4004"
			|| d.Location.Span.Start == source.IndexOf("Begin(2)", StringComparison.Ordinal));
		Assert.Contains(document.GetDocumentSymbols(), symbol => symbol.Name == "Main");
	}

	[Fact]
	public void UnfinishedCall_DiagnosticDescribesTheCallNotAVariableType()
	{
		const string source = "void Clear(int mask) {}\n"
			+ "void Begin(int mode) {}\n"
			+ "int Main() { Clear(1);\n"
			+ "    Clear\n"
			+ "    Begin(2);\n"
			+ "    return 0;\n}\n";
		var document = Open(source);
		var start = source.IndexOf("    Clear\n", StringComparison.Ordinal) + 4;
		Assert.Contains(document.GetDiagnostics(), diagnostic =>
			diagnostic.Id == "CVL4173"
			&& diagnostic.Location.Span.Start == start
			&& diagnostic.Message.Contains("Incomplete call 'Clear'", StringComparison.Ordinal));
		Assert.DoesNotContain(document.GetDiagnostics(), diagnostic =>
			diagnostic.Id is "CVL4004" or "CVL1045");
	}

	[Fact]
	public void LegitimateMultilineTypedDeclaration_IsNotMistakenForUnfinishedCall()
	{
		const string source =
			"struct Point { int x; }\n"
			+ "int Main() { Point\n"
			+ "    value; return 0; }\n";
		Assert.DoesNotContain(Open(source).GetDiagnostics(), d => d.Id == "CVL4173");
	}

	[Fact]
	public void MultilineTypedVariable_RemainsLegal()
	{
		const string source = "int Main() {\n    double\n    value = 1.0;\n    return 0;\n}\n";
		var diagnostics = Open(source).GetDiagnostics();
		Assert.DoesNotContain(diagnostics, d => d.Id == "CVL4173");
	}

	[Fact]
	public void CustomReturnTypeAcrossLines_IsNotMasked()
	{
		const string source = "MyType\nMain() { return default; }\n";
		var diagnostics = Open(source).GetDiagnostics();
		Assert.DoesNotContain(diagnostics, d => d.Id == "CVL4173");
	}

	[Fact]
	public void SyntaxError_MissingToken_GetsItsOwnStableId()
	{
		// A missing statement terminator -> "missing ';'".
		var document = Open("int main()\n{\n    int x = 0\n    return 0;\n}\n");

		var diagnostics = document.GetDiagnostics();
		Assert.NotEmpty(diagnostics);
		Assert.DoesNotContain(diagnostics, d => d.Id == PlaceholderId);
		Assert.Contains(diagnostics, d => d.Id == "CVL4174");
	}

	[Fact]
	public void LexerError_UnrecognizedCharacter_GetsItsOwnStableId()
	{
		// `$` is not part of the language, so the lexer cannot recognize it.
		var document = Open("int main()\n{\n    int x = 0;\n    $;\n    return 0;\n}\n");

		var diagnostics = document.GetDiagnostics();
		Assert.NotEmpty(diagnostics);
		Assert.DoesNotContain(diagnostics, d => d.Id == PlaceholderId);
		Assert.Contains(diagnostics, d => d.Id == "CVL1911");
	}

	[Fact]
	public void LexerError_IsReportedOncePerOccurrence()
	{
		// Two independent unrecognized characters on separate lines must yield two diagnostics,
		// not one, and no CVL0000 placeholder.
		var document = Open("int main()\n{\n    $;\n    @;\n    return 0;\n}\n");

		var diagnostics = document.GetDiagnostics();
		Assert.DoesNotContain(diagnostics, d => d.Id == PlaceholderId);
		Assert.Equal(2, diagnostics.Count(d => d.Id == "CVL1911"));
	}

	[Fact]
	public void LexerError_IsNotWrittenToTheConsole()
	{
		// The lexer used to keep ANTLR's default listener, which printed
		// "token recognition error at: '...'" to stderr on every parse. The language server then
		// surfaced that as log spam instead of a diagnostic.
		var original = Console.Error;
		var captured = new StringWriter();
		Console.SetError(captured);
		try
		{
			var document = Open("int main()\n{\n    int x = 0;\n    $;\n    return 0;\n}\n");
			_ = document.GetDiagnostics();
		}
		finally
		{
			Console.SetError(original);
		}

		Assert.DoesNotContain("token recognition error", captured.ToString(), StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void ProductionSources_DoNotContainThePlaceholderId()
	{
		var root = FindRepositoryRoot();
		Assert.False(root is null, "Could not locate the repository root (src/Cvolo.slnx).");

		var offenders = new List<string>();
		foreach (var file in Directory.EnumerateFiles(Path.Combine(root!, "src"), "*.cs", SearchOption.AllDirectories))
		{
			var relative = Path.GetRelativePath(root!, file);
			if (relative.Contains("\\bin\\", StringComparison.Ordinal) ||
				relative.Contains("\\obj\\", StringComparison.Ordinal))
			{
				continue;
			}

			// Test projects may reference the placeholder to prove it is never emitted.
			if (relative.Contains("Cvolo.Tests", StringComparison.Ordinal))
				continue;

			if (File.ReadAllText(file).Contains(PlaceholderId, StringComparison.Ordinal))
				offenders.Add(relative);
		}

		Assert.Empty(offenders);
	}

	private static string? FindRepositoryRoot()
	{
		var directory = new DirectoryInfo(AppContext.BaseDirectory);
		while (directory is not null)
		{
			if (File.Exists(Path.Combine(directory.FullName, "src", "Cvolo.slnx")))
				return directory.FullName;
			directory = directory.Parent;
		}

		return null;
	}
}
