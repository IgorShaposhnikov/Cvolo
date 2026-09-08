using Cvolo.Analysis.Symbols.FFI;
using Cvolo.Projects;

namespace Cvolo.Strategies;

internal sealed class IrOnlyStrategy : ICompilationStrategy
{
	public int Execute(string llPath, CompilationProject project, string? linkerPath, string? linkerName, string optLevel = "Os", bool verbose = false, IEnumerable<NativeLibraryInfo>? nativeLibraries = null, string? targetOs = null)
	{
		if (verbose)
		{
			Console.WriteLine($"Generated LLVM IR: {llPath}");
		}

		return 0;
	}
}
