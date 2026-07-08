using Cvolo.Projects;

namespace Cvolo.Strategies;

internal sealed class IrOnlyStrategy : ICompilationStrategy
{
	public int Execute(string llPath, CompilationProject project, string? linkerPath, string? linkerName, bool verbose = false)
	{
		if (verbose)
		{
			Console.WriteLine($"Generated LLVM IR: {llPath}");
		}

		return 0; // Exits cleanly
	}
}
