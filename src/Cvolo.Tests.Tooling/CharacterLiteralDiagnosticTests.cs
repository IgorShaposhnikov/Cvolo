using Cvolo.Compiler.Tooling;
using Xunit;

namespace Cvolo.Tests.Tooling;

public sealed class CharacterLiteralDiagnosticTests
{
	private static (DocumentSnapshot Document, IReadOnlyList<Diagnostic> Diagnostics) Open(string source)
	{
		var fixture = TempProject.Create(("Main.cvl", source));
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		var document = project.InitialSnapshot.GetDocument(project.GetDocumentId("Main.cvl"));
		return (document, document.GetDiagnostics());
	}

	[Fact]
	public void SingleCharacterLiteral_IsValid()
	{
		var (_, diagnostics) = Open("int main() { val x = 'a'; return 0; }\n");
		Assert.Empty(diagnostics);
	}

	[Fact]
	public void EscapedCharacterLiteral_IsValid()
	{
		var (_, diagnostics) = Open("int main() { val x = '\\n'; return 0; }\n");
		Assert.Empty(diagnostics);
	}

	[Theory]
	[InlineData("ab")]
	[InlineData("hello")]
	[InlineData("GLFW Window successfully terminated.")]
	public void MultipleCharacterLiteral_IsReportedOverTheWholeLiteral(string literal)
	{
		var source = "int main() { val x = '" + literal + "'; return 0; }\n";
		var (document, diagnostics) = Open(source);

		var finding = Assert.Single(diagnostics, d => d.Id == "CVL1907");
		Assert.Equal("'" + literal + "'", document.Text.GetText(finding.Location.Span));
		Assert.DoesNotContain(diagnostics, d => d.Id == "CVL0000");
	}

	[Fact]
	public void EmptyCharacterLiteral_IsReportedOverTheWholeLiteral()
	{
		var (document, diagnostics) = Open("int main() { val x = ''; return 0; }\n");

		var finding = Assert.Single(diagnostics, d => d.Id == "CVL1905");
		Assert.Equal("''", document.Text.GetText(finding.Location.Span));
	}
}
