using Cvolo.Compiler.Tooling;
using Cvolo.Compiler.Tooling.Completion;

namespace Cvolo.Tests.Tooling;

/// <summary>
/// Type-name completion must be compiler-aware: built-in primitive type names and accessible
/// user-defined types belong in a declaration / type-argument slot even while the source is
/// incomplete. A generic argument must never receive unrelated statement keywords such as <c>if</c>.
/// </summary>
public sealed class TypeCompletionTests
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

	[Theory]
	[InlineData("i|", "int")]
	[InlineData("in|", "int")]
	[InlineData("stri|", "string")]
	[InlineData("str|", "string")]
	[InlineData("bo|", "bool")]
	[InlineData("flo|", "float")]
	[InlineData("dou|", "double")]
	[InlineData("lo|", "long")]
	[InlineData("ch|", "char")]
	public void DeclarationPosition_OffersPrimitiveTypeByName(string body, string expected)
	{
		var result = Complete($"int main()\n{{\n    {body}\n}}\n");

		Assert.Contains(result.Candidates, c => c.Label == expected && c.Kind == CompletionKind.Keyword);
	}

	[Fact]
	public void StatementPosition_KeepsStatementKeywordAlongsideThePrimitive()
	{
		var result = Complete("int main()\n{\n    i|\n}\n");

		Assert.Contains(result.Candidates, c => c.Label == "int" && c.Kind == CompletionKind.Keyword);
		Assert.Contains(result.Candidates, c => c.Label == "if" && c.Kind == CompletionKind.Keyword);
	}

	[Fact]
	public void DeclarationPrefix_ReplacesOnlyTheTypedPrefix()
	{
		var result = Complete("int main()\n{\n    in|\n}\n");

		Assert.Contains(result.Candidates, c => c.Label == "int");
		Assert.Equal(2, result.ReplacementRange.Length);
	}

	[Fact]
	public void UserDefinedType_IsOfferedInDeclarationPosition()
	{
		var result = Complete("struct Window { int Width; }\nint main()\n{\n    Window|\n}\n");

		Assert.Contains(result.Candidates, c => c.Label == "Window" && c.Kind == CompletionKind.Type);
	}

	[Theory]
	[InlineData("Option<i|", "int")]
	[InlineData("Option<str|", "string")]
	[InlineData("Option<Option<i|", "int")]
	[InlineData("Box<bo|", "bool")]
	public void GenericArgument_OffersTypeNames(string body, string expected)
	{
		var result = Complete($"struct Box<T> {{ T Value; }}\nint main()\n{{\n    {body}\n}}\n");

		Assert.Contains(result.Candidates, c => c.Label == expected);
	}

	[Fact]
	public void GenericArgument_DoesNotOfferStatementKeywords()
	{
		var result = Complete("int main()\n{\n    Option<i|\n}\n");

		Assert.Contains(result.Candidates, c => c.Label == "int");
		Assert.DoesNotContain(result.Candidates, c => c.Label == "if");
		Assert.DoesNotContain(result.Candidates, c => c.Label == "return");
	}

	[Fact]
	public void GenericArgument_DoesNotOfferVoid()
	{
		var result = Complete("int main()\n{\n    Option<v|\n}\n");

		Assert.DoesNotContain(result.Candidates, c => c.Label == "void");
	}

	[Fact]
	public void GenericArgument_OffersUserDefinedType()
	{
		var result = Complete("struct Window { int Width; }\nint main()\n{\n    Option<Window|\n}\n");

		Assert.Contains(result.Candidates, c => c.Label == "Window" && c.Kind == CompletionKind.Type);
	}

	[Fact]
	public void CompletionStillWorksAfterAnIncompleteInitializer()
	{
		var result = Complete(
			"struct CharacterBody { float X; }\n" +
			"struct Window { CharacterBody Body; int Width; }\n" +
			"int main()\n{\n" +
			"    Window windo = Window\n    {\n\n    };\n" +
			"    i|\n}\n");

		Assert.Contains(result.Candidates, c => c.Label == "int");
	}

	[Fact]
	public void EveryPrimitiveTypeNameIsReachableFromItsPrefix()
	{
		string[] primitives =
		[
			"bool", "byte", "sbyte", "short", "ushort", "int", "uint",
			"long", "ulong", "nint", "nuint", "float", "double", "char", "string"
		];

		foreach (var primitive in primitives)
		{
			var prefix = primitive[..2];
			var result = Complete($"int main()\n{{\n    {prefix}|\n}}\n");
			Assert.Contains(result.Candidates, c => c.Label == primitive && c.Kind == CompletionKind.Keyword);
		}
	}

	[Fact]
	public void ProgressiveTypingOnOneSnapshot_KeepsOfferingTheMatchingType()
	{
		const string skeleton = "int main()\n{{\n    {0}\n}}\n";
		using var fixture = TempProject.Create(("Main.cvl", string.Format(skeleton, "i")));
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		var snapshot = project.InitialSnapshot;
		var documentId = project.GetDocumentId("Main.cvl");

		foreach (var prefix in new[] { "i", "in", "int" })
		{
			var text = string.Format(skeleton, prefix);
			var probe = snapshot.WithDocument(documentId, SourceText.From(text));
			var probeDocument = probe.GetDocument(documentId);
			var result = probeDocument.GetCompletions("int main()\n{\n    ".Length + prefix.Length);

			Assert.Contains(result.Candidates, c => c.Label == "int" && c.Kind == CompletionKind.Keyword);
		}
	}

	[Fact]
	public void ParameterTypePosition_OffersOnlyTypes()
	{
		// A function parameter type is a type-only position: no statement keywords may leak.
		var result = Complete("int f(d|) { return 0; }\nint main() { return 0; }\n");

		Assert.Contains(result.Candidates, c => c.Label == "double" && c.Kind == CompletionKind.Keyword);
		Assert.DoesNotContain(result.Candidates, c => c.Label == "default");
		Assert.DoesNotContain(result.Candidates, c => c.Label == "defer");
	}

	[Fact]
	public void ParameterTypePositionAfterComma_OffersOnlyTypes()
	{
		var result = Complete("int f(int a, d|) { return 0; }\nint main() { return 0; }\n");

		Assert.Contains(result.Candidates, c => c.Label == "double" && c.Kind == CompletionKind.Keyword);
		Assert.DoesNotContain(result.Candidates, c => c.Label == "default");
	}

	[Fact]
	public void StatementPosition_StillOffersKeywordsAndTypes()
	{
		var result = Complete("int main()\n{\n    d|\n}\n");

		Assert.Contains(result.Candidates, c => c.Label == "double" && c.Kind == CompletionKind.Keyword);
		Assert.Contains(result.Candidates, c => c.Label == "default" && c.Kind == CompletionKind.Keyword);
	}

	[Fact]
	public void ProgressiveGenericArgumentTyping_KeepsOfferingTheMatchingType()
	{
		foreach (var prefix in new[] { "d", "do", "dou", "double" })
		{
			var result = Complete($"int main()\n{{\n    int? x = Option<{prefix}|\n    return 0;\n}}\n");

			Assert.Contains(result.Candidates, c => c.Label == "double" && c.Kind == CompletionKind.Keyword);
			Assert.DoesNotContain(result.Candidates, c => c.Label == "default");
		}
	}
}
