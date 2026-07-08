using System.CommandLine;
using Cvolo.Projects;

namespace Cvolo.Commands;

internal sealed class NewCommand : Command
{
	public NewCommand() : base("new", "Create a new template Cvolo project namespace.")
	{
		var nameArg = new Argument<string>("name") { Description = "The name of the new project directory" };
		Add(nameArg);

		SetAction(parseResult =>
		{
			var name = parseResult.GetValue(nameArg)!;
			try
			{
				CompilationProject.CreateNewProject(name);
				Environment.Exit(0);
			}
			catch (Exception ex)
			{
				Console.Error.WriteLine($"Error: {ex.Message}");
				Environment.Exit(1);
			}
		});
	}
}
