using System.CommandLine;
using Cvolo.Packaging;

namespace Cvolo.CLI.Packages;

public sealed class InstallCommand : Command
{
	public InstallCommand(PackageInstaller installer) : base("install", "Verify, thin for the host, and cache a local .cvlib archive.")
	{
		var path = new Argument<string>("path") { Description = "Local .cvlib archive." };
		Add(path);
		SetAction(parseResult =>
		{
			try
			{
				var result = installer.InstallFromFile(parseResult.GetValue(path)!);
				if (result.WasUnsigned)
					Console.Error.WriteLine($"warning {PackageDiagnosticIds.UnsignedWarning}: Package '{result.PackageId}@{result.Version}' is unsigned; signature verification skipped (Merkle integrity verified).");
				Console.WriteLine(result.AlreadyInstalled ? "Already installed." : $"Installed {result.PackageId} {result.Version} → {result.OutputPath}");
				Console.WriteLine($"  Source: {result.Source}");
				Console.WriteLine($"  Hash:   {result.ContentHash}");
				return 0;
			}
			catch (Exception ex)
			{
				Console.Error.WriteLine($"error: {ex.Message}");
				return 1;
			}
		});
	}
}
