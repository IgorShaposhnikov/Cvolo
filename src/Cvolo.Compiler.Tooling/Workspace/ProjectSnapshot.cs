using Cvolo.Compiler.Tooling.Completion;
using Cvolo.Compiler.Tooling.Internal;
using Cvolo.Projects;

namespace Cvolo.Compiler.Tooling;

/// <summary>
/// An immutable snapshot of a project's documents at a point in time.
/// Snapshots support concurrent reads and branching via <see cref="WithDocument"/>;
/// analysis is lazy and computed once per snapshot.
/// </summary>
public sealed class ProjectSnapshot
{
	private readonly IReadOnlyDictionary<DocumentId, DocumentSnapshot> _documents;
	private readonly IReadOnlyList<ExternalSemanticUnit> _externalUnits;
	private readonly IReadOnlyList<PackageSourceDocument> _packageSources;
	private readonly Lazy<AnalyzedProject> _lazyAnalysis;
	private readonly Lazy<NavigationIndex> _lazyNavigation;

	/// <summary>
	/// The identifier of the project this snapshot belongs to.
	/// </summary>
	public ProjectId ProjectId { get; }
	/// <summary>
	/// The document identifiers, in snapshot order.
	/// </summary>
	public IReadOnlyList<DocumentId> DocumentIds { get; }
	/// <summary>
	/// The documents keyed by <see cref="DocumentId"/>.
	/// </summary>
	public IReadOnlyDictionary<DocumentId, DocumentSnapshot> Documents => _documents;

	private ProjectSnapshot(
		ProjectId projectId,
		IReadOnlyDictionary<DocumentId, DocumentSnapshot> documents,
		IReadOnlyList<ExternalSemanticUnit> externalUnits,
		IReadOnlyList<PackageSourceDocument> packageSources)
	{
		ProjectId = projectId;
		_documents = documents;
		_externalUnits = externalUnits;
		_packageSources = packageSources;
		DocumentIds = [.. documents.Keys];
		_lazyAnalysis = new Lazy<AnalyzedProject>(() => BinderAdapter.AnalyzeSnapshot(this));
		_lazyNavigation = new Lazy<NavigationIndex>(() => NavigationIndex.Build(this));
	}

	internal static ProjectSnapshot CreateOwned(
		ProjectId projectId,
		IReadOnlyDictionary<DocumentId, DocumentSnapshot> documents,
		IReadOnlyList<ExternalSemanticUnit>? externalUnits = null,
		IReadOnlyList<PackageSourceDocument>? packageSources = null)
	{
		var snapshot = new ProjectSnapshot(projectId, documents, externalUnits ?? [], packageSources ?? []);

		foreach (var document in snapshot.Documents.Values)
			document.OwningSnapshot = snapshot;

		return snapshot;
	}

	internal AnalyzedProject GetAnalysis()
	{
		return _lazyAnalysis.Value;
	}

	internal NavigationIndex GetNavigationIndex()
	{
		return _lazyNavigation.Value;
	}

	/// <summary>
	/// The non-file semantic units (package/artifact API units) that participate in this
	/// snapshot's binding. They have no <see cref="DocumentId"/> and never appear in
	/// <see cref="Documents"/>.
	/// </summary>
	internal IReadOnlyList<ExternalSemanticUnit> ExternalUnits => _externalUnits;

	internal IReadOnlyList<PackageSourceDocument> PackageSources => _packageSources;

	/// <summary>
	/// Returns the source declarations of <paramref name="symbol"/> within this snapshot. A symbol
	/// id obtained from a different snapshot yields an empty result.
	/// </summary>
	public IReadOnlyList<SymbolDefinition> GetDefinitions(SymbolId symbol)
	{
		return NavigationService.GetDefinitions(this, symbol);
	}

	public IReadOnlyList<PackageSourceDefinition> GetPackageSourceDefinitions(SymbolId symbol)
	{
		return NavigationService.GetPackageSourceDefinitions(this, symbol);
	}

	public bool TryGetPackageSource(string filePath, out PackageSourceDocument source)
	{
		ArgumentNullException.ThrowIfNull(filePath);

		var candidate = Path.GetFullPath(filePath);
		var comparison = OperatingSystem.IsWindows()
			? StringComparison.OrdinalIgnoreCase
			: StringComparison.Ordinal;

		foreach (PackageSourceDocument item in _packageSources)
		{
			if (string.Equals(item.FilePath, candidate, comparison))
			{
				source = item;
				return true;
			}
		}

		source = null!;
		return false;
	}

	public IReadOnlyList<PackageSourceDocument> GetPackageSources(string packageId, string version)
	{
		ArgumentNullException.ThrowIfNull(packageId);
		ArgumentNullException.ThrowIfNull(version);

		var result = new List<PackageSourceDocument>();

		foreach (PackageSourceDocument source in _packageSources)
		{
			if (string.Equals(source.PackageId, packageId, StringComparison.OrdinalIgnoreCase)
				&& string.Equals(source.Version, version, StringComparison.Ordinal))
			{
				result.Add(source);
			}
		}

		return result;
	}

	/// <summary>
	/// Resolves the lazily-resolvable fields of a callable completion candidate whose initial
	/// items were computed against this snapshot. A completion item id obtained from a different
	/// snapshot (or an unrecognized one) deterministically yields null rather than an error.
	/// </summary>
	public CompletionResolvedInfo? ResolveCompletion(CompletionItemId itemId)
	{
		return CompletionService.Resolve(this, itemId);
	}

	/// <summary>
	/// Returns project-source occurrences semantically bound to <paramref name="symbol"/> in this snapshot.
	/// </summary>
	public IReadOnlyList<SymbolReference> GetReferences(SymbolId symbol, bool includeDeclaration = false)
	{
		return ReferenceRenameService.GetReferences(this, symbol, includeDeclaration);
	}

	/// <summary>
	/// Computes a complete semantic rename plan without mutating this snapshot.
	/// </summary>
	public RenameResult RenameSymbol(SymbolId symbol, string newName)
	{
		ArgumentNullException.ThrowIfNull(newName);
		return ReferenceRenameService.Rename(this, symbol, newName);
	}

	/// <summary>
	/// Returns the document with the given <paramref name="documentId"/>.
	/// Throws <see cref="KeyNotFoundException"/> when the id is not in this snapshot.
	/// </summary>
	public DocumentSnapshot GetDocument(DocumentId documentId)
	{
		if (_documents.TryGetValue(documentId, out var snapshot))
			return snapshot;

		throw new KeyNotFoundException($"Document {documentId} not found in this snapshot.");
	}

	/// <summary>
	/// Attempts to retrieve the document with the given <paramref name="documentId"/>.
	/// Returns false when the id is not in this snapshot.
	/// </summary>
	public bool TryGetDocument(DocumentId documentId, out DocumentSnapshot document)
	{
		return _documents.TryGetValue(documentId, out document!);
	}

	/// <summary>
	/// Returns a new snapshot identical to this one except that the document identified by
	/// <paramref name="documentId"/> now has <paramref name="source"/> as its text.
	/// The caller's other documents keep their current text; this snapshot is never mutated.
	/// Throws <see cref="KeyNotFoundException"/> for an unknown id and
	/// <see cref="ArgumentNullException"/> for a null <paramref name="source"/>.
	/// </summary>
	public ProjectSnapshot WithDocument(DocumentId documentId, SourceText source)
	{
		ArgumentNullException.ThrowIfNull(source);

		if (!_documents.TryGetValue(documentId, out var existing))
			throw new KeyNotFoundException($"Document {documentId} not found in this project.");

		var newDocuments = new Dictionary<DocumentId, DocumentSnapshot>(_documents.Count);

		foreach (var (id, old) in _documents)
		{
			var text = id == documentId ? source : old.Text;
			newDocuments[id] = new DocumentSnapshot(id, old.FilePath, text, null);
		}

		return CreateOwned(ProjectId, newDocuments, _externalUnits, _packageSources);
	}
}
