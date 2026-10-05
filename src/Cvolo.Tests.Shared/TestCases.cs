namespace Cvolo.Tests.Shared;

/// <summary>
/// Resolves immutable test fixtures copied to the active test assembly's <c>TestCases</c> directory.
/// Test code should address fixtures by logical relative path and never reconstruct Cvolo source in C#.
/// </summary>
public static class TestCases
{
	public const string DirectoryName = "TestCases";

	/// <summary>Physical fixture root for the currently executing test project.</summary>
	public static string Root => Path.Combine(AppContext.BaseDirectory, DirectoryName);

	/// <summary>Returns the physical path of one fixture.</summary>
	public static string PathOf(string relativePath)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
		var normalized = Normalize(relativePath);
		var path = Path.GetFullPath(Path.Combine(Root, normalized));
		var root = Path.GetFullPath(Root) + Path.DirectorySeparatorChar;
		if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
			throw new ArgumentException($"Fixture path escapes '{DirectoryName}': {relativePath}", nameof(relativePath));
		return path;
	}

	/// <summary>Reads one text fixture exactly as stored on disk.</summary>
	public static string ReadText(string relativePath) => File.ReadAllText(PathOf(relativePath));


	/// <summary>Reads a fixture containing one cursor marker and removes the marker from the returned text.</summary>
	public static MarkedTestCase ReadCursor(string relativePath, char marker = '|')
	{
		var text = ReadText(relativePath);
		var position = text.IndexOf(marker);
		if (position < 0)
			throw new InvalidDataException($"Fixture '{relativePath}' does not contain cursor marker '{marker}'.");
		if (text.IndexOf(marker, position + 1) >= 0)
			throw new InvalidDataException($"Fixture '{relativePath}' contains more than one cursor marker '{marker}'.");
		return new MarkedTestCase(text.Remove(position, 1), position);
	}

	/// <summary>Reads a fixture template and substitutes explicit <c>{{NAME}}</c> tokens.</summary>
	public static string ReadTemplate(string relativePath, params (string Name, string Value)[] values)
	{
		var text = ReadText(relativePath);
		foreach (var (name, value) in values)
			text = text.Replace("{{" + name + "}}", value, StringComparison.Ordinal);
		return text;
	}

	/// <summary>Copies one fixture to a writable test workspace.</summary>
	public static string CopyFile(string relativePath, string destinationPath)
	{
		var destinationDirectory = Path.GetDirectoryName(destinationPath);
		if (!string.IsNullOrEmpty(destinationDirectory))
			Directory.CreateDirectory(destinationDirectory);
		File.Copy(PathOf(relativePath), destinationPath, overwrite: true);
		return destinationPath;
	}

	/// <summary>Copies a complete fixture directory to a writable test workspace.</summary>
	public static void CopyDirectory(string relativeDirectory, string destinationDirectory)
	{
		var sourceDirectory = PathOf(relativeDirectory);
		if (!Directory.Exists(sourceDirectory))
			throw new DirectoryNotFoundException($"Test fixture directory was not found: {sourceDirectory}");

		Directory.CreateDirectory(destinationDirectory);
		foreach (var sourcePath in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
		{
			var relative = Path.GetRelativePath(sourceDirectory, sourcePath);
			var destinationPath = Path.Combine(destinationDirectory, relative);
			Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
			File.Copy(sourcePath, destinationPath, overwrite: true);
		}
	}

	private static string Normalize(string path) =>
		path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
			.TrimStart(Path.DirectorySeparatorChar);
}

public readonly record struct MarkedTestCase(string Text, int Position);
