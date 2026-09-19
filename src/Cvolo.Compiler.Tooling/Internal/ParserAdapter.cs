using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;
using Cvolo.Syntax.Antlr;

namespace Cvolo.Compiler.Tooling.Internal;

/// <summary>
/// Internal bridge over the syntax parser: parses every document into Core AST units and
/// converts parser diagnostics into tooling diagnostics, mapping each source file to its <see cref="DocumentId"/>.
/// </summary>
internal static class ParserAdapter
{
	/// <summary>
	/// Parses all <paramref name="documents"/> (one parser instance per document).
	/// Returns the non-null AST units, the per-document unit map (null where a document failed to
	/// parse), and the combined parse diagnostics across all documents.
	/// </summary>
	public static (IReadOnlyList<CompilationUnitSyntax> Units, IReadOnlyDictionary<DocumentId, CompilationUnitSyntax?> UnitsByDocument, IReadOnlyList<Diagnostic> Diagnostics) ParseAll(IReadOnlyDictionary<DocumentId, DocumentSnapshot> documents)
	{
		var units = new List<CompilationUnitSyntax>();
		var unitsByDocument = new Dictionary<DocumentId, CompilationUnitSyntax?>();
		var allDiagnostics = new List<Diagnostic>();
		var docByFilePath = new Dictionary<string, DocumentId>(StringComparer.OrdinalIgnoreCase);

		foreach (var (docId, doc) in documents)
		{
			docByFilePath[doc.FilePath] = docId;
		}

		foreach (var (docId, doc) in documents)
		{
			var context = new CompilationContext(doc.Text.ToString(), doc.FilePath);
			var parser = new AntlrSyntaxParser();
			var unit = parser.Parse(context);

			unitsByDocument[docId] = unit;

			if (unit is not null)
				units.Add(unit);

			allDiagnostics.AddRange(DiagnosticAdapter.ConvertDiagnostics(
				parser.Diagnostics, docByFilePath));
		}

		return (units, unitsByDocument, allDiagnostics);
	}
}
