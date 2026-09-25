using Cvolo.Compiler.Tooling;
using Cvolo.Compiler.Tooling.Completion;

namespace Cvolo.Tests.Tooling;

public sealed class Inc7DocumentationAndOwnerTests
{
	private static (string Source, int Position) SplitCursor(string sourceWithMarker)
	{
		var position = sourceWithMarker.IndexOf('|');
		Assert.True(position >= 0, "Test fixture must contain a '|' cursor marker.");
		return (sourceWithMarker.Remove(position, 1), position);
	}

	[Fact]
	public void SignatureHelp_SeparatesSignatureAndParameterDocumentation()
	{
		const string source =
			"/// Adds two integer values.\n" +
			"int Sum(\n" +
			"    /// First value.\n" +
			"    int a,\n" +
			"    /// Second value.\n" +
			"    int b)\n" +
			"{\n" +
			"    return a + b;\n" +
			"}\n" +
			"int main() { return Sum(1, 2); }\n";
		using var fixture = TempProject.Create(("Main.cvl", source));
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		var document = project.InitialSnapshot.GetDocument(project.GetDocumentId("Main.cvl"));
		var position = source.IndexOf("1, 2", StringComparison.Ordinal) + "1, ".Length;

		var help = document.GetSignatureHelp(position);

		Assert.NotNull(help);
		var signature = Assert.Single(help!.Signatures);
		Assert.Equal("int Sum(int a, int b)", signature.Label);
		Assert.Equal("Adds two integer values.", signature.Documentation);
		Assert.Equal("First value.", signature.Parameters[0].Documentation);
		Assert.Equal("Second value.", signature.Parameters[1].Documentation);
		Assert.Equal("int a", signature.Label.Substring(signature.Parameters[0].LabelSpan.Start, signature.Parameters[0].LabelSpan.Length));
		Assert.Equal("int b", signature.Label.Substring(signature.Parameters[1].LabelSpan.Start, signature.Parameters[1].LabelSpan.Length));
	}

	[Fact]
	public void CompletionResolve_IgnoresOrdinaryLineCommentDocumentation()
	{
		const string sourceWithMarker =
			"// This is an implementation note, not API documentation.\n" +
			"int Sum(int a, int b) { return a + b; }\n" +
			"int main() { return Sum|; }\n";
		var (source, position) = SplitCursor(sourceWithMarker);
		using var fixture = TempProject.Create(("Main.cvl", source));
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		var snapshot = project.InitialSnapshot;
		var document = snapshot.GetDocument(project.GetDocumentId("Main.cvl"));

		var completion = document.GetCompletions(position);
		var candidate = Assert.Single(completion.Candidates, item => item.Label == "Sum");
		Assert.NotNull(candidate.ItemId);

		var resolved = snapshot.ResolveCompletion(candidate.ItemId!.Value);

		Assert.NotNull(resolved);
		Assert.Null(resolved!.Documentation);
	}

	[Fact]
	public void MemberCompletion_ExpandsOnlyTheSelectedReceiverOverloadGroup()
	{
		const string sourceWithMarker =
			"struct Cat {}\n" +
			"struct Dog {}\n" +
			"extension Cat { string Name(int value) { return \"\"; } }\n" +
			"extension Dog { string Name(string text) { return \"\"; } }\n" +
			"int main() { val Cat cat = default(Cat); cat.N|; return 0; }\n";
		var (source, position) = SplitCursor(sourceWithMarker);
		using var fixture = TempProject.Create(("Main.cvl", source));
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		var document = project.InitialSnapshot.GetDocument(project.GetDocumentId("Main.cvl"));

		var completion = document.GetCompletions(position);
		var names = completion.Candidates.Where(item => item.Label == "Name").ToArray();

		var candidate = Assert.Single(names);
		Assert.Equal(CompletionKind.Method, candidate.Kind);
		Assert.Equal("string Name(int value)", candidate.Detail);
		Assert.DoesNotContain("ref", candidate.Detail ?? string.Empty, StringComparison.Ordinal);
		Assert.DoesNotContain("string text", candidate.Detail ?? string.Empty, StringComparison.Ordinal);
	}
	[Fact]
	public void MemberCompletion_SingleCallable_KeepsPlainCallAndCallableInsertionPlan()
	{
		const string sourceWithMarker =
			"struct Cat {}\n" +
			"extension Cat { string Name(int value) { return \"\"; } }\n" +
			"int main() { val Cat cat = default(Cat); cat.Na|; return 0; }\n";
		var (source, position) = SplitCursor(sourceWithMarker);
		using var fixture = TempProject.Create(("Main.cvl", source));
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		var document = project.InitialSnapshot.GetDocument(project.GetDocumentId("Main.cvl"));

		var completion = document.GetCompletions(position);
		var candidate = Assert.Single(completion.Candidates, item => item.Label == "Name");

		Assert.Equal("Name()", candidate.PlainInsertText);
		Assert.NotNull(candidate.InsertionPlan);
		Assert.Contains(candidate.InsertionPlan!.SnippetSegments, segment => segment is CompletionLiteral { Text: "(" });
		Assert.Contains(candidate.InsertionPlan.SnippetSegments, segment => segment is CompletionLiteral { Text: ")" });
	}

	[Fact]
	public void CallableCompletion_DoesNotDuplicateExistingOpenParenthesis()
	{
		const string sourceWithMarker =
			"int Add(int value) { return value; }\n" +
			"int main() { return Add|(); }\n";
		var (source, position) = SplitCursor(sourceWithMarker);
		using var fixture = TempProject.Create(("Main.cvl", source));
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		var document = project.InitialSnapshot.GetDocument(project.GetDocumentId("Main.cvl"));

		var completion = document.GetCompletions(position);
		var candidate = Assert.Single(completion.Candidates, item => item.Label == "Add");

		Assert.Equal("Add", candidate.PlainInsertText);
	}

}
