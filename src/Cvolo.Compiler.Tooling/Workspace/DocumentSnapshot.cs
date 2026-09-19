using Cvolo.Compiler.Tooling.Completion;
using Cvolo.Compiler.Tooling.Internal;

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

		if (position > Text.Length)
			throw new ArgumentOutOfRangeException(nameof(position));

		if (OwningSnapshot is null)
			return new CompletionResult(new TextSpan(position, 0), []);

		return CompletionService.Compute(OwningSnapshot, this, position);
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

		if (position > Text.Length)
			throw new ArgumentOutOfRangeException(nameof(position));

		if (OwningSnapshot is null)
			return null;

		return NavigationService.GetSymbol(OwningSnapshot, this, position);
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
}
