using Cvolo.Compiler.Tooling;

namespace Cvolo.Tests.Tooling;

/// <summary>
/// Implementations keep the two ways a contract can be satisfied apart. An interface is nominal, so
/// only an <c>extension X : I</c> conforms and its methods are the ones the compiler binds. A
/// protocol is structural, so a type conforms because it happens to provide the members; the answer
/// comes from the compiler's own conformance rules.
/// </summary>
public sealed class ImplementationNavigationTests
{
	private const string Source =
		"public interface IWidget\n" +
		"{\n" +
		"    int Size();\n" +
		"    void Draw();\n" +
		"}\n" +
		"\n" +
		"public struct Button\n" +
		"{\n" +
		"    public int Width;\n" +
		"}\n" +
		"\n" +
		"public struct Other\n" +
		"{\n" +
		"    public int Weight;\n" +
		"}\n" +
		"\n" +
		"public extension Button : IWidget\n" +
		"{\n" +
		"    public int Size()\n" +
		"    {\n" +
		"        return Width;\n" +
		"    }\n" +
		"\n" +
		"    public void Draw()\n" +
		"    {\n" +
		"    }\n" +
		"}\n" +
		"\n" +
		"public extension Other\n" +
		"{\n" +
		"    public int Size()\n" +
		"    {\n" +
		"        return Weight;\n" +
		"    }\n" +
		"}\n" +
		"\n" +
		"protocol IReader\n" +
		"{\n" +
		"    void Read();\n" +
		"}\n" +
		"\n" +
		"protocol IStream : IReader\n" +
		"{\n" +
		"    void Flush();\n" +
		"}\n" +
		"\n" +
		"public struct File\n" +
		"{\n" +
		"    public int Position;\n" +
		"}\n" +
		"\n" +
		"public extension File\n" +
		"{\n" +
		"    public void Read()\n" +
		"    {\n" +
		"    }\n" +
		"\n" +
		"    public void Flush()\n" +
		"    {\n" +
		"    }\n" +
		"}\n" +
		"\n" +
		"int main()\n" +
		"{\n" +
		"    return 0;\n" +
		"}\n";

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

	private static int After(string source, string prefix) => At(source, prefix) + prefix.Length;

	private IReadOnlyList<ToolingImplementation> ImplementationsAt(DocumentSnapshot document, int position)
	{
		SymbolLookupResult? symbol = document.GetSymbolAtPosition(position);
		Assert.NotNull(symbol);
		return document.OwningSnapshot!.GetImplementations(symbol!.SymbolId);
	}

	[Fact]
	public void AnInterface_ResolvesTheExtensionThatConformsToIt()
	{
		var x = Open(("main.cvl", Source));
		using (x.Fixture)
		{
			IReadOnlyList<ToolingImplementation> implementations =
				ImplementationsAt(x.Document, After(Source, "public interface "));

			var only = Assert.Single(implementations);
			Assert.Equal(ToolingImplementationKind.Implements, only.Kind);
			Assert.Equal("Button", only.DisplayText);
			Assert.Equal(After(Source, "public extension "), only.Definition.SelectionSpan.Start);
		}
	}

	[Fact]
	public void AnInterfaceMember_ResolvesTheImplementingMethodOnly()
	{
		var x = Open(("main.cvl", Source));
		using (x.Fixture)
		{
			// `Size` is also provided by an extension on `Other`, but that extension never claims
			// IWidget, so it is not an implementation of the interface member.
			IReadOnlyList<ToolingImplementation> implementations =
				ImplementationsAt(x.Document, At(Source, "    int Size();") + "    int ".Length);

			var only = Assert.Single(implementations);
			Assert.Equal(ToolingImplementationKind.Implements, only.Kind);
			Assert.Equal(At(Source, "    public int Size()") + "    public int ".Length, only.Definition.SelectionSpan.Start);
		}
	}

	[Fact]
	public void AProtocol_ResolvesTheTypesThatConformStructurally()
	{
		var x = Open(("main.cvl", Source));
		using (x.Fixture)
		{
			IReadOnlyList<ToolingImplementation> implementations =
				ImplementationsAt(x.Document, After(Source, "protocol "));

			var only = Assert.Single(implementations);
			Assert.Equal(ToolingImplementationKind.ConformsTo, only.Kind);
			Assert.Equal("File", only.DisplayText);
			Assert.Equal(At(Source, "public struct File") + "public struct ".Length, only.Definition.SelectionSpan.Start);
		}
	}

	[Fact]
	public void AProtocolMember_ResolvesTheConformingMethod()
	{
		var x = Open(("main.cvl", Source));
		using (x.Fixture)
		{
			IReadOnlyList<ToolingImplementation> implementations =
				ImplementationsAt(x.Document, At(Source, "    void Read();") + "    void ".Length);

			var only = Assert.Single(implementations);
			Assert.Equal(ToolingImplementationKind.ConformsTo, only.Kind);
			Assert.Equal("File.Read", only.DisplayText);
			Assert.Equal(At(Source, "    public void Read()") + "    public void ".Length, only.Definition.SelectionSpan.Start);
		}
	}

	[Fact]
	public void AnInheritedProtocol_AlsoResolvesItsBaseProtocolMembers()
	{
		var x = Open(("main.cvl", Source));
		using (x.Fixture)
		{
			// A type conforms to `IStream` only if it provides the inherited `Read` as well.
			IReadOnlyList<ToolingImplementation> implementations =
				ImplementationsAt(x.Document, After(Source, "protocol IStream"));

			var only = Assert.Single(implementations);
			Assert.Equal("File", only.DisplayText);
		}
	}

	[Fact]
	public void ASymbolThatIsNotAContract_HasNoImplementations()
	{
		var x = Open(("main.cvl", Source));
		using (x.Fixture)
		{
			IReadOnlyList<ToolingImplementation> implementations =
				ImplementationsAt(x.Document, After(Source, "public struct "));

			Assert.Empty(implementations);
		}
	}
}
