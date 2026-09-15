using System.CommandLine;

namespace Cvolo.CLI.Packages;

public sealed class PackCommand : Command
{
	public PackCommand() : base("pack", "Package a Cvolo project into a .cvlib archive.")
	{
		// Handler is added in Phase 1.
	}
}