using Cvolo.Compiler.Tooling;

namespace Cvolo.Tests.Tooling;

/// <summary>
/// Go to Type Definition asks what semantic type a position has, which is a different relation
/// from where the symbol was declared. A declaration or type use resolves to the type itself; a
/// value resolves to the type the compiler gave it, including through a ref wrapper, a raw
/// pointer, or a generic instantiation (whose source target is the generic owner). A position the
/// compiler cannot type is unavailable rather than guessed (§51).
/// </summary>
public sealed class TypeNavigationTests
{
	private static (CvoloProject Project, ProjectSnapshot Snapshot, DocumentSnapshot Document, TempProject Fixture) Open(
		params (string File, string Source)[] files)
	{
		var fixture = TempProject.Create(files);
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		var snapshot = project.InitialSnapshot;
		return (project, snapshot, snapshot.GetDocument(project.GetDocumentId(files[0].File)), fixture);
	}

	private static int At(string source, string needle, int start = 0) =>
		source.IndexOf(needle, start, StringComparison.Ordinal);

	private const string Source =
		"public struct Header\n" +
		"{\n" +
		"    public byte Kind;\n" +
		"    public int Length;\n" +
		"}\n" +
		"\n" +
		"public struct Holder\n" +
		"{\n" +
		"    public Header Inner;\n" +
		"}\n" +
		"\n" +
		"Header GetHeader()\n" +
		"{\n" +
		"    return Header { Kind: 0, Length: 0 };\n" +
		"}\n" +
		"\n" +
		"int First(Header value)\n" +
		"{\n" +
		"    return value.Length;\n" +
		"}\n" +
		"\n" +
		"int ReadThrough(refvar Header value)\n" +
		"{\n" +
		"    return value.Length;\n" +
		"}\n" +
		"\n" +
		"unsafe int RawLength(Header* pointer)\n" +
		"{\n" +
		"    return 0;\n" +
		"}\n" +
		"\n" +
		"int OptionalSize(Option<int> value)\n" +
		"{\n" +
		"    return 0;\n" +
		"}\n" +
		"\n" +
		"int main()\n" +
		"{\n" +
		"    val Header explicitValue = GetHeader();\n" +
		"    var inferred = GetHeader();\n" +
		"    val int total = explicitValue.Length;\n" +
		"    return total;\n" +
		"}\n";

	private static int HeaderNameStart() => At(Source, "public struct Header") + "public struct ".Length;

	[Fact]
	public void ATypeDeclaration_ResolvesTheDeclaredType()
	{
		var x = Open(("main.cvl", Source));

		using (x.Fixture)
		{
			IReadOnlyList<SymbolDefinition> definitions = x.Snapshot.GetTypeDefinitions(x.Document.Id, HeaderNameStart());

			SymbolDefinition definition = Assert.Single(definitions);
			Assert.Equal(x.Document.Id, definition.DocumentId);
			Assert.Equal(HeaderNameStart(), definition.SelectionSpan.Start);
		}
	}

	[Fact]
	public void ATypeUse_ResolvesTheNamedType()
	{
		var x = Open(("main.cvl", Source));

		using (x.Fixture)
		{
			var position = At(Source, "val Header explicitValue") + "val ".Length;
			SymbolDefinition definition = Assert.Single(x.Snapshot.GetTypeDefinitions(x.Document.Id, position));

			Assert.Equal(HeaderNameStart(), definition.SelectionSpan.Start);
		}
	}

	[Fact]
	public void AKnownVariable_ResolvesItsDeclaredType()
	{
		var x = Open(("main.cvl", Source));

		using (x.Fixture)
		{
			var position = At(Source, "val Header explicitValue") + "val Header ".Length;
			SymbolDefinition definition = Assert.Single(x.Snapshot.GetTypeDefinitions(x.Document.Id, position));

			Assert.Equal(HeaderNameStart(), definition.SelectionSpan.Start);
		}
	}

	[Fact]
	public void AnInferredLocal_ResolvesTheTypeTheCompilerInferred()
	{
		var x = Open(("main.cvl", Source));

		using (x.Fixture)
		{
			var position = At(Source, "var inferred") + "var ".Length;
			SymbolDefinition definition = Assert.Single(x.Snapshot.GetTypeDefinitions(x.Document.Id, position));

			Assert.Equal(HeaderNameStart(), definition.SelectionSpan.Start);
		}
	}

	[Fact]
	public void AParameter_ResolvesItsDeclaredType()
	{
		var x = Open(("main.cvl", Source));

		using (x.Fixture)
		{
			var position = At(Source, "int First(Header value)") + "int First(Header ".Length;
			SymbolDefinition definition = Assert.Single(x.Snapshot.GetTypeDefinitions(x.Document.Id, position));

			Assert.Equal(HeaderNameStart(), definition.SelectionSpan.Start);
		}
	}

	[Fact]
	public void AField_ResolvesTheTypeItWasDeclaredWith()
	{
		var x = Open(("main.cvl", Source));

		using (x.Fixture)
		{
			var position = At(Source, "public Header Inner") + "public Header ".Length;
			SymbolDefinition definition = Assert.Single(x.Snapshot.GetTypeDefinitions(x.Document.Id, position));

			Assert.Equal(HeaderNameStart(), definition.SelectionSpan.Start);
		}
	}

	[Fact]
	public void ARefvarValue_ResolvesTheUnderlyingNamedType()
	{
		var x = Open(("main.cvl", Source));

		using (x.Fixture)
		{
			var position = At(Source, "int ReadThrough(refvar Header value)") + "int ReadThrough(refvar Header ".Length;
			SymbolDefinition definition = Assert.Single(x.Snapshot.GetTypeDefinitions(x.Document.Id, position));

			Assert.Equal(HeaderNameStart(), definition.SelectionSpan.Start);
		}
	}

	[Fact]
	public void ARawPointerValue_ResolvesThePrimaryNamedPointeeType()
	{
		var x = Open(("main.cvl", Source));

		using (x.Fixture)
		{
			var position = At(Source, "unsafe int RawLength(Header* pointer)") + "unsafe int RawLength(Header* ".Length;
			SymbolDefinition definition = Assert.Single(x.Snapshot.GetTypeDefinitions(x.Document.Id, position));

			Assert.Equal(HeaderNameStart(), definition.SelectionSpan.Start);
		}
	}

	[Fact]
	public void AGenericValue_ResolvesTheGenericOwnerInTheBaseLibrary()
	{
		var x = Open(("main.cvl", Source));

		using (x.Fixture)
		{
			var position = At(Source, "int OptionalSize(Option<int> value)") + "int OptionalSize(Option<int> ".Length;
			SymbolDefinition definition = Assert.Single(x.Snapshot.GetTypeDefinitions(x.Document.Id, position));

			Assert.NotEqual(x.Document.Id, definition.DocumentId);
			Assert.EndsWith("Option.cvl", x.Snapshot.GetDocument(definition.DocumentId).FilePath, StringComparison.Ordinal);
		}
	}

	[Fact]
	public void APrimitiveValue_HasNoTypeDeclarationToNavigateTo()
	{
		var x = Open(("main.cvl", Source));

		using (x.Fixture)
		{
			var position = At(Source, "val int total") + "val int ".Length;

			Assert.Empty(x.Snapshot.GetTypeDefinitions(x.Document.Id, position));
		}
	}

	[Fact]
	public void TheOrdinaryDefinitionOfALocal_StillPointsAtTheLocal()
	{
		var x = Open(("main.cvl", Source));

		using (x.Fixture)
		{
			var position = At(Source, "val Header explicitValue") + "val Header ".Length;

			SymbolLookupResult? symbol = x.Document.GetSymbolAtPosition(position);
			Assert.NotNull(symbol);
			SymbolDefinition definition = Assert.Single(x.Snapshot.GetDefinitions(symbol!.SymbolId));
			Assert.Equal(position, definition.SelectionSpan.Start);

			SymbolDefinition typeDefinition = Assert.Single(x.Snapshot.GetTypeDefinitions(x.Document.Id, position));
			Assert.Equal(HeaderNameStart(), typeDefinition.SelectionSpan.Start);
		}
	}

	[Fact]
	public void ATypeDeclaredInAnotherDocument_ResolvesToThatDocument()
	{
		const string types = "public struct Widget\n{\n    public int Count;\n}\n";
		const string main = "int main()\n{\n    val Widget value = Widget { Count: 0 };\n    return 0;\n}\n";
		var x = Open(("types.cvl", types), ("main.cvl", main));

		using (x.Fixture)
		{
			DocumentSnapshot mainDocument = x.Snapshot.GetDocument(x.Project.GetDocumentId("main.cvl"));
			var position = At(main, "val Widget value") + "val ".Length;

			SymbolDefinition definition = Assert.Single(x.Snapshot.GetTypeDefinitions(mainDocument.Id, position));

			Assert.EndsWith("types.cvl", x.Snapshot.GetDocument(definition.DocumentId).FilePath, StringComparison.Ordinal);
		}
	}
}
