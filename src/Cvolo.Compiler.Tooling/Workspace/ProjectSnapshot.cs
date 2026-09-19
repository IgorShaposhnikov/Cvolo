using Cvolo.Compiler.Tooling.Internal;

namespace Cvolo.Compiler.Tooling;

/// <summary>
/// An immutable snapshot of a project's documents at a point in time.
/// Snapshots support concurrent reads and branching via <see cref="WithDocument"/>;
/// analysis is lazy and computed once per snapshot.
/// </summary>
public sealed class ProjectSnapshot
{
	private readonly IReadOnlyDictionary<DocumentId, DocumentSnapshot> _documents;
	private readonly Lazy<AnalyzedProject> _lazyAnalysis;

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

	private ProjectSnapshot(ProjectId projectId, IReadOnlyDictionary<DocumentId, DocumentSnapshot> documents)
	{
		ProjectId = projectId;
		_documents = documents;
		DocumentIds = [.. documents.Keys];
		_lazyAnalysis = new Lazy<AnalyzedProject>(() => BinderAdapter.AnalyzeSnapshot(this));
	}

	internal static ProjectSnapshot CreateOwned(ProjectId projectId, IReadOnlyDictionary<DocumentId, DocumentSnapshot> documents)
	{
		var snapshot = new ProjectSnapshot(projectId, documents);

		foreach (var document in snapshot.Documents.Values)
			document.OwningSnapshot = snapshot;

		return snapshot;
	}

	internal AnalyzedProject GetAnalysis()
	{
		return _lazyAnalysis.Value;
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

		return CreateOwned(ProjectId, newDocuments);
	}
}
