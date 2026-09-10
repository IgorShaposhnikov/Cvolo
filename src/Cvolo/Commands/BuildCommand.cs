using System.CommandLine;
using Cvolo.Drivers;

namespace Cvolo.Commands;

internal sealed class BuildCommand : Command
{
	private readonly ICompilerDriver _compilerDriver;
	public BuildCommand(ICompilerDriver compilerDriver) : base("build", "Compiles Cvolo project files into target binaries.")
	{
		_compilerDriver = compilerDriver;

		var pathArg = new Argument<string>("path") { Description = "The path to the Cvolo source file, directory, or .cvlproj file." };
		var llvmOption = new Option<bool>("--llvm") { Description = "Generate .ll LLVM IR only (no linking)" };
		var sharedOption = new Option<bool>("--shared") { Description = "Build a shared library (.dll/.so)" };
		var emitIrOption = new Option<bool>("--emit-ir") { Description = "Print generated IR directly to stdout" };
		var verboseOption = new Option<bool>("--verbose", "-v") { Description = "Show verbose compiler debug and linkage information." };
		var optOption = new Option<string>("--optimize", "-O", "Os") { Description = "Select optimization level (O0, O1, O2, O3, Os, Oz)" };
		var emitLoweredOption = new Option<bool>("--emit-lowered", "-l") { Description = "Print lowered Cvolo source code directly to stdout" };
		var nowarnOption = new Option<string>("--nowarn") { Description = "Comma-separated diagnostic ids whose warnings are suppressed (e.g. CVL1001)" };
		var legacyVisibilityOption = new Option<bool>("--legacy-visibility") { Description = "Disable the visibility system and treat all declarations as public (v0.2.0-alpha behavior)" };
		var strictOption = new Option<bool>("--strict-option") { Description = "Disable the '?' optional type syntax; require explicit Option<T> types" };
		var noTbaaOption = new Option<bool>("--no-tbaa") { Description = "Disable generation of !tbaa alias-analysis metadata nodes" };
		var targetOption = new Option<string>("--target") { Description = "Target OS for native library resolution (host, windows, linux, macos). Controls which win:/linux:/mac: [LibraryImport] path is forwarded to the linker." };
		var checkedFfiBoundsOption = new Option<bool>("--checked-ffi-bounds") { Description = "Generate explicit null-check prologues in expose extern functions for debug builds" };

		Add(pathArg);
		Add(llvmOption);
		Add(sharedOption);
		Add(emitIrOption);
		Add(optOption);
		Add(verboseOption);
		Add(emitLoweredOption);
		Add(nowarnOption);
		Add(legacyVisibilityOption);
		Add(strictOption);
		Add(noTbaaOption);
		Add(targetOption);
		Add(checkedFfiBoundsOption);

		SetAction((ParseResult parseResult) =>
		{
			var path = parseResult.GetValue(pathArg)!;
			var llvmOnly = parseResult.GetValue(llvmOption);
			var isShared = parseResult.GetValue(sharedOption);
			var emitIrVal = parseResult.GetValue(emitIrOption);
			var optLevel = parseResult.GetValue(optOption) ?? "Os";
			var emitLoweredVal = parseResult.GetValue(emitLoweredOption);
			var noWarnVal = parseResult.GetValue(nowarnOption);
			var legacyVisibilityVal = parseResult.GetValue(legacyVisibilityOption);
			var strictOptionVal = parseResult.GetValue(strictOption);
			var noTbaaVal = parseResult.GetValue(noTbaaOption);
			var targetOsVal = parseResult.GetValue(targetOption);
			var checkedFfiBoundsVal = parseResult.GetValue(checkedFfiBoundsOption);

			var exitCode = _compilerDriver.Compile(path, llvmOnly, isShared, emitIrVal, optLevel, emitLowered: emitLoweredVal, noWarn: noWarnVal, legacyVisibility: legacyVisibilityVal, strictOption: strictOptionVal, noTbaa: noTbaaVal, targetOs: targetOsVal, checkedFfiBounds: checkedFfiBoundsVal);
			Environment.Exit(exitCode);
		});
	}
}
