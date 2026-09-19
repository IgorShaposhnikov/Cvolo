using Cvolo.Analysis;
using Cvolo.Core.AST.Base;

namespace Cvolo.Compiler.Tooling.Internal;

/// <summary>
/// The immutable result of analyzing a <see cref="ProjectSnapshot"/>: the tooling diagnostics, the
/// parsed AST units, the per-document unit map, and the retained binder <see cref="BindingContext"/>
/// used by completion queries.
/// </summary>
internal sealed class AnalyzedProject
{
	/// <summary>
	/// The combined parse and bind diagnostics, matching the compiler pipeline's gating exactly.
	/// </summary>
	public required IReadOnlyList<Diagnostic> ResultDiagnostics { get; init; }

	/// <summary>
	/// The AST units that parsed successfully, in snapshot document order.
	/// </summary>
	public required IReadOnlyList<CompilationUnitSyntax> Units { get; init; }

	/// <summary>
	/// The parsed unit per document, or null for documents whose text failed to parse.
	/// </summary>
	public required IReadOnlyDictionary<DocumentId, CompilationUnitSyntax?> UnitsByDocument { get; init; }

	/// <summary>
	/// The retained binder context (non-null whenever at least one document parsed), or null.
	/// </summary>
	public required BindingContext? BinderContext { get; init; }
}

/// <summary>
/// Internal bridge over the analysis binder: runs the compiler's parse + bind pipeline over a
/// <see cref="ProjectSnapshot"/> and yields tooling diagnostics for the whole project, retaining the
/// bound <see cref="BindingContext"/> for completion queries.
/// </summary>
internal static class BinderAdapter
{
	/// <summary>
	/// Analyzes <paramref name="snapshot"/> by parsing all documents and binding the parsed units
	/// (so the binder context is available to completion even when an unrelated document has parse
	/// errors). Bind diagnostics are only merged into the result when no parse errors remain,
	/// preserving the existing diagnostics contract.
	/// </summary>
	public static AnalyzedProject AnalyzeSnapshot(ProjectSnapshot snapshot)
	{
		var allDiagnostics = new List<Diagnostic>();
		var docByFilePath = new Dictionary<string, DocumentId>(StringComparer.OrdinalIgnoreCase);

		foreach (var (docId, doc) in snapshot.Documents)
		{
			docByFilePath[doc.FilePath] = docId;
		}

		// Parse all files.
		var (units, unitsByDocument, parseDiagnostics) = ParserAdapter.ParseAll(snapshot.Documents);
		allDiagnostics.AddRange(parseDiagnostics);

		BindingContext? binderContext = null;

		if (units.Count > 0)
		{
			var binder = new Binder();

			// Map AST units to compilation contexts for the binder.
			foreach (var unit in units)
			{
				if (unit.Context is not null)
					binder.Context.FileContexts[unit] = unit.Context;
			}

			binder.Bind(units);
			binderContext = binder.Context;

			// Bind diagnostics are surfaced only when every document parsed cleanly, matching the
			// pre-existing contract (bind diagnostics are still produced so completion can reuse them).
			if (parseDiagnostics.All(d => d.Severity != DiagnosticSeverity.Error))
			{
				allDiagnostics.AddRange(DiagnosticAdapter.ConvertDiagnostics(
					binder.Diagnostics, docByFilePath));
			}
		}

		return new AnalyzedProject
		{
			ResultDiagnostics = allDiagnostics,
			Units = units,
			UnitsByDocument = unitsByDocument,
			BinderContext = binderContext,
		};
	}
}