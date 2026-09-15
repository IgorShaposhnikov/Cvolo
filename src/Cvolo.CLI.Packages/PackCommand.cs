using System.CommandLine;
using Cvolo.Packaging;

namespace Cvolo.CLI.Packages;

public sealed class PackCommand : Command
{
	public PackCommand() : base("pack", "Package a Cvolo project into a .cvlib archive.")
	{
		var inputArgument = new Argument<string>("path")
		{
			Description = "Path to the .cvlproj file or project directory."
		};
		var outputOption = new Option<string>("--output", "-o")
		{
			Description = "Output .cvlib path (default: <project>/bin/<PackageId>.cvlib)."
		};
		var targetOption = new Option<string[]>("--target", "-t")
		{
			AllowMultipleArgumentsPerToken = true,
			Description = "Target triple or portable name (win-x64, linux-arm64) to pack. Repeatable."
		};
		var amalgamateOption = new Option<bool>("--amalgamate")
		{
			Description = "Pack every <TargetFrameworks> declared in the .cvlproj."
		};
		var stripBinariesOption = new Option<bool>("--strip-binaries")
		{
			Description = "Strip per-target linkage dependencies from the package. Default profile for cvolo pack."
		};
		var systemLinkOption = new Option<bool>("--system-link")
		{
			Description = "Keep system linkage dependencies in the package (reserved; current profile is strip-binaries)."
		};
		var stripSourceOption = new Option<bool>("--strip-source")
		{
			Description = "Omit the LZ4 source buffer (Sector 5) from the package."
		};
		var signOption = new Option<string>("--sign")
		{
			Description = "Sign the archive with an Ed25519 key: path to a raw 32-byte seed file."
		};
		var noSignOption = new Option<bool>("--no-sign")
		{
			Description = "Write an unsigned archive (96 zero signature bytes). Default for cvolo pack."
		};
		var verboseOption = new Option<bool>("--verbose", "-v")
		{
			Description = "Show verbose pack pipeline information."
		};

		Add(inputArgument);
		Add(outputOption);
		Add(targetOption);
		Add(amalgamateOption);
		Add(stripBinariesOption);
		Add(systemLinkOption);
		Add(stripSourceOption);
		Add(signOption);
		Add(noSignOption);
		Add(verboseOption);

		SetAction((ParseResult parseResult) =>
		{
			var path = parseResult.GetValue(inputArgument)!;
			var options = new PackOptions
			{
				Targets = parseResult.GetValue(targetOption) ?? [],
				Amalgamate = parseResult.GetValue(amalgamateOption),
				OutputPath = parseResult.GetValue(outputOption) ?? string.Empty,
				Profile = parseResult.GetValue(systemLinkOption) && !parseResult.GetValue(stripBinariesOption)
					? LinkageProfile.StripBinaries // MVP: StripBinaries only; SystemLink lands with the report pipeline.
					: LinkageProfile.StripBinaries,
				StripSource = parseResult.GetValue(stripSourceOption),
				SigningKeyPath = parseResult.GetValue(signOption),
				NoSign = parseResult.GetValue(noSignOption),
				Verbose = parseResult.GetValue(verboseOption)
			};

			try
			{
				var result = PackPipeline.Execute(path, options);
				PrintSummary(result);
			}
			catch (Exception ex)
			{
				Console.Error.WriteLine($"error: {ex.Message}");
				Environment.Exit(1);
			}
		});
	}

	private static void PrintSummary(PackResult result)
	{
		Console.WriteLine($"Packed {result.PackageId} {result.Version} \u2192 {result.OutputPath}");
		Console.WriteLine($"  Targets: {string.Join(", ", result.Targets)}");
		Console.WriteLine($"  Size:    {result.FileSize} bytes");
		Console.WriteLine($"  Root:    {result.MerkleRootHex}");
	}
}
