using Cvolo.Compiler.Tooling;
using Xunit;

namespace Cvolo.Tests.Tooling;

public sealed class GenericCallValidationTests
{
	private static IReadOnlyList<Diagnostic> Diagnose(string member)
	{
		var source = "int main() { val layout = " + member + "; return 0; }\n";
		var fixture = TempProject.Create(("Main.cvl", source));
		using (fixture)
		{
			var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
			var document = project.InitialSnapshot.GetDocument(project.GetDocumentId("Main.cvl"));
			return document.GetDiagnostics();
		}
	}

	[Fact]
	public void GenericAssociatedCall_WithTypeArgument_IsValid()
	{
		Assert.Empty(Diagnose("Layout.FromType<int>()"));
	}

	[Fact]
	public void GenericAssociatedCall_WithoutTypeArgument_IsDiagnosed()
	{
		var diagnostics = Diagnose("Layout.FromType()");

		Assert.Contains(diagnostics, d => d.Id == "CVL4063");
		Assert.DoesNotContain(diagnostics, d => d.Id == "CVL0000");
		Assert.DoesNotContain(diagnostics, d => d.Id == "CVL4084");
	}

	[Fact]
	public void GenericAssociatedCall_WithEmptyTypeArgumentList_IsDiagnosed()
	{
		// An empty angle list is a syntax error, not an inference failure: the
		// parser reports it locally at the '<>' and no type argument is resolved.
		var diagnostics = Diagnose("Layout.FromType<>()");

		Assert.Contains(diagnostics, d => d.Id == "CVL4173");
		Assert.DoesNotContain(diagnostics, d => d.Id == "CVL0000");
	}
}
