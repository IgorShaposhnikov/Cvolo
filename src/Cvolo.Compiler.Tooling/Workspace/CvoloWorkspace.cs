using Cvolo.Compiler.Tooling.Internal;

namespace Cvolo.Compiler.Tooling;

/// <summary>
/// The root of an editing session. A workspace owns the allocation of
/// <see cref="ProjectId"/> and <see cref="DocumentId"/> instances, which are unique and
/// opaque within the workspace's session.
/// </summary>
public sealed class CvoloWorkspace
{
	private readonly Guid _sessionId = Guid.NewGuid();
	private int _nextProjectId;
	private int _nextDocumentId;

	private CvoloWorkspace()
	{
	}

	/// <summary>
	/// Creates a new, empty workspace session.
	/// </summary>
	public static CvoloWorkspace Create()
	{
		return new();
	}

	/// <summary>
	/// Opens the project at <paramref name="projectPath"/> (a .cvlproj file, a directory, or a
	/// single .cvl file) and returns its initial immutable snapshot.
	/// Throws <see cref="FileNotFoundException"/> when the path does not exist.
	/// </summary>
	public CvoloProject OpenProject(string projectPath)
	{
		ArgumentNullException.ThrowIfNull(projectPath);

		var absolutePath = Path.GetFullPath(projectPath);
		var projectId = AllocateProjectId();
		var documents = CompilerProjectAdapter.DiscoverDocuments(absolutePath, AllocateDocumentId);
		var snapshot = ProjectSnapshot.CreateOwned(projectId, documents);

		return new CvoloProject(this, projectId, absolutePath, snapshot);
	}

	internal ProjectId AllocateProjectId()
	{
		var id = new ProjectId(_sessionId, _nextProjectId++);
		return id;
	}

	internal DocumentId AllocateDocumentId()
	{
		var id = new DocumentId(_sessionId, _nextDocumentId++);
		return id;
	}
}
