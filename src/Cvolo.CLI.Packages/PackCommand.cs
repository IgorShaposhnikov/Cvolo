using System.CommandLine;
using Cvolo.Packaging;

namespace Cvolo.CLI.Packages;

public sealed class PackCommand : Command
{
	private readonly TextWriter _stdout;
	private readonly TextWriter _stderr;

	public PackCommand() : this(Console.Out, Console.Error)
	{
	}

	public PackCommand(TextWriter stdout, TextWriter stderr) : base("pack", "Package a Cvolo project into a .cvlib archive.")
	{
		_stdout = stdout ?? throw new ArgumentNullException(nameof(stdout));
		_stderr = stderr ?? throw new ArgumentNullException(nameof(stderr));
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
			Description = "Write an unsigned archive (96 zero signature bytes)."
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

		SetAction((ParseResult parseResult) => Run(() =>
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

			var result = PackPipeline.Execute(path, options);
			PrintSummary(result);
		}));
	}

	private int Run(Action action)
	{
		try
		{
			action();
			return 0;
		}
		catch (Exception ex)
		{
			_stderr.WriteLine($"error: {ex.Message}");
			return 1;
		}
	}

	private void PrintSummary(PackResult result)
	{
		_stdout.WriteLine($"Packed {result.PackageId} {result.Version} \u2192 {result.OutputPath}");
		_stdout.WriteLine($"  Targets: {string.Join(", ", result.Targets)}");
		_stdout.WriteLine($"  Size:    {result.FileSize} bytes");
		_stdout.WriteLine($"  Root:    {result.MerkleRootHex}");
	}
}
