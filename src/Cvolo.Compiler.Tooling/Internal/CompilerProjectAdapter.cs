using Cvolo.Packaging;
using Cvolo.Projects;

namespace Cvolo.Compiler.Tooling.Internal;

/// <summary>
/// Internal bridge over the shared compiler project/universe loader: resolves a project input path
/// to the same semantic universe the compiler sees (project sources + standard library +
/// ProjectReference sources + package/.cvlib API units) while keeping compiler/packaging types out
/// of the public boundary. File-backed sources become <see cref="DocumentSnapshot"/>s; package
/// units are carried separately as <see cref="ExternalSemanticUnit"/>s.
/// </summary>
internal static class CompilerProjectAdapter
{
	/// <summary>
	/// Builds the snapshot documents and the external (non-file) semantic units for
	/// <paramref name="projectPath"/> using the compiler's own universe rules.
	/// </summary>
	public static (
		IReadOnlyDictionary<DocumentId, DocumentSnapshot> Documents,
		IReadOnlyList<ExternalSemanticUnit> ExternalUnits) DiscoverDocuments(
		string projectPath,
		Func<DocumentId> allocateDocumentId,
		IReadOnlyList<string>? libraryPaths = null,
		PackageCache? packageCache = null)
	{
		var semanticRoot = Directory.Exists(projectPath)
			? Path.GetFullPath(projectPath)
			: Path.GetDirectoryName(Path.GetFullPath(projectPath))!;

		var manifestBacked = IsManifestBackedInput(projectPath);
		var resolvedLibraryPaths = manifestBacked
			? null
			: libraryPaths?
				.Where(path => !string.IsNullOrWhiteSpace(path))
				.Select(path => Path.IsPathRooted(path)
					? Path.GetFullPath(path)
					: Path.GetFullPath(path, semanticRoot))
				.ToArray();

		var universe = ProjectUniverseLoader.Load(new ProjectUniverseRequest(
			projectPath,
			CompilerBaseDir: AppContext.BaseDirectory,
			LoadPackages: true,
			PackageCache: packageCache,
			LibraryPaths: resolvedLibraryPaths,
			RestorePackages: manifestBacked,
			IgnoreAncestorProject: !manifestBacked));

		var documents = new Dictionary<DocumentId, DocumentSnapshot>();

		foreach (var file in universe.SourceFiles)
		{
			var fullPath = Path.GetFullPath(file);
			var text = File.ReadAllText(fullPath);
			var sourceText = SourceText.From(text);
			var docId = allocateDocumentId();
			documents[docId] = new DocumentSnapshot(docId, fullPath, sourceText, null);
		}

		return (documents, universe.ExternalUnits);
	}
	private static bool IsManifestBackedInput(string projectPath)
	{
		if (File.Exists(projectPath))
			return string.Equals(Path.GetExtension(projectPath), ".cvlproj", StringComparison.OrdinalIgnoreCase);

		if (!Directory.Exists(projectPath))
			return false;

		return Directory.GetFiles(projectPath, "*.cvlproj", SearchOption.TopDirectoryOnly).Length == 1;
	}

}