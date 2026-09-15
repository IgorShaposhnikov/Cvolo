using System.CommandLine;

namespace Cvolo.CLI.Packages;

public sealed class PkgCommand : Command
{
	public PkgCommand() : base("pkg", "Cvolo package manager.")
	{
		// Subcommands are added in later phases.
	}
}