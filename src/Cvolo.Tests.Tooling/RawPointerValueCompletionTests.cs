using Cvolo.Compiler.Tooling;
using Cvolo.Compiler.Tooling.Completion;

namespace Cvolo.Tests.Tooling;

/// <summary>
/// A raw-pointer field rejects `null` in safe code, so the editor must not suggest a keyword the
/// compiler will refuse. The same slot inside an unsafe region still accepts `null`, and a
/// non-pointer field is unaffected.
/// </summary>
public sealed class RawPointerValueCompletionTests
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
	public void SafeRawPointerValueSlot_DoesNotOfferNull_ButOffersDefault()
	{
		var result = Complete(
			"struct Window { void* Ptr; int Width; }\n" +
			"int main()\n" +
			"{\n" +
			"    Window w = Window { Ptr = | };\n" +
			"    return 0;\n" +
			"}\n");

		Assert.DoesNotContain(result.Candidates, c => c.Label == "null" && c.Kind == CompletionKind.Keyword);
		Assert.Contains(result.Candidates, c => c.Label == "default" && c.Kind == CompletionKind.Keyword);
	}

	[Fact]
	public void UnsafeRawPointerValueSlot_StillOffersNull()
	{
		var result = Complete(
			"struct Window { void* Ptr; int Width; }\n" +
			"int main()\n" +
			"{\n" +
			"    unsafe\n" +
			"    {\n" +
			"        Window w = Window { Ptr = | };\n" +
			"    }\n" +
			"    return 0;\n" +
			"}\n");

		Assert.Contains(result.Candidates, c => c.Label == "null" && c.Kind == CompletionKind.Keyword);
	}

	[Fact]
	public void NonPointerValueSlot_StillOffersNull()
	{
		var result = Complete(
			"struct Bag { int Value; }\n" +
			"int main()\n" +
			"{\n" +
			"    Bag b = Bag { Value = | };\n" +
			"    return 0;\n" +
			"}\n");

		Assert.Contains(result.Candidates, c => c.Label == "null" && c.Kind == CompletionKind.Keyword);
	}
}
