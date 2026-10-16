using Cvolo.Compiler.Tooling;
using Cvolo.Compiler.Tooling.Completion;

namespace Cvolo.Tests.Tooling;

/// <summary>
/// The name slot of a declaration must not fall back to the ambient value surface. An incomplete
/// declaration (for example <c>Window |</c> or <c>Window g|</c>, written without a terminator) has
/// no bound declaration node, so without an explicit context the query leaked unrelated functions
/// and globals into a slot that can only name a variable.
/// </summary>
public sealed class DeclarationCompletionTests
{
	private const string Prelude =
		"struct Window { void* glfwWindow; int Width; }\n" +
		"int getchar(int c) { return c; }\n" +
		"int main() {\n";

	private static (string Source, int Position) SplitCursor(string sourceWithMarker)
	{
		var position = sourceWithMarker.IndexOf('|');
		Assert.True(position >= 0, "Test fixture must contain a '|' cursor marker.");
		return (sourceWithMarker.Remove(position, 1), position);
	}

	private static CompletionResult Complete(string body)
	{
		var (source, position) = SplitCursor(Prelude + body + "\n}\n");
		using var fixture = TempProject.Create(("Main.cvl", source));
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		var document = project.InitialSnapshot.GetDocument(project.GetDocumentId("Main.cvl"));
		return document.GetCompletions(position);
	}

	private static bool Contains(CompletionResult result, string label, CompletionKind kind)
		=> result.Candidates.Any(c => c.Label == label && c.Kind == kind);

	[Theory]
	[InlineData("    Window g|")]
	[InlineData("    Window |")]
	[InlineData("    Window win|")]
	public void DeclarationNameSlot_DoesNotOfferFunctionsOrGlobals(string body)
	{
		var result = Complete(body);

		var all = string.Join(", ", result.Candidates.Select(c => $"{c.Label}:{c.Kind}"));
		Assert.DoesNotContain(result.Candidates, c => c.Kind == CompletionKind.Function || c.Kind == CompletionKind.Method);
		Assert.DoesNotContain(result.Candidates, c => c.Label == "getchar");
		Assert.DoesNotContain(result.Candidates, c => c.Label == "main");
	}

	[Fact]
	public void PartialTypeName_IsStillClassifiedAsAType()
	{
		var result = Complete("    Win|");

		Assert.True(Contains(result, "Window", CompletionKind.Type),
			string.Join(", ", result.Candidates.Select(c => $"{c.Label}:{c.Kind}")));
	}
}
