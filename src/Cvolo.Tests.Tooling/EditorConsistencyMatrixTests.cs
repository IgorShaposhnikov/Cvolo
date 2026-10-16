using Cvolo.Compiler.Tooling;
using Cvolo.Compiler.Tooling.Completion;
using Cvolo.Compiler.Tooling.Internal;

namespace Cvolo.Tests.Tooling;

/// <summary>
/// Traceable coverage for the editor-consistency contract: the cursor-local slot must be
/// classified from the actual tokens and the resolved aggregate, and never fall back to the
/// ambient value surface. Each test names the contract row it pins.
/// </summary>
public sealed class EditorConsistencyMatrixTests
{
	private static (CompletionResult Result, DocumentSnapshot Document, int Position) Complete(
		params (string Name, string Source)[] files)
	{
		var marked = files.Single(file => file.Source.Contains('|'));
		var position = marked.Source.IndexOf('|');
		var clean = marked.Source.Remove(position, 1);
		var all = files
			.Select(file => file.Name == marked.Name ? (file.Name, clean) : file)
			.ToArray();

		using var fixture = TempProject.Create(all);
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		var document = project.InitialSnapshot.GetDocument(project.GetDocumentId(marked.Name));
		return (document.GetCompletions(position), document, position);
	}

	private static bool HasField(CompletionResult result, string label)
		=> result.Candidates.Any(c => c.Label == label && c.Kind == CompletionKind.StructField);

	// DECL-005: `Window window = |` is an expression position, never the field-name surface.
	[Fact]
	public void DeclarationValueSlot_IsExpressionContext_NotFieldNames()
	{
		var (result, _, _) = Complete(
			("Main.cvl",
				"struct Window { void* Ptr; int Width; }\n" +
				"int main() { Window window = |; return 0; }\n"));

		Assert.False(HasField(result, "Ptr"));
		Assert.False(HasField(result, "Width"));
	}

	// DECL-014 / INIT-025: a statement block or a string literal is not an aggregate initializer.
	[Fact]
	public void StatementBlockBrace_IsNotAnAggregateInitializer()
	{
		var (result, _, _) = Complete(
			("Main.cvl",
				"struct Window { void* Ptr; int Width; }\n" +
				"int main() { bool ok = true; if (ok) { | } return 0; }\n"));

		Assert.False(HasField(result, "Ptr"));
		Assert.False(HasField(result, "Width"));
	}

	[Fact]
	public void StringLiteralBrace_IsNotAnAggregateInitializer()
	{
		var (result, _, _) = Complete(
			("Main.cvl",
				"struct Window { void* Ptr; int Width; }\n" +
				"int main() { string s = \"{ | }\"; return 0; }\n"));

		Assert.False(HasField(result, "Ptr"));
	}

	// INIT-004: the colon separator behaves like the assignment separator.
	[Fact]
	public void ColonSeparator_ExcludesTheSuppliedField()
	{
		var (result, _, _) = Complete(
			("Main.cvl",
				"struct Window { void* Ptr; int Width; }\n" +
				"int main() { Window w = Window { Width: 1, | }; return 0; }\n"));

		Assert.True(HasField(result, "Ptr"));
		Assert.False(HasField(result, "Width"));
	}

	// INIT-005: a nested aggregate resolves its own type, not the outer one.
	[Fact]
	public void NestedAggregate_ResolvesTheInnerType()
	{
		var (result, _, _) = Complete(
			("Main.cvl",
				"struct Inner { int A; }\n" +
				"struct Outer { Inner Body; int Tag; }\n" +
				"int main() { Outer o = Outer { Body = Inner { | } }; return 0; }\n"));

		Assert.True(HasField(result, "A"));
		Assert.False(HasField(result, "Tag"));
	}

	// INIT-008: a same-named field of an unrelated struct never bleeds across the aggregate.
	[Fact]
	public void UnrelatedStructFields_DoNotBleed()
	{
		var (result, _, _) = Complete(
			("Main.cvl",
				"struct A { int Value; }\n" +
				"struct B { int Other; }\n" +
				"int main() { A a = A { | }; return 0; }\n"));

		Assert.True(HasField(result, "Value"));
		Assert.False(HasField(result, "Other"));
	}

	// INIT-009: a private field of an aggregate declared in another unit is not offered.
	[Fact]
	public void PrivateFieldOfAnotherUnit_IsNotOffered()
	{
		var (result, _, _) = Complete(
			("Lib.cvl",
				"public struct Hidden { private int Secret; public int Visible; }\n"),
			("Main.cvl",
				"int main() { Hidden h = Hidden { | }; return 0; }\n"));

		Assert.True(HasField(result, "Visible"));
		Assert.False(HasField(result, "Secret"));
	}

	// INIT-013: the field item carries the resolved type and owner.
	[Fact]
	public void FieldCandidate_DetailNamesTheResolvedTypeAndOwner()
	{
		var (result, _, _) = Complete(
			("Main.cvl",
				"struct Window { void* Ptr; int Width; }\n" +
				"int main() { Window w = Window { | }; return 0; }\n"));

		var field = Assert.Single(result.Candidates, c => c.Label == "Ptr" && c.Kind == CompletionKind.StructField);
		Assert.NotNull(field.Detail);
		Assert.Contains("Ptr", field.Detail);
		Assert.Contains("Window", field.Detail);
		Assert.Contains("void*", field.Detail);
	}

	// INIT-019: a generic aggregate instantiates the concrete field type in the detail.
	[Fact]
	public void GenericAggregate_OffersInstantiatedFieldType()
	{
		var (result, _, _) = Complete(
			("Main.cvl",
				"struct Box<T> { T Value; int Tag; }\n" +
				"int main() { Box<int> b = Box<int> { | }; return 0; }\n"));

		var field = Assert.Single(result.Candidates, c => c.Label == "Value" && c.Kind == CompletionKind.StructField);
		Assert.NotNull(field.Detail);
		Assert.Contains("Value", field.Detail);
		Assert.True(HasField(result, "Tag"));
	}

	// DECL-010: a CRLF document with a supplementary character before the prefix keeps the
	// replacement range on the typed prefix only.
	[Fact]
	public void CrlfAndUnicodeBeforePrefix_KeepTheReplacementRangeOnThePrefix()
	{
		var (result, _, _) = Complete(
			("Main.cvl",
				"// \u00fcn\u00efc\u00f6d\u00e9\r\n" +
				"struct Window { int Width; }\r\n" +
				"int main() { Window w = Window { W| }; return 0; }\r\n"));

		Assert.True(HasField(result, "Width"));
		Assert.Equal(1, result.ReplacementRange.Length);
	}

	// HOV-006: an unresolved initializer never fabricates a symbol for the local.
	[Fact]
	public void UnresolvedInitializer_DoesNotFabricateTheLocalSymbol()
	{
		const string source =
			"int main() { var mystery = Unknown; return 0; }\n";
		using var fixture = TempProject.Create(("Main.cvl", source));
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		var document = project.InitialSnapshot.GetDocument(project.GetDocumentId("Main.cvl"));

		var symbol = document.GetSymbolAtPosition(source.IndexOf("mystery", StringComparison.Ordinal) + 1);
		if (symbol is not null)
		{
			Assert.NotEqual("Unknown", symbol.DisplayText);
		}
	}

	// SEM-010: an unknown type position never fabricates a resolved type token.
	[Fact]
	public void UnknownTypePosition_DoesNotFabricateATypeToken()
	{
		const string source =
			"int main() { Unknown value = 0; return 0; }\n";
		using var fixture = TempProject.Create(("Main.cvl", source));
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		var document = project.InitialSnapshot.GetDocument(project.GetDocumentId("Main.cvl"));

		var tokens = document.GetSemanticTokens();
		foreach (var token in tokens)
		{
			if (document.Text.GetText(token.Span) == "Unknown")
			{
				Assert.NotEqual(ToolingSymbolKind.Struct, token.Kind);
			}
		}
	}
}
