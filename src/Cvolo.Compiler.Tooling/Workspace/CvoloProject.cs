using Cvolo.Compiler.Tooling.Internal;
using Cvolo.Packaging;

namespace Cvolo.Compiler.Tooling;

/// <summary>
/// An opened project in a workspace: a stable <see cref="Id"/>, the canonical absolute
/// <see cref="ProjectPath"/>, and the immutable <see cref="InitialSnapshot"/> captured at open time.
/// </summary>
public sealed class CvoloProject
{
	private readonly CvoloWorkspace _workspace;
	private readonly IReadOnlyList<string> _libraryPaths;
	private readonly PackageCache? _packageCache;
	private readonly IReadOnlyDictionary<string, DocumentId> _documentIds;

	/// <summary>
	/// The workspace-session-local identifier of this project.
	/// </summary>
	public ProjectId Id { get; }
	/// <summary>
	/// The canonical absolute path of the project (file or directory).
	/// </summary>
	public string ProjectPath { get; }
	/// <summary>
	/// The immutable snapshot of the project as it was when opened.
	/// </summary>
	public ProjectSnapshot InitialSnapshot { get; }

	internal CvoloProject(
		CvoloWorkspace workspace,
		ProjectId id,
		string projectPath,
		ProjectSnapshot initialSnapshot,
		IReadOnlyList<string>? libraryPaths = null,
		PackageCache? packageCache = null,
		IReadOnlyDictionary<string, DocumentId>? documentIds = null)
	{
		_workspace = workspace;
		Id = id;
		ProjectPath = projectPath;
		InitialSnapshot = initialSnapshot;
		_libraryPaths = libraryPaths ?? [];
		_packageCache = packageCache;
		_documentIds = documentIds ?? CreateDocumentIdMap(initialSnapshot, null);
	}

	/// <summary>
	/// Recomputes this project's semantic universe from <paramref name="sourceOverrides"/>
	/// (canonical physical path to in-memory text) and returns a new project whose
	/// <see cref="InitialSnapshot"/> is the advanced snapshot. Documents whose paths remain
	/// present keep their <see cref="DocumentId"/> across the closure change.
	/// </summary>
	public CvoloProject Advance(IReadOnlyDictionary<string, string>? sourceOverrides)
	{
		var (documents, externalUnits, packageSources, baseSourceFiles) = CompilerProjectAdapter.DiscoverDocuments(
			ProjectPath,
			_workspace.AllocateDocumentId,
			_libraryPaths,
			_packageCache,
			sourceOverrides,
			_documentIds,
			allowRestore: false);

		var snapshot = ProjectSnapshot.CreateOwned(Id, documents, externalUnits, packageSources, baseSourceFiles);
		return new CvoloProject(
			_workspace,
			Id,
			ProjectPath,
			snapshot,
			_libraryPaths,
			_packageCache,
			CreateDocumentIdMap(snapshot, _documentIds));
	}

	private static IReadOnlyDictionary<string, DocumentId> CreateDocumentIdMap(
		ProjectSnapshot snapshot,
		IReadOnlyDictionary<string, DocumentId>? previous)
	{
		var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
		var map = previous is null
			? new Dictionary<string, DocumentId>(comparer)
			: new Dictionary<string, DocumentId>(previous, comparer);

		foreach (var document in snapshot.Documents.Values)
			map[document.FilePath] = document.Id;

		return map;
	}

	/// <summary>
	/// Attempts to resolve <paramref name="path"/> (absolute or relative to the project root) to a
	/// <see cref="DocumentId"/> in <see cref="InitialSnapshot"/>. Comparison is case-insensitive on
	/// Windows and case-sensitive elsewhere. Returns false when no document matches.
	/// </summary>
	public bool TryGetDocumentId(string path, out DocumentId documentId)
	{
		ArgumentNullException.ThrowIfNull(path);

		var projectRoot = Directory.Exists(ProjectPath)
			? ProjectPath
			: Path.GetDirectoryName(ProjectPath)
				?? throw new InvalidOperationException($"Cannot determine project root for '{ProjectPath}'.");

		var candidate = Path.IsPathRooted(path)
			? Path.GetFullPath(path)
			: Path.GetFullPath(Path.Combine(projectRoot, path));

		var comparison = OperatingSystem.IsWindows()
			? StringComparison.OrdinalIgnoreCase
			: StringComparison.Ordinal;

		foreach (var doc in InitialSnapshot.Documents.Values)
		{
			if (string.Equals(doc.FilePath, candidate, comparison))
			{
				documentId = doc.Id;
				return true;
			}
		}

		documentId = default;
		return false;
	}
}
