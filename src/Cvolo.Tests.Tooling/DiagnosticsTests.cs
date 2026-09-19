using Cvolo.Compiler.Tooling;

namespace Cvolo.Tests.Tooling;

public sealed class DiagnosticsTests
{
	// A foreach whose iterator is invalid must be reported on the iterator expression itself,
	// not on the whole `foreach (...) { ... }` statement (which painted the entire loop body).
	private static string IteratorSource(string enumeratorMembers) =>
		"struct Range { int Start; int End; }\n" +
		"extension Range { Enumerator GetEnumerator() { return Enumerator { Value: Start }; } }\n" +
		"struct Enumerator { int Value; }\n" +
		"extension Enumerator {\n" +
		enumeratorMembers +
		"}\n" +
		"int Main() {\n" +
		"    var Range r(Start: 0, End: 3);\n" +
		"    foreach (val item in r) {\n" +
		"    }\n" +
		"    return 0;\n" +
		"}\n";

	private static void AssertForeachIteratorDiagnosticSpan(string source, string diagnosticId)
	{
		using var fixture = TempProject.Create(("Main.cvl", source));
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		var docId = project.GetDocumentId("Main.cvl");
		var document = project.InitialSnapshot.GetDocument(docId);

		var diagnostic = Assert.Single(document.GetDiagnostics(), d => d.Id == diagnosticId);

		// The iterator expression is the `r` in `foreach (val item in r)`.
		var expectedStart = source.IndexOf("in r)", StringComparison.Ordinal) + "in ".Length;
		Assert.Equal("r", source.Substring(expectedStart, 1));
		Assert.Equal(new TextSpan(expectedStart, 1), diagnostic.Location.Span);
	}

	[Fact]
	public void ForeachMoveNextNotBool_DiagnosticPointsAtIteratorExpression()
	{
		AssertForeachIteratorDiagnosticSpan(
			IteratorSource("    int MoveNext() { return Value; }\n    int Current() { return Value; }\n"),
			"CVL1083");
	}

	[Fact]
	public void ForeachMissingMoveNext_DiagnosticPointsAtIteratorExpression()
	{
		AssertForeachIteratorDiagnosticSpan(
			IteratorSource("    int Current() { return Value; }\n"),
			"CVL1081");
	}

	[Fact]
	public void ForeachMissingCurrent_DiagnosticPointsAtIteratorExpression()
	{
		AssertForeachIteratorDiagnosticSpan(
			IteratorSource("    bool MoveNext() { return true; }\n"),
			"CVL1082");
	}

	[Fact]
	public void UnknownReturnType_DiagnosticPointsAtReturnType_NotWholeFunction()
	{
		const string source = "refint Subtract(int left, int right) {\n    return left - right;\n}\n";
		using var fixture = TempProject.Create(("Main.cvl", source));
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		var docId = project.GetDocumentId("Main.cvl");
		var document = project.InitialSnapshot.GetDocument(docId);

		var diagnostic = Assert.Single(document.GetDiagnostics(), d => d.Message.Contains("Unknown return type", StringComparison.Ordinal));

		var expectedStart = source.IndexOf("refint", StringComparison.Ordinal);
		Assert.Equal(new TextSpan(expectedStart, "refint".Length), diagnostic.Location.Span);
	}

	[Fact]
	public void DuplicateFunction_DiagnosticPointsAtFunctionName_NotWholeFunction()
	{
		const string source =
			"int Add(int a, int b) { return a + b; }\n" +
			"int Add(int a, int b) { return a + b; }\n" +
			"int Main() { return 0; }\n";
		using var fixture = TempProject.Create(("Main.cvl", source));
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		var docId = project.GetDocumentId("Main.cvl");
		var document = project.InitialSnapshot.GetDocument(docId);

		var diagnostic = Assert.Single(document.GetDiagnostics(), d => d.Message.Contains("Duplicate definition of function", StringComparison.Ordinal));

		// The second (duplicate) declaration's name token.
		var secondAdd = source.IndexOf("Add", source.IndexOf("Add", StringComparison.Ordinal) + 1, StringComparison.Ordinal);
		Assert.Equal(new TextSpan(secondAdd, "Add".Length), diagnostic.Location.Span);
	}

	[Fact]
	public void AutoInferenceMutabilityWarning_PointsAtMethodName_NotWholeMethod()
	{
		const string source =
			"struct Counter { int Value; }\n" +
			"extension Counter {\n" +
			"    int Bump() { Value = Value + 1; return Value; }\n" +
			"}\n" +
			"int Main() { return 0; }\n";
		using var fixture = TempProject.Create(("Main.cvl", source));
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		var docId = project.GetDocumentId("Main.cvl");
		var document = project.InitialSnapshot.GetDocument(docId);

		var diagnostic = Assert.Single(document.GetDiagnostics(), d => d.Id == "CVL1011");

		var expectedStart = source.IndexOf("Bump", StringComparison.Ordinal);
		Assert.Equal(new TextSpan(expectedStart, "Bump".Length), diagnostic.Location.Span);
	}

	[Fact]
	public void DuplicateConstructor_DiagnosticPointsAtConstructorName_NotWholeConstructor()
	{
		const string source =
			"struct Name { int value; }\n" +
			"extension Name {\n" +
			"    Name(int v) { value = v; }\n" +
			"    Name(int v) { value = v; }\n" +
			"}\n" +
			"int main() { return 0; }\n";
		using var fixture = TempProject.Create(("Main.cvl", source));
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		var docId = project.GetDocumentId("Main.cvl");
		var document = project.InitialSnapshot.GetDocument(docId);

		var diagnostic = Assert.Single(document.GetDiagnostics(), d => d.Message.Contains("Duplicate constructor signature", StringComparison.Ordinal));

		var first = source.IndexOf("Name(int v)", StringComparison.Ordinal);
		var second = source.IndexOf("Name(int v)", first + 1, StringComparison.Ordinal);
		Assert.Equal(new TextSpan(second, "Name".Length), diagnostic.Location.Span);
	}

	[Fact]
	public void DefensiveInitialization_DiagnosticPointsAtConstructorName_NotWholeConstructor()
	{
		const string source =
			"struct Pair { int A; int B; }\n" +
			"extension Pair {\n" +
			"    Pair(int a) { A = a; }\n" +
			"}\n" +
			"int main() { return 0; }\n";
		using var fixture = TempProject.Create(("Main.cvl", source));
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		var docId = project.GetDocumentId("Main.cvl");
		var document = project.InitialSnapshot.GetDocument(docId);

		var diagnostic = Assert.Single(document.GetDiagnostics(), d => d.Message.Contains("Defensive initialization", StringComparison.Ordinal));

		var expectedStart = source.IndexOf("Pair(int a)", StringComparison.Ordinal);
		Assert.Equal(new TextSpan(expectedStart, "Pair".Length), diagnostic.Location.Span);
	}

	[Fact]
	public void UnknownExtendedType_DiagnosticPointsAtTypeName_NotWholeExtensionBlock()
	{
		const string source =
			"extension Namea {\n" +
			"    int Foo() { return 0; }\n" +
			"}\n" +
			"int main() { return 0; }\n";
		using var fixture = TempProject.Create(("Main.cvl", source));
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		var docId = project.GetDocumentId("Main.cvl");
		var document = project.InitialSnapshot.GetDocument(docId);

		var diagnostic = Assert.Single(document.GetDiagnostics(), d => d.Message.Contains("inside extension block", StringComparison.Ordinal));

		var expectedStart = source.IndexOf("Namea", StringComparison.Ordinal);
		Assert.Equal(new TextSpan(expectedStart, "Namea".Length), diagnostic.Location.Span);
	}

	[Fact]
	public void MalformedLiteralInArgument_AnchorsParseErrorOnTheLiteral_NotPunctuation()
	{
		// `1value` is a bad integer-suffix token; the parser skips it and would otherwise
		// report the cascade on the `:`/`)`. The diagnostic must land on `1value`.
		const string source =
			"struct Name { int value; }\n" +
			"int main() {\n" +
			"    var Name n(1value: 0);\n" +
			"    return 0;\n" +
			"}\n";
		using var fixture = TempProject.Create(("Main.cvl", source));
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		var docId = project.GetDocumentId("Main.cvl");
		var document = project.InitialSnapshot.GetDocument(docId);

		var diagnostic = Assert.Single(document.GetDiagnostics());

		var expectedStart = source.IndexOf("1value", StringComparison.Ordinal);
		Assert.Equal(new TextSpan(expectedStart, "1value".Length), diagnostic.Location.Span);
	}

	[Fact]
	public void MalformedSource_ReturnsParserDiagnostic_AttributedToDocument()
	{
		Timed.Out(() =>
		{
			using var fixture = TempProject.Create(("Main.cvl", "int main( {\n    return 0;\n}\n"));
			var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
			var docId = project.GetDocumentId("Main.cvl");
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
			var docId = project.GetDocumentId("Main.cvl");
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
			var docId = project.GetDocumentId("Main.cvl");

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
			var docId = project.GetDocumentId("Main.cvl");

			var first = project.InitialSnapshot.GetDocument(docId).GetDiagnostics();
			var second = project.InitialSnapshot.GetDocument(docId).GetDiagnostics();

			Assert.NotEmpty(first);
			Assert.Equal(first, second);
		});
	}
}
