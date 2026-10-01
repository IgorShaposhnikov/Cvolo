using Cvolo.Compiler.Tooling;
using Xunit;

namespace Cvolo.Tests.Tooling;

public sealed class ParserRecoveryTests
{
	private const string Head =
		"""
		public struct Widget { public int Ptr; }
		public extension Widget
		{
		    public Widget .Make<T>() { return Widget { Ptr: 0 }; }
		    public Widget Bump() { return Widget { Ptr: Ptr + 1 }; }
		}
		""";

	private static string Source(string member) =>
		Head + "\nint main() { val w = " + member + "; val y = 1; return 0; }\n";

	private static (DocumentSnapshot Document, string Text) Open(string member)
	{
		var text = Source(member);
		var fixture = TempProject.Create(("Main.cvl", text));
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		var document = project.InitialSnapshot.GetDocument(project.GetDocumentId("Main.cvl"));
		return (document, text);
	}

	private static List<(string Text, ToolingSymbolKind Kind)> Tokens(DocumentSnapshot document) =>
		document.GetSemanticTokens()
			.Select(token => (document.Text.GetText(token.Span), token.Kind))
			.ToList();

	[Fact]
	public void CompleteCall_HasNoDiagnosticsAndKeepsItsClassification()
	{
		var (document, _) = Open("Widget.Make<int>()");

		Assert.Empty(document.GetDiagnostics());

		var tokens = Tokens(document);
		Assert.Contains(tokens, token => token.Text == "Widget" && token.Kind == ToolingSymbolKind.Struct);
		Assert.Contains(tokens, token => token.Text == "Make" && token.Kind == ToolingSymbolKind.AssociatedFunction);
	}

	[Fact]
	public void IncompleteMemberAccess_IsDiagnosedLocallyAndDoesNotPoisonWhatFollows()
	{
		var (document, text) = Open("Widget.Make");

		var diagnostics = document.GetDiagnostics();
		var finding = Assert.Single(diagnostics);
		var incompleteStart = text.IndexOf("Widget.Make", StringComparison.Ordinal);
		Assert.InRange(
			finding.Location.Span.Start,
			incompleteStart,
			incompleteStart + "Widget.Make".Length - 1);

		var yStart = text.IndexOf("val y", StringComparison.Ordinal);
		Assert.All(diagnostics, d => Assert.True(d.Location.Span.Start < yStart, "the incomplete expression must not poison the statements below it"));

		var tokens = Tokens(document);
		Assert.Contains(tokens, token => token.Text == "Widget" && token.Kind == ToolingSymbolKind.Struct);
		Assert.Contains(tokens, token => token.Text == "Make" && token.Kind == ToolingSymbolKind.AssociatedFunction);
	}

	[Fact]
	public void IncompleteGenericMemberAccess_IsDiagnosedLocallyAndKeepsCompletionAndClassification()
	{
		var (document, text) = Open("Widget.Make<");

		var diagnostics = document.GetDiagnostics();
		Assert.Single(diagnostics);
		var yStart = text.IndexOf("val y", StringComparison.Ordinal);
		Assert.All(diagnostics, d => Assert.True(d.Location.Span.Start < yStart, "the incomplete expression must not poison the statements below it"));

		var tokens = Tokens(document);
		Assert.Contains(tokens, token => token.Text == "Widget" && token.Kind == ToolingSymbolKind.Struct);
		Assert.Contains(tokens, token => token.Text == "Make" && token.Kind == ToolingSymbolKind.AssociatedFunction);
		Assert.Contains(tokens, token => token.Text == "<" && token.Kind == ToolingSymbolKind.Operator);

		var completionPosition = yStart + "val ".Length;
		Assert.NotEmpty(document.GetCompletions(completionPosition).Candidates);
	}

	[Fact]
	public void IncompleteMemberAccess_KeepsCompletionAvailable()
	{
		var (document, text) = Open("Widget.Make");

		var completionPosition = text.IndexOf("val y", StringComparison.Ordinal) + "val ".Length;
		Assert.NotEmpty(document.GetCompletions(completionPosition).Candidates);
	}
}
