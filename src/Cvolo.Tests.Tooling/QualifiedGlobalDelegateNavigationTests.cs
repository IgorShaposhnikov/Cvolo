using Cvolo.Compiler.Tooling;

namespace Cvolo.Tests.Tooling;

/// <summary>
/// Navigation for a global delegate-valued variable reached through a namespace alias, mirroring the
/// supplied OpenGL package: <c>using Native = OpenGL.Native;</c> then <c>Native.DeleteShader(shader)</c>.
/// The member belongs to the aliased namespace, not to a type, so the callee must resolve to the
/// global variable declaration rather than to a same-named wrapper function or delegate type.
/// </summary>
public sealed class QualifiedGlobalDelegateNavigationTests
{
	private static (ProjectSnapshot Snapshot, DocumentSnapshot Api, DocumentSnapshot Native, TempProject Fixture) Open()
	{
		const string NativeSource =
			"namespace NativeCallbackTest;\n" +
			"\n" +
			"unsafe \"C\" delegate int Callback(int value);\n" +
			"\n" +
			"global var Callback Slot;\n";

		const string ApiSource =
			"using SlotAlias = NativeCallbackTest;\n" +
			"\n" +
			"namespace Api;\n" +
			"\n" +
			"unsafe \"C\" int Increment(int value) { return value + 1; }\n" +
			"\n" +
			"int main()\n" +
			"{\n" +
			"    unsafe\n" +
			"    {\n" +
			"        SlotAlias.Slot = &Increment;\n" +
			"        if (SlotAlias.Slot(41) != 42) { return 1; }\n" +
			"    }\n" +
			"    return 0;\n" +
			"}\n";

		var fixture = TempProject.Create(("Api.cvl", ApiSource), ("Native.cvl", NativeSource));
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		var snapshot = project.InitialSnapshot;
		return (
			snapshot,
			snapshot.GetDocument(project.GetDocumentId("Api.cvl")),
			snapshot.GetDocument(project.GetDocumentId("Native.cvl")),
			fixture);
	}

	private static int At(string source, string needle) =>
		source.IndexOf(needle, StringComparison.Ordinal);

	[Fact]
	public void AliasQualifiedGlobalDelegateCall_ResolvesToTheGlobalDeclaration()
	{
		var x = Open();
		using (x.Fixture)
		{
			const string ApiSource =
				"using SlotAlias = NativeCallbackTest;\n" +
				"\n" +
				"namespace Api;\n" +
				"\n" +
				"unsafe \"C\" int Increment(int value) { return value + 1; }\n" +
				"\n" +
				"int main()\n" +
				"{\n" +
				"    unsafe\n" +
				"    {\n" +
				"        SlotAlias.Slot = &Increment;\n" +
				"        if (SlotAlias.Slot(41) != 42) { return 1; }\n" +
				"    }\n" +
				"    return 0;\n" +
				"}\n";

			var callPosition = At(ApiSource, "SlotAlias.Slot(41)") + "SlotAlias.".Length;
			var symbol = x.Api.GetSymbolAtPosition(callPosition);

			Assert.NotNull(symbol);
			Assert.Equal(ToolingSymbolKind.Global, symbol!.Kind);

			var definition = Assert.Single(x.Snapshot.GetDefinitions(symbol.SymbolId));
			Assert.Equal(x.Native.Id, definition.DocumentId);
			Assert.Equal("Slot", x.Native.Text.GetText(definition.SelectionSpan));
		}
	}

	[Fact]
	public void AliasQualifiedGlobalDelegateAssignmentTarget_ResolvesToTheGlobalDeclaration()
	{
		var x = Open();
		using (x.Fixture)
		{
			const string ApiSource =
				"using SlotAlias = NativeCallbackTest;\n" +
				"\n" +
				"namespace Api;\n" +
				"\n" +
				"unsafe \"C\" int Increment(int value) { return value + 1; }\n" +
				"\n" +
				"int main()\n" +
				"{\n" +
				"    unsafe\n" +
				"    {\n" +
				"        SlotAlias.Slot = &Increment;\n" +
				"        if (SlotAlias.Slot(41) != 42) { return 1; }\n" +
				"    }\n" +
				"    return 0;\n" +
				"}\n";

			var assignPosition = At(ApiSource, "SlotAlias.Slot = &Increment") + "SlotAlias.".Length;
			var symbol = x.Api.GetSymbolAtPosition(assignPosition);

			Assert.NotNull(symbol);
			Assert.Equal(ToolingSymbolKind.Global, symbol!.Kind);

			var definition = Assert.Single(x.Snapshot.GetDefinitions(symbol.SymbolId));
			Assert.Equal(x.Native.Id, definition.DocumentId);
			Assert.Equal("Slot", x.Native.Text.GetText(definition.SelectionSpan));
		}
	}
}
