using Cvolo.Compiler.Tooling;
using Cvolo.Packaging;

namespace Cvolo.Tests.Tooling;

public sealed class CodeFixTests
{
	private const string FloatLiteralSource = "int main()\n{\n    float f = 1.0;\n    return 0;\n}\n";

	private static (ProjectSnapshot Snapshot, DocumentSnapshot Document, DocumentId DocumentId) Open(string source)
	{
		using var fixture = TempProject.Create(("Main.cvl", source));
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		var documentId = project.GetDocumentId("Main.cvl");
		return (project.InitialSnapshot, project.InitialSnapshot.GetDocument(documentId), documentId);
	}

	[Fact]
	public void GetCodeFixes_OnFloatLiteralDiagnostic_ReturnsFloatSuffixInsertion()
	{
		var (snapshot, document, documentId) = Open(FloatLiteralSource);

		var diagnostic = Assert.Single(document.GetDiagnostics(), d => d.Id == "CVL1900");
		var fix = Assert.Single(document.GetCodeFixes(0, document.Text.Length));

		Assert.False(string.IsNullOrWhiteSpace(fix.Title));
		Assert.Equal(diagnostic, Assert.Single(fix.Diagnostics));

		var success = Assert.IsType<CodeFixSuccess>(snapshot.ResolveCodeFix(fix.Id));
		var edit = Assert.Single(success.Edits);

		Assert.Equal(documentId, edit.Document);
		Assert.Equal(0, edit.Span.Length);
		Assert.Equal(FloatLiteralSource.IndexOf("1.0", StringComparison.Ordinal) + "1.0".Length, edit.Span.Start);
		Assert.Equal("f", edit.NewText);

		var edited = FloatLiteralSource[..edit.Span.Start] + edit.NewText + FloatLiteralSource[edit.Span.End..];
		Assert.Contains("float f = 1.0f;", edited, StringComparison.Ordinal);
	}

	[Fact]
	public void GetCodeFixes_RangeOutsideDiagnostic_ReturnsEmpty()
	{
		var (_, document, _) = Open(FloatLiteralSource);

		Assert.NotEmpty(document.GetCodeFixes(0, document.Text.Length));
		Assert.Empty(document.GetCodeFixes(0, "int main()".Length));
	}

	[Fact]
	public void ResolveCodeFix_FromDifferentSnapshot_ReturnsFailure()
	{
		var (snapshot, document, documentId) = Open(FloatLiteralSource);
		var fix = Assert.Single(document.GetCodeFixes(0, document.Text.Length));

		var branched = snapshot.WithDocument(documentId, SourceText.From(FloatLiteralSource + "\n"));
		Assert.IsType<CodeFixFailure>(branched.ResolveCodeFix(fix.Id));
	}

	[Fact]
	public void GetCodeFixes_ForForeignDocument_Throws()
	{
		var (otherSnapshot, _, _) = Open(FloatLiteralSource);
		var (_, _, foreignDocumentId) = Open(FloatLiteralSource);

		Assert.Throws<KeyNotFoundException>(() => otherSnapshot.GetCodeFixes(foreignDocumentId, new TextSpan(0, 0)));
	}

	[Fact]
	public void GetCodeFixes_OnUnresolvedFunctionInNamespace_ReturnsAddUsingInsertion()
	{
		using var fixture = TempProject.Create(
			("Library.cvl", "namespace GLib\n{\n    public void Foo()\n    {\n    }\n}\n"),
			("Main.cvl", "int main()\n{\n    Foo();\n    return 0;\n}\n"));

		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		var documentId = project.GetDocumentId("Main.cvl");
		var document = project.InitialSnapshot.GetDocument(documentId);

		var diagnostic = Assert.Single(document.GetDiagnostics(), d => d.Id == "CVL1078");
		var fix = Assert.Single(document.GetCodeFixes(0, document.Text.Length));

		Assert.Contains("GLib", fix.Title, StringComparison.Ordinal);
		Assert.Equal(diagnostic, Assert.Single(fix.Diagnostics));

		var success = Assert.IsType<CodeFixSuccess>(project.InitialSnapshot.ResolveCodeFix(fix.Id));
		var edit = Assert.Single(success.Edits);

		Assert.Equal(documentId, edit.Document);
		Assert.Equal(0, edit.Span.Start);
		Assert.Equal(0, edit.Span.Length);
		Assert.Contains("using GLib;", edit.NewText, StringComparison.Ordinal);
	}

	[Fact]
	public void GetCodeFixes_OnDottedUnresolvedCall_DoesNotOfferAddUsing()
	{
		using var fixture = TempProject.Create(
			("Main.cvl", "int main()\n{\n    Console.Write(\"x\");\n    return 0;\n}\n"));

		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		var documentId = project.GetDocumentId("Main.cvl");
		var document = project.InitialSnapshot.GetDocument(documentId);

		Assert.Empty(document.GetDiagnostics().Where(d => d.Id == "CVL1078"));
		Assert.Empty(document.GetCodeFixes(0, document.Text.Length));
	}

	[Fact]
	public void GetCodeFixes_OnUnresolvedExternalPackageFunction_ReturnsAddUsingInsertion()
	{
		var metadata = new PackageApiMetadata
		{
			Units =
			[
				new PackageApiUnit
				{
					Namespace = "Widgets",
					Functions = [new PackageApiFunction { Name = "Foo" }]
				}
			]
		};

		var (snapshot, document) = ExternalPackageToolingFixture.Create(
			"int main()\n{\n    Foo();\n    return 0;\n}\n", metadata);

		var diagnostic = Assert.Single(document.GetDiagnostics(), d => d.Id == "CVL1078");
		var fix = Assert.Single(document.GetCodeFixes(0, document.Text.Length));

		Assert.Contains("Widgets", fix.Title, StringComparison.Ordinal);
		Assert.Equal(diagnostic, Assert.Single(fix.Diagnostics));

		var success = Assert.IsType<CodeFixSuccess>(snapshot.ResolveCodeFix(fix.Id));
		var edit = Assert.Single(success.Edits);

		Assert.Contains("using Widgets;", edit.NewText, StringComparison.Ordinal);
	}
}
