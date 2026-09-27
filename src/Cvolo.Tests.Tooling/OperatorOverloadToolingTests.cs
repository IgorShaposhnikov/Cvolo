using Cvolo.Compiler.Tooling;

namespace Cvolo.Tests.Tooling;

/// <summary>
/// Verifies that an operator overload behaves like a first-class declaration in editor features
/// while keeping its punctuation identity: the operator token is the declaration's name, so the
/// outline and semantic tokens present the operator spelling, rename stays unavailable, and the
/// mangled registration token never leaks into member completion.
/// </summary>
public sealed class OperatorOverloadToolingTests
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
		"public struct Vec2 { public int X; public int Y; }\n" +
		"public extension Vec2 {\n" +
		"    public Vec2 operator +(Vec2 left, Vec2 right) { return left; }\n" +
		"    public Vec2 operator -(Vec2 value) { return value; }\n" +
		"    public int Magnitude(Vec2 value) { return value.X; }\n" +
		"}\n" +
		"int main() {\n" +
		"    val Vec2 a = Vec2 { X: 1, Y: 2 };\n" +
		"    val Vec2 b = Vec2 { X: 3, Y: 4 };\n" +
		"    val Vec2 c = a + b;\n" +
		"    return 0;\n" +
		"}\n";

	[Fact]
	public void OperatorDeclaration_HoversAsAnOperatorSignature()
	{
		var x = Open(("Main.cvl", Source));
		using (x.Fixture)
		{
			var symbol = x.Document.GetSymbolAtPosition(At(Source, "operator +") + "operator ".Length);
			Assert.NotNull(symbol);
			Assert.Equal(ToolingSymbolKind.Operator, symbol!.Kind);
			Assert.Equal("Vec2 operator +(Vec2 left, Vec2 right)", symbol.DisplayText);

			var definition = Assert.Single(x.Snapshot.GetDefinitions(symbol.SymbolId));

			// The selection range is the operator token alone, never the `operator` keyword.
			Assert.Equal("+", x.Document.Text.GetText(definition.SelectionSpan));
		}
	}

	[Fact]
	public void OperatorDeclaration_AppearsInTheDocumentOutline()
	{
		var x = Open(("Main.cvl", Source));
		using (x.Fixture)
		{
			var extension = Assert.Single(
				x.Document.GetDocumentSymbols(),
				symbol => symbol.Name == "extension Vec2");

			var operators = extension.Children
				.Where(child => child.Kind == ToolingSymbolKind.Operator)
				.ToArray();

			Assert.Equal(2, operators.Length);
			Assert.Equal(["operator +", "operator -"], operators.Select(symbol => symbol.Name).ToArray());
			Assert.Equal("Vec2 operator +(Vec2 left, Vec2 right)", operators[0].Detail);
		}
	}

	[Fact]
	public void OperatorDeclaration_SemanticTokenCoversTheOperatorToken()
	{
		var x = Open(("Main.cvl", Source));
		using (x.Fixture)
		{
			var declarationStart = At(Source, "operator +") + "operator ".Length;
			var declarationToken = Assert.Single(
				x.Document.GetSemanticTokens().Where(token => token.Kind == ToolingSymbolKind.Operator),
				token => token.Span.Start == declarationStart);

			Assert.Equal("+", x.Document.Text.GetText(declarationToken.Span));
		}
	}

	[Fact]
	public void OperatorDeclaration_IsNotRenameable()
	{
		var x = Open(("Main.cvl", Source));
		using (x.Fixture)
		{
			var symbol = x.Document.GetSymbolAtPosition(At(Source, "operator +") + "operator ".Length);
			Assert.NotNull(symbol);

			// An operator's name is punctuation, so there is no identifier to replace.
			Assert.Null(x.Document.PrepareRename(At(Source, "operator +") + "operator ".Length));
			Assert.IsType<RenameFailure>(x.Snapshot.RenameSymbol(symbol!.SymbolId, "Plus"));
		}
	}

	[Fact]
	public void TypeCompletion_NeverOffersTheMangledOperatorToken()
	{
		const string typed = "struct Vec2 { public int X; public int Y; }\n" +
			"public extension Vec2 {\n" +
			"    public Vec2 operator +(Vec2 left, Vec2 right) { return left; }\n" +
			"}\n" +
			"int main() {\n" +
			"    val Vec2 a = Vec2 { X: 1, Y: 2 };\n" +
			"    val int n = Vec2.\n" +
			"}\n";

		var x = Open(("Main.cvl", typed));
		using (x.Fixture)
		{
			var completion = x.Document.GetCompletions(typed.IndexOf("Vec2.\n", StringComparison.Ordinal) + "Vec2.".Length);
			var labels = completion.Candidates.Select(candidate => candidate.Label).ToArray();

			Assert.DoesNotContain("op_add", labels);
			Assert.DoesNotContain(labels, label => label.StartsWith("op_", StringComparison.Ordinal));
		}
	}
}
