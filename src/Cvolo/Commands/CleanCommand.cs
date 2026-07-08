using System.CommandLine;

namespace Cvolo.Commands;

internal sealed class CleanCommand : Command
{
	public CleanCommand() : base("clean", "Safely deletes standard C# style compiling folders (/obj and /bin).")
	{
		var pathArg = new Argument<string>("path") { Description = "The path to your project directory or .cvlproj file." };

		Add(pathArg);

		SetAction(parseResult =>
		{
			var path = parseResult.GetValue(pathArg)!;
			var projectDir = Directory.Exists(path) ? path : Path.GetDirectoryName(Path.GetFullPath(path));

			if (projectDir == null || !Directory.Exists(projectDir))
			{
				Console.Error.WriteLine($"Error: project directory '{path}' not found.");
				Environment.Exit(1);
				return;
			}

			var objDir = Path.Combine(projectDir, "obj");
			var binDir = Path.Combine(projectDir, "bin");

			try
			{
				if (Directory.Exists(objDir))
				{
					Directory.Delete(objDir, recursive: true);
					Console.WriteLine($"Deleted: {objDir}");
				}

				if (Directory.Exists(binDir))
				{
					Directory.Delete(binDir, recursive: true);
					Console.WriteLine($"Deleted: {binDir}");
				}

				Console.WriteLine("Clean completed successfully.");
				Environment.Exit(0);
			}
			catch (Exception ex)
			{
				Console.Error.WriteLine($"Error during cleanup: {ex.Message}");
				Environment.Exit(1);
			}
		});
	}
}
