using Cvolo.Compiler.Tooling;
using Cvolo.Compiler.Tooling.Completion;
using Cvolo.Compiler.Tooling.Internal;

namespace Cvolo.Tests.Tooling;

/// <summary>
/// A receiver written as an aggregate type name (<c>Option.</c>) selects that aggregate's own
/// members. A union reached this way must still offer its variants - including the void variant
/// used to build the empty case - rather than nothing.
/// </summary>
public sealed class UnionTypeNameCompletionTests
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
	public void TypeQualifiedOption_OffersTheNoneVariant()
	{
		var result = Complete(
			"int main() {\n" +
			"    Option<int> x = Option.|\n" +
			"    return 0;\n" +
			"}\n");

		Assert.Contains(result.Candidates, c => c.Label == "None" && c.Kind == CompletionKind.UnionVariant);
	}

	[Fact]
	public void TypeQualifiedNullable_OffersTheNoneVariant()
	{
		var result = Complete(
			"int main() {\n" +
			"    int? x = Option.|\n" +
			"    return 0;\n" +
			"}\n");

		Assert.Contains(result.Candidates, c => c.Label == "None" && c.Kind == CompletionKind.UnionVariant);
	}

	// An explicit generic receiver must resolve independently of any expected-type inference.
	[Fact]
	public void ExplicitGenericReceiver_OffersVariants()
	{
		var result = Complete(
			"int main() {\n" +
			"    Option<int> x = Option<int>.|\n" +
			"    return 0;\n" +
			"}\n");

		Assert.Contains(result.Candidates, c => c.Label == "None" && c.Kind == CompletionKind.UnionVariant);
		Assert.Contains(result.Candidates, c => c.Label == "Some" && c.Kind == CompletionKind.UnionVariant);
	}

	// A local variable that shadows the type name turns `Option.` into a value member access, so
	// the union's variants must not be offered.
	[Fact]
	public void TypeQualifiedReceiver_ShadowedByLocal_DoesNotOfferVariants()
	{
		var result = Complete(
			"int main() {\n" +
			"    int Option = 1;\n" +
			"    Option.|\n" +
			"    return 0;\n" +
			"}\n");

		Assert.DoesNotContain(result.Candidates, c => c.Label == "None" && c.Kind == CompletionKind.UnionVariant);
	}
}
