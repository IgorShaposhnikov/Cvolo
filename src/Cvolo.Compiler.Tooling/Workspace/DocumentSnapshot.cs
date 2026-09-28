using Cvolo.Compiler.Tooling.Completion;
using Cvolo.Compiler.Tooling.Internal;
using Cvolo.Compiler.Tooling.SignatureHelp;

namespace Cvolo.Compiler.Tooling;

/// <summary>
/// An immutable view of a single document: its stable <see cref="Id"/>, canonical
/// <see cref="FilePath"/>, and current <see cref="Text"/>. The snapshot is owned by the
/// <see cref="ProjectSnapshot"/> it was created from.
/// </summary>
public sealed class DocumentSnapshot
{
	/// <summary>
	/// The workspace-session-local identifier of this document.
	/// </summary>
	public DocumentId Id { get; }
	/// <summary>
	/// The canonical absolute path of the document on disk.
	/// </summary>
	public string FilePath { get; }
	/// <summary>
	/// The immutable source text of the document.
	/// </summary>
	public SourceText Text { get; }

	internal ProjectSnapshot? OwningSnapshot { get; set; }

	internal DocumentSnapshot(DocumentId id, string filePath, SourceText text, ProjectSnapshot? owningSnapshot)
	{
		Id = id;
		FilePath = filePath;
		Text = text;
		OwningSnapshot = owningSnapshot;
	}

	/// <summary>
	/// Returns this document's diagnostics within its owning snapshot, computing the
	/// project-wide analysis lazily on first access. Repetition on the same snapshot is stable.
	/// </summary>
	public IReadOnlyList<Diagnostic> GetDiagnostics()
	{
		if (OwningSnapshot is null)
		{
			return [];
		}

		var all = OwningSnapshot.GetAnalysis().ResultDiagnostics;
		var result = new List<Diagnostic>();

		foreach (var diagnostic in all)
		{
			if (diagnostic.Location.DocumentId == Id)
				result.Add(diagnostic);
		}

		return result;
	}

	/// <summary>
	/// Returns the compiler-provided code fixes for diagnostics in this document whose primary span
	/// intersects the given zero-based UTF-16 <paramref name="start"/>/<paramref name="length"/> range.
	/// Throws <see cref="ArgumentOutOfRangeException"/> when the range falls outside the document text.
	/// </summary>
	public IReadOnlyList<CodeFixInfo> GetCodeFixes(int start, int length)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(start);
		ArgumentOutOfRangeException.ThrowIfNegative(length);
		ArgumentOutOfRangeException.ThrowIfGreaterThan(start, Text.Length);
		ArgumentOutOfRangeException.ThrowIfGreaterThan(length, Text.Length - start);

		if (OwningSnapshot is null)
			return [];

		return OwningSnapshot.GetCodeFixes(Id, new TextSpan(start, length));
	}

	/// <summary>
	/// Resolves the complete edit set of a compiler-provided code fix obtained from this document's
	/// owning snapshot.
	/// </summary>
	public CodeFixResolution ResolveCodeFix(CodeFixId fix)
	{
		return OwningSnapshot is null
			? new CodeFixFailure("The document is not attached to a project snapshot.")
			: OwningSnapshot.ResolveCodeFix(fix);
	}

	/// <summary>
	/// Computes the completion items at the given zero-based UTF-16 <paramref name="position"/> within
	/// this document, using the owning snapshot's parsed and bound analysis. The result carries the
	/// replacement range (covering the identifier or keyword being typed, or a zero-length span) and
	/// an ordered, deduplicated candidate list.
	/// Throws <see cref="ArgumentOutOfRangeException"/> when <paramref name="position"/> falls outside
	/// [0, <see cref="Text.Length"/>].
	/// </summary>
	public CompletionResult GetCompletions(int position)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(position);

		ArgumentOutOfRangeException.ThrowIfGreaterThan(position, Text.Length);

		if (OwningSnapshot is null)
			return new CompletionResult(new TextSpan(position, 0), []);

		return CompletionService.Compute(OwningSnapshot, this, position);
	}

	/// <summary>
	/// Returns compiler-backed signature help for the call whose argument list contains the given
	/// UTF-16 <paramref name="position"/>. Ordinary function overload resolution and nominal delegate
	/// invocations use the binder's already-resolved target; when the callable is overloaded, every
	/// overload is returned with the resolved one selected. Returns null when no callable is bound.
	/// </summary>
	public SignatureHelpInfo? GetSignatureHelp(int position)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(position);

		ArgumentOutOfRangeException.ThrowIfGreaterThan(position, Text.Length);

		if (OwningSnapshot is null)
			return null;

		return SignatureHelpService.Compute(OwningSnapshot, this, position);
	}

	/// <summary>
	/// Resolves the semantic symbol at the given zero-based UTF-16 <paramref name="position"/>
	/// within this document using the owning snapshot's binding. Returns null when no trustworthy
	/// symbol is bound there.
	/// Throws <see cref="ArgumentOutOfRangeException"/> when <paramref name="position"/> falls
	/// outside [0, <see cref="Text.Length"/>].
	/// </summary>
	public SymbolLookupResult? GetSymbolAtPosition(int position)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(position);

		ArgumentOutOfRangeException.ThrowIfGreaterThan(position, Text.Length);

		if (OwningSnapshot is null)
			return null;

		return NavigationService.GetSymbol(OwningSnapshot, this, position);
	}

	/// <summary>
	/// Resolves and validates the renameable semantic occurrence at <paramref name="position"/>.
	/// </summary>
	public RenamePreparation? PrepareRename(int position)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(position);
		ArgumentOutOfRangeException.ThrowIfGreaterThan(position, Text.Length);
		return OwningSnapshot is null ? null : ReferenceRenameService.PrepareRename(OwningSnapshot, this, position);
	}

	/// <summary>
	/// Returns the semantic declaration outline for this document in deterministic source order.
	/// Local variables, parameters, and anonymous syntax are not part of the outline.
	/// </summary>
	public IReadOnlyList<DocumentSymbolInfo> GetDocumentSymbols()
	{
		if (OwningSnapshot is null)
			return [];

		return NavigationService.GetDocumentSymbols(OwningSnapshot, Id);
	}

	/// <summary>
	/// Returns the semantic tokens for this document from the owning snapshot's binding, in
	/// ascending source order. Occurrences that cannot be classified reliably are omitted.
	/// </summary>
	public IReadOnlyList<SemanticTokenInfo> GetSemanticTokens()
	{
		if (OwningSnapshot is null)
			return [];

		return SemanticTokenService.Compute(OwningSnapshot, this);
	}

	/// <summary>
	/// Returns the editor inlay hints for the requested zero-based UTF-16
	/// <paramref name="start"/>/<paramref name="length"/> range of this document, in ascending source
	/// order. Work is restricted to the requested range, and every hint is derived from a resolved
	/// compiler fact: a construct the compiler could not resolve contributes no hint rather than a
	/// guess. A zero-length range is treated as the caret.
	/// Throws <see cref="ArgumentOutOfRangeException"/> when the range falls outside the document text.
	/// </summary>
	public IReadOnlyList<ToolingInlayHint> GetInlayHints(int start, int length, ToolingInlayHintOptions? options = null)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(start);
		ArgumentOutOfRangeException.ThrowIfNegative(length);
		ArgumentOutOfRangeException.ThrowIfGreaterThan(start, Text.Length);
		ArgumentOutOfRangeException.ThrowIfGreaterThan(length, Text.Length - start);

		if (OwningSnapshot is null)
			return [];

		return InlayHintService.GetInlayHints(OwningSnapshot, this, new TextSpan(start, length), options ?? ToolingInlayHintOptions.Default);
	}

	/// <summary>
	/// Returns the semantic occurrences bound to the same declaration as the given zero-based UTF-16
	/// <paramref name="position"/>, each classified as a read, a write, both, or a declaration.
	/// Returns an empty list when the position binds to nothing, which is the common case: there is
	/// no textual same-spelling fallback.
	/// Throws <see cref="ArgumentOutOfRangeException"/> when <paramref name="position"/> falls
	/// outside [0, <see cref="Text.Length"/>].
	/// </summary>
	public IReadOnlyList<ToolingDocumentHighlight> GetDocumentHighlights(int position)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(position);
		ArgumentOutOfRangeException.ThrowIfGreaterThan(position, Text.Length);

		if (OwningSnapshot is null)
			return [];

		return DocumentHighlightService.GetDocumentHighlights(OwningSnapshot, this, position);
	}

	/// <summary>
	/// Returns the editor CodeLens entries for this document: a semantic reference count per named
	/// declaration (zero included), a compact layout summary per concrete type, and native-linkage
	/// facts for declarations with resolved interop metadata. The whole document is answered from one
	/// batched snapshot pass, so a document with N declarations never causes N project scans.
	/// </summary>
	public IReadOnlyList<ToolingCodeLensInfo> GetCodeLenses(ToolingCodeLensOptions? options = null)
	{
		if (OwningSnapshot is null)
			return [];

		return CodeLensService.GetCodeLenses(OwningSnapshot, this, options ?? ToolingCodeLensOptions.Default);
	}

	/// <summary>
	/// Returns the type layout the caret at <paramref name="position"/> resolves to, or null when the
	/// position is not on a concrete type with an authoritative layout.
	/// Throws <see cref="ArgumentOutOfRangeException"/> when <paramref name="position"/> falls
	/// outside [0, <see cref="Text.Length"/>].
	/// </summary>
	public TypeLayoutInspection? GetTypeLayoutAtPosition(int position)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(position);
		ArgumentOutOfRangeException.ThrowIfGreaterThan(position, Text.Length);

		if (OwningSnapshot is null)
			return null;

		return CodeLensService.GetTypeLayoutAtPosition(OwningSnapshot, this, position);
	}

	/// <summary>
	/// Returns the layout of the type named by a stored subject, re-resolved against this document's
	/// owning snapshot, or null when the name no longer resolves to a concrete type. This is how a
	/// viewer refreshes without ever holding a snapshot-scoped symbol.
	/// </summary>
	public TypeLayoutInspection? GetTypeLayoutBySubject(string subject)
	{
		ArgumentNullException.ThrowIfNull(subject);

		if (OwningSnapshot is null)
			return null;

		return CodeLensService.GetTypeLayoutBySubject(OwningSnapshot, this, subject);
	}

	/// <summary>
	/// Returns the foldable regions of this document, ordered by start position and then by
	/// decreasing length. Regions come from the parsed syntax and the comment tokens, so an
	/// incomplete document still folds the parts the parser recovered confidently, and a region
	/// never ends on the line it starts on.
	/// </summary>
	public IReadOnlyList<ToolingFoldingRange> GetFoldingRanges()
	{
		if (OwningSnapshot is null)
			return [];

		return FoldingRangeService.GetFoldingRanges(OwningSnapshot, this);
	}

	/// <summary>
	/// Returns one smart-selection chain per requested zero-based UTF-16 <paramref name="positions"/>,
	/// in request order. Each entry is the innermost range of that chain; its <see
	/// cref="ToolingSelectionRange.Parent"/> chain grows outwards through the enclosing expressions,
	/// block, declaration and unit. A position outside the document yields null at that index.
	/// Throws <see cref="ArgumentOutOfRangeException"/> when any position is negative.
	/// </summary>
	public IReadOnlyList<ToolingSelectionRange?> GetSelectionRanges(IReadOnlyList<int> positions)
	{
		ArgumentNullException.ThrowIfNull(positions);

		foreach (var position in positions)
			ArgumentOutOfRangeException.ThrowIfNegative(position);

		if (OwningSnapshot is null)
			return new ToolingSelectionRange?[positions.Count];

		return SelectionRangeService.GetSelectionRanges(OwningSnapshot, this, positions);
	}
}
