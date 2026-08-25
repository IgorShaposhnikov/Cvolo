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
		var nowarnOption = new Option<string>("--nowarn") { Description = "Comma-separated diagnostic ids whose warnings are suppressed (e.g. CVL1001)" };

		Add(pathArg);
		Add(optOption);
		Add(emitLoweredOption);
		Add(nowarnOption);

		SetAction(parseResult =>
		{
			var path = parseResult.GetValue(pathArg)!;
			var optLevel = parseResult.GetValue(optOption) ?? "Os";
			var emitLoweredVal = parseResult.GetValue(emitLoweredOption);
			var noWarnVal = parseResult.GetValue(nowarnOption);

			var exitCode = _compilerDriver.Compile(path, llvmOnly: false, isShared: false, emitIr: false, optLevel, checkOnly: false, runAfterCompile: true, emitLowered: emitLoweredVal, noWarn: noWarnVal);
			Environment.Exit(exitCode);
		});
	}
}
