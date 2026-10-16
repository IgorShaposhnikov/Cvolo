using Cvolo.Compiler.Tooling;

namespace Cvolo.Tests.Tooling;

/// <summary>
/// Semantic-token presentation matters: namespaces, aliases, immutable globals and
/// delegate-valued globals must keep their true symbol identity. Presentation categories are
/// driven by the resolved declaration, never by spelling (an upper-cased name is not a
/// constant, a delegate call is not an imported extern function).
/// </summary>
public sealed class SemanticPresentationMatrixTests
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
	public void NamespaceDeclaration_IsClassifiedAsNamespace()
	{
		const string s = "namespace Deep;\nglobal int Thing = 1;\nint main() { return Deep.Thing; }\n";
		var x = Open("Main.cvl", ("Main.cvl", s));
		using (x.Fixture)
		{
			var tokens = WithText(x.Document, x.Document.GetSemanticTokens(), "Deep");

			Assert.True(tokens.Count > 0, string.Join(", ", x.Document.GetSemanticTokens().Select(t => $"{x.Document.Text.GetText(t.Span)}:{t.Kind}")));
			Assert.Contains(tokens, t => t.Kind == ToolingSymbolKind.Namespace);
			Assert.DoesNotContain(tokens, t => t.Kind == ToolingSymbolKind.Parameter);
		}
	}

	[Fact]
	public void NamespaceAlias_IsClassifiedAsNamespaceAlias()
	{
		const string s = "namespace Native;\nusing N = Native;\nint main() { return 0; }\n";
		var x = Open("Main.cvl", ("Main.cvl", s));
		using (x.Fixture)
		{
			var tokens = WithText(x.Document, x.Document.GetSemanticTokens(), "N");

			Assert.NotEmpty(tokens);
			Assert.Contains(tokens, t => t.Kind == ToolingSymbolKind.NamespaceAlias);
			Assert.DoesNotContain(tokens, t => t.Kind == ToolingSymbolKind.Function);
		}
	}

	[Fact]
	public void UppercaseImmutableGlobal_KeepsImmutableGlobalIdentity()
	{
		// An upper-cased immutable global looks C#-const-like; it must still be a Global with the
		// immutable-global presentation marker, not an enum member or a parameter.
		const string s = "namespace GL;\nglobal bool False = false;\nint main() { return 0; }\n";
		var x = Open("Main.cvl", ("Main.cvl", s));
		using (x.Fixture)
		{
			var tokens = WithText(x.Document, x.Document.GetSemanticTokens(), "False");

			Assert.NotEmpty(tokens);
			Assert.All(tokens, t => Assert.Equal(ToolingSymbolKind.Global, t.Kind));
			Assert.All(tokens, t => Assert.True(t.Modifiers.HasFlag(SemanticTokenModifiers.Constant)));
			Assert.All(tokens, t => Assert.True(t.Modifiers.HasFlag(SemanticTokenModifiers.Static)));
		}
	}

	[Fact]
	public void UppercaseMutableGlobal_IsNotConstantBySpelling()
	{
		const string s = "global var int Version = 1;\nint main() { Version = 2; return Version; }\n";
		var x = Open("Main.cvl", ("Main.cvl", s));
		using (x.Fixture)
		{
			var tokens = WithText(x.Document, x.Document.GetSemanticTokens(), "Version");

			Assert.NotEmpty(tokens);
			Assert.All(tokens, t => Assert.Equal(ToolingSymbolKind.Global, t.Kind));
			Assert.All(tokens, t => Assert.False(t.Modifiers.HasFlag(SemanticTokenModifiers.Constant)));
		}
	}

	[Fact]
	public void DelegateValuedGlobalInNamespace_InvocationStaysGlobal()
	{
		const string native = "namespace Native;\nunsafe \"C\" delegate void GlProc();\nglobal var GlProc UniformMatrix4fv;\n";
		const string main =
			"using Native;\nnamespace App;\nint main() { Native.UniformMatrix4fv(); return 0; }\n";
		var x = Open("Main.cvl", ("Native.cvl", native), ("Main.cvl", main));
		using (x.Fixture)
		{
			var tokens = WithText(x.Document, x.Document.GetSemanticTokens(), "UniformMatrix4fv");

			Assert.NotEmpty(tokens);
			Assert.All(tokens, t => Assert.Equal(ToolingSymbolKind.Global, t.Kind));
			Assert.All(tokens, t => Assert.False(t.Modifiers.HasFlag(SemanticTokenModifiers.ExternFunction)));
		}
	}

	[Fact]
	public void CrlfAndSurrogate_BeforeIdentifiers_KeepExactTokenSpans()
	{
		const string s = "// \U0001F600 comment\r\nstruct Point { int value; }\r\nint main() { val Point p = Point { value: 1 }; return p.value; }\r\n";
		var x = Open("Main.cvl", ("Main.cvl", s));
		using (x.Fixture)
		{
			var tokens = x.Document.GetSemanticTokens();

			Assert.NotEmpty(tokens);
			foreach (var token in tokens)
			{
				var text = x.Document.Text.GetText(token.Span);
				Assert.Matches("^[A-Za-z_][A-Za-z0-9_]*$", text);
			}

			for (var i = 1; i < tokens.Count; i++)
			{
				Assert.True(tokens[i - 1].Span.Start < tokens[i].Span.Start);
				Assert.True(tokens[i - 1].Span.End <= tokens[i].Span.Start, "tokens must not overlap");
			}
		}
	}

	[Fact]
	public void RepeatedTokenRequests_OnTheSameSnapshot_AreDeterministic()
	{
		const string s = "struct Point { int value; }\nint main() { val Point p = Point { value: 1 }; return p.value; }\n";
		var x = Open("Main.cvl", ("Main.cvl", s));
		using (x.Fixture)
		{
			var first = x.Document.GetSemanticTokens();
			var second = x.Document.GetSemanticTokens();

			Assert.Equal(first.Count, second.Count);
			for (var i = 0; i < first.Count; i++)
			{
				Assert.Equal(first[i].Span, second[i].Span);
				Assert.Equal(first[i].Kind, second[i].Kind);
				Assert.Equal(first[i].Modifiers, second[i].Modifiers);
			}
		}
	}
}
