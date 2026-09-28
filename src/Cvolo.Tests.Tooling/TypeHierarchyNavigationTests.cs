using Cvolo.Compiler.Tooling;

namespace Cvolo.Tests.Tooling;

public sealed class TypeHierarchyNavigationTests
{
	private const string Source =
		"public interface IWidget\n" +
		"{\n" +
		"    void Print();\n" +
		"}\n" +
		"\n" +
		"public interface IButton : IWidget\n" +
		"{\n" +
		"    void Click();\n" +
		"}\n" +
		"\n" +
		"public protocol IEquatable\n" +
		"{\n" +
		"    int Key();\n" +
		"}\n" +
		"\n" +
		"public protocol IHashable : IEquatable\n" +
		"{\n" +
		"    nuint Hash();\n" +
		"}\n" +
		"\n" +
		"public interface IRecord : IHashable\n" +
		"{\n" +
		"    void Describe();\n" +
		"}\n" +
		"\n" +
		"public struct Impl\n" +
		"{\n" +
		"    public int Value;\n" +
		"}\n" +
		"\n" +
		"public extension Impl : IWidget\n" +
		"{\n" +
		"    public void Print() { }\n" +
		"}\n" +
		"\n" +
		"int main()\n" +
		"{\n" +
		"    return 0;\n" +
		"}\n";

	private const string MultipleBases =
		"public interface IX\n" +
		"{\n" +
		"    void X();\n" +
		"}\n" +
		"\n" +
		"public interface IY\n" +
		"{\n" +
		"    void Y();\n" +
		"}\n" +
		"\n" +
		"public interface IZ : IX, IY\n" +
		"{\n" +
		"    void Z();\n" +
		"}\n";

	private const string Cyclic =
		"public protocol IA : IB\n" +
		"{\n" +
		"    int A();\n" +
		"}\n" +
		"\n" +
		"public protocol IB : IA\n" +
		"{\n" +
		"    int B();\n" +
		"}\n";

	private static (CvoloProject Project, ProjectSnapshot Snapshot, DocumentSnapshot Document, TempProject Fixture) Open(
		params (string File, string Source)[] files)
	{
		var fixture = TempProject.Create(files);
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		var snapshot = project.InitialSnapshot;
		return (project, snapshot, snapshot.GetDocument(project.GetDocumentId(files[0].File)), fixture);
	}

	private static int At(string source, string needle, int start = 0) => source.IndexOf(needle, start, StringComparison.Ordinal);

	private static ToolingHierarchyItem Prepare(ProjectSnapshot snapshot, DocumentSnapshot document, string source, string name)
	{
		ToolingHierarchyItem? item = snapshot.PrepareTypeHierarchy(document.Id, At(source, name));
		Assert.NotNull(item);
		return item!;
	}

	[Fact]
	public void AnInterfaceDeclaration_IsAHierarchyItem()
	{
		var x = Open(("main.cvl", Source));
		using (x.Fixture)
		{
			ToolingHierarchyItem item = Prepare(x.Snapshot, x.Document, Source, "IWidget");
			Assert.Equal("IWidget", item.Name);
			Assert.Equal(ToolingSymbolKind.Interface, item.Kind);
			Assert.Equal(x.Document.Id, item.Definition.DocumentId);
		}
	}

	[Fact]
	public void AnInterface_ResolvesItsBaseInterface()
	{
		var x = Open(("main.cvl", Source));
		using (x.Fixture)
		{
			ToolingHierarchyItem button = Prepare(x.Snapshot, x.Document, Source, "IButton");
			IReadOnlyList<ToolingHierarchyItem> bases = x.Snapshot.GetSupertypes(button.SymbolId);
			var parent = Assert.Single(bases);
			Assert.Equal("IWidget", parent.Name);
			Assert.Equal(ToolingSymbolKind.Interface, parent.Kind);
		}
	}

	[Fact]
	public void AProtocol_ResolvesItsBaseProtocol()
	{
		var x = Open(("main.cvl", Source));
		using (x.Fixture)
		{
			ToolingHierarchyItem hashable = Prepare(x.Snapshot, x.Document, Source, "IHashable");
			var parent = Assert.Single(x.Snapshot.GetSupertypes(hashable.SymbolId));
			Assert.Equal("IEquatable", parent.Name);
			Assert.Equal(ToolingSymbolKind.Protocol, parent.Kind);
		}
	}

	[Fact]
	public void AnInterface_ResolvesAProtocolBase()
	{
		var x = Open(("main.cvl", Source));
		using (x.Fixture)
		{
			ToolingHierarchyItem record = Prepare(x.Snapshot, x.Document, Source, "IRecord");
			var parent = Assert.Single(x.Snapshot.GetSupertypes(record.SymbolId));
			Assert.Equal("IHashable", parent.Name);
			Assert.Equal(ToolingSymbolKind.Protocol, parent.Kind);
		}
	}

	[Fact]
	public void MultipleBases_ReturnEveryParent()
	{
		var x = Open(("main.cvl", MultipleBases));
		using (x.Fixture)
		{
			ToolingHierarchyItem z = Prepare(x.Snapshot, x.Document, MultipleBases, "IZ");
			IReadOnlyList<ToolingHierarchyItem> bases = x.Snapshot.GetSupertypes(z.SymbolId);
			Assert.Equal(new[] { "IX", "IY" }, bases.Select(baseItem => baseItem.Name));
		}
	}

	[Fact]
	public void Subtypes_ReturnDirectChildren()
	{
		var x = Open(("main.cvl", Source));
		using (x.Fixture)
		{
			ToolingHierarchyItem widget = Prepare(x.Snapshot, x.Document, Source, "IWidget");
			var child = Assert.Single(x.Snapshot.GetSubtypes(widget.SymbolId));
			Assert.Equal("IButton", child.Name);
			Assert.Equal(ToolingSymbolKind.Interface, child.Kind);
		}
	}

	[Fact]
	public void AConcreteConformer_IsNotAHierarchyChild()
	{
		var x = Open(("main.cvl", Source));
		using (x.Fixture)
		{
			// Impl conforms to IWidget through `extension Impl : IWidget`, but it is a concrete type,
			// so the declared hierarchy must not list it (§13).
			ToolingHierarchyItem widget = Prepare(x.Snapshot, x.Document, Source, "IWidget");
			Assert.DoesNotContain(x.Snapshot.GetSubtypes(widget.SymbolId), item => item.Name == "Impl");

			// A concrete type is not a hierarchy item at all.
			Assert.Null(x.Snapshot.PrepareTypeHierarchy(x.Document.Id, At(Source, "Impl")));
		}
	}

	[Fact]
	public void AFollowUpCall_ReResolvesTheContractByName()
	{
		var x = Open(("main.cvl", Source));
		using (x.Fixture)
		{
			ToolingHierarchyItem widget = Prepare(x.Snapshot, x.Document, Source, "IWidget");
			ToolingHierarchyItem? again = x.Snapshot.GetTypeHierarchyByKey(x.Document.Id, widget.Name);
			Assert.NotNull(again);
			Assert.Equal("IWidget", again!.Name);
			Assert.Equal(widget.Definition.SelectionSpan.Start, again.Definition.SelectionSpan.Start);
		}
	}

	[Fact]
	public void ACyclicHierarchy_TerminatesWithTheDirectBases()
	{
		var x = Open(("main.cvl", Cyclic));
		using (x.Fixture)
		{
			ToolingHierarchyItem a = Prepare(x.Snapshot, x.Document, Cyclic, "IA");
			var parent = Assert.Single(x.Snapshot.GetSupertypes(a.SymbolId));
			Assert.Equal("IB", parent.Name);
		}
	}
}
