using Cvolo.Projects;

namespace Cvolo.Strategies;

public interface ICompilationStrategy
{
	int Execute(string llPath, CompilationProject project, string? linkerPath, string? linkerName, string optLevel = "Os", bool verbose = false);
}
