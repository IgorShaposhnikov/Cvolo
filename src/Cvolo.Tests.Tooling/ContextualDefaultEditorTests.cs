using Cvolo.Compiler.Tooling;
using Cvolo.Compiler.Tooling.Completion;

namespace Cvolo.Tests.Tooling;

/// <summary>
/// Editor-facing outcomes for contextual defaults: the diagnostics a user sees and the
/// candidates offered at a field-value slot must agree with the compiler's own verdict.
/// The editor must never present a raw-pointer default as a type mismatch, and must never
/// nudge a raw pointer toward a null literal.
/// </summary>
public sealed class ContextualDefaultEditorTests
{
	private static DocumentSnapshot Document(string source)
	{
		var fixture = TempProject.Create(("Main.cvl", source));
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		return project.InitialSnapshot.GetDocument(project.GetDocumentId("Main.cvl"));
	}

	[Fact]
	public void SafeRawPointerNullField_ReportsTheSafetyCauseWithoutATypeMismatch()
	{
		var document = Document(
			"struct Window { void* Ptr; int Width; }\n" +
			"int main() { Window w = Window { Ptr = null, Width = 1 }; return 0; }\n");

		var diagnostics = document.GetDiagnostics();

		Assert.Contains(diagnostics, d => d.Id == "CVL1104");
		Assert.DoesNotContain(diagnostics, d => d.Id == "CVL4019");
	}

	[Fact]
	public void UnsafeRawPointerNullField_IsAccepted()
	{
		var document = Document(
			"struct Window { void* Ptr; int Width; }\n" +
			"int main() { unsafe { Window w = Window { Ptr = null, Width = 1 }; } return 0; }\n");

		Assert.DoesNotContain(document.GetDiagnostics(), d => d.Severity == DiagnosticSeverity.Error);
	}

	[Fact]
	public void DefaultFieldValues_AreAcceptedWithoutAMismatch()
	{
		var document = Document(
			"struct Window { void* Ptr; int Width; }\n" +
			"int main() { Window w = Window { Ptr = default, Width = default }; return 0; }\n");

		Assert.DoesNotContain(
			document.GetDiagnostics(),
			d => d.Id is "CVL4019" or "CVL4196" or "CVL4197");
	}

	[Fact]
	public void FieldValuePosition_DoesNotOfferAggregateFieldNames()
	{
		var source =
			"struct Window { void* Ptr; int Width; }\n" +
			"int main() { Window w = Window { Ptr =  }; return 0; }\n";
		var position = source.IndexOf("Ptr =  ", StringComparison.Ordinal) + "Ptr = ".Length;
		var document = Document(source);

		var result = document.GetCompletions(position);

		Assert.DoesNotContain(result.Candidates, c => c.Kind == CompletionKind.StructField);
	}
}
