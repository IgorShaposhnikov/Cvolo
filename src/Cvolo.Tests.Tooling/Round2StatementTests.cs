using Cvolo.Compiler.Tooling;
using Xunit;

namespace Cvolo.Tests.Tooling;

public sealed class Round2StatementTests
{
	private static (DocumentSnapshot Document, IReadOnlyList<Diagnostic> Diagnostics) Open(string source)
	{
		var fixture = TempProject.Create(("Main.cvl", source));
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		var document = project.InitialSnapshot.GetDocument(project.GetDocumentId("Main.cvl"));
		return (document, document.GetDiagnostics());
	}

	[Fact]
	public void AssignmentUsedAsCondition_IsRejected()
	{
		var (document, diagnostics) = Open("int main() { var g = 0; if (g = 5) { } return 0; }\n");
		var finding = Assert.Single(diagnostics, d => d.Id == "CVL4176");
		Assert.Contains("bool", finding.Message, StringComparison.Ordinal);
		Assert.Equal("g = 5", document.Text.GetText(finding.Location.Span));
		Assert.DoesNotContain(diagnostics, d => d.Id == "CVL0000");
	}

	[Fact]
	public void WhileAssignmentUsedAsCondition_IsRejected()
	{
		var (_, diagnostics) = Open("int main() { var g = 0; while (g = 5) { } return 0; }\n");
		Assert.Contains(diagnostics, d => d.Id == "CVL4176");
	}

	[Theory]
	[InlineData("int main() { var g = 0; if (g == 5) { } return 0; }\n")]
	[InlineData("int main() { var g = 0; if (g != 5) { } return 0; }\n")]
	[InlineData("int main() { var g = 0; g = 5; return 0; }\n")]
	public void ValidConditionForms_AreAccepted(string source)
	{
		var (_, diagnostics) = Open(source);
		Assert.DoesNotContain(diagnostics, d => d.Id == "CVL4176");
	}

	[Fact]
	public void SameScopeDuplicateLocal_IsRejectedOnce()
	{
		var (document, diagnostics) = Open("int main() { var param = 'a'; var param = 1; return 0; }\n");
		var finding = Assert.Single(diagnostics, d => d.Id == "CVL4140");
		Assert.Equal("var param = 1;", document.Text.GetText(finding.Location.Span));
		Assert.DoesNotContain(diagnostics, d => d.Id == "CVL0000");
	}

	[Fact]
	public void DistinctIdentifierNames_AreNotReportedAsDuplicates()
	{
		var (_, diagnostics) = Open("int main() { var params = 1; var param = 2; return 0; }\n");
		Assert.DoesNotContain(diagnostics, d => d.Id == "CVL4140");
	}
}
