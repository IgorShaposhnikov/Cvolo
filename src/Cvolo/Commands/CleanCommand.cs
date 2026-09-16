using System.CommandLine;
using Cvolo.Packaging;

namespace Cvolo.Commands;

internal sealed class CleanCommand : Command
{
	public CleanCommand() : base("clean", "Deletes generated build outputs from /obj and /bin.")
	{
		var pathArg = new Argument<string>("path") { Description = "The path to your project directory or .cvlproj file." };
		var configurationOption = new Option<string?>("--configuration", "-c")
		{
			Description = "Clean only one build configuration (Debug or Release). Without this option all configurations are removed."
		};

		Add(pathArg);
		Add(configurationOption);

		SetAction(parseResult =>
		{
			var path = parseResult.GetValue(pathArg)!;
			var projectDir = Directory.Exists(path) ? Path.GetFullPath(path) : Path.GetDirectoryName(Path.GetFullPath(path));

			if (projectDir is null || !Directory.Exists(projectDir))
			{
				Console.Error.WriteLine($"Error: project directory '{path}' not found.");
				Environment.Exit(1);
				return;
			}

			try
			{
				var configuration = parseResult.GetValue(configurationOption);
				var deleted = BuildCleaner.Clean(projectDir, configuration);
				foreach (var directory in deleted)
				{
					Console.WriteLine($"Deleted: {directory}");
				}

				if (deleted.Count == 0)
				{
					var scope = configuration is null
						? "build outputs"
						: $"{BuildOutputLayout.NormalizeConfiguration(configuration)} build outputs";
					Console.WriteLine($"Nothing to clean: {scope} do not exist.");
				}

				Console.WriteLine("Clean completed successfully.");
				Environment.Exit(0);
			}
			catch (ArgumentException ex)
			{
				Console.Error.WriteLine($"Error: {ex.Message}");
				Environment.Exit(1);
			}
			catch (Exception ex)
			{
				Console.Error.WriteLine($"Error during cleanup: {ex.Message}");
				Environment.Exit(1);
			}
		});
	}
}
