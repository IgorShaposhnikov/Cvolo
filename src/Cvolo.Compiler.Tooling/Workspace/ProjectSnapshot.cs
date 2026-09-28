using Cvolo.Compiler.Tooling.Completion;
using Cvolo.Compiler.Tooling.Internal;
using Cvolo.Core.AST.Base;
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
	private readonly Lazy<CodeFixIndex> _lazyCodeFixes;
	private readonly Lazy<OccurrenceIndex> _lazyOccurrences;
	private readonly Lazy<IReadOnlyDictionary<DocumentId, CompilationUnitSyntax?>> _lazyParsedUnits;

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
		IReadOnlyList<PackageSourceDocument> packageSources,
		IReadOnlyList<string> baseSourceFiles)
	{
		ProjectId = projectId;
		_documents = documents;
		_externalUnits = externalUnits;
		_packageSources = packageSources;
		BaseSourceFiles = baseSourceFiles;
		DocumentIds = [.. documents.Keys];
		_lazyAnalysis = new Lazy<AnalyzedProject>(() => BinderAdapter.AnalyzeSnapshot(this));
		_lazyNavigation = new Lazy<NavigationIndex>(() => NavigationIndex.Build(this));
		_lazyCodeFixes = new Lazy<CodeFixIndex>(() => CodeFixIndex.Build(this));
		_lazyOccurrences = new Lazy<OccurrenceIndex>(() => OccurrenceIndex.Build(this));
		_lazyParsedUnits = new Lazy<IReadOnlyDictionary<DocumentId, CompilationUnitSyntax?>>(
			() => ParserAdapter.ParseAll(_documents).UnitsByDocument);
	}

	internal static ProjectSnapshot CreateOwned(
		ProjectId projectId,
		IReadOnlyDictionary<DocumentId, DocumentSnapshot> documents,
		IReadOnlyList<ExternalSemanticUnit>? externalUnits = null,
		IReadOnlyList<PackageSourceDocument>? packageSources = null,
		IReadOnlyList<string>? baseSourceFiles = null)
	{
		var snapshot = new ProjectSnapshot(projectId, documents, externalUnits ?? [], packageSources ?? [], baseSourceFiles ?? []);

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

	internal CodeFixIndex GetCodeFixIndex()
	{
		return _lazyCodeFixes.Value;
	}

	/// <summary>
	/// The one project-wide semantic occurrence index for this snapshot. It is built on first use by
	/// a feature that needs many symbols at once, so a document with a hundred declarations does not
	/// trigger a hundred project scans.
	/// </summary>
	internal OccurrenceIndex GetOccurrenceIndex()
	{
		return _lazyOccurrences.Value;
	}

	/// <summary>
	/// The parsed syntax of every document, keyed by <see cref="DocumentId"/>, where a document that
	/// failed to parse maps to null. Purely syntactic features such as folding and smart selection
	/// use this so they never force a full semantic analysis of the project.
	/// </summary>
	internal bool TryGetParsedUnit(DocumentId document, out CompilationUnitSyntax? unit)
	{
		return _lazyParsedUnits.Value.TryGetValue(document, out unit);
	}

	/// <summary>
	/// The non-file semantic units (package/artifact API units) that participate in this
	/// snapshot's binding. They have no <see cref="DocumentId"/> and never appear in
	/// <see cref="Documents"/>.
	/// </summary>
	internal IReadOnlyList<ExternalSemanticUnit> ExternalUnits => _externalUnits;

	internal IReadOnlyList<PackageSourceDocument> PackageSources => _packageSources;

	internal IReadOnlyList<string> BaseSourceFiles { get; }

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

	/// <summary>
	/// Returns the concrete places that satisfy the contract named by <paramref name="symbol"/>:
	/// the extensions that implement an interface (or one of its members), and the concrete types
	/// that conform to a protocol (or provide one of its members) by the compiler's own conformance
	/// rules. A symbol that is not a contract yields an empty result.
	/// </summary>
	public IReadOnlyList<ToolingImplementation> GetImplementations(SymbolId symbol)
	{
		return ImplementationNavigation.GetImplementations(this, symbol);
	}

	public IReadOnlyList<CodeFixInfo> GetCodeFixes(DocumentId document, TextSpan range)
	{
		if (!_documents.ContainsKey(document))
			throw new KeyNotFoundException($"Document {document} not found in this snapshot.");

		return CodeFixService.GetCodeFixes(this, document, range);
	}

	public CodeFixResolution ResolveCodeFix(CodeFixId fix)
	{
		return CodeFixService.ResolveCodeFix(this, fix);
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
	/// Returns the editor CodeLens entries for one document: a semantic reference count per named
	/// declaration (zero included), a compact layout summary per concrete type, and native-linkage
	/// facts for declarations with resolved interop metadata. The whole document is answered from one
	/// batched snapshot pass.
	/// </summary>
	public IReadOnlyList<ToolingCodeLensInfo> GetCodeLenses(DocumentId document, ToolingCodeLensOptions? options = null)
	{
		if (!_documents.ContainsKey(document))
			throw new KeyNotFoundException($"Document {document} not found in this snapshot.");

		return CodeLensService.GetCodeLenses(this, document, options ?? ToolingCodeLensOptions.Default);
	}

	/// <summary>
	/// Returns the type layout the caret at <paramref name="position"/> resolves to, or null when the
	/// position is not on a concrete type with an authoritative layout.
	/// </summary>
	public TypeLayoutInspection? GetTypeLayoutAtPosition(DocumentId document, int position)
	{
		if (!_documents.ContainsKey(document))
			return null;

		return CodeLensService.GetTypeLayoutAtPosition(this, _documents[document], position);
	}

	/// <summary>
	/// Returns the layout of the type named by a stored subject, re-resolved against this snapshot, or
	/// null when the name no longer resolves to a concrete type. A viewer keeps only the subject it was
	/// given and asks again through here after the project changes, so the facts it shows are never
	/// carried across a snapshot.
	/// </summary>
	public TypeLayoutInspection? GetTypeLayoutBySubject(DocumentId document, string subject)
	{
		if (!_documents.ContainsKey(document))
			return null;

		return CodeLensService.GetTypeLayoutBySubject(this, _documents[document], subject);
	}

	/// <summary>
	/// Returns the declarations that define the semantic type the caret at <paramref name="position"/>
	/// resolves to, or an empty list when the position binds to no type that has a source declaration.
	/// This is Go to Type Definition: a local <c>layout</c> still has its own declaration as its ordinary
	/// definition, but its type definition is <c>struct Layout</c>.
	/// </summary>
	public IReadOnlyList<SymbolDefinition> GetTypeDefinitions(DocumentId document, int position)
	{
		if (!_documents.ContainsKey(document))
			return [];

		return TypeNavigation.GetTypeDefinitions(this, _documents[document], position);
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

		return CreateOwned(ProjectId, newDocuments, _externalUnits, _packageSources, BaseSourceFiles);
	}
}
