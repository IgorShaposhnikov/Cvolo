using Cvolo.Compiler.Tooling;

namespace Cvolo.Tests.Tooling;

/// <summary>
/// INC 3.1 CONST: only a compiler-proven compile-time constant — an immutable top-level
/// <c>global</c> — carries the <see cref="SemanticTokenModifiers.Constant"/> marker, at the
/// declaration and every usage. Mutable globals, local <c>val</c>s and enum members never do.
/// </summary>
public sealed class ConstantSemanticTokenTests
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
	public void ImmutableGlobal_HasConstantModifier_AtDeclarationAndUse()
	{
		const string s = "global int Seed = 1;\nint main() { return Seed; }\n";
		var x = Open("Main.cvl", ("Main.cvl", s));
		using (x.Fixture)
		{
			var tokens = WithText(x.Document, x.Document.GetSemanticTokens(), "Seed");

			Assert.Equal(2, tokens.Count);
			Assert.All(tokens, t => Assert.Equal(ToolingSymbolKind.Global, t.Kind));
			Assert.All(tokens, t => Assert.True(t.Modifiers.HasFlag(SemanticTokenModifiers.Constant)));
			Assert.All(tokens, t => Assert.True(t.Modifiers.HasFlag(SemanticTokenModifiers.Readonly)));
			Assert.All(tokens, t => Assert.True(t.Modifiers.HasFlag(SemanticTokenModifiers.Static)));
			Assert.Contains(tokens, t => t.Modifiers.HasFlag(SemanticTokenModifiers.Declaration));
		}
	}

	[Fact]
	public void MutableGlobal_IsNotAConstant()
	{
		const string s = "global var int Cell = 1;\nint main() { return Cell; }\n";
		var x = Open("Main.cvl", ("Main.cvl", s));
		using (x.Fixture)
		{
			var tokens = WithText(x.Document, x.Document.GetSemanticTokens(), "Cell");

			Assert.NotEmpty(tokens);
			Assert.All(tokens, t => Assert.True(t.Modifiers.HasFlag(SemanticTokenModifiers.Static)));
			Assert.All(tokens, t => Assert.False(t.Modifiers.HasFlag(SemanticTokenModifiers.Constant)));
			Assert.All(tokens, t => Assert.False(t.Modifiers.HasFlag(SemanticTokenModifiers.Readonly)));
		}
	}

	[Fact]
	public void ImmutableLocal_IsNotAConstant()
	{
		const string s = "int main() {\n    val int count = 1;\n    return count;\n}\n";
		var x = Open("Main.cvl", ("Main.cvl", s));
		using (x.Fixture)
		{
			var tokens = WithText(x.Document, x.Document.GetSemanticTokens(), "count");

			Assert.NotEmpty(tokens);
			Assert.Contains(tokens, t => t.Modifiers.HasFlag(SemanticTokenModifiers.Readonly));
			Assert.All(tokens, t => Assert.False(t.Modifiers.HasFlag(SemanticTokenModifiers.Constant)));
			Assert.All(tokens, t => Assert.False(t.Modifiers.HasFlag(SemanticTokenModifiers.Static)));
		}
	}

	[Fact]
	public void ConstantReachedThroughNamespaceAlias_HasConstantModifier()
	{
		const string native = "namespace Native;\nglobal int Version = 3;\n";
		const string api = "using N = Native;\nnamespace Api;\nint main() { return N.Version; }\n";
		var x = Open("Api.cvl", ("Native.cvl", native), ("Api.cvl", api));
		using (x.Fixture)
		{
			var tokens = WithText(x.Document, x.Document.GetSemanticTokens(), "Version");

			Assert.NotEmpty(tokens);
			Assert.All(tokens, t => Assert.True(t.Modifiers.HasFlag(SemanticTokenModifiers.Constant)));
		}
	}
}
