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
}
