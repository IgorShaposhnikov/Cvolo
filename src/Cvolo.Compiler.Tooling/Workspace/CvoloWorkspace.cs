using Cvolo.Compiler.Tooling.Internal;
using Cvolo.Observability.Abstractions;
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

	private CvoloWorkspace(ICompilerObserver observer)
	{
		Observer = observer;
	}

	/// <summary>
	/// Creates a new, empty workspace session with observability disabled (no-op events, §47).
	/// </summary>
	public static CvoloWorkspace Create()
	{
		return new(NoopCompilerObserver.Instance);
	}

	/// <summary>
	/// Creates a new, empty workspace session that reports compiler-observability events
	/// (lazy analysis, binder, parser) to <paramref name="observer"/>. The observer travels
	/// with the workspace into every snapshot it opens; no global/static wiring (§29.4, §39).
	/// </summary>
	public static CvoloWorkspace Create(ICompilerObserver observer)
	{
		ArgumentNullException.ThrowIfNull(observer);
		return new(observer);
	}

	/// <summary>
	/// The observer every snapshot opened by this workspace reports to.
	/// </summary>
	internal ICompilerObserver Observer { get; }

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

	/// <summary>
	/// Opens a self-contained session over the extracted source files of a single
	/// package version. Package sources bind together without the consuming
	/// project's external units so navigation inside a package's own sources
	/// resolves locally.
	/// </summary>
	public CvoloProject OpenPackageSource(string projectPath, IReadOnlyList<PackageSourceDocument> sources)
	{
		ArgumentNullException.ThrowIfNull(projectPath);
		ArgumentNullException.ThrowIfNull(sources);

		var absolutePath = Path.GetFullPath(projectPath);
		var projectId = AllocateProjectId();
		var documents = new Dictionary<DocumentId, DocumentSnapshot>(sources.Count);

		foreach (PackageSourceDocument source in sources)
		{
			var documentId = AllocateDocumentId();
			documents[documentId] = new DocumentSnapshot(documentId, source.FilePath, SourceText.From(source.Source), null);
		}

		var snapshot = ProjectSnapshot.CreateOwned(projectId, documents, observer: Observer);

		return new CvoloProject(this, projectId, absolutePath, snapshot);
	}

	/// <summary>
	/// Captures the complete semantic universe for <paramref name="request"/> through
	/// <paramref name="reader"/>, recording every byte consumed. The returned capture can later be
	/// built into a snapshot with <see cref="BuildSnapshotFromCaptureV1"/> without any further input
	/// reads. Discovery (membership, packages, standard library) happens here, never during a build.
	/// </summary>
	public CaptureResult CaptureUniverseV1(ToolingCaptureRequest request, ICompilerInputReader reader)
	{
		return ToolingCaptureService.Capture(this, request, reader);
	}

	/// <summary>
	/// Reconstructs a fresh, generation-owned snapshot from a frozen capture with no file-system
	/// access. Every document and external unit is parsed anew, so the resulting snapshot shares no
	/// live AST object with any other generation. Document ids are preserved from
	/// <paramref name="priorProject"/> when the paths are unchanged.
	/// </summary>
	public BuildResult BuildSnapshotFromCaptureV1(FrozenUniverseCapture capture, CvoloProject? priorProject = null)
	{
		return ToolingCaptureService.Build(this, capture, priorProject);
	}

	private CvoloProject OpenProjectCore(string projectPath, IReadOnlyList<string> libraryPaths, PackageCache? packageCache)
	{
		ArgumentNullException.ThrowIfNull(projectPath);
		ArgumentNullException.ThrowIfNull(libraryPaths);

		var absolutePath = Path.GetFullPath(projectPath);
		var projectId = AllocateProjectId();
		var (documents, externalUnits, packageSources, baseSourceFiles) = CompilerProjectAdapter.DiscoverDocuments(
			absolutePath,
			AllocateDocumentId,
			libraryPaths,
			packageCache);
		var snapshot = ProjectSnapshot.CreateOwned(projectId, documents, externalUnits, packageSources, baseSourceFiles, observer: Observer);

		return new CvoloProject(this, projectId, absolutePath, snapshot, libraryPaths, packageCache);
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