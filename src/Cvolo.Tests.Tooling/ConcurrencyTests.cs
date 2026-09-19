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
			var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
			var baseSnapshot = project.InitialSnapshot;
			var docId = project.GetDocumentId("Main.cvl");
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
	[Fact]
	public void ConcurrentCompletions_OnDifferentNamespaceContexts_DoNotBleedAndRestoreBinderState()
	{
		Timed.Out(() =>
		{
			static (string Source, int Position) Split(string sourceWithMarker)
			{
				var position = sourceWithMarker.IndexOf('|');
				Assert.True(position >= 0);
				return (sourceWithMarker.Remove(position, 1), position);
			}

			var (sourceA, positionA) = Split(
				"namespace AppA;\nusing LibA;\nint RunA() { return Alph|; }\n");
			var (sourceB, positionB) = Split(
				"namespace AppB;\nusing LibB;\nint RunB() { return Bet|; }\n");

			using var fixture = TempProject.Create(
				("A.cvl", sourceA),
				("B.cvl", sourceB),
				("LibA.cvl", "namespace LibA;\npublic int Alpha() { return 1; }\n"),
				("LibB.cvl", "namespace LibB;\npublic int Beta() { return 2; }\n"));

			var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
			var snapshot = project.InitialSnapshot;
			var documentA = snapshot.GetDocument(project.GetDocumentId("A.cvl"));
			var documentB = snapshot.GetDocument(project.GetDocumentId("B.cvl"));

			var analysis = snapshot.GetAnalysis();
			Assert.NotNull(analysis.BinderContext);
			var binderContext = analysis.BinderContext!;
			var baselineUnit = binderContext.CurrentUnit;
			var baselineNamespace = binderContext.CurrentNamespace;

			var expectedA = documentA.GetCompletions(positionA);
			var expectedB = documentB.GetCompletions(positionB);

			Assert.Contains(expectedA.Candidates, c => c.Label == "Alpha");
			Assert.DoesNotContain(expectedA.Candidates, c => c.Label == "Beta");
			Assert.Contains(expectedB.Candidates, c => c.Label == "Beta");
			Assert.DoesNotContain(expectedB.Candidates, c => c.Label == "Alpha");

			Parallel.For(0, 64, i =>
			{
				var actual = (i & 1) == 0
					? documentA.GetCompletions(positionA)
					: documentB.GetCompletions(positionB);
				var expected = (i & 1) == 0 ? expectedA : expectedB;

				Assert.Equal(expected.ReplacementRange, actual.ReplacementRange);
				Assert.True(expected.Candidates.SequenceEqual(actual.Candidates));
			});

			var afterA = documentA.GetCompletions(positionA);
			var afterB = documentB.GetCompletions(positionB);
			Assert.True(expectedA.Candidates.SequenceEqual(afterA.Candidates));
			Assert.True(expectedB.Candidates.SequenceEqual(afterB.Candidates));
			Assert.Same(baselineUnit, binderContext.CurrentUnit);
			Assert.Equal(baselineNamespace, binderContext.CurrentNamespace);
		});
	}

}
