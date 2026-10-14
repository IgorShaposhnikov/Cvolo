using Cvolo.Compiler.Tooling;

namespace Cvolo.Tests.Tooling;

/// <summary>
/// Regression fixtures for INC 3.1 G1/R4/R5: a parser fault in one region must not
/// swallow structurally independent faults later on the same physical source line.
/// The old <c>SyntaxErrorListener</c> suppressed every later diagnostic that shared a
/// line with an anchored fault; these tests pin the bounded-recovery-region behaviour.
/// </summary>
public sealed class SyntaxErrorRecoveryTests
{
	private static (DocumentSnapshot Document, IReadOnlyList<Diagnostic> Diagnostics) Analyze(string source)
	{
		var fixture = TempProject.Create(("Main.cvl", source));
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		var document = project.InitialSnapshot.GetDocument(project.GetDocumentId("Main.cvl"));
		return (document, document.GetDiagnostics());
	}

	[Fact]
	public void IndependentUnexpectedToken_AfterAnchoredBadLiteral_OnSameLine_IsRetained()
	{
		// REC-057: the malformed `1value:` argument is one fault; `foo bar` is a second,
		// structurally independent fault on the same physical line. Both must survive.
		const string source =
			"struct Name { int value; }\n" +
			"int main() {\n" +
			"    var Name n(1value: 0); val x = foo bar;\n" +
			"    return 0;\n" +
			"}\n";

		var (document, diagnostics) = Analyze(source);

		var literalStart = source.IndexOf("1value", System.StringComparison.Ordinal);
		Assert.Contains(diagnostics, d => d.Id == "CVL1903" && d.Location.Span.Start == literalStart);
		Assert.Contains(diagnostics, d => d.Id == "CVL4173" && d.Location.Span.Start == literalStart);

		var barStart = source.IndexOf("bar", System.StringComparison.Ordinal);
		Assert.Contains(diagnostics, d => d.Id == "CVL4173" && d.Location.Span.Start == barStart);

		Assert.DoesNotContain(diagnostics, d => d.Id == "CVL0000");
		Assert.True(document.Text.GetText(diagnostics.Single(d => d.Id == "CVL1903").Location.Span) == "1value");
	}

	[Fact]
	public void UnsupportedCoalesceAssign_AndIndependentFault_OnSameLine_AreBothReported()
	{
		// REC-058: `??=` is its own fault; the trailing `;` is an independent expression
		// fault on the same line. The dedicated `??=` diagnostic must not hide it.
		const string source = "int main() {\n    val a = 1; a ??= 2; b + ; return 0;\n}\n";

		var (_, diagnostics) = Analyze(source);

		Assert.Contains(diagnostics, d => d.Id == "CVL4187");
		Assert.Contains(diagnostics, d => d.Id == "CVL4173");
	}

	[Fact]
	public void TwoIndependentMalformedInitializers_OnSameLine_AreBothDiagnosed()
	{
		const string source =
			"struct Name { int value; }\n" +
			"int main() {\n" +
			"    var Name n(1value: 0); var Name m(2value: 0);\n" +
			"    return 0;\n" +
			"}\n";

		var (document, diagnostics) = Analyze(source);

		Assert.Equal(2, diagnostics.Count(d => d.Id == "CVL1903"));
		Assert.Contains(diagnostics, d => document.Text.GetText(d.Location.Span) == "1value");
		Assert.Contains(diagnostics, d => document.Text.GetText(d.Location.Span) == "2value");
	}

	[Fact]
	public void ValidProgram_HasNoSyntaxDiagnostics()
	{
		const string source = "int main() { val x = 1 + 2; return 0; }\n";
		var (_, diagnostics) = Analyze(source);
		Assert.DoesNotContain(diagnostics, d => d.Id is "CVL4173" or "CVL4174" or "CVL4175");
	}
}
