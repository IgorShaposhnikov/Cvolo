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

	[Fact]
	public void AggregateInitializer_ValuePosition_DoesNotOfferFieldNames()
	{
		var result = Complete(
			"struct Window { void* Ptr; int Width; }\n" +
			"int main() { Window w = Window { Ptr = | }; return 0; }\n");

		Assert.True(
			!result.Candidates.Any(c => c.Kind == CompletionKind.StructField),
			string.Join(", ", result.Candidates.Select(c => $"{c.Label}:{c.Kind}")));
	}

	// An incomplete initializer ending at a trailing comma is not parseable, so the parser loses
	// the initializer node. The name slot after the comma still names a field and must offer the
	// remaining fields — never an ambient local such as `deltaTime`.
	[Fact]
	public void AggregateInitializer_AfterTrailingComma_OffersRemainingFields()
	{
		var result = Complete(
			"struct CharacterBody { float X; }\n" +
			"struct Player { CharacterBody Body; float Yaw; float Pitch; }\n" +
			"int main() { float deltaTime = 0; Player p = Player { Body = default, | }; return 0; }\n");

		Assert.Contains(result.Candidates, c => c.Label == "Yaw" && c.Kind == CompletionKind.StructField);
		Assert.Contains(result.Candidates, c => c.Label == "Pitch" && c.Kind == CompletionKind.StructField);
		Assert.DoesNotContain(result.Candidates, c => c.Label == "Body");
		Assert.DoesNotContain(result.Candidates, c => c.Label == "deltaTime");
	}

	[Fact]
	public void AggregateInitializer_AfterTrailingCommaMultiline_OffersRemainingFields()
	{
		var result = Complete(
			"struct CharacterBody { float X; }\n" +
			"struct Player { CharacterBody Body; float Yaw; float Pitch; }\n" +
			"int main()\n{\n" +
			"    Player p = Player\n    {\n" +
			"        Body = default,\n        |\n    };\n" +
			"    return 0;\n}\n");

		Assert.Contains(result.Candidates, c => c.Label == "Yaw" && c.Kind == CompletionKind.StructField);
		Assert.Contains(result.Candidates, c => c.Label == "Pitch" && c.Kind == CompletionKind.StructField);
		Assert.DoesNotContain(result.Candidates, c => c.Label == "Body");
	}

	[Fact]
	public void AggregateInitializer_PartialName_OffersOnlyMatchingField()
	{
		var result = Complete(
			"struct Player { float Yaw; float Pitch; }\n" +
			"int main() { Player p = Player { Y| }; return 0; }\n");

		Assert.Contains(result.Candidates, c => c.Label == "Yaw" && c.Kind == CompletionKind.StructField);
		Assert.DoesNotContain(result.Candidates, c => c.Label == "Pitch");
	}

	[Fact]
	public void AggregateInitializer_EmptyMultiline_OffersFieldsNotGlobals()
	{
		var result = Complete(
			"struct CharacterBody { float X; }\n" +
			"struct Player { CharacterBody Body; float Yaw; float Pitch; }\n" +
			"int getchar(int c) { return c; }\n" +
			"int main()\n{\n" +
			"    Player p = Player\n    {\n        |\n    };\n" +
			"    return 0;\n}\n");

		Assert.Contains(result.Candidates, c => c.Label == "Body" && c.Kind == CompletionKind.StructField);
		Assert.Contains(result.Candidates, c => c.Label == "Yaw" && c.Kind == CompletionKind.StructField);
		Assert.Contains(result.Candidates, c => c.Label == "Pitch" && c.Kind == CompletionKind.StructField);
		Assert.DoesNotContain(result.Candidates, c => c.Label == "getchar");
	}
}
