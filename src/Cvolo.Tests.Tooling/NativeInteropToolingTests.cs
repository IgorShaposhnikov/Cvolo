using Cvolo.Compiler.Tooling;
using Cvolo.Compiler.Tooling.Completion;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;

namespace Cvolo.Tests.Tooling;

/// <summary>
/// Regression coverage for the package-native tooling surface. The external declarations are
/// reconstructed from PackageApiMetadata before they enter the tooling binder, matching installed
/// package semantics without requiring a global package-cache fixture.
/// </summary>
public sealed class NativeInteropToolingTests
{
	private static int At(string source, string needle, int delta = 0) =>
		source.IndexOf(needle, StringComparison.Ordinal) + delta;

	[Fact]
	public void PackageMetadata_ReconstructsNativeInteropAstForTooling()
	{
		var metadata = ExternalPackageToolingFixture.NativeSurface();
		var unit = Assert.Single(metadata.CreateCompilationUnits("NativeApi", "1.0.0"));
		var members = unit.NamespaceDeclaration!.Members;

		var nativeDelegate = Assert.Single(members.OfType<DelegateDeclarationSyntax>());
		Assert.True(nativeDelegate.IsNative);
		Assert.Equal("C", nativeDelegate.CallingConvention);

		var rawUnion = Assert.Single(members.OfType<UnionDeclarationSyntax>());
		Assert.True(rawUnion.IsUnsafe);
		Assert.Equal(Visibility.Public, Assert.Single(rawUnion.Fields).Visibility);

		var foreignGlobal = Assert.Single(members.OfType<GlobalVariableDeclarationSyntax>());
		Assert.True(foreignGlobal.IsForeign);
		Assert.Equal("system", foreignGlobal.CallingConvention);
		Assert.Contains(foreignGlobal.Attributes, attribute => attribute.Name == "LibraryImport");
		Assert.Contains(foreignGlobal.Attributes, attribute => attribute.Name == "ImportName");
	}

	[Fact]
	public void ImportedNativeSurface_ParticipatesInCompletionAndSemanticHighlighting()
	{
		const string source = """
using NativeApi;
int Read(Callback callback, Payload payload) {
    unsafe {
        val int copy = Counter;
        return payload.Value + copy;
    }
}
""";
		var (_, document) = ExternalPackageToolingFixture.Create(source, ExternalPackageToolingFixture.NativeSurface());

		var delegateCompletion = document.GetCompletions(At(source, "Callback", "Call".Length));
		Assert.Contains(delegateCompletion.Candidates, candidate => candidate.Label == "Callback" && candidate.Kind == CompletionKind.Type);

		var unionCompletion = document.GetCompletions(At(source, "Payload", "Pay".Length));
		Assert.Contains(unionCompletion.Candidates, candidate => candidate.Label == "Payload" && candidate.Kind == CompletionKind.Type);

		var counterCompletion = document.GetCompletions(At(source, "Counter", "Cou".Length));
		Assert.Contains(counterCompletion.Candidates, candidate => candidate.Label == "Counter" && candidate.Kind == CompletionKind.Global);

		var memberCompletion = document.GetCompletions(At(source, "Value", "Val".Length));
		Assert.Contains(memberCompletion.Candidates, candidate => candidate.Label == "Value" && candidate.Kind == CompletionKind.UnionVariant);

		var tokens = document.GetSemanticTokens();
		Assert.Contains(tokens, token => document.Text.GetText(token.Span) == "Callback" && token.Kind == ToolingSymbolKind.Delegate);
		Assert.Contains(tokens, token => document.Text.GetText(token.Span) == "Payload" && token.Kind == ToolingSymbolKind.Union);
		Assert.Contains(tokens, token => document.Text.GetText(token.Span) == "Counter" && token.Kind == ToolingSymbolKind.Global);
		Assert.Contains(tokens, token => document.Text.GetText(token.Span) == "Value" && token.Kind == ToolingSymbolKind.Field);
	}

	[Fact]
	public void HoverOnImportedNativeDelegate_PreservesCallingConventionAndExternalIdentity()
	{
		const string source = "using NativeApi;\nint Use(Callback callback) { return 0; }\n";
		var (snapshot, document) = ExternalPackageToolingFixture.Create(source, ExternalPackageToolingFixture.NativeSurface());

		var symbol = document.GetSymbolAtPosition(At(source, "Callback"));

		Assert.NotNull(symbol);
		Assert.Equal(ToolingSymbolKind.Delegate, symbol!.Kind);
		Assert.Equal("unsafe \"C\" delegate int Callback(int value)", symbol.DisplayText);
		Assert.NotNull(symbol.NativeInterop);
		Assert.Equal(NativeInteropKind.NativeDelegate, symbol.NativeInterop!.Kind);
		Assert.Equal("C", symbol.NativeInterop.CallingConvention);
		Assert.Empty(snapshot.GetDefinitions(symbol.SymbolId));
	}

	[Fact]
	public void HoverOnImportedRawUnionAndForeignGlobal_ReconstructsInteropMetadata()
	{
		const string source = """
using NativeApi;
int Read(Payload payload) {
    unsafe { return payload.Value + Counter; }
}
""";
		var (snapshot, document) = ExternalPackageToolingFixture.Create(source, ExternalPackageToolingFixture.NativeSurface());

		var union = document.GetSymbolAtPosition(At(source, "Payload"));
		Assert.NotNull(union);
		Assert.Equal("unsafe union Payload", union!.DisplayText);
		Assert.Equal(NativeInteropKind.RawUnion, union.NativeInterop?.Kind);
		Assert.Empty(snapshot.GetDefinitions(union.SymbolId));

		var global = document.GetSymbolAtPosition(At(source, "Counter"));
		Assert.NotNull(global);
		Assert.Equal("extern \"system\" global var int Counter", global!.DisplayText);
		Assert.Equal(NativeInteropKind.ForeignGlobal, global.NativeInterop?.Kind);
		Assert.Equal("system", global.NativeInterop?.CallingConvention);
		Assert.Equal("native_counter", global.NativeInterop?.ImportName);
		Assert.Equal("native", global.NativeInterop?.LibraryName);
		Assert.Equal("native.lib", global.NativeInterop?.WinPath);
		Assert.Equal("./libnative.so", global.NativeInterop?.LinuxPath);
		Assert.Equal("./libnative.dylib", global.NativeInterop?.MacPath);
		Assert.Empty(snapshot.GetDefinitions(global.SymbolId));
	}

	[Fact]
	public void SourceRawUnionField_NavigatesToItsDeclaration()
	{
		const string source = """
unsafe union Payload {
    int Value;
}
int Read(Payload payload) {
    unsafe { return payload.Value; }
}
""";
		using var fixture = TempProject.Create(("Main.cvl", source));
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		var snapshot = project.InitialSnapshot;
		var document = snapshot.GetDocument(project.GetDocumentId("Main.cvl"));

		var symbol = document.GetSymbolAtPosition(source.LastIndexOf("Value", StringComparison.Ordinal));

		Assert.NotNull(symbol);
		Assert.Equal(ToolingSymbolKind.Field, symbol!.Kind);
		var definition = Assert.Single(snapshot.GetDefinitions(symbol.SymbolId));
		Assert.Equal("Value", document.Text.GetText(definition.SelectionSpan));
	}

	[Fact]
	public void SourceExternBlockGlobal_HoverAndNavigationUseInheritedLibraryMetadata()
	{
		const string source = """
[LibraryImport("native", win: "native.lib", linux: "./libnative.so", mac: "./libnative.dylib")]
extern "system" {
    [ImportName("native_counter")] global var int Counter;
}
int Read() { return Counter; }
""";
		using var fixture = TempProject.Create(("Main.cvl", source));
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		var snapshot = project.InitialSnapshot;
		var document = snapshot.GetDocument(project.GetDocumentId("Main.cvl"));

		var symbol = document.GetSymbolAtPosition(source.LastIndexOf("Counter", StringComparison.Ordinal));

		Assert.NotNull(symbol);
		Assert.Equal(ToolingSymbolKind.Global, symbol!.Kind);
		Assert.Equal(NativeInteropKind.ForeignGlobal, symbol.NativeInterop?.Kind);
		Assert.Equal("system", symbol.NativeInterop?.CallingConvention);
		Assert.Equal("native_counter", symbol.NativeInterop?.ImportName);
		Assert.Equal("native", symbol.NativeInterop?.LibraryName);
		Assert.Equal("native.lib", symbol.NativeInterop?.WinPath);
		Assert.Equal("./libnative.so", symbol.NativeInterop?.LinuxPath);
		Assert.Equal("./libnative.dylib", symbol.NativeInterop?.MacPath);
		var definition = Assert.Single(snapshot.GetDefinitions(symbol.SymbolId));
		Assert.Equal("Counter", document.Text.GetText(definition.SelectionSpan));
	}

	[Fact]
	public void ExternBlockGlobal_IsPresentInDocumentNavigationOutline()
	{
		const string source = """
[LibraryImport("native")]
extern "C" {
    [ImportName("native_counter")] global var int Counter;
}
int main() { return 0; }
""";
		using var fixture = TempProject.Create(("Main.cvl", source));
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		var document = project.InitialSnapshot.GetDocument(project.GetDocumentId("Main.cvl"));

		var symbols = document.GetDocumentSymbols();
		Assert.Contains(symbols, symbol => symbol.Name == "Counter" && symbol.Kind == ToolingSymbolKind.Global);
	}
}
