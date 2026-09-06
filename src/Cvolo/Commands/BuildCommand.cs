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

			var exitCode = _compilerDriver.Compile(path, llvmOnly, isShared, emitIrVal, optLevel, emitLowered: emitLoweredVal, noWarn: noWarnVal, legacyVisibility: legacyVisibilityVal, strictOption: strictOptionVal);
			Environment.Exit(exitCode);
		});
	}
}
