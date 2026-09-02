using System.CommandLine;
using Cvolo.Drivers;

namespace Cvolo.Commands;

internal sealed class CheckCommand : Command
{
	private readonly ICompilerDriver _compilerDriver;

	public CheckCommand(ICompilerDriver compilerDriver) : base("check", "Runs syntactic parsing and binder-level semantic validations without compiling.")
	{
		_compilerDriver = compilerDriver;

		var pathArg = new Argument<string>("path") { Description = "The path to the Cvolo source file, directory, or .cvlproj file." };
		var legacyVisibilityOption = new Option<bool>("--legacy-visibility") { Description = "Disable the visibility system and treat all declarations as public (v0.2.0-alpha behavior)" };
		Add(pathArg);
		Add(legacyVisibilityOption);

		SetAction(parseResult =>
		{
			var path = parseResult.GetValue(pathArg)!;
			var legacyVisibilityVal = parseResult.GetValue(legacyVisibilityOption);
			var exitCode = _compilerDriver.Compile(path, llvmOnly: false, isShared: false, emitIr: false, optLevel: "O0", checkOnly: true, legacyVisibility: legacyVisibilityVal);
			Environment.Exit(exitCode);
		});
	}
}
