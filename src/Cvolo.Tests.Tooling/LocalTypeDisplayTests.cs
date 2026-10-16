using Cvolo.Compiler.Tooling;

namespace Cvolo.Tests.Tooling;

/// <summary>
/// Hover/navigation display for locals must reflect the compiler-resolved type, not the written
/// <c>var</c> placeholder. The value's type is inferred from its initializer by the compiler; the
/// editor only presents it.
/// </summary>
public sealed class LocalTypeDisplayTests
{
	private static (CvoloProject Project, ProjectSnapshot Snapshot, DocumentSnapshot Document, TempProject Fixture) Open(string source)
	{
		var fixture = TempProject.Create(("Main.cvl", source));
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		var snapshot = project.InitialSnapshot;
		return (project, snapshot, snapshot.GetDocument(project.GetDocumentId("Main.cvl")), fixture);
	}

	private static int At(string source, string needle, int delta = 0) => source.IndexOf(needle, StringComparison.Ordinal) + delta;

	[Fact]
	public void VarLocal_DisplayUsesInferredAggregateType()
	{
		const string s = "struct Window { int Width; }\nint main() { var window = Window { Width = 1 }; return window.Width; }\n";
		var x = Open(s);
		using (x.Fixture)
		{
			var declaration = x.Document.GetSymbolAtPosition(At(s, "window ="));
			var use = x.Document.GetSymbolAtPosition(s.LastIndexOf("window", StringComparison.Ordinal));
			Assert.NotNull(declaration);
			Assert.NotNull(use);
			Assert.Equal("Window window", declaration!.DisplayText);
			Assert.Equal("Window window", use!.DisplayText);
		}
	}

	[Fact]
	public void VarLocal_DisplayUsesInferredPrimitiveType()
	{
		const string s = "int main() { var count = 1; return count; }\n";
		var x = Open(s);
		using (x.Fixture)
		{
			var declaration = x.Document.GetSymbolAtPosition(At(s, "count ="));
			Assert.NotNull(declaration);
			Assert.Equal("int count", declaration!.DisplayText);
		}
	}

	[Fact]
	public void ExplicitlyTypedLocal_DisplayUsesWrittenType()
	{
		const string s = "struct Window { int Width; }\nint main() { Window window = Window { Width = 1 }; return window.Width; }\n";
		var x = Open(s);
		using (x.Fixture)
		{
			var symbol = x.Document.GetSymbolAtPosition(At(s, "window ="));
			Assert.NotNull(symbol);
			Assert.Equal("Window window", symbol!.DisplayText);
		}
	}

	[Fact]
	public void TypeNameToken_StillResolvesToTheTypeSymbol()
	{
		const string s = "struct Window { int Width; }\nint main() { var window = Window { Width = 1 }; return window.Width; }\n";
		var x = Open(s);
		using (x.Fixture)
		{
			var symbol = x.Document.GetSymbolAtPosition(At(s, "Window {"));
			Assert.NotNull(symbol);
			Assert.Equal(ToolingSymbolKind.Struct, symbol!.Kind);
			Assert.Equal("struct Window", symbol.DisplayText);
		}
	}

	[Fact]
	public void VarLocal_DisplayPreservesGenericTypeArgument()
	{
		const string s = "int main() { var o = default(int?); return 0; }\n";
		var x = Open(s);
		using (x.Fixture)
		{
			var symbol = x.Document.GetSymbolAtPosition(At(s, "o ="));
			Assert.NotNull(symbol);
			Assert.Equal("Option<int> o", symbol!.DisplayText);
		}
	}

	[Fact]
	public void VarLocal_DisplayPreservesFixedArrayType()
	{
		const string s = "int main() { var a = default(int[4]); return 0; }\n";
		var x = Open(s);
		using (x.Fixture)
		{
			var symbol = x.Document.GetSymbolAtPosition(At(s, "a ="));
			Assert.NotNull(symbol);
			Assert.Equal("int[4] a", symbol!.DisplayText);
		}
	}

	[Fact]
	public void VarLocal_DisplayPreservesRawPointerType()
	{
		const string s = "int main()\n{\n    unsafe\n    {\n        var p = default(void*);\n    }\n    return 0;\n}\n";
		var x = Open(s);
		using (x.Fixture)
		{
			var symbol = x.Document.GetSymbolAtPosition(At(s, "p ="));
			Assert.NotNull(symbol);
			Assert.Equal("void* p", symbol!.DisplayText);
		}
	}
}
