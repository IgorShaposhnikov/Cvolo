using System.Collections.Concurrent;
using Cvolo.Compiler.Tooling;

namespace Cvolo.Tests.Tooling;

public sealed class ConcurrencyTests
{
	private const string BaseText = "int main() {\n    return 0;\n}\n";

	[Fact]
	public void ConcurrentReads_OnOneSnapshot_ReturnStableResults()
	{
		Timed.Out(() =>
		{
			using var fixture = TempProject.Create(
				("Main.cvl", "int main() {\n    val int x = \"boom\";\n    return 0;\n}\n"),
				("Lib.cvl", "int Helper() { return 42; }\n"));
			var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
			var snapshot = project.InitialSnapshot;
			var docId = project.GetDocumentId("Main.cvl");

			var expectedDiagnostics = snapshot.GetDocument(docId).GetDiagnostics().ToArray();
			var expectedOffset = snapshot.GetDocument(docId).Text.GetOffset(new LinePosition(1, 0));

			Parallel.For(0, 32, _ =>
			{
				var document = snapshot.GetDocument(docId);
				Assert.Equal(expectedOffset, document.Text.GetOffset(new LinePosition(1, 0)));
				Assert.Equal(new LinePosition(1, 0), document.Text.GetLinePosition(expectedOffset));

				var diagnostics = document.GetDiagnostics();
				Assert.Equal(expectedDiagnostics, diagnostics);
			});
		});
	}

	[Fact]
	public void ConcurrentReads_OnManySnapshots_AreStable()
	{
		Timed.Out(() =>
		{
			using var fixture = TempProject.Create(
				("Main.cvl", "int main() {\n    val int x = \"boom\";\n    return 0;\n}\n"),
				("Lib.cvl", "int Helper() { return 42; }\n"));
			var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
			var baseSnapshot = project.InitialSnapshot;
			var docId = project.GetDocumentId("Main.cvl");

			var expectedDiagnostics = baseSnapshot.GetDocument(docId).GetDiagnostics();
			var branches = Enumerable.Range(0, 4)
				.Select(i => baseSnapshot.WithDocument(docId, SourceText.From($"int main() {{ return {i}; }}\n")))
				.ToArray();

			Parallel.For(0, 12, i =>
			{
				var branch = branches[i % branches.Length];
				var diagnostics = branch.GetDocument(docId).GetDiagnostics();
				Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error);

				var baseDocDiagnostics = baseSnapshot.GetDocument(docId).GetDiagnostics();
				Assert.Equal(expectedDiagnostics, baseDocDiagnostics);
			});
		});
	}

	[Fact]
	public void ConcurrentBranching_FromOneBase_KeepsBranchesIndependent()
	{
		Timed.Out(() =>
		{
			using var fixture = TempProject.Create(("Main.cvl", BaseText));
			var baseSnapshot = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath).InitialSnapshot;
			var docId = baseSnapshot.DocumentIds[0];
			var results = new ConcurrentBag<(int Index, string Text)>();

			Parallel.For(0, 32, i =>
			{
				var text = $"int main() {{ return {i}; }}\n";
				var branch = baseSnapshot.WithDocument(docId, SourceText.From(text));
				results.Add((i, branch.GetDocument(docId).Text.ToString()));
			});

			Assert.Equal(32, results.Count);
			foreach (var (index, text) in results)
				Assert.Equal($"int main() {{ return {index}; }}\n", text);

			Assert.Equal(BaseText, baseSnapshot.GetDocument(docId).Text.ToString());
		});
	}

	[Fact]
	public void ConcurrentDocumentIdLookups_FromManyThreads_AreConsistent()
	{
		Timed.Out(() =>
		{
			using var fixture = TempProject.Create(
				("Main.cvl", BaseText),
				("Lib.cvl", "int Helper() { return 42; }\n"));
			var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
			var snapshot = project.InitialSnapshot;
			var mainId = project.GetDocumentId("Main.cvl");

			Parallel.For(0, 64, _ =>
			{
				Assert.True(project.TryGetDocumentId("Main.cvl", out var id));
				Assert.Equal(mainId, id);
				Assert.NotNull(snapshot.GetDocument(mainId));
			});
		});
	}
}
