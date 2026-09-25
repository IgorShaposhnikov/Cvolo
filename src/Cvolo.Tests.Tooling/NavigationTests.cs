using Cvolo.Compiler.Tooling;

namespace Cvolo.Tests.Tooling;

public sealed class NavigationTests
{
	private static (CvoloProject Project, ProjectSnapshot Snapshot, DocumentSnapshot Document, TempProject Fixture) Open(params (string File, string Source)[] files)
	{
		var fixture = TempProject.Create(files);
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		var snapshot = project.InitialSnapshot;
		return (project, snapshot, snapshot.GetDocument(project.GetDocumentId(files[0].File)), fixture);
	}

	private static int At(string source, string needle, int delta = 0) => source.IndexOf(needle, StringComparison.Ordinal) + delta;

	[Fact]
	public void ParameterReference_ResolvesWithExactIdentityAndDefinition()
	{
		const string s = "int Twice(int value) { return value + value; }\n";
		var x = Open(("Main.cvl", s));
		using (x.Fixture)
		{
			var a = x.Document.GetSymbolAtPosition(At(s, "value +"));
			var b = x.Document.GetSymbolAtPosition(s.LastIndexOf("value", StringComparison.Ordinal));
			Assert.NotNull(a);
			Assert.NotNull(b);
			Assert.Equal(ToolingSymbolKind.Parameter, a!.Kind);
			Assert.Equal(a.SymbolId, b!.SymbolId);
			Assert.Equal("int value", a.DisplayText);
			var defs = x.Snapshot.GetDefinitions(a.SymbolId);
			Assert.Single(defs);
			Assert.Equal("value", x.Document.Text.GetText(defs[0].SelectionSpan));
		}
	}

	[Fact]
	public void LocalShadowing_UsesNearestDeclaration()
	{
		const string s = "global var int value = 1;\nint main() { val int value = 2; return value; }\n";
		var x = Open(("Main.cvl", s));
		using (x.Fixture)
		{
			var use = x.Document.GetSymbolAtPosition(s.LastIndexOf("value", StringComparison.Ordinal));
			Assert.NotNull(use);
			Assert.Equal(ToolingSymbolKind.Local, use!.Kind);
			var global = x.Document.GetSymbolAtPosition(At(s, "value = 1"));
			Assert.NotNull(global);
			Assert.NotEqual(global!.SymbolId, use.SymbolId);
		}
	}

	[Fact]
	public void CrossFileFunction_DefinitionTargetsOtherSnapshotDocument()
	{
		const string main = "int main() { return Helper(); }\n";
		const string lib = "int Helper() { return 42; }\n";
		var x = Open(("Main.cvl", main), ("Lib.cvl", lib));
		using (x.Fixture)
		{
			var symbol = x.Document.GetSymbolAtPosition(At(main, "Helper"));
			Assert.NotNull(symbol);
			var def = Assert.Single(x.Snapshot.GetDefinitions(symbol!.SymbolId));
			Assert.Equal(x.Project.GetDocumentId("Lib.cvl"), def.DocumentId);
			Assert.Equal("Helper", x.Snapshot.GetDocument(def.DocumentId).Text.GetText(def.SelectionSpan));
		}
	}

	[Fact]
	public void StructFieldMember_ResolvesToFieldDeclaration()
	{
		const string s = "struct Node { public int value; }\nint main() { val Node n = Node { value: 1 }; return n.value; }\n";
		var x = Open(("Main.cvl", s));
		using (x.Fixture)
		{
			var symbol = x.Document.GetSymbolAtPosition(s.LastIndexOf("value", StringComparison.Ordinal));
			Assert.NotNull(symbol);
			Assert.Equal(ToolingSymbolKind.Field, symbol!.Kind);
			Assert.Equal("int value", symbol.DisplayText);
			Assert.Single(x.Snapshot.GetDefinitions(symbol.SymbolId));
		}
	}

	[Fact]
	public void ForeignSnapshotSymbolId_DoesNotResolve()
	{
		const string s = "int Helper() { return 1; }\nint main() { return Helper(); }\n";
		var x = Open(("Main.cvl", s));
		using (x.Fixture)
		{
			var symbol = x.Document.GetSymbolAtPosition(s.LastIndexOf("Helper", StringComparison.Ordinal));
			Assert.NotNull(symbol);
			var derived = x.Snapshot.WithDocument(x.Document.Id, SourceText.From(s.Replace("return 1", "return 2")));
			Assert.Empty(derived.GetDefinitions(symbol!.SymbolId));
			Assert.Single(x.Snapshot.GetDefinitions(symbol.SymbolId));
		}
	}

	[Fact]
	public void DocumentSymbols_AreHierarchicalAndExcludeLocals()
	{
		const string s = "struct Node { public int value; }\nextension Node { int Get() { val int local = value; return local; } }\nint main() { return 0; }\n";
		var x = Open(("Main.cvl", s));
		using (x.Fixture)
		{
			var symbols = x.Document.GetDocumentSymbols();
			Assert.Equal(3, symbols.Count);
			var node = Assert.Single(symbols.Where(z => z.Name == "Node" && z.Kind == ToolingSymbolKind.Struct));
			Assert.Contains(node.Children, c => c.Name == "value");
			Assert.DoesNotContain(symbols.SelectMany(Flatten), z => z.Name == "local");
		}
	}

	private static IEnumerable<DocumentSymbolInfo> Flatten(DocumentSymbolInfo s)
	{
		yield return s;
		foreach (var c in s.Children)
		foreach (var n in Flatten(c))
			yield return n;
	}

	[Fact]
	public void DelegateDeclaration_ResolvesWithExactIdentityAndDefinition()
	{
		const string s = "delegate int BinaryOp(int a, int b);\nint main() { return 0; }\n";
		var x = Open(("Main.cvl", s));
		using (x.Fixture)
		{
			var symbol = x.Document.GetSymbolAtPosition(At(s, "BinaryOp"));
			Assert.NotNull(symbol);
			Assert.Equal(ToolingSymbolKind.Delegate, symbol!.Kind);
			Assert.Equal("delegate int BinaryOp(int a, int b)", symbol.DisplayText);
			var def = Assert.Single(x.Snapshot.GetDefinitions(symbol.SymbolId));
			Assert.Equal("BinaryOp", x.Document.Text.GetText(def.SelectionSpan));
		}
	}

	[Fact]
	public void DelegateTypeReference_ResolvesToDelegateDeclaration()
	{
		const string s = "delegate int BinaryOp(int a, int b);\nint main() { val BinaryOp op; return 0; }\n";
		var x = Open(("Main.cvl", s));
		using (x.Fixture)
		{
			var symbol = x.Document.GetSymbolAtPosition(At(s, "BinaryOp op"));
			Assert.NotNull(symbol);
			Assert.Equal(ToolingSymbolKind.Delegate, symbol!.Kind);
			Assert.Equal("delegate int BinaryOp(int a, int b)", symbol.DisplayText);
			var def = Assert.Single(x.Snapshot.GetDefinitions(symbol.SymbolId));
			Assert.Equal("BinaryOp", x.Document.Text.GetText(def.SelectionSpan));
		}
	}

	[Fact]
	public void DocumentSymbols_IncludeDelegateDeclaration()
	{
		const string s = "delegate int BinaryOp(int a, int b);\nstruct Node { public int value; }\nint main() { return 0; }\n";
		var x = Open(("Main.cvl", s));
		using (x.Fixture)
		{
			var symbols = x.Document.GetDocumentSymbols();
			var op = Assert.Single(symbols.Where(z => z.Name == "BinaryOp" && z.Kind == ToolingSymbolKind.Delegate));
			Assert.Equal("delegate int BinaryOp(int a, int b)", op.Detail);
			Assert.Empty(op.Children);
		}
	}

	[Fact]
	public void InvalidPosition_ThrowsAndUnresolvedNameReturnsNull()
	{
		const string s = "int main() { return Missing; }\n";
		var x = Open(("Main.cvl", s));
		using (x.Fixture)
		{
			Assert.Throws<ArgumentOutOfRangeException>(() => x.Document.GetSymbolAtPosition(-1));
			Assert.Throws<ArgumentOutOfRangeException>(() => x.Document.GetSymbolAtPosition(s.Length + 1));
			Assert.Null(x.Document.GetSymbolAtPosition(At(s, "Missing")));
		}
	}

	[Fact]
	public async Task ParallelQueries_AreDeterministic()
	{
		const string s = "int Helper(int x) { return x; }\nint main() { return Helper(1); }\n";
		var x = Open(("Main.cvl", s));
		using (x.Fixture)
		{
			var pos = s.LastIndexOf("Helper", StringComparison.Ordinal);
			var tasks = Enumerable.Range(0, 32).Select(_ => Task.Run(() => x.Document.GetSymbolAtPosition(pos))).ToArray();
			var all = await Task.WhenAll(tasks);
			Assert.All(all, r => Assert.NotNull(r));
			Assert.Single(all.Select(r => r!.SymbolId).Distinct());
		}
	}

	[Fact]
	public void EnumVariant_ResolvesToVariantDeclaration()
	{
		const string s = "enum Color { Red, Green }\nint main() { return Color.Red; }\n";
		var x = Open(("Main.cvl", s));
		using (x.Fixture)
		{
			var symbol = x.Document.GetSymbolAtPosition(At(s, "Red"));
			Assert.NotNull(symbol);
			Assert.Equal(ToolingSymbolKind.EnumMember, symbol!.Kind);
			var def = Assert.Single(x.Snapshot.GetDefinitions(symbol.SymbolId));
			Assert.Equal("Red", x.Document.Text.GetText(def.SelectionSpan));
		}
	}

	[Fact]
	public void ExtensionMethodCall_ResolvesToMethodDeclaration()
	{
		const string s = "struct Node { public int value; }\nextension Node { int Twice() { return value + value; } }\nint main() { val Node n = Node { value: 1 }; return n.Twice(); }\n";
		var x = Open(("Main.cvl", s));
		using (x.Fixture)
		{
			var symbol = x.Document.GetSymbolAtPosition(s.LastIndexOf("Twice", StringComparison.Ordinal));
			Assert.NotNull(symbol);
			Assert.Equal(ToolingSymbolKind.ExtensionMethod, symbol!.Kind);
			var def = Assert.Single(x.Snapshot.GetDefinitions(symbol.SymbolId));
			Assert.Equal("Twice", x.Document.Text.GetText(def.SelectionSpan));
		}
	}

	[Fact]
	public void TypeReference_ResolvesToTypeDeclaration()
	{
		const string s = "struct Node { public int value; }\nint main() { val Node n = Node { value: 1 }; return 0; }\n";
		var x = Open(("Main.cvl", s));
		using (x.Fixture)
		{
			var symbol = x.Document.GetSymbolAtPosition(At(s, "Node n"));
			Assert.NotNull(symbol);
			Assert.Equal(ToolingSymbolKind.Struct, symbol!.Kind);
			Assert.Equal("struct Node", symbol.DisplayText);
			var def = Assert.Single(x.Snapshot.GetDefinitions(symbol.SymbolId));
			Assert.Equal("Node", x.Document.Text.GetText(def.SelectionSpan));
		}
	}

	[Fact]
	public void OverlayDefinition_UsesEditedTargetSpan()
	{
		const string main = "int main() { return Helper(); }\n";
		const string lib = "int Helper() { return 1; }\n";
		var x = Open(("Main.cvl", main), ("Lib.cvl", lib));
		using (x.Fixture)
		{
			var libId = x.Project.GetDocumentId("Lib.cvl");
			var edited = x.Snapshot.WithDocument(libId, SourceText.From("\n\nint Helper() { return 2; }\n"));

			var symbol = edited.GetDocument(x.Document.Id).GetSymbolAtPosition(At(main, "Helper"));
			Assert.NotNull(symbol);
			var def = Assert.Single(edited.GetDefinitions(symbol!.SymbolId));
			Assert.Equal(libId, def.DocumentId);
			Assert.Equal("Helper", edited.GetDocument(libId).Text.GetText(def.SelectionSpan));
		}
	}

	[Fact]
	public void SnapshotIsolation_OldSnapshotResolvesOldText()
	{
		const string s = "int Helper() { return 1; }\nint main() { return Helper(); }\n";
		var x = Open(("Main.cvl", s));
		using (x.Fixture)
		{
			var oldSymbol = x.Document.GetSymbolAtPosition(s.LastIndexOf("Helper", StringComparison.Ordinal));
			Assert.NotNull(oldSymbol);

			var editedText = s.Replace("Helper", "Renamed");
			var edited = x.Snapshot.WithDocument(x.Document.Id, SourceText.From(editedText));

			Assert.Single(x.Snapshot.GetDefinitions(oldSymbol!.SymbolId));

			var newSymbol = edited.GetDocument(x.Document.Id).GetSymbolAtPosition(editedText.LastIndexOf("Renamed", StringComparison.Ordinal));
			Assert.NotNull(newSymbol);
			Assert.Equal("Renamed", newSymbol!.Name);
			Assert.Empty(edited.GetDefinitions(oldSymbol.SymbolId));
		}
	}

	[Fact]
	public void ForeignSnapshotIds_AreRejected()
	{
		const string s = "int main() { return 0; }\n";
		var x = Open(("Main.cvl", s));
		var other = Open(("Other.cvl", s));
		using (x.Fixture)
		using (other.Fixture)
		{
			Assert.Empty(x.Snapshot.GetDefinitions(default));
			Assert.Throws<KeyNotFoundException>(() => x.Snapshot.GetDocument(other.Document.Id));
		}
	}

	[Fact]
	public void SameSymbolSharesId_DistinctSymbolsDiffer()
	{
		const string s = "int Helper() { return 1; }\nint Other() { return 2; }\nint main() { return Helper() + Other() + Helper(); }\n";
		var x = Open(("Main.cvl", s));
		using (x.Fixture)
		{
			var firstHelper = x.Document.GetSymbolAtPosition(At(s, "Helper() +"));
			var lastHelper = x.Document.GetSymbolAtPosition(s.LastIndexOf("Helper", StringComparison.Ordinal));
			var other = x.Document.GetSymbolAtPosition(At(s, "Other() +"));
			Assert.NotNull(firstHelper);
			Assert.NotNull(lastHelper);
			Assert.NotNull(other);
			Assert.Equal(firstHelper!.SymbolId, lastHelper!.SymbolId);
			Assert.NotEqual(firstHelper.SymbolId, other!.SymbolId);
		}
	}

	[Fact]
	public void Documentation_IsExtractedFromTheDeclaringFile()
	{
		const string main =
			"/// <summary>\n" +
			"/// Doubles the value.\n" +
			"/// </summary>\n" +
			"int Twice(int value) { return value + value; }\n" +
			"int main() { return Twice(1) + LibHelper(); }\n";
		const string lib = "/// Returns a constant.\nint LibHelper() { return 7; }\n";
		var x = Open(("Main.cvl", main), ("Lib.cvl", lib));
		using (x.Fixture)
		{
			var local = x.Document.GetSymbolAtPosition(At(main, "Twice(1)"));
			Assert.NotNull(local);
			Assert.Equal("Doubles the value.", local!.Documentation);

			// Cross-file: documentation is read from Lib.cvl, not the requesting document.
			var crossFile = x.Document.GetSymbolAtPosition(At(main, "LibHelper()"));
			Assert.NotNull(crossFile);
			Assert.Equal("Returns a constant.", crossFile!.Documentation);
		}
	}

	[Fact]
	public void ExtensionMethodImplementingInterface_DefinitionTargetsInterfaceMember()
	{
		const string s =
			"interface S { int Sum(); }\n" +
			"struct Point { public int x; }\n" +
			"extension Point : S { int Sum() { return 1; } }\n";
		var x = Open(("Main.cvl", s));
		using (x.Fixture)
		{
			var extensionAt = s.IndexOf("extension", StringComparison.Ordinal);
			var symbol = x.Document.GetSymbolAtPosition(s.IndexOf("Sum()", extensionAt, StringComparison.Ordinal) + 1);
			Assert.NotNull(symbol);
			Assert.Equal(ToolingSymbolKind.ExtensionMethod, symbol!.Kind);

			var def = Assert.Single(x.Snapshot.GetDefinitions(symbol.SymbolId));
			Assert.Equal("Sum", x.Document.Text.GetText(def.SelectionSpan));
			Assert.True(def.SelectionSpan.Start < extensionAt, "the definition must be the interface member, not the extension method");
		}
	}

	[Fact]
	public void ExtensionMethodCall_StillTargetsImplementation_NotInterface()
	{
		const string s =
			"interface S { int Sum(); }\n" +
			"struct Point { public int x; }\n" +
			"extension Point : S { int Sum() { return 1; } }\n" +
			"int Caller() { val Point p = Point { x: 1 }; return p.Sum(); }\n";
		var x = Open(("Main.cvl", s));
		using (x.Fixture)
		{
			var extensionAt = s.IndexOf("extension", StringComparison.Ordinal);
			var symbol = x.Document.GetSymbolAtPosition(s.LastIndexOf("Sum", StringComparison.Ordinal));
			Assert.NotNull(symbol);
			Assert.Equal(ToolingSymbolKind.ExtensionMethod, symbol!.Kind);

			var def = Assert.Single(x.Snapshot.GetDefinitions(symbol.SymbolId));
			Assert.True(def.SelectionSpan.Start > extensionAt, "a call must target the implementing method, not the interface member");
		}
	}

	[Fact]
	public void StructInitializationTypeName_ResolvesToStructDeclaration()
	{
		const string s =
			"struct WindowParameters { public int width; }\n" +
			"int main() { val WindowParameters p = WindowParameters { width: 1 }; return 0; }\n";
		var x = Open(("Main.cvl", s));
		using (x.Fixture)
		{
			var symbol = x.Document.GetSymbolAtPosition(s.LastIndexOf("WindowParameters", StringComparison.Ordinal));
			Assert.NotNull(symbol);
			Assert.Equal(ToolingSymbolKind.Struct, symbol!.Kind);
			var def = Assert.Single(x.Snapshot.GetDefinitions(symbol.SymbolId));
			Assert.Equal("WindowParameters", x.Document.Text.GetText(def.SelectionSpan));
			Assert.Equal(At(s, "WindowParameters {"), def.SelectionSpan.Start);
		}
	}

	[Fact]
	public void ExtensionConstructorCall_ResolvesToStructDeclaration()
	{
		const string s =
			"struct WindowParameters { public int width; }\n" +
			"struct Window { public int handle; }\n" +
			"extension Window { public Window(WindowParameters args) { handle = 0; } }\n" +
			"int main() { val WindowParameters p = WindowParameters { width: 1 }; val Window w = Window(p); return 0; }\n";
		var x = Open(("Main.cvl", s));
		using (x.Fixture)
		{
			var symbol = x.Document.GetSymbolAtPosition(s.IndexOf("Window(p)", StringComparison.Ordinal));
			Assert.NotNull(symbol);
			Assert.Equal(ToolingSymbolKind.Struct, symbol!.Kind);
			var def = Assert.Single(x.Snapshot.GetDefinitions(symbol.SymbolId));
			Assert.Equal("Window", x.Document.Text.GetText(def.SelectionSpan));
			Assert.Equal(s.IndexOf("struct Window {", StringComparison.Ordinal) + "struct ".Length, def.SelectionSpan.Start);
		}
	}

	[Fact]
	public void InterfaceMember_ResolvesToItsOwnDeclaration()
	{
		const string s = "interface S { int Sum(); }\nint main() { return 0; }\n";
		var x = Open(("Main.cvl", s));
		using (x.Fixture)
		{
			var symbol = x.Document.GetSymbolAtPosition(At(s, "Sum"));
			Assert.NotNull(symbol);
			Assert.Equal(ToolingSymbolKind.Method, symbol!.Kind);
			var def = Assert.Single(x.Snapshot.GetDefinitions(symbol.SymbolId));
			Assert.Equal("Sum", x.Document.Text.GetText(def.SelectionSpan));
		}
	}

	[Fact]
	public void ExtensionConformanceName_ResolvesToInterfaceDeclaration()
	{
		const string s =
			"interface S { int Sum(); }\n" +
			"struct Point { public int x; }\n" +
			"extension Point : S { int Sum() { return 1; } }\n";
		var x = Open(("Main.cvl", s));
		using (x.Fixture)
		{
			var symbol = x.Document.GetSymbolAtPosition(s.IndexOf(": S", StringComparison.Ordinal) + 2);
			Assert.NotNull(symbol);
			Assert.Equal(ToolingSymbolKind.Interface, symbol!.Kind);
			var def = Assert.Single(x.Snapshot.GetDefinitions(symbol.SymbolId));
			Assert.Equal("S", x.Document.Text.GetText(def.SelectionSpan));
			Assert.Equal(At(s, "S {"), def.SelectionSpan.Start);
		}
	}

	[Fact]
	public void ExtensionName_ResolvesToExtendedTypeDeclaration()
	{
		const string s =
			"struct NonBoolRange { public int Start; public int End; }\n" +
			"extension NonBoolRange { int Count() { return 0; } }\n";
		var x = Open(("Main.cvl", s));
		using (x.Fixture)
		{
			var extensionAt = s.IndexOf("extension", StringComparison.Ordinal);
			var symbol = x.Document.GetSymbolAtPosition(s.IndexOf("NonBoolRange", extensionAt, StringComparison.Ordinal) + 1);
			Assert.NotNull(symbol);
			Assert.Equal(ToolingSymbolKind.Struct, symbol!.Kind);
			var def = Assert.Single(x.Snapshot.GetDefinitions(symbol.SymbolId));
			Assert.Equal("NonBoolRange", x.Document.Text.GetText(def.SelectionSpan));
			Assert.Equal(At(s, "NonBoolRange {"), def.SelectionSpan.Start);
		}
	}

	[Fact]
	public void ImplicitExtensionFieldReference_ResolvesToFieldDeclaration()
	{
		const string s =
			"struct NonBoolRange { public int Start; public int End; }\n" +
			"extension NonBoolRange { int Count() { return Start - 1; } }\n";
		var x = Open(("Main.cvl", s));
		using (x.Fixture)
		{
			var symbol = x.Document.GetSymbolAtPosition(s.LastIndexOf("Start", StringComparison.Ordinal) + 1);
			Assert.NotNull(symbol);
			Assert.Equal(ToolingSymbolKind.Field, symbol!.Kind);
			var def = Assert.Single(x.Snapshot.GetDefinitions(symbol.SymbolId));
			Assert.Equal("Start", x.Document.Text.GetText(def.SelectionSpan));
			Assert.Equal(At(s, "Start;"), def.SelectionSpan.Start);
		}
	}

	[Fact]
	public void AttributeName_ResolvesToMarkerTypeInStandardLibrary()
	{
		const string s =
			"[Error]\n" +
			"struct MyError { public int Code; }\n" +
			"int main() { return 0; }\n";
		var x = Open(("Main.cvl", s));
		using (x.Fixture)
		{
			var symbol = x.Document.GetSymbolAtPosition(s.IndexOf("[Error]", StringComparison.Ordinal) + 2);
			Assert.NotNull(symbol);
			Assert.Equal(ToolingSymbolKind.Struct, symbol!.Kind);
			var def = Assert.Single(x.Snapshot.GetDefinitions(symbol.SymbolId));
			var declaring = x.Snapshot.GetDocument(def.DocumentId);
			Assert.EndsWith("ErrorAttribute.cvl", declaring.FilePath, StringComparison.OrdinalIgnoreCase);
			Assert.Equal("ErrorAttribute", declaring.Text.GetText(def.SelectionSpan));
		}
	}

	[Fact]
	public void MalformedDeclaration_DoesNotThrowFromDocumentSymbolsOrTokens()
	{
		const string s = "int () { return 0; }\n";
		var x = Open(("Main.cvl", s));
		using (x.Fixture)
		{
			Assert.NotNull(x.Document.GetDocumentSymbols());
			Assert.NotNull(x.Document.GetSemanticTokens());
		}
	}

	[Fact]
	public void TryCatchFinallyBodies_ResolveCallsAndLocals()
	{
		const string s =
			"int Sum(int v) { return v; }\n" +
			"int main() {\n" +
			"    val int a = 1;\n" +
			"    try {\n" +
			"        val int b = Sum(a);\n" +
			"        return b;\n" +
			"    } catch {\n" +
			"        return 0;\n" +
			"    } finally {\n" +
			"        val int c = Sum(a);\n" +
			"    }\n" +
			"}\n";
		var x = Open(("Main.cvl", s));
		using (x.Fixture)
		{
			var tryCall = x.Document.GetSymbolAtPosition(s.IndexOf("Sum(a)", StringComparison.Ordinal) + 1);
			var finallyCall = x.Document.GetSymbolAtPosition(s.LastIndexOf("Sum(a)", StringComparison.Ordinal) + 1);
			var finallyLocal = x.Document.GetSymbolAtPosition(s.LastIndexOf("Sum(a)", StringComparison.Ordinal) + 4);
			Assert.NotNull(tryCall);
			Assert.NotNull(finallyCall);
			Assert.NotNull(finallyLocal);
			Assert.Equal(ToolingSymbolKind.Function, tryCall!.Kind);
			Assert.Equal(ToolingSymbolKind.Function, finallyCall!.Kind);
			Assert.Equal(ToolingSymbolKind.Local, finallyLocal!.Kind);
		}
	}
}
