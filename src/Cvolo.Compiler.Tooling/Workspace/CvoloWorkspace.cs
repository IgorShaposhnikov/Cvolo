using Cvolo.Compiler.Tooling.Internal;
using Cvolo.Packaging;

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
		return OpenProject(projectPath, []);
	}

	/// <summary>
	/// Opens a project/directory/source file and additionally mounts explicit <c>.cvlib</c> files
	/// (or directories containing them) into the semantic universe. Explicit libraries are intended
	/// for editor/loose-workspace scenarios; project PackageReference resolution remains authoritative
	/// when a <c>.cvlproj</c> is present.
	/// </summary>
	public CvoloProject OpenProject(string projectPath, IReadOnlyList<string> libraryPaths)
	{
		return OpenProjectCore(projectPath, libraryPaths, packageCache: null);
	}

	/// <summary>
	/// Test/internal hook that keeps package restore isolated in a caller-provided cache.
	/// The public workspace API continues to use the normal user package cache.
	/// </summary>
	internal CvoloProject OpenProject(string projectPath, IReadOnlyList<string> libraryPaths, PackageCache packageCache)
	{
		ArgumentNullException.ThrowIfNull(packageCache);
		return OpenProjectCore(projectPath, libraryPaths, packageCache);
	}

	private CvoloProject OpenProjectCore(string projectPath, IReadOnlyList<string> libraryPaths, PackageCache? packageCache)
	{
		ArgumentNullException.ThrowIfNull(projectPath);
		ArgumentNullException.ThrowIfNull(libraryPaths);

		var absolutePath = Path.GetFullPath(projectPath);
		var projectId = AllocateProjectId();
		var (documents, externalUnits) = CompilerProjectAdapter.DiscoverDocuments(
			absolutePath,
			AllocateDocumentId,
			libraryPaths,
			packageCache);
		var snapshot = ProjectSnapshot.CreateOwned(projectId, documents, externalUnits);

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