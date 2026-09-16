using Cvolo.Compiler.Tooling;

namespace Cvolo.Tests.Tooling;

internal static class ProjectTestExtensions
{
	public static DocumentId GetDocumentId(this CvoloProject project, string fileName)
	{
		Assert.True(project.TryGetDocumentId(fileName, out var id), $"No document named '{fileName}'.");
		return id;
	}
}
