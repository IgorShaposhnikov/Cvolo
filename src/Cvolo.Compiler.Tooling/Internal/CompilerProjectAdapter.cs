namespace Cvolo.Compiler.Tooling.Internal;

/// <summary>
/// Internal bridge over local project loading: resolves a project input path to
/// the set of source documents while keeping compiler/packaging types out of the public boundary.
/// </summary>
internal static class CompilerProjectAdapter
{
	/// <summary>
	/// Discovers the source files for <paramref name="projectPath"/> (a .cvlproj file, a directory,
	/// or a single .cvl file), reads each into a <see cref="SourceText"/>, and allocates a
	/// <see cref="DocumentId"/> per document via <paramref name="allocateDocumentId"/>.
	/// </summary>
	public static IReadOnlyDictionary<DocumentId, DocumentSnapshot> DiscoverDocuments(string projectPath, Func<DocumentId> allocateDocumentId, bool includeStandardLibrary = false)
	{
		var sourceFiles = DiscoverSourceFiles(projectPath, includeStandardLibrary);
		var documents = new Dictionary<DocumentId, DocumentSnapshot>();

		foreach (var file in sourceFiles)
		{
			var fullPath = Path.GetFullPath(file);
			var text = File.ReadAllText(fullPath);
			var sourceText = SourceText.From(text);
			var docId = allocateDocumentId();
			documents[docId] = new DocumentSnapshot(docId, fullPath, sourceText, null);
		}

		return documents;
	}

	private static List<string> DiscoverSourceFiles(string inputPath, bool includeStandardLibrary)
	{
		if (File.Exists(inputPath) && inputPath.EndsWith(".cvlproj", StringComparison.OrdinalIgnoreCase))
		{
			return DiscoverFromProjectFile(inputPath, includeStandardLibrary);
		}

		if (Directory.Exists(inputPath))
		{
			var projectFiles = Directory.GetFiles(inputPath, "*.cvlproj", SearchOption.TopDirectoryOnly);
			if (projectFiles.Length > 1)
				throw new InvalidOperationException($"Multiple .cvlproj files found in '{inputPath}'. Pass the project file explicitly.");
			if (projectFiles.Length == 1)
				return DiscoverFromProjectFile(projectFiles[0], includeStandardLibrary);

			return Directory.GetFiles(inputPath, "*.cvl", SearchOption.AllDirectories).ToList();
		}

		if (File.Exists(inputPath) && Path.GetExtension(inputPath).Equals(".cvl", StringComparison.OrdinalIgnoreCase))
		{
			return [Path.GetFullPath(inputPath)];
		}

		throw new FileNotFoundException($"Input path '{inputPath}' not found");
	}

	private static List<string> DiscoverFromProjectFile(string projectFilePath, bool includeStandardLibrary)
	{
		return ProjectFileLoader.DiscoverSourceFiles(projectFilePath, includeStandardLibrary);
	}
}
