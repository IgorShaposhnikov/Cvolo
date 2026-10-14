using Cvolo.Compiler.Tooling;

namespace Cvolo.Tests.Tooling;

/// <summary>
/// INC 3.1 NAV: navigation resolves by compiler symbol identity, not by spelling. A name that
/// happens to match a declaration elsewhere (a wrapper function, another file, another namespace)
/// must not capture a reference that binds to a different symbol, and malformed code in one
/// declaration must not break navigation for a structurally separate, valid declaration.
/// </summary>
public sealed class NavigationIdentityTests
{
	private static (CvoloProject Project, ProjectSnapshot Snapshot, DocumentSnapshot Document, TempProject Fixture) Open(string primary, params (string FileName, string Source)[] sources)
	{
		var fixture = TempProject.Create(sources);
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		var snapshot = project.InitialSnapshot;
		return (project, snapshot, snapshot.GetDocument(project.GetDocumentId(primary)), fixture);
	}

	[Fact]
	public void QualifiedGlobalDelegateCall_ResolvesToTheGlobal_NotASameNamedFunction()
	{
		const string native = "namespace OpenGL.Native;\n"
			+ "unsafe \"C\" delegate void DeleteShaderProc(uint shader);\n"
			+ "global var DeleteShaderProc DeleteShader;\n";
		// Same leaf name as the global slot, but a different symbol.
		const string api = "using Native = OpenGL.Native;\n"
			+ "namespace OpenGL;\n"
			+ "public void DeleteShader(uint shader) { }\n"
			+ "public void Driver(uint shader) { unsafe { Native.DeleteShader(shader); } }\n";
		var x = Open("Api.cvl", ("Native.cvl", native), ("Api.cvl", api));
		using (x.Fixture)
		{
			int position = api.IndexOf("Native.DeleteShader") + "Native.".Length;

			var symbol = x.Document.GetSymbolAtPosition(position);

			Assert.NotNull(symbol);
			Assert.Equal("DeleteShader", symbol!.Name);
			Assert.Equal(ToolingSymbolKind.Global, symbol.Kind);

			var definitions = x.Snapshot.GetDefinitions(symbol.SymbolId);

			Assert.Single(definitions);
			Assert.Equal(x.Project.GetDocumentId("Native.cvl"), definitions[0].DocumentId);
		}
	}

	[Fact]
	public void MalformedNeighbourDeclaration_DoesNotBreakNavigationForAValidGlobal()
	{
		const string source = "global int Version = 3;\n"
			+ "int Broken() { return 1 2; }\n"
			+ "int main() { return Version; }\n";
		var x = Open("Main.cvl", ("Main.cvl", source));
		using (x.Fixture)
		{
			int position = source.LastIndexOf("Version");

			var symbol = x.Document.GetSymbolAtPosition(position);

			Assert.NotNull(symbol);
			Assert.Equal("Version", symbol!.Name);
			Assert.Equal(ToolingSymbolKind.Global, symbol.Kind);
			Assert.Single(x.Snapshot.GetDefinitions(symbol.SymbolId));
		}
	}
}
