using Cvolo.Compiler.Tooling;

namespace Cvolo.Tests.Tooling;

/// <summary>
/// INC 3.1 EXT: only a compiler-authored imported extern function — a standalone
/// <c>extern</c> declaration or a member of an importing <c>extern "C"</c>/<c>extern "system"</c>
/// block with no Cvolo body — carries the <see cref="SemanticTokenModifiers.ExternFunction"/>
/// marker. Exported <c>expose extern</c> functions, native-ABI Cvolo functions with a body,
/// delegates, delegate-valued globals, loaders and wrappers never do.
/// </summary>
public sealed class ExternFunctionSemanticTokenTests
{
	private static (CvoloProject Project, ProjectSnapshot Snapshot, DocumentSnapshot Document, TempProject Fixture) Open(string primary, params (string FileName, string Source)[] sources)
	{
		var fixture = TempProject.Create(sources);
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		var snapshot = project.InitialSnapshot;
		return (project, snapshot, snapshot.GetDocument(project.GetDocumentId(primary)), fixture);
	}

	private static List<SemanticTokenInfo> WithText(DocumentSnapshot document, IReadOnlyList<SemanticTokenInfo> tokens, string text)
		=> [.. tokens.Where(t => document.Text.GetText(t.Span) == text)];

	[Fact]
	public void StandaloneExternFunction_HasExternModifier_AtDeclarationAndCall()
	{
		const string s = "extern int abs(int x);\nint main() { return abs(-1); }\n";
		var x = Open("Main.cvl", ("Main.cvl", s));
		using (x.Fixture)
		{
			var tokens = WithText(x.Document, x.Document.GetSemanticTokens(), "abs");

			Assert.NotEmpty(tokens);
			Assert.All(tokens, t => Assert.Equal(ToolingSymbolKind.Function, t.Kind));
			Assert.All(tokens, t => Assert.True(t.Modifiers.HasFlag(SemanticTokenModifiers.ExternFunction)));
			Assert.Contains(tokens, t => t.Modifiers.HasFlag(SemanticTokenModifiers.Declaration));
		}
	}

	[Fact]
	public void ImportingExternBlockFunction_HasExternModifier()
	{
		const string s = "[LibraryImport(\"test\")]\nextern \"C\" {\n    void DoSomething();\n}\n\nint main() { return 0; }\n";
		var x = Open("Main.cvl", ("Main.cvl", s));
		using (x.Fixture)
		{
			var tokens = WithText(x.Document, x.Document.GetSemanticTokens(), "DoSomething");

			Assert.NotEmpty(tokens);
			Assert.All(tokens, t => Assert.Equal(ToolingSymbolKind.Function, t.Kind));
			Assert.All(tokens, t => Assert.True(t.Modifiers.HasFlag(SemanticTokenModifiers.ExternFunction)));
		}
	}

	[Fact]
	public void ExportExternBlockFunction_IsNotMarked()
	{
		const string s = "expose extern \"C\" {\n    public int RenderFrame(int x) { return x; }\n}\n\nint main() { return 0; }\n";
		var x = Open("Main.cvl", ("Main.cvl", s));
		using (x.Fixture)
		{
			var tokens = WithText(x.Document, x.Document.GetSemanticTokens(), "RenderFrame");

			Assert.NotEmpty(tokens);
			Assert.All(tokens, t => Assert.False(t.Modifiers.HasFlag(SemanticTokenModifiers.ExternFunction)));
		}
	}

	[Fact]
	public void OrdinaryFunction_IsNotMarked()
	{
		const string s = "int Add(int a, int b) { return a + b; }\nint main() { return Add(1, 2); }\n";
		var x = Open("Main.cvl", ("Main.cvl", s));
		using (x.Fixture)
		{
			var tokens = WithText(x.Document, x.Document.GetSemanticTokens(), "Add");

			Assert.NotEmpty(tokens);
			Assert.All(tokens, t => Assert.False(t.Modifiers.HasFlag(SemanticTokenModifiers.ExternFunction)));
		}
	}

	[Fact]
	public void NativeAbiFunctionWithBody_IsNotMarked()
	{
		const string s = "unsafe \"C\" int Increment(int value) { return value + 1; }\nint main() { return Increment(1); }\n";
		var x = Open("Main.cvl", ("Main.cvl", s));
		using (x.Fixture)
		{
			var tokens = WithText(x.Document, x.Document.GetSemanticTokens(), "Increment");

			Assert.NotEmpty(tokens);
			Assert.All(tokens, t => Assert.False(t.Modifiers.HasFlag(SemanticTokenModifiers.ExternFunction)));
		}
	}

	[Fact]
	public void GlobalDelegateVariable_IsNotMarked()
	{
		const string s = "unsafe \"C\" delegate int Callback(int value);\nglobal var Callback Slot;\nint main() { return 0; }\n";
		var x = Open("Main.cvl", ("Main.cvl", s));
		using (x.Fixture)
		{
			var tokens = WithText(x.Document, x.Document.GetSemanticTokens(), "Slot");

			Assert.NotEmpty(tokens);
			Assert.All(tokens, t => Assert.False(t.Modifiers.HasFlag(SemanticTokenModifiers.ExternFunction)));
		}
	}
}
