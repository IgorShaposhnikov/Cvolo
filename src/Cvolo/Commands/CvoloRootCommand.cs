using System.CommandLine;

namespace Cvolo.Commands;

internal sealed class CvoloRootCommand : RootCommand
{
	public CvoloRootCommand(
		BuildCommand buildCommand,
		NewCommand newCommand,
		RunCommand runCommand,
		CheckCommand checkCommand,
		CleanCommand cleanCommand)
		: base("Cvolo Compiler - compiles C# syntax elegance to native optimized machine binaries.")
	{
		Add(buildCommand);
		Add(newCommand);
		Add(runCommand);
		Add(checkCommand);
		Add(cleanCommand);
	}
}
