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
	// Parsing dominates a cold project analysis (all documents re-parsed). The tooling re-parses the
	// whole snapshot on every edit and for each completion recovery probe, but only a single document
	// actually changes between snapshots. Caching the parse by (path, text) lets unchanged documents
	// reuse their AST unit and compilation context; the binder reads the parsed unit and only mutates
	// it deterministically (ResolvedStructTypeName), so a cached unit is safe to re-bind.
	private static readonly object CacheLock = new();
	private static readonly Dictionary<(string FilePath, string Text), (CompilationUnitSyntax? Unit, DiagnosticBag Diagnostics)> ParseCache = new();
	private const int ParseCacheLimit = 256;

	private static (CompilationUnitSyntax? Unit, DiagnosticBag Diagnostics) ParseOne(string filePath, string text, bool useCache)
	{
		var key = (filePath, text);

		if (useCache)
		{
			lock (CacheLock)
			{
				if (ParseCache.TryGetValue(key, out var cached))
					return cached;
			}
		}

		var context = new CompilationContext(text, filePath);
		var parser = new AntlrSyntaxParser();
		var unit = parser.Parse(context);
		var entry = (unit, parser.Diagnostics);

		if (useCache)
		{
			lock (CacheLock)
			{
				if (ParseCache.Count >= ParseCacheLimit)
					ParseCache.Clear();

				ParseCache[key] = entry;
			}
		}

		return entry;
	}

	/// <summary>
	/// Parses all <paramref name="documents"/> (one parser instance per document).
	/// Returns the non-null AST units, the per-document unit map (null where a document failed to
	/// parse), and the combined parse diagnostics across all documents.
	/// </summary>
	public static (IReadOnlyList<CompilationUnitSyntax> Units, IReadOnlyDictionary<DocumentId, CompilationUnitSyntax?> UnitsByDocument, IReadOnlyList<Diagnostic> Diagnostics) ParseAll(IReadOnlyDictionary<DocumentId, DocumentSnapshot> documents)
		=> Parse(documents, useCache: true);

	/// <summary>
	/// Parses all <paramref name="documents"/> without consulting or populating the process-wide parse
	/// cache. Every call allocates fresh <see cref="CompilationUnitSyntax"/> instances so that no live
	/// syntax object is shared between two independently bindable generations.
	/// </summary>
	public static (IReadOnlyList<CompilationUnitSyntax> Units, IReadOnlyDictionary<DocumentId, CompilationUnitSyntax?> UnitsByDocument, IReadOnlyList<Diagnostic> Diagnostics) ParseFresh(IReadOnlyDictionary<DocumentId, DocumentSnapshot> documents)
		=> Parse(documents, useCache: false);

	private static (IReadOnlyList<CompilationUnitSyntax> Units, IReadOnlyDictionary<DocumentId, CompilationUnitSyntax?> UnitsByDocument, IReadOnlyList<Diagnostic> Diagnostics) Parse(IReadOnlyDictionary<DocumentId, DocumentSnapshot> documents, bool useCache)
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
			var (unit, diagnostics) = ParseOne(doc.FilePath, doc.Text.ToString(), useCache);

			unitsByDocument[docId] = unit;

			if (unit is not null)
				units.Add(unit);

			allDiagnostics.AddRange(DiagnosticAdapter.ConvertDiagnostics(
				diagnostics, docByFilePath));
		}

		return (units, unitsByDocument, allDiagnostics);
	}
}
