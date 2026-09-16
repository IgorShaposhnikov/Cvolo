using Cvolo.Analysis;

namespace Cvolo.Compiler.Tooling.Internal;

/// <summary>
/// Internal bridge over the analysis binder: runs the compiler's parse + bind pipeline over a
/// <see cref="ProjectSnapshot"/> and yields tooling diagnostics for the whole project.
/// </summary>
internal static class BinderAdapter
{
	/// <summary>
	/// Analyzes <paramref name="snapshot"/> by parsing all documents and, when no parse errors
	/// remain, binding the project. Parse and bind diagnostics are merged into one list.
	/// </summary>
	public static IReadOnlyList<Diagnostic> AnalyzeSnapshot(ProjectSnapshot snapshot)
	{
		var allDiagnostics = new List<Diagnostic>();
		var docByFilePath = new Dictionary<string, DocumentId>(StringComparer.OrdinalIgnoreCase);

		foreach (var (docId, doc) in snapshot.Documents)
		{
			docByFilePath[doc.FilePath] = docId;
		}

		// Parse all files
		var (units, parseDiagnostics) = ParserAdapter.ParseAll(snapshot.Documents);
		allDiagnostics.AddRange(parseDiagnostics);

		// Only bind if all files parsed successfully
		if (units.Count > 0 && parseDiagnostics.All(d => d.Severity != DiagnosticSeverity.Error))
		{
			var binder = new Binder();

			// Map AST units to compilation contexts for the binder
			foreach (var unit in units)
			{
				if (unit.Context is not null)
					binder.Context.FileContexts[unit] = unit.Context;
			}

			binder.Bind(units);

			allDiagnostics.AddRange(DiagnosticAdapter.ConvertDiagnostics(
				binder.Diagnostics, docByFilePath));
		}

		return allDiagnostics;
	}
}
