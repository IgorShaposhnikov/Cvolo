using Cvolo.Core.Diagnostics;
using Cvolo.Projects;

namespace Cvolo.Strategies;

internal sealed class IrOnlyStrategy : ICompilationStrategy
{
	public int Execute(string llPath, CompilationProject project, string? linkerPath, string? linkerName, string optLevel = "Os", bool verbose = false, IEnumerable<NativeLinkInput>? nativeLibraries = null, string? targetOs = null, string? nativeTargetLabel = null, IEnumerable<string>? additionalInputs = null, DebugLinkOptions? debug = null)
	{
		// No linker runs in IR-only mode, so the module keeps its debug metadata in the .ll file and
		// nothing needs to be finalized here.
		if (verbose)
		{
			Console.WriteLine($"{AnsiConsoleStyle.Success("Generated LLVM IR:")} {AnsiConsoleStyle.Path(llPath)}");
		}

		return 0;
	}
}
