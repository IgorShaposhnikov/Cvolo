using System.Text;

namespace Cvolo.Tests.Tooling;

internal sealed class TempProject : IDisposable
{
	private readonly string _root;
	public string ProjectFilePath { get; }


	private TempProject(string root, string projectFilePath)
	{
		_root = root;
		ProjectFilePath = projectFilePath;
	}

	public static TempProject Create(params (string FileName, string Source)[] sources)
	{
		var root = Path.Combine(Path.GetTempPath(), "cvolo-tooling-tests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);

		var projectFile = Path.Combine(root, "Fixture.cvlproj");
		File.WriteAllText(
			projectFile,
			"<Project Sdk=\"Cvolo.Sdk\">\n  <PropertyGroup>\n    <OutputType>Exe</OutputType>\n    <AssemblyName>Fixture</AssemblyName>\n  </PropertyGroup>\n</Project>\n",
			new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

		foreach (var (fileName, source) in sources)
			File.WriteAllText(Path.Combine(root, fileName), source, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

		return new TempProject(root, projectFile);
	}

	public string GetPath(string fileName) => Path.Combine(_root, fileName);

	public void Dispose()
	{
		try
		{
			Directory.Delete(_root, recursive: true);
		}
		catch
		{
			// Best-effort cleanup of ephemeral fixtures.
		}
	}
}
