using System.CommandLine;
using Cvolo.CLI.Packages;

namespace Cvolo.Commands;

internal sealed class CvoloRootCommand : RootCommand
{
	public CvoloRootCommand(
		BuildCommand buildCommand,
		NewCommand newCommand,
		RunCommand runCommand,
		CheckCommand checkCommand,
		CleanCommand cleanCommand,
		RestoreCommand restoreCommand,
		PackCommand packCommand,
		PkgCommand pkgCommand)
		: base("Cvolo Compiler - compiles C# syntax elegance to native optimized machine binaries.")
	{
		Add(buildCommand);
		Add(newCommand);
		Add(runCommand);
		Add(checkCommand);
		Add(cleanCommand);
		Add(restoreCommand);
		Add(packCommand);
		Add(pkgCommand);
	}
}
