using Cvolo.Tests.Shared;

namespace Cvolo.Tests.Tooling;

/// <summary>
/// Tooling-test compatibility wrapper over the shared <see cref="TestProject"/> workspace.
/// New tests should prefer <see cref="FromFixtureDirectory"/> so Cvolo source stays in TestCases.
/// </summary>
internal sealed class TempProject : IDisposable
{
	private readonly TestProject _project;

	private TempProject(TestProject project) => _project = project;

	public string ProjectFilePath => _project.ProjectFilePath;

	/// <summary>
	/// Transitional API for tests that still synthesize source dynamically. Do not use it for fixed
	/// test programs; those belong in TestCases and should use <see cref="FromFixtureDirectory"/>.
	/// </summary>
	public static TempProject Create(params (string FileName, string Source)[] sources) =>
		new(TestProject.FromSources(sources, tempCategory: "cvolo-tooling-tests"));

	public static TempProject FromFixtureDirectory(string relativeDirectory) =>
		new(TestProject.FromFixtureDirectory(relativeDirectory, tempCategory: "cvolo-tooling-tests"));

	public string GetPath(string fileName) => _project.GetPath(fileName);

	public void Dispose() => _project.Dispose();
}
