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

		var all = OwningSnapshot.GetAnalysis();
		var result = new List<Diagnostic>();

		foreach (var diagnostic in all)
		{
			if (diagnostic.Location.DocumentId == Id)
				result.Add(diagnostic);
		}

		return result;
	}
}
