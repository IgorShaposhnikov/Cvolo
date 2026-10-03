using Cvolo.Compiler.Tooling;
using Cvolo.Compiler.Tooling.Completion;

namespace Cvolo.Tests.Tooling;

/// <summary>
/// Feature: Namespace Alias Using Directives (spec 0.0.21) in editor features.
/// <c>using Alias = Namespace.Path;</c> makes the alias a namespace prefix: completion after
/// <c>Alias.</c> enumerates the target namespace's declarations, and the symbol surfaces as a
/// namespace alias carrying its target namespace.
/// </summary>
public sealed class NamespaceAliasToolingTests
{
	private static (string Source, int Position) SplitCursor(string sourceWithMarker)
	{
		var position = sourceWithMarker.IndexOf('|');
		Assert.True(position >= 0, "Test fixture must contain a '|' cursor marker.");
		return (sourceWithMarker.Remove(position, 1), position);
	}

	private static CompletionResult Complete(string sourceWithMarker, params (string File, string Source)[] otherFiles)
	{
		var (source, position) = SplitCursor(sourceWithMarker);
		var docs = new (string File, string Source)[otherFiles.Length + 1];
		docs[0] = ("Main.cvl", source);
		Array.Copy(otherFiles, 0, docs, 1, otherFiles.Length);

		using var fixture = TempProject.Create(docs);
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		var document = project.InitialSnapshot.GetDocument(project.GetDocumentId("Main.cvl"));
		return document.GetCompletions(position);
	}

	private static bool Contains(CompletionResult result, string label, CompletionKind kind) =>
		result.Candidates.Any(c => c.Label == label && c.Kind == kind);

	private static int At(string source, string needle, int start = 0) =>
		source.IndexOf(needle, start, StringComparison.Ordinal);

	[Fact]
	public void AliasCompletion_AfterAliasDot_OffersTargetNamespaceMembers()
	{
		var result = Complete(
			"using Shapes = ShapeLib;\n" +
			"namespace App;\n" +
			"int main() { return Shapes.Su|; }\n",
			("ShapeLib.cvl", "namespace ShapeLib;\npublic double SumOf(double a, double b) { return a + b; }\n"));

		Assert.True(Contains(result, "SumOf", CompletionKind.Function));
	}

	[Fact]
	public void AliasCompletion_AfterAliasDot_IsNotTreatedAsAValue()
	{
		var result = Complete(
			"using Shapes = ShapeLib;\n" +
			"namespace App;\n" +
			"int main() { return Shapes.Su|; }\n",
			("ShapeLib.cvl", "namespace ShapeLib;\npublic double SumOf(double a, double b) { return a + b; }\n"));

		Assert.DoesNotContain(result.Candidates, c => c.Label == "Shapes" && c.Kind == CompletionKind.Local);
	}

	[Fact]
	public void AliasHover_AtUse_NamesTheAliasAndTargetNamespace()
	{
		const string source =
			"using Native = OpenGL.Native;\n" +
			"namespace App;\n" +
			"int main() { return Native; }\n";

		using var fixture = TempProject.Create(("Main.cvl", source), ("Api.cvl", "namespace OpenGL.Native;\npublic int Viewport;\n"));
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		var document = project.InitialSnapshot.GetDocument(project.GetDocumentId("Main.cvl"));

		var aliasPosition = At(source, "return Native") + "return ".Length;
		var symbol = document.GetSymbolAtPosition(aliasPosition);

		Assert.NotNull(symbol);
		Assert.Equal(ToolingSymbolKind.NamespaceAlias, symbol!.Kind);
		Assert.Equal("Native", symbol.Name);
		Assert.Equal("namespace alias Native = OpenGL.Native", symbol.DisplayText);
	}

	[Fact]
	public void AliasHover_AtDeclaration_NamesTheAliasAndTargetNamespace()
	{
		const string source =
			"using Native = OpenGL.Native;\n" +
			"namespace App;\n" +
			"int main() { return 0; }\n";

		using var fixture = TempProject.Create(("Main.cvl", source), ("Api.cvl", "namespace OpenGL.Native;\npublic int Viewport;\n"));
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		var document = project.InitialSnapshot.GetDocument(project.GetDocumentId("Main.cvl"));

		var symbol = document.GetSymbolAtPosition(At(source, "using Native") + "using ".Length);

		Assert.NotNull(symbol);
		Assert.Equal(ToolingSymbolKind.NamespaceAlias, symbol!.Kind);
		Assert.Equal("namespace alias Native = OpenGL.Native", symbol.DisplayText);
	}

	[Fact]
	public void AliasNavigation_AtUse_GoesToTheAliasDeclaration()
	{
		const string source =
			"using Native = OpenGL.Native;\n" +
			"namespace App;\n" +
			"int main() { return Native; }\n";

		using var fixture = TempProject.Create(("Main.cvl", source), ("Api.cvl", "namespace OpenGL.Native;\npublic int Viewport;\n"));
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		var snapshot = project.InitialSnapshot;
		var document = snapshot.GetDocument(project.GetDocumentId("Main.cvl"));

		var aliasPosition = At(source, "return Native") + "return ".Length;
		var symbol = document.GetSymbolAtPosition(aliasPosition);
		Assert.NotNull(symbol);

		var definition = Assert.Single(snapshot.GetDefinitions(symbol!.SymbolId));

		// The declaration target is the alias name only - never the target namespace text.
		Assert.Equal("Native", document.Text.GetText(definition.SelectionSpan));
	}
}
