using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.FFI;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.Diagnostics;
using Cvolo.Tests.Core;

namespace Cvolo.Tests;

public sealed class UnsafeCAbiInteropTests : CompilerTestBase
{
	[Fact]
	public void NativeDelegateDeclarations_AreBoundWithSourceConvention()
	{
		var (asts, context) = AnalyzeProject("UnsafeCAbi/NativeDelegateDeclarations.cvl");

		Assert.NotNull(asts);
		Assert.DoesNotContain(context.Diagnostics.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);

		var c = Assert.Single(context.DelegateTypes.Values, d => d.Name.EndsWith("CCallback", StringComparison.Ordinal));
		Assert.True(c.IsNative);
		Assert.Equal("C", c.CallingConvention);

		var system = Assert.Single(context.DelegateTypes.Values, d => d.Name.EndsWith("SystemCallback", StringComparison.Ordinal));
		Assert.True(system.IsNative);
		Assert.Equal("system", system.CallingConvention);
	}

	[Theory]
	[InlineData("UnsafeCAbi/NativeDelegateNullInitUnsafeFail.cvl")]
	[InlineData("UnsafeCAbi/NativeDelegateNullCompareUnsafeFail.cvl")]
	[InlineData("UnsafeCAbi/NativeDelegateNullUnboundFail.cvl")]
	[InlineData("UnsafeCAbi/NativeDelegateNullArgumentUnsafeFail.cvl")]
	public void NativeDelegateExplicitNull_RequiresUnsafeCapability(string caseFile)
	{
		var (exitCode, _, stderr) = RunCompilerCheck(caseFile);

		Assert.Equal(1, exitCode);
		Assert.Contains("CVL1104", stderr);
	}

	[Theory]
	[InlineData("UnsafeCAbi/NativeDelegateSliceFail.cvl", "CVLF2050")]
	[InlineData("UnsafeCAbi/NativeDelegateAggregateParamFail.cvl", "CVLF2051")]
	[InlineData("UnsafeCAbi/NativeAbiAggregateReturnFail.cvl", "CVLF2051")]
	[InlineData("UnsafeCAbi/ExternAggregateParamFail.cvl", "CVLF2051")]
	[InlineData("UnsafeCAbi/ExternFixedArrayParamFail.cvl", "CVLF2050")]
	[InlineData("UnsafeCAbi/NativeDelegateBlockConventionFail.cvl", "CVL1700")]
	[InlineData("UnsafeCAbi/UnsafeUnionTaggedOperationFail.cvl", "CVLF2041")]
	[InlineData("UnsafeCAbi/UnsafeUnionFieldAccessUnsafeFail.cvl", "CVLF2043")]
	[InlineData("UnsafeCAbi/UnsafeUnionResourceFieldFail.cvl", "CVLF2040")]
	[InlineData("UnsafeCAbi/NativeResourceStructParamFail.cvl", "CVLF2053")]
	[InlineData("UnsafeCAbi/NativeResourceOverloadPostValidationFail.cvl", "CVLF2053")]
	[InlineData("UnsafeCAbi/NativeEnumImplicitStorageFail.cvl", "CVLF2054")]
	[InlineData("UnsafeCAbi/NativeDelegateGenericFail.cvl", "CVLF2062")]
	[InlineData("UnsafeCAbi/UnsafeUnionGenericFail.cvl", "CVLF2044")]
	[InlineData("UnsafeCAbi/NativeDelegateRefThisFail.cvl", "CVL1301")]
	[InlineData("UnsafeCAbi/NativeDelegateRefVarThisFail.cvl", "CVL1301")]
	public void NativeInterop_InvalidDeclarations_AreRejected(string caseFile, string diagnosticId)
	{
		var (exitCode, stdout, stderr) = RunCompilerCheck(caseFile);

		Assert.Equal(1, exitCode);
		Assert.Contains(diagnosticId, stderr);
	}


	[Theory]
	[InlineData("UnsafeCAbi/NativeFunctionAddress.cvl", "NativeFunctionAddress")]
	[InlineData("UnsafeCAbi/NativeFunctionAddressOverload.cvl", "NativeFunctionAddressOverload")]
	[InlineData("UnsafeCAbi/NativeFunctionAddressArgument.cvl", "NativeFunctionAddressArgument")]
	[InlineData("UnsafeCAbi/NativeDelegateCasts.cvl", "NativeDelegateCasts")]
	[InlineData("UnsafeCAbi/NativeDelegateCallCollision.cvl", "NativeDelegateCallCollision")]
	[InlineData("UnsafeCAbi/NativeDelegateStructField.cvl", "NativeDelegateStructField")]
	[InlineData("UnsafeCAbi/NativeDelegateQualifiedGlobal.cvl", "NativeDelegateQualifiedGlobal")]
	[InlineData("UnsafeCAbi/NativeDelegateNullArgument.cvl", "NativeDelegateNullArgument")]
	[InlineData("UnsafeCAbi/NativeExternAddress.cvl", "NativeExternAddress")]
	[InlineData("UnsafeCAbi/NativeDelegateBool.cvl", "NativeDelegateBool")]
	[InlineData("UnsafeCAbi/NativeDelegateUnsafeLocalMutation.cvl", "NativeDelegateUnsafeLocalMutation")]
	[InlineData("UnsafeCAbi/NativeAggregateLayoutSize.cvl", "NativeAggregateLayoutSize")]
	[InlineData("UnsafeCAbi/NativeAggregateRoundtrip.cvl", "NativeAggregateRoundtrip")]
	[InlineData("UnsafeCAbi/NativeDelegateNullUnsafeBody.cvl", "NativeDelegateNullUnsafeBody")]
	[InlineData("UnsafeCAbi/NativeDelegateNullUnsafeBlock.cvl", "NativeDelegateNullUnsafeBlock")]
	[InlineData("UnsafeCAbi/NativeDelegateSafeCopyStore.cvl", "NativeDelegateSafeCopyStore")]
	[InlineData("UnsafeCAbi/NativeDelegateUnboundNestedUnsafe.cvl", "NativeDelegateUnboundNestedUnsafe")]
	[InlineData("UnsafeCAbi/NativeEnumExplicitStorage.cvl", "NativeEnumExplicitStorage")]
	[InlineData("UnsafeCAbi/RawUnionAbiSafeMixed.cvl", "RawUnionAbiSafeMixed")]
	public void NativeFunctionPointers_CompileAndExecute(string caseFile, string binaryName)
	{
		var (exitCode, stdout, stderr) = RunCompiler(caseFile);
		AssertCompilationSucceeded(exitCode, stdout, stderr, caseFile);

		var (runCode, _) = ExecuteBinary(binaryName, "UnsafeCAbi");
		Assert.Equal(0, runCode);
	}

	[Theory]
	[InlineData("UnsafeCAbi/NativeFunctionAddressTargetlessFail.cvl", "CVLF2010")]
	[InlineData("UnsafeCAbi/NativeFunctionAddressUnsafeFail.cvl", "CVLF2016")]
	[InlineData("UnsafeCAbi/NativeFunctionAddressSignatureFail.cvl", "CVLF2012")]
	[InlineData("UnsafeCAbi/NativeFunctionAddressConventionFail.cvl", "CVLF2013")]
	[InlineData("UnsafeCAbi/NativeDelegateCallUnsafeFail.cvl", "CVLF2021")]
	[InlineData("UnsafeCAbi/NativeDelegateCastUnsafeFail.cvl", "CVLF2030")]
	[InlineData("UnsafeCAbi/NativeFunctionAddressGenericFail.cvl", "CVLF2014")]
	[InlineData("UnsafeCAbi/NativeDelegateNominalImplicitFail.cvl", "CVL1302")]
	[InlineData("UnsafeCAbi/SafeNativeDelegateCastFail.cvl", "CVL1333")]
	[InlineData("UnsafeCAbi/NativeSafeDelegateCastFail.cvl", "CVL1333")]
	[InlineData("UnsafeCAbi/SafeToNativeImplicitFail.cvl", "CVL1332")]
	[InlineData("UnsafeCAbi/NativeToSafeImplicitFail.cvl", "CVL1332")]
	[InlineData("UnsafeCAbi/NativeDelegateUnboundAddressFail.cvl", "CVLF2016")]
	[InlineData("UnsafeCAbi/NativeDelegateUnboundInvokeFail.cvl", "CVLF2021")]
	[InlineData("UnsafeCAbi/NativeDelegateUnboundCastFail.cvl", "CVLF2030")]
	[InlineData("UnsafeCAbi/NativeDelegateDefaultUnsafeFail.cvl", "CVLF2032")]
	[InlineData("UnsafeCAbi/NativeDelegateGlobalExplicitNullFail.cvl", "CVLF2032")]
	public void NativeFunctionPointers_InvalidOperations_AreRejected(string caseFile, string diagnosticId)
	{
		var (exitCode, stdout, stderr) = RunCompilerCheck(caseFile);

		Assert.Equal(1, exitCode);
		Assert.Contains(diagnosticId, stderr);
	}


	[Fact]
	public void NativeAbiFunction_IsRegisteredAsAddressableNativeFunction()
	{
		var (_, context) = AnalyzeProject("UnsafeCAbi/NativeFunctionAddress.cvl");
		Assert.DoesNotContain(context.Diagnostics.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
		var twice = Assert.Single(context.OverloadedFunctions.Values.SelectMany(x => x), f => f.Name.Contains("Twice", StringComparison.Ordinal));
		Assert.True(twice.IsNativeAbi);
		Assert.Equal("C", twice.CallingConvention);
	}

	[Fact]
	public void StandaloneForeignGlobalWithoutLibrary_IsRejectedDuringAnalysis()
	{
		var (_, context) = AnalyzeProject("UnsafeCAbi/ForeignGlobalRequiresLibraryFail.cvl");
		Assert.Contains(context.Diagnostics.Diagnostics, d => d.Id == DiagnosticIds.ForeignGlobalRequiresLibrary);
	}

	[Fact]
	public void StandaloneForeignGlobals_PreserveLibraryAndImportMetadata()
	{
		var (asts, context) = AnalyzeProject("UnsafeCAbi/ForeignGlobalStandalone.cvl");

		Assert.NotNull(asts);
		Assert.DoesNotContain(context.Diagnostics.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
		Assert.True(context.NativeLibraries.ContainsKey("native"));

		var version = Assert.Single(context.GlobalVariables.Select(x => x.Symbol), v => v.Name == "Version");
		Assert.True(version.IsForeign);
		Assert.False(version.IsMutable);
		Assert.Equal("native_version", version.ImportName);
		Assert.Equal("native", version.LibraryName);
		Assert.Equal("C", version.CallingConvention);
		Assert.Equal("native.lib", version.WinPath);
		Assert.Equal("./libnative.so", version.LinuxPath);
		Assert.Equal("./libnative.dylib", version.MacPath);

		var error = Assert.Single(context.GlobalVariables.Select(x => x.Symbol), v => v.Name == "Error");
		Assert.True(error.IsForeign);
		Assert.True(error.IsMutable);
		Assert.Equal("native_error", error.ImportName);
	}

	[Fact]
	public void ExternBlockForeignGlobals_AreRegisteredAsForeignStorage()
	{
		var (asts, context) = AnalyzeProject("UnsafeCAbi/ForeignGlobalBlock.cvl");

		Assert.NotNull(asts);
		Assert.DoesNotContain(context.Diagnostics.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);

		var version = Assert.Single(context.GlobalVariables.Select(x => x.Symbol), v => v.Name == "Version");
		Assert.True(version.IsForeign);
		Assert.False(version.IsMutable);
		Assert.Equal("native_version", version.ImportName);
		Assert.Equal("native", version.LibraryName);

		var error = Assert.Single(context.GlobalVariables.Select(x => x.Symbol), v => v.Name == "Error");
		Assert.True(error.IsForeign);
		Assert.True(error.IsMutable);
		Assert.Equal("native_error", error.ImportName);
	}

	[Theory]
	[InlineData("UnsafeCAbi/ForeignGlobalRequiresLibraryFail.cvl", "CVLF2001")]
	[InlineData("UnsafeCAbi/ForeignGlobalUnsafeTypeFail.cvl", "CVLF2004")]
	[InlineData("UnsafeCAbi/RawUnionUnsafeArrayFieldFail.cvl", "CVLF2040")]
	public void NativeStorage_InvalidDeclarations_AreRejected(string caseFile, string diagnosticId)
	{
		var (exitCode, stdout, stderr) = RunCompilerCheck(caseFile);
		Assert.Equal(1, exitCode);
		Assert.Contains(diagnosticId, stderr);
	}

	[Fact]
	public void NativeDelegateGlobal_DefaultsToNull()
	{
		const string caseFile = "UnsafeCAbi/NativeDelegateGlobalZeroInit.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(caseFile);
		AssertCompilationSucceeded(exitCode, stdout, stderr, caseFile);

		var (runCode, _) = ExecuteBinary("NativeDelegateGlobalZeroInit", "UnsafeCAbi");
		Assert.Equal(0, runCode);
	}

	[Fact]
	public void NativeAbiBoolFunction_BridgesInternalAndNativeRepresentation()
	{
		const string caseFile = "UnsafeCAbi/NativeAbiBoolDirect.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(caseFile);
		AssertCompilationSucceeded(exitCode, stdout, stderr, caseFile);

		var (runCode, _) = ExecuteBinary("NativeAbiBoolDirect", "UnsafeCAbi");
		Assert.Equal(0, runCode);
	}

	[Fact]
	public void NativeAbiBool_ScalarBoundaryUsesI1ValueType()
	{
		const string caseFile = "UnsafeCAbi/NativeAbiBoolDirect.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(caseFile, "--llvm", "--emit-ir", "-O0");
		AssertCompilationSucceeded(exitCode, stdout, stderr, caseFile);

		var normalized = stdout.Replace("\r\n", "\n");
		Assert.Matches(@"define[^\n]*\bi1\b[^\n]*@[^\n]*Flip[^\n]*\(i1\b", normalized);
	}

	[Fact]
	public void ForeignBoolGlobal_UsesByteObjectStorage()
	{
		const string caseFile = "UnsafeCAbi/ForeignBoolGlobal.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(caseFile, "--llvm", "--emit-ir", "-O0");
		AssertCompilationSucceeded(exitCode, stdout, stderr, caseFile);

		var normalized = stdout.Replace("\r\n", "\n");
		Assert.Contains("@native_flag = external global i8", normalized);
	}

	[Fact]
	public void NativeBoolAbiPolicy_UsesZeroExtOnSupportedDesktopTargets()
	{
		var policy = typeof(Cvolo.Emitter.LLVM.CodeGenerator).Assembly
			.GetType("Cvolo.Emitter.LLVM.Codegen.TypeLowering.NativeBoolAbiPolicy", throwOnError: true)!;
		var usesZeroExt = policy.GetMethod("UsesZeroExtension", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

		static bool Invoke(System.Reflection.MethodInfo method, string triple)
			=> (bool)method.Invoke(null, [triple])!;

		Assert.True(Invoke(usesZeroExt, "x86_64-pc-windows-msvc"));
		Assert.True(Invoke(usesZeroExt, "x86_64-unknown-linux-gnu"));
		Assert.True(Invoke(usesZeroExt, "arm64-apple-darwin"));
		Assert.False(Invoke(usesZeroExt, "wasm32-unknown-unknown"));
	}

	[Fact]
	public void NativeAbiBool_EmitsZeroExtOnSupportedHostTarget()
	{
		const string caseFile = "UnsafeCAbi/NativeAbiBoolDirect.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(caseFile, "--llvm", "--emit-ir", "-O0");
		AssertCompilationSucceeded(exitCode, stdout, stderr, caseFile);

		var normalized = stdout.Replace("\r\n", "\n");
		Assert.Contains("zeroext i1", normalized);
	}

	[Fact]
	public void NativeDelegateBool_IndirectCallCarriesZeroExtAttributes()
	{
		const string caseFile = "UnsafeCAbi/NativeDelegateBool.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(caseFile, "--llvm", "--emit-ir", "-O0");
		AssertCompilationSucceeded(exitCode, stdout, stderr, caseFile);

		var normalized = stdout.Replace("\r\n", "\n");
		Assert.Matches(@"call[^\n]*zeroext i1[^\n]*\(i1 zeroext", normalized);
	}

	[Fact]
	public void SystemCallingConvention_UsesStdcallOnlyOnWin32()
	{
		var resolver = typeof(Cvolo.Emitter.LLVM.CodeGenerator).Assembly
			.GetType("Cvolo.Emitter.LLVM.Codegen.TypeLowering.CallingConventionResolver", throwOnError: true)!;
		var resolve = resolver.GetMethod("Resolve", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

		static uint Invoke(System.Reflection.MethodInfo method, string convention, string triple)
			=> (uint)method.Invoke(null, [convention, triple])!;

		var cWin64 = Invoke(resolve, "C", "x86_64-pc-windows-msvc");
		var systemWin64 = Invoke(resolve, "system", "x86_64-pc-windows-msvc");
		var cWin32 = Invoke(resolve, "C", "i686-pc-windows-msvc");
		var systemWin32 = Invoke(resolve, "system", "i686-pc-windows-msvc");
		var cLinux = Invoke(resolve, "C", "x86_64-unknown-linux-gnu");
		var systemLinux = Invoke(resolve, "system", "x86_64-unknown-linux-gnu");

		Assert.Equal(cWin64, systemWin64);
		Assert.NotEqual(cWin32, systemWin32);
		Assert.Equal(cLinux, systemLinux);
	}

	[Fact]
	public void AggregateSemanticClassifier_IsFailClosedForUnverifiedPointerWidth()
	{
		var onePointer = new StructTypeSymbol("OnePointer", [new StructFieldSymbol("Value", TypeSymbol.NInt)]);
		Assert.True(NativeAbiAggregateClassification.IsVerifiedForDirectBoundary(onePointer, 8, true));
		Assert.False(NativeAbiAggregateClassification.IsVerifiedForDirectBoundary(onePointer, 4, true));
		Assert.False(NativeAbiAggregateClassification.IsVerifiedForDirectBoundary(onePointer, 8, false));
	}

	[Fact]
	public void ForeignGlobal_WithoutImportName_UsesBareNativeSymbol()
	{
		const string caseFile = "UnsafeCAbi/ForeignGlobalDefaultSymbolName.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(caseFile, "--llvm", "--emit-ir", "-O0");
		AssertCompilationSucceeded(exitCode, stdout, stderr, caseFile);
		var normalized = stdout.Replace("\r\n", "\n");
		Assert.Contains("@Version = external global i32", normalized);
		Assert.DoesNotContain("@NativeNs.Version = external global", normalized);
	}

	[Fact]
	public void ForeignReadonlyGlobal_AssignmentIsRejected()
	{
		var (exitCode, _, stderr) = RunCompilerCheck("UnsafeCAbi/ForeignReadonlyGlobalAssignFail.cvl");
		Assert.Equal(1, exitCode);
		Assert.Contains("immutable", stderr.ToLowerInvariant());
	}

	[Fact]
	public void NativeInterop_CompilesAndRunsWithoutStandardLibrary()
	{
		const string caseFile = "UnsafeCAbi/NativeInteropNoStdlib.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(caseFile, "--no-stdlib");
		AssertCompilationSucceeded(exitCode, stdout, stderr, caseFile);

		var (runCode, _) = ExecuteBinary("NativeInteropNoStdlib", "UnsafeCAbi");
		Assert.Equal(0, runCode);
	}

	[Fact]
	public void RawUnionFields_OverlapAtOffsetZero()
	{
		const string caseFile = "UnsafeCAbi/RawUnionOverlap.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(caseFile);
		AssertCompilationSucceeded(exitCode, stdout, stderr, caseFile);

		var (runCode, _) = ExecuteBinary("RawUnionOverlap", "UnsafeCAbi");
		Assert.Equal(0, runCode);
	}
}
