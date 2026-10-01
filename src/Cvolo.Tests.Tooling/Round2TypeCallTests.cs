using Cvolo.Compiler.Tooling;
using Xunit;

namespace Cvolo.Tests.Tooling;

public sealed class Round2TypeCallTests
{
	private const string TypeCall = "struct WP { } int main() { var p = WP(); return 0; }\n";

	private static DocumentSnapshot Open(string source)
	{
		var fixture = TempProject.Create(("Main.cvl", source));
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		return project.InitialSnapshot.GetDocument(project.GetDocumentId("Main.cvl"));
	}

	[Fact]
	public void TypeNameUsedAsACall_KeepsItsTypeSemanticToken()
	{
		var document = Open(TypeCall);
		var callStart = TypeCall.IndexOf("WP()", StringComparison.Ordinal);
		var tokens = document.GetSemanticTokens()
			.Select(token => (Text: document.Text.GetText(token.Span), token.Span, token.Kind))
			.ToList();

		Assert.Contains(tokens, token => token.Span.Start == callStart && token.Kind == ToolingSymbolKind.Struct);
	}

	[Fact]
	public void TypeNameUsedAsACall_IsReportedAsNotCallable()
	{
		var document = Open(TypeCall);
		var diagnostics = document.GetDiagnostics();

		Assert.Contains(diagnostics, diagnostic => diagnostic.Id == "CVL4177");
		Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "CVL1078");
		Assert.DoesNotContain(diagnostics, diagnostic => diagnostic.Id == "CVL0000");
	}
}
