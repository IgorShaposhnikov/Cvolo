using System.Diagnostics;
using Cvolo.Projects;

namespace Cvolo.Strategies;

internal sealed class LinkStrategy(string binDirectory) : ICompilationStrategy
{
	public int Execute(string llPath, CompilationProject project, string? linkerPath, string? linkerName, bool verbose = false)
	{
		if (linkerPath is null)
		{
			Console.Error.WriteLine("Error: no compatible linker found (bundled clang, system clang, gcc, or g++). Install LLVM or GCC tools to compile.");
			return 1;
		}

		var binaryExt = project.IsShared
			? (OperatingSystem.IsWindows() ? ".dll" : ".so")
			: (OperatingSystem.IsWindows() ? ".exe" : "");
		var binaryPath = Path.Combine(binDirectory, project.OutputName + binaryExt);

		var typeFlag = project.IsShared ? " -shared" : "";

		// Subsystem flag is needed when using Clang (bundled or system) on Windows
		var isClang = linkerName == "bundled-clang" || linkerName == "clang";
		var subsystemFlag = isClang && OperatingSystem.IsWindows() && !project.IsShared
			? " -Xlinker /subsystem:console"
			: "";

		if (verbose)
		{
			Console.WriteLine($"Linking using: {linkerName}...");
		}

		// Configure ProcessStartInfo to redirect and suppress standard output and standard error if not in verbose mode
		var psi = new ProcessStartInfo
		{
			FileName = linkerPath,
			Arguments = $"-o \"{binaryPath}\" \"{llPath}\"{typeFlag}{subsystemFlag}",
			RedirectStandardOutput = !verbose,
			RedirectStandardError = !verbose,
			UseShellExecute = false,
			CreateNoWindow = !verbose
		};

		using var linkResult = Process.Start(psi);
		linkResult?.WaitForExit();

		if (linkResult?.ExitCode == 0)
		{
			if (verbose)
			{
				Console.WriteLine($"Built: {binaryPath}");
			}

			return 0;
		}

		// Fallback: If linking failed, print Clang's actual errors even in silent mode so the developer knows what went wrong!
		if (!verbose && linkResult is not null)
		{
			var errors = linkResult.StandardError.ReadToEnd();
			Console.Error.WriteLine("Linking failed:");
			Console.Error.WriteLine(errors);
		}

		return 1;
	}
}
