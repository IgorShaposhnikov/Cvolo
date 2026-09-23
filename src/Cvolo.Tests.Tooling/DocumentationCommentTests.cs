using Cvolo.Compiler.Tooling;

namespace Cvolo.Tests.Tooling;

/// <summary>
/// Verifies that hover documentation follows Cvolo's explicit documentation-comment contract.
/// Ordinary line comments are source comments only and must never leak into public symbol docs.
/// </summary>
public sealed class DocumentationCommentTests
{
	[Fact]
	public void OrdinaryLineComment_IsNotExposedAsDocumentation()
	{
		const string source = """
// Implementation note only.
int Twice(int value) { return value + value; }
int Main() { return Twice(2); }
""";
		using var fixture = TempProject.Create(("Main.cvl", source));
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		var snapshot = project.InitialSnapshot;
		var document = snapshot.GetDocument(project.GetDocumentId("Main.cvl"));

		var symbol = document.GetSymbolAtPosition(source.LastIndexOf("Twice", StringComparison.Ordinal));

		Assert.NotNull(symbol);
		Assert.Null(symbol!.Documentation);
	}

	[Fact]
	public void TripleSlashComment_IsExposedAsDocumentation()
	{
		const string source = """
/// Doubles the supplied value.
/// <summary>Used by hover.</summary>
int Twice(int value) { return value + value; }
int Main() { return Twice(2); }
""";
		using var fixture = TempProject.Create(("Main.cvl", source));
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		var snapshot = project.InitialSnapshot;
		var document = snapshot.GetDocument(project.GetDocumentId("Main.cvl"));

		var symbol = document.GetSymbolAtPosition(source.LastIndexOf("Twice", StringComparison.Ordinal));

		Assert.NotNull(symbol);
		Assert.Equal("Doubles the supplied value.\nUsed by hover.", symbol!.Documentation);
	}

	[Fact]
	public void OrdinaryCommentBetweenDocsAndDeclaration_BreaksDocumentationAttachment()
	{
		const string source = """
/// This documentation is no longer adjacent.
// Ordinary comment creates a boundary.
int Twice(int value) { return value + value; }
int Main() { return Twice(2); }
""";
		using var fixture = TempProject.Create(("Main.cvl", source));
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		var snapshot = project.InitialSnapshot;
		var document = snapshot.GetDocument(project.GetDocumentId("Main.cvl"));

		var symbol = document.GetSymbolAtPosition(source.LastIndexOf("Twice", StringComparison.Ordinal));

		Assert.NotNull(symbol);
		Assert.Null(symbol!.Documentation);
	}
}
