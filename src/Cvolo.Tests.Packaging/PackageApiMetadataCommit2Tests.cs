using System.Text.Json;
using Cvolo.Analysis;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using Cvolo.Core.Diagnostics;
using Cvolo.Packaging;
using Cvolo.Syntax.Antlr;

namespace Cvolo.Tests.Packaging;

public sealed class PackageApiMetadataCommit2Tests
{
	[Fact]
	public void Metadata_RoundTripsPublicNativeDelegateAndRawUnion()
	{
		var source = Parse("""
namespace NativeApi;

public unsafe "C" delegate int Callback(int value);

unsafe "system" {
    public delegate void SystemCallback(nint value);
}

public unsafe union NativeValue {
    public int Integer;
    public double Real;
}
""");

		var metadata = PackageApiMetadata.FromCompilationUnits([source]);
		var unit = Assert.Single(metadata.Units);

		Assert.Equal(2, unit.Delegates.Count);
		var callback = Assert.Single(unit.Delegates, type => type.Name == "Callback");
		Assert.True(callback.IsNative);
		Assert.Equal("C", callback.CallingConvention);
		Assert.Equal("int", callback.ReturnType);
		Assert.Equal("int", Assert.Single(callback.Parameters).Type);

		var systemCallback = Assert.Single(unit.Delegates, type => type.Name == "SystemCallback");
		Assert.True(systemCallback.IsNative);
		Assert.Equal("system", systemCallback.CallingConvention);

		var rawUnion = Assert.Single(unit.Unions);
		Assert.Equal("NativeValue", rawUnion.Name);
		Assert.True(rawUnion.IsUnsafe);
		Assert.Collection(rawUnion.Fields,
			field => { Assert.Equal("int", field.Type); Assert.Equal("Integer", field.Name); Assert.Equal(Visibility.Public, field.Visibility); },
			field => { Assert.Equal("double", field.Type); Assert.Equal("Real", field.Name); Assert.Equal(Visibility.Public, field.Visibility); });

		var rehydrated = Assert.Single(metadata.CreateCompilationUnits("native-api", "1.0.0"));
		var members = rehydrated.NamespaceDeclaration!.Members;
		var delegateDecls = members.OfType<DelegateDeclarationSyntax>().ToArray();
		Assert.Equal(2, delegateDecls.Length);
		Assert.All(delegateDecls, declaration => Assert.True(declaration.IsNative));
		Assert.Contains(delegateDecls, declaration => declaration.Name == "Callback" && declaration.CallingConvention == "C");
		Assert.Contains(delegateDecls, declaration => declaration.Name == "SystemCallback" && declaration.CallingConvention == "system");

		var unionDecl = Assert.Single(members.OfType<UnionDeclarationSyntax>());
		Assert.True(unionDecl.IsUnsafe);
		Assert.Equal(["Integer", "Real"], unionDecl.Fields.Select(field => field.Name).ToArray());
	}

	[Fact]
	public void Metadata_RoundTripsStandaloneForeignGlobalAndNativeLibrary()
	{
		var source = Parse("""
namespace NativeApi;

[LibraryImport("native_state", win: "./native_state.lib", linux: "./libnative_state.so", mac: "./libnative_state.dylib")]
[ImportName("native_counter")]
public extern "C" global var int Counter;
""");

		var metadata = PackageApiMetadata.FromCompilationUnits([source]);
		var library = Assert.Single(metadata.NativeLibraries);
		Assert.Equal("native_state", library.LibraryName);
		Assert.Equal("./native_state.lib", library.WinPath);
		Assert.Equal("./libnative_state.so", library.LinuxPath);
		Assert.Equal("./libnative_state.dylib", library.MacPath);

		var global = Assert.Single(Assert.Single(metadata.Units).Globals);
		Assert.True(global.IsForeign);
		Assert.Equal("C", global.CallingConvention);
		Assert.Equal("native_counter", global.ImportName);
		Assert.Equal("native_state", global.LibraryName);
		Assert.Equal("./native_state.lib", global.WinPath);
		Assert.Equal("./libnative_state.so", global.LinuxPath);
		Assert.Equal("./libnative_state.dylib", global.MacPath);

		var rehydrated = Assert.Single(metadata.CreateCompilationUnits("native-api", "1.0.0"));
		var declaration = Assert.Single(rehydrated.NamespaceDeclaration!.Members.OfType<GlobalVariableDeclarationSyntax>());
		Assert.True(declaration.IsForeign);
		Assert.Equal("C", declaration.CallingConvention);
		Assert.Contains(declaration.Attributes, attribute => attribute.Name == "LibraryImport");
		Assert.Contains(declaration.Attributes, attribute => attribute.Name == "ImportName");
	}

	[Fact]
	public void Metadata_RoundTripsForeignGlobalInheritedFromExternBlock()
	{
		var source = Parse("""
namespace NativeApi;

[LibraryImport("native_state", linux: "./libnative_state.so")]
extern "C" {
    [ImportName("native_counter")]
    public global var int Counter;
}
""");

		var metadata = PackageApiMetadata.FromCompilationUnits([source]);
		var global = Assert.Single(Assert.Single(metadata.Units).Globals);
		Assert.True(global.IsForeign);
		Assert.Equal("C", global.CallingConvention);
		Assert.Equal("native_counter", global.ImportName);
		Assert.Equal("native_state", global.LibraryName);
		Assert.Equal("./libnative_state.so", global.LinuxPath);

		// Extern-block ownership is intentionally normalized to an equivalent standalone foreign
		// declaration in metadata. This keeps the package API self-contained while preserving the
		// exact linker and native-symbol binding information required by the consumer.
		var rehydrated = Assert.Single(metadata.CreateCompilationUnits("native-api", "1.0.0"));
		var declaration = Assert.Single(rehydrated.NamespaceDeclaration!.Members.OfType<GlobalVariableDeclarationSyntax>());
		Assert.True(declaration.IsForeign);
		Assert.Contains(declaration.Attributes, attribute => attribute.Name == "LibraryImport");
		Assert.Contains(declaration.Attributes, attribute => attribute.Name == "ImportName");
	}

	[Fact]
	public void Metadata_JsonRoundTripPreservesCommit2Surface()
	{
		var source = Parse("""
namespace NativeApi;

public unsafe "C" delegate int Callback(int value);

public unsafe union NativeValue {
    public int Integer;
    internal double HiddenReal;
}

[LibraryImport("native_state", linux: "./libnative_state.so")]
[ImportName("native_counter")]
public extern "system" global var int Counter;
""");

		var serialized = PackageApiMetadata.FromCompilationUnits([source]).Serialize();
		var metadata = JsonSerializer.Deserialize<PackageApiMetadata>(serialized);
		Assert.NotNull(metadata);
		Assert.Equal(PackageApiMetadata.FormatId, metadata!.Format);

		var unit = Assert.Single(metadata.Units);
		var callback = Assert.Single(unit.Delegates);
		Assert.True(callback.IsNative);
		Assert.Equal("C", callback.CallingConvention);

		var rawUnion = Assert.Single(unit.Unions);
		Assert.True(rawUnion.IsUnsafe);
		Assert.Equal(Visibility.Internal, rawUnion.Fields.Single(field => field.Name == "HiddenReal").Visibility);

		var global = Assert.Single(unit.Globals);
		Assert.True(global.IsForeign);
		Assert.Equal("system", global.CallingConvention);
		Assert.Equal("native_counter", global.ImportName);
		Assert.Equal("native_state", global.LibraryName);
		Assert.Equal("./libnative_state.so", global.LinuxPath);
	}

	[Fact]
	public void Metadata_MergesPlatformPathsForRepeatedNativeLibrary()
	{
		var source = Parse("""
namespace NativeApi;

[LibraryImport("native_state", win: "./native_state.lib")]
extern "C" {
    global int WindowsState;
}

[LibraryImport("native_state", linux: "./libnative_state.so", mac: "./libnative_state.dylib")]
extern "C" {
    global int UnixState;
}
""");

		var library = Assert.Single(PackageApiMetadata.FromCompilationUnits([source]).NativeLibraries);
		Assert.Equal("native_state", library.LibraryName);
		Assert.Equal("./native_state.lib", library.WinPath);
		Assert.Equal("./libnative_state.so", library.LinuxPath);
		Assert.Equal("./libnative_state.dylib", library.MacPath);
	}

	[Fact]
	public void UnionFieldVisibility_PreservesExplicitModifierAndEnclosingInheritance()
	{
		var local = Parse("""
private union HiddenResult {
    int HiddenCode;
}

public unsafe union NativeValue {
    internal int InternalValue;
    public double PublicValue;
}
""");

		var localBinder = BindWithExternalPackageUnits([], [local]);
		var hidden = Assert.IsType<UnionTypeSymbol>(localBinder.Context.UnionTypes["HiddenResult"]);
		Assert.Equal(Visibility.Private, Assert.Single(hidden.Fields).Visibility);

		var metadata = PackageApiMetadata.FromCompilationUnits([local]);
		var packageUnit = Assert.Single(metadata.CreateCompilationUnits("native-api", "1.0.0"));
		var packageBinder = BindExternalPackageUnits([packageUnit]);
		var nativeValue = Assert.IsType<UnionTypeSymbol>(packageBinder.Context.UnionTypes["NativeValue"]);

		Assert.Equal(Visibility.Internal, Assert.Single(nativeValue.Fields, field => field.Name == "InternalValue").Visibility);
		Assert.Equal(Visibility.Public, Assert.Single(nativeValue.Fields, field => field.Name == "PublicValue").Visibility);
	}

	[Fact]
	public void RehydratedPackageApi_PreservesNominalTypeIdentityAcrossSignatures()
	{
		var source = Parse("""
namespace Handles;

public struct Handle {
    public int Value;
}

public struct OtherHandle {
    public int Value;
}

public Handle Echo(Handle value) { return value; }
""");

		var metadata = PackageApiMetadata.FromCompilationUnits([source]);
		var packageUnit = Assert.Single(metadata.CreateCompilationUnits("handles", "2.4.0"));
		var binder = BindExternalPackageUnits([packageUnit]);

		var handle = Assert.IsType<StructTypeSymbol>(binder.Context.StructTypes["Handles.Handle"]);
		var otherHandle = Assert.IsType<StructTypeSymbol>(binder.Context.StructTypes["Handles.OtherHandle"]);
		var function = Assert.Single(binder.Context.OverloadedFunctions["Handles.Echo"]);

		// Package metadata must rehydrate one canonical nominal symbol. Parameters and return values
		// reference that declaration rather than independent structural copies with the same shape.
		Assert.Same(handle, function.ReturnType);
		Assert.Same(handle, Assert.Single(function.Parameters).Type);
		Assert.NotEqual(handle, otherHandle);
	}

	[Fact]
	public void NativeDelegateNominalIdentityAcrossPackages()
	{
		var alphaSource = Parse("""
namespace Alpha;

public unsafe "C" delegate int Callback(int value);
public unsafe "C" delegate int First(int value);
public unsafe "C" delegate int Second(int value);
public int UseFirst(First callback) { return 0; }
""");
		var betaSource = Parse("""
namespace Beta;

public unsafe "C" delegate int Callback(int value);
""");

		// Serialize through JSON before rehydration so the assertion covers the actual package
		// metadata representation rather than sharing syntax or symbol instances with the producer.
		var alphaMetadata = JsonSerializer.Deserialize<PackageApiMetadata>(PackageApiMetadata.FromCompilationUnits([alphaSource]).Serialize());
		var betaMetadata = JsonSerializer.Deserialize<PackageApiMetadata>(PackageApiMetadata.FromCompilationUnits([betaSource]).Serialize());
		Assert.NotNull(alphaMetadata);
		Assert.NotNull(betaMetadata);

		var alphaUnit = Assert.Single(alphaMetadata!.CreateCompilationUnits("alpha-native", "1.0.0"));
		var betaUnit = Assert.Single(betaMetadata!.CreateCompilationUnits("beta-native", "1.0.0"));
		var binder = BindExternalPackageUnits([alphaUnit, betaUnit]);

		var alphaCallback = Assert.IsType<DelegateTypeSymbol>(binder.Context.DelegateTypes["Alpha.Callback"]);
		var betaCallback = Assert.IsType<DelegateTypeSymbol>(binder.Context.DelegateTypes["Beta.Callback"]);
		var first = Assert.IsType<DelegateTypeSymbol>(binder.Context.DelegateTypes["Alpha.First"]);
		var second = Assert.IsType<DelegateTypeSymbol>(binder.Context.DelegateTypes["Alpha.Second"]);
		var useFirst = Assert.Single(binder.Context.OverloadedFunctions["Alpha.UseFirst"]);

		Assert.True(alphaCallback.IsNative);
		Assert.True(betaCallback.IsNative);
		Assert.Equal("C", alphaCallback.CallingConvention);
		Assert.Equal("C", betaCallback.CallingConvention);
		Assert.NotSame(alphaCallback, betaCallback);
		Assert.NotEqual(alphaCallback, betaCallback);
		Assert.NotSame(first, second);
		Assert.NotEqual(first, second);

		// Function signatures must point at the canonical declaration symbol. Reconstructing a
		// signature-shaped delegate here would violate nominal identity across the package boundary.
		Assert.Same(first, Assert.Single(useFirst.Parameters).Type);
	}

	[Fact]
	public void RehydratedPackageApis_KeepSameShortNameDistinctAcrossNamespaces()
	{
		var first = new PackageApiMetadata
		{
			Units = [new PackageApiUnit
			{
				Namespace = "Alpha",
				Structs = [new PackageApiStruct { Name = "Token", Fields = [new PackageApiStructField("int", "Value", Visibility.Public)] }]
			}]
		}.CreateCompilationUnits("alpha", "1.0.0").Single();

		var second = new PackageApiMetadata
		{
			Units = [new PackageApiUnit
			{
				Namespace = "Beta",
				Structs = [new PackageApiStruct { Name = "Token", Fields = [new PackageApiStructField("int", "Value", Visibility.Public)] }]
			}]
		}.CreateCompilationUnits("beta", "1.0.0").Single();

		var binder = BindExternalPackageUnits([first, second]);
		var alpha = binder.Context.StructTypes["Alpha.Token"];
		var beta = binder.Context.StructTypes["Beta.Token"];

		Assert.NotSame(alpha, beta);
		Assert.NotEqual(alpha, beta);
	}

	[Fact]
	public void ConsumerBinding_ReusesProducerNominalTypeAcrossPackageBoundary()
	{
		var producer = Parse("""
namespace Handles;

public struct Handle {
    public int Value;
}

public Handle Echo(Handle value) { return value; }
""");

		var packageUnit = Assert.Single(PackageApiMetadata.FromCompilationUnits([producer]).CreateCompilationUnits("handles", "2.4.0"));
		var consumer = Parse("""
using Handles;

int Consume(Handle value) {
    Handle echoed = Echo(value);
    return echoed.Value;
}
""");

		var binder = BindWithExternalPackageUnits([packageUnit], [consumer]);
		var handle = Assert.IsType<StructTypeSymbol>(binder.Context.StructTypes["Handles.Handle"]);
		var producerFunction = Assert.Single(binder.Context.OverloadedFunctions["Handles.Echo"]);
		var consumerFunction = Assert.Single(binder.Context.OverloadedFunctions["Consume"]);

		Assert.Same(handle, producerFunction.ReturnType);
		Assert.Same(handle, Assert.Single(producerFunction.Parameters).Type);
		Assert.Same(handle, Assert.Single(consumerFunction.Parameters).Type);
	}

	[Fact]
	public void RehydratedForeignGlobal_RegistersNativeBindingMetadataInConsumerBinder()
	{
		var producer = Parse("""
namespace NativeApi;

[LibraryImport("native_state", linux: "./libnative_state.so")]
[ImportName("native_counter")]
public extern "C" global var int Counter;
""");

		var packageUnit = Assert.Single(PackageApiMetadata.FromCompilationUnits([producer]).CreateCompilationUnits("native-api", "1.0.0"));
		var consumer = Parse("""
using NativeApi;

int ReadCounter() { return Counter; }
""");

		var binder = BindWithExternalPackageUnits([packageUnit], [consumer]);
		var symbol = binder.Context.GlobalsByQualifiedName["NativeApi.Counter"];
		Assert.True(symbol.IsForeign);
		Assert.Equal("native_counter", symbol.ImportName);
		Assert.Equal("native_state", symbol.LibraryName);
		Assert.Equal("C", symbol.CallingConvention);

		var library = binder.Context.NativeLibraries["native_state"];
		Assert.Equal("./libnative_state.so", library.LinuxPath);
	}

	private static CompilationUnitSyntax Parse(string source)
	{
		var context = new CompilationContext(source, "<metadata-test>");
		var parser = new AntlrSyntaxParser();
		var unit = parser.Parse(context);
		Assert.NotNull(unit);
		Assert.False(parser.Diagnostics.HasErrors, string.Join(Environment.NewLine, parser.Diagnostics.Diagnostics.Select(diagnostic => diagnostic.Message)));
		return unit!;
	}

	private static Binder BindExternalPackageUnits(IReadOnlyList<CompilationUnitSyntax> units) =>
		BindWithExternalPackageUnits(units, []);

	private static Binder BindWithExternalPackageUnits(
		IReadOnlyList<CompilationUnitSyntax> externalUnits,
		IReadOnlyList<CompilationUnitSyntax> consumerUnits)
	{
		var binder = new Binder();
		foreach (var unit in externalUnits)
		{
			binder.Context.FileContexts[unit] = unit.Context;
			binder.Context.ExternalPackageUnits.Add(unit);
		}

		foreach (var unit in consumerUnits)
			binder.Context.FileContexts[unit] = unit.Context;

		binder.Bind([.. externalUnits, .. consumerUnits]);
		Assert.False(binder.Diagnostics.HasErrors, string.Join(Environment.NewLine, binder.Diagnostics.Diagnostics.Select(diagnostic => diagnostic.Message)));
		return binder;
	}
}
