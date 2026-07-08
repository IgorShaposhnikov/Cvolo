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
		Add(pathArg);

		SetAction(parseResult =>
		{
			var path = parseResult.GetValue(pathArg)!;
			var exitCode = _compilerDriver.Compile(path, llvmOnly: false, isShared: false, emitIr: false, optLevel: "O0", checkOnly: true);
			Environment.Exit(exitCode);
		});
	}
}
