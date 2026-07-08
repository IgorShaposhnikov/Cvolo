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

		Add(pathArg);
		Add(llvmOption);
		Add(sharedOption);
		Add(emitIrOption);
		Add(optOption);
		Add(verboseOption);

		SetAction((ParseResult parseResult) =>
		{
			var path = parseResult.GetValue(pathArg)!;
			var llvmOnly = parseResult.GetValue(llvmOption);
			var isShared = parseResult.GetValue(sharedOption);
			var emitIrVal = parseResult.GetValue(emitIrOption);
			var optLevel = parseResult.GetValue(optOption) ?? "Os";

			var exitCode = _compilerDriver.Compile(path, llvmOnly, isShared, emitIrVal, optLevel);
			Environment.Exit(exitCode);
		});
	}
}
