namespace Cvolo.Compiler.Tooling;

/// <summary>
/// An opened project in a workspace: a stable <see cref="Id"/>, the canonical absolute
/// <see cref="ProjectPath"/>, and the immutable <see cref="InitialSnapshot"/> captured at open time.
/// </summary>
public sealed class CvoloProject
{
	private readonly CvoloWorkspace _workspace;

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

	internal CvoloProject(CvoloWorkspace workspace, ProjectId id, string projectPath, ProjectSnapshot initialSnapshot)
	{
		_workspace = workspace;
		Id = id;
		ProjectPath = projectPath;
		InitialSnapshot = initialSnapshot;
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
