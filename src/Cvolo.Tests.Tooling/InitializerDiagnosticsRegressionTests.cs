using Cvolo.Compiler.Tooling;

namespace Cvolo.Tests.Tooling;

/// <summary>Regression fixtures for the initializer diagnostics/error-member contract.</summary>
public sealed class InitializerDiagnosticsRegressionTests
{
	private const string Pair =
		"public struct Pair<T, R> { public T Value1; public R Value2; }\n";

	private const string UnknownColor =
		"struct Point { int X; int Y; Color BaseColor; }\n";

	private static IReadOnlyList<Diagnostic> Analyze(string source)
	{
		using var fixture = TempProject.Create(("Main.cvl", source));
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		return project.InitialSnapshot.GetDocument(project.GetDocumentId("Main.cvl")).GetDiagnostics();
	}

	[Fact]
	public void UnknownFieldType_NestedInitializer_DoesNotInventMissingField()
	{
		var source = UnknownColor +
			"int main() { var Point p(X: 10, Y: 20, BaseColor: (R: 255, G: 0, B: 0)); return 0; }\n";
		var diagnostics = Analyze(source);
		Assert.Single(diagnostics, d => d.Id == "CVL4135");
		Assert.DoesNotContain(diagnostics, d => d.Id == "CVL4093");
		Assert.DoesNotContain(diagnostics, d => d.Id == "CVL4019");
		Assert.DoesNotContain(diagnostics, d => d.Id == "CVL0000");
	}

	[Fact]
	public void ActuallyMissingMember_RemainsIndependentOfInvalidDeclaration()
	{
		var source = UnknownColor +
			"int main() { val p = Point { X: 1, Y: 2, OtherColor: 3, BaseColor: (R: 3) }; return 0; }\n";
		var diagnostics = Analyze(source);
		Assert.Single(diagnostics, d => d.Id == "CVL4135");
		Assert.Single(diagnostics, d => d.Id == "CVL4093");
		Assert.DoesNotContain(diagnostics, d => d.Id == "CVL4019");
	}

	[Fact]
	public void TwoUnknownDeclarations_ProduceTwoIndependentRoots()
	{
		var diagnostics = Analyze("struct A { Missing Item; }\nstruct B { Missing Item; }\nint main() { return 0; }\n");
		Assert.Equal(2, diagnostics.Count(d => d.Id == "CVL4135"));
	}

	[Fact]
	public void ReusedInvalidMember_StillEmitsOneDeclarationError()
	{
		var source = UnknownColor +
			"int main() { val a = Point { X: 1, Y: 2, BaseColor: (R: 1) }; " +
			"val b = Point { X: 3, Y: 4, BaseColor: (G: 2) }; return 0; }\n";
		var diagnostics = Analyze(source);
		Assert.Single(diagnostics, d => d.Id == "CVL4135");
		Assert.DoesNotContain(diagnostics, d => d.Id == "CVL4093");
	}

	[Fact]
	public void OpenGenericInitializer_HasOneRoot_AndNoFieldConversions()
	{
		var diagnostics = Analyze(Pair + "int main() { val p = Pair { Value1: 1, Value2: 2 }; return 0; }\n");
		Assert.Single(diagnostics, d => d.Id == "CVL4219");
		Assert.DoesNotContain(diagnostics, d => d.Id == "CVL4019");
	}

	[Fact]
	public void SpecializedGenericInitializer_StillAcceptsCorrectValues()
	{
		var diagnostics = Analyze(Pair + "int main() { val p = Pair<int, int> { Value1: 1, Value2: 2 }; return 0; }\n");
		Assert.DoesNotContain(diagnostics, d => d.Id == "CVL4019" || d.Id == "CVL4219");
	}

	[Fact]
	public void SpecializedGenericInitializer_ReportsOneBadFieldOnce()
	{
		var diagnostics = Analyze(Pair + "int main() { val p = Pair<int, bool> { Value1: 1, Value2: 2 }; return 0; }\n");
		Assert.Single(diagnostics, d => d.Id == "CVL4019");
	}

	[Fact]
	public void TwoWrongFields_AreTwoIndependentErrors()
	{
		var diagnostics = Analyze(Pair + "int main() { val p = Pair<bool, bool> { Value1: 1, Value2: 2 }; return 0; }\n");
		Assert.Equal(2, diagnostics.Count(d => d.Id == "CVL4019"));
	}

	[Fact]
	public void MissingSemicolon_AnchorsAtPreviousDeclarationEnd()
	{
		var source = "struct Point { int X; }\nint main() {\n    val p = Point { X: 1 }\n    return 0;\n}\n";
		var diagnostics = Analyze(source);
		var diagnostic = Assert.Single(diagnostics, d => d.Id == "CVL4174");
		Assert.Contains("Expected ';' after variable declaration", diagnostic.Message, StringComparison.Ordinal);
		Assert.Equal(source.IndexOf("X: 1 }", StringComparison.Ordinal) + "X: 1 }".Length,
			diagnostic.Location.Span.Start);
		Assert.Equal(0, diagnostic.Location.Span.Length);
	}

	[Fact]
	public void MissingBrace_ReportsExpectedInitializerCloser()
	{
		var source = Pair + "int main() {\n val p = Pair<int, int> { Value1: 1, Value2: 2\n return 0;\n}\n";
		var diagnostics = Analyze(source);
		Assert.Contains(diagnostics, d => d.Id == "CVL4174" && d.Message.Contains("Expected '}' to close struct initializer", StringComparison.Ordinal));
	}

	[Fact]
	public void ValidReturnStatement_DoesNotReceivePunctuationError()
	{
		var diagnostics = Analyze("int main() { return 0; }\n");
		Assert.DoesNotContain(diagnostics, d => d.Id is "CVL4173" or "CVL4174");
	}
}
