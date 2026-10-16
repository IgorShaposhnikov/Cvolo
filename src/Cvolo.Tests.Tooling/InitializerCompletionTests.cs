using Cvolo.Compiler.Tooling;
using Cvolo.Compiler.Tooling.Completion;
using Cvolo.Compiler.Tooling.Internal;

namespace Cvolo.Tests.Tooling;

/// <summary>
/// Completion inside an aggregate literal (`Window { ... }`) must offer only the resolved
/// aggregate's own instance fields. It must never leak ambient globals or free functions, and it
/// must not re-offer a field that was already supplied.
/// </summary>
public sealed class InitializerCompletionTests
{
	private static CompletionResult Complete(string sourceWithMarker)
	{
		var position = sourceWithMarker.IndexOf('|');
		Assert.True(position >= 0, "Test fixture must contain a '|' cursor marker.");
		var source = sourceWithMarker.Remove(position, 1);
		using var fixture = TempProject.Create(("Main.cvl", source));
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		var document = project.InitialSnapshot.GetDocument(project.GetDocumentId("Main.cvl"));
		return document.GetCompletions(position);
	}

	[Fact]
	public void AggregateInitializer_OffersFieldNotGlobals()
	{
		var result = Complete(
			"struct Window { void* glfwWindow; int Width; }\n" +
			"int getchar(int c) { return c; }\n" +
			"int main() { Window w = Window { g| }; return 0; }\n");

		Assert.Contains(result.Candidates, c => c.Label == "glfwWindow" && c.Kind == CompletionKind.StructField);
		Assert.DoesNotContain(result.Candidates, c => c.Label == "getchar");
		Assert.Equal(1, result.ReplacementRange.Length);
	}

	[Fact]
	public void AggregateInitializer_FieldInsertionAppendsAssignment()
	{
		var result = Complete(
			"struct Window { void* glfwWindow; int Width; }\n" +
			"int main() { Window w = Window { g| }; return 0; }\n");

		var field = Assert.Single(result.Candidates, c => c.Label == "glfwWindow" && c.Kind == CompletionKind.StructField);
		Assert.Equal("glfwWindow = ", field.PlainInsertText);
	}

	[Fact]
	public void AggregateInitializer_ExcludesAlreadySuppliedField()
	{
		var result = Complete(
			"struct Window { void* glfwWindow; int Width; }\n" +
			"int main() { Window w = Window { Width = 1, W|idth = 2 }; return 0; }\n");

		Assert.DoesNotContain(result.Candidates, c => c.Label == "Width" && c.Kind == CompletionKind.StructField);
	}
}
