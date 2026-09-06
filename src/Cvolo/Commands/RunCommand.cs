using System.CommandLine;
using Cvolo.Drivers;

namespace Cvolo.Commands;

internal sealed class RunCommand : Command
{
	private readonly ICompilerDriver _compilerDriver;

	public RunCommand(ICompilerDriver compilerDriver) : base("run", "Compiles your Cvolo project and immediately executes the output binary.")
	{
		_compilerDriver = compilerDriver;

		var pathArg = new Argument<string>("path") { Description = "The path to the Cvolo source file, directory, or .cvlproj file." };
		var optOption = new Option<string>("--optimize", "Os", "-O") { Description = "Select optimization level (O0, O1, O2, O3, Os, Oz)" };
		var emitLoweredOption = new Option<bool>("--emit-lowered", "-l") { Description = "Print lowered Cvolo source code directly to stdout" };
		var nowarnOption = new Option<string>("--nowarn") { Description = "Comma-separated diagnostic ids whose warnings are suppressed (e.g. CVL1003)" };
		var warnOption = new Option<bool>("--warn") { Description = "Show warnings during compilation (hidden by default in run)" };
		var verboseOption = new Option<bool>("--verbose", "-v") { Description = "Show verbose compiler debug information (implies --warn)" };
		var legacyVisibilityOption = new Option<bool>("--legacy-visibility") { Description = "Disable the visibility system and treat all declarations as public (v0.2.0-alpha behavior)" };
		var strictOption = new Option<bool>("--strict-option") { Description = "Disable the '?' optional type syntax; require explicit Option<T> types" };

		Add(pathArg);
		Add(optOption);
		Add(emitLoweredOption);
		Add(nowarnOption);
		Add(warnOption);
		Add(verboseOption);
		Add(legacyVisibilityOption);
		Add(strictOption);

		SetAction(parseResult =>
		{
			var path = parseResult.GetValue(pathArg)!;
			var optLevel = parseResult.GetValue(optOption) ?? "Os";
			var emitLoweredVal = parseResult.GetValue(emitLoweredOption);
			var noWarnVal = parseResult.GetValue(nowarnOption);
			var warnVal = parseResult.GetValue(warnOption);
			var verboseVal = parseResult.GetValue(verboseOption);
			var legacyVisibilityVal = parseResult.GetValue(legacyVisibilityOption);
			var strictOptionVal = parseResult.GetValue(strictOption);
			if (verboseVal) warnVal = true;

			var exitCode = _compilerDriver.Compile(path, llvmOnly: false, isShared: false, emitIr: false, optLevel, checkOnly: false, runAfterCompile: true, verbose: verboseVal, emitLowered: emitLoweredVal, noWarn: noWarnVal, suppressWarnings: !warnVal, legacyVisibility: legacyVisibilityVal, strictOption: strictOptionVal);
			Environment.Exit(exitCode);
		});
	}
}
