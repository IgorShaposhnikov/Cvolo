using Cvolo.Compiler.Tooling;

namespace Cvolo.Tests.Tooling;

public sealed class DiagnosticsTests
{
	[Fact]
	public void MalformedSource_ReturnsParserDiagnostic_AttributedToDocument()
	{
		Timed.Out(() =>
		{
			using var fixture = TempProject.Create(("Main.cvl", "int main( {\n    return 0;\n}\n"));
			var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
			var docId = project.InitialSnapshot.DocumentIds[0];
			var document = project.InitialSnapshot.GetDocument(docId);

			var diagnostics = document.GetDiagnostics();

			Assert.Contains(diagnostics, d => d.Severity == DiagnosticSeverity.Error);

			var error = Assert.Single(diagnostics, d => d.Severity == DiagnosticSeverity.Error);
			Assert.Equal(docId, error.Location.DocumentId);
			Assert.False(string.IsNullOrEmpty(error.Id));
			Assert.False(string.IsNullOrEmpty(error.Message));
			Assert.InRange(error.Location.Span.Start, 0, document.Text.Length);
			Assert.InRange(error.Location.Span.End, 0, document.Text.Length);
		});
	}

	[Fact]
	public void SyntacticallyValidSource_WithSemanticError_ReturnsBinderDiagnostic()
	{
		Timed.Out(() =>
		{
			using var fixture = TempProject.Create(
				("Main.cvl", "int main() {\n    val int x = \"boom\";\n    return 0;\n}\n"),
				("Lib.cvl", "int Helper() { return 42; }\n"));
			var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
			var mainId = project.GetDocumentId("Main.cvl");
			var libId = project.GetDocumentId("Lib.cvl");

			var mainDiagnostics = project.InitialSnapshot.GetDocument(mainId).GetDiagnostics();
			var libDiagnostics = project.InitialSnapshot.GetDocument(libId).GetDiagnostics();

			Assert.Contains(mainDiagnostics, d => d.Severity == DiagnosticSeverity.Error);
			Assert.Empty(libDiagnostics);
		});
	}

	[Fact]
	public void ValidCrossFileProject_ProducesNoErrors()
	{
		Timed.Out(() =>
		{
			using var fixture = TempProject.Create(
				("Main.cvl", "int main() {\n    val int x = Add(1, 2);\n    return x;\n}\n"),
				("Lib.cvl", "int Add(int a, int b) { return a + b; }\n"));
			var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);

			foreach (var docId in project.InitialSnapshot.DocumentIds)
			{
				var diagnostics = project.InitialSnapshot.GetDocument(docId).GetDiagnostics();
				Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error);
			}
		});
	}

	[Fact]
	public void EverySnapshotDiagnostic_ResolvesWithinSameSnapshot()
	{
		Timed.Out(() =>
		{
			using var fixture = TempProject.Create(
				("Main.cvl", "int main() {\n    val int x = \"boom\";\n    return 0;\n}\n"),
				("Lib.cvl", "int Helper() { return 42; }\n"));
			var snapshot = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath).InitialSnapshot;

			foreach (var docId in snapshot.DocumentIds)
			{
				var document = snapshot.GetDocument(docId);

				foreach (var diagnostic in document.GetDiagnostics())
				{
					Assert.True(snapshot.TryGetDocument(diagnostic.Location.DocumentId, out _),
						"Primary location must resolve inside the producing snapshot.");
					var text = snapshot.GetDocument(diagnostic.Location.DocumentId).Text;
					Assert.InRange(diagnostic.Location.Span.End, 0, text.Length);
				}
			}
		});
	}

	[Fact]
	public void CrossFileRelatedLocations_CanTargetAnotherDocumentId()
	{
		Timed.Out(() =>
		{
			using var fixture = TempProject.Create(
				("Main.cvl", "int main() { return 0; }\n"),
				("Lib.cvl", "int Helper() { return 42; }\n"));
			var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
			var snapshot = project.InitialSnapshot;
			var mainId = project.GetDocumentId("Main.cvl");
			var libId = project.GetDocumentId("Lib.cvl");

			var primary = new DiagnosticLocation(mainId, new TextSpan(0, 4), "primary");
			var related = new DiagnosticLocation(libId, new TextSpan(2, 6), "related in another file");
			var diagnostic = new Diagnostic(
				DiagnosticSeverity.Info,
				"CVLFIX000",
				"cross-file information",
				primary,
				[related]);

			Assert.Equal(libId, diagnostic.RelatedLocations[0].DocumentId);
			Assert.Equal(mainId, diagnostic.Location.DocumentId);

			Assert.True(snapshot.TryGetDocument(diagnostic.Location.DocumentId, out _));
			Assert.True(snapshot.TryGetDocument(diagnostic.RelatedLocations[0].DocumentId, out _));
		});
	}

	[Fact]
	public void OldSnapshotDiagnostics_DoNotChange_AfterDerivingBranch()
	{
		Timed.Out(() =>
		{
			using var fixture = TempProject.Create(("Main.cvl", "int main() {\n    val int x = \"boom\";\n    return 0;\n}\n"));
			var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
			var baseSnapshot = project.InitialSnapshot;
			var docId = baseSnapshot.DocumentIds[0];
			var before = baseSnapshot.GetDocument(docId).GetDiagnostics().ToArray();

			var branch = baseSnapshot.WithDocument(docId, SourceText.From("int main() { return 1; }\n"));

			var after = baseSnapshot.GetDocument(docId).GetDiagnostics();
			Assert.Equal(before, after);

			var branchDiagnostics = branch.GetDocument(docId).GetDiagnostics();
			Assert.DoesNotContain(branchDiagnostics, d => d.Severity == DiagnosticSeverity.Error);
		});
	}

	[Fact]
	public void SavedSpan_FromOldSnapshot_IsNotMeaningful_AgainstDifferentBranch()
	{
		Timed.Out(() =>
		{
			using var fixture = TempProject.Create(("Main.cvl", "int main() {\n    val int x = \"boom\";\n    return 0;\n}\n"));
			var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
			var baseSnapshot = project.InitialSnapshot;
			var docId = baseSnapshot.DocumentIds[0];

			var baseText = baseSnapshot.GetDocument(docId).Text.ToString();
			var savedDiagnostics = baseSnapshot.GetDocument(docId).GetDiagnostics();

			Assert.True(savedDiagnostics.All(d => d.Location.Span.End <= baseText.Length),
				"Saved spans are valid for the producing snapshot's text.");

			var editedText = "int main(){}";
			var branch = baseSnapshot.WithDocument(docId, SourceText.From(editedText));

			Assert.NotEqual(baseText.Length, editedText.Length);

			var branchDiagnostics = branch.GetDocument(docId).GetDiagnostics();
			Assert.True(branchDiagnostics.All(d => d.Location.Span.End <= editedText.Length),
				"Branch diagnostics are valid for the branch text; a saved span from the base snapshot is not.");
		});
	}

	[Fact]
	public void RepeatedAccess_OnSameImmutableSnapshot_ReturnsStableResults()
	{
		Timed.Out(() =>
		{
			using var fixture = TempProject.Create(("Main.cvl", "int main() {\n    val int x = \"boom\";\n    return 0;\n}\n"));
			var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
			var docId = project.InitialSnapshot.DocumentIds[0];

			var first = project.InitialSnapshot.GetDocument(docId).GetDiagnostics();
			var second = project.InitialSnapshot.GetDocument(docId).GetDiagnostics();

			Assert.NotEmpty(first);
			Assert.Equal(first, second);
		});
	}
}
