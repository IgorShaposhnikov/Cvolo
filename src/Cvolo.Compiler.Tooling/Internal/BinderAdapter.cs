using Cvolo.Analysis;
using Cvolo.Analysis.Passes.Validation;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using Cvolo.Core.AST.Expressions;
using Cvolo.Core.Diagnostics;
using Cvolo.Projects;

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
	// Parsed AST units are shared across snapshots (see ParserAdapter's parse cache), and binding
	// mutates a small, deterministic amount of node state. Serialize binding so two analyses never
	// touch the same cached unit concurrently.
	private static readonly object BindLock = new();

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

		// Parse all files (generation-owned text when the snapshot came from a frozen capture).
		var (units, unitsByDocument, parseDiagnostics) = snapshot.ParseAll();

		BindingContext? binderContext = null;
		DiagnosticBag? binderDiagnostics = null;

		// Bind once to disambiguate declarations in their real namespace/type
		// environment. Reparse only proven ambiguities and bind that final AST
		// once more; ordinary editing needs only the original single bind.
		for (var pass = 0; pass < 2; pass++)
		{
			binderContext = null;
			binderDiagnostics = null;
			if (units.Count == 0 && snapshot.ExternalUnits.Count == 0)
				break;

			var binder = new Binder();

			// Map AST units to compilation contexts for the binder.
			foreach (var unit in units)
			{
				if (unit.Context is not null)
					binder.Context.FileContexts[unit] = unit.Context;
			}

			// Package/artifact units carry the same declarations the compiler binds. Add them to
			// the bound unit set and classify them exactly as the compiler driver does.
			// A newly typed empty duplicate must never replace the already
			// established layout of a richer struct in another editor document.
			// This ordering affects Tooling's recovery binding only; every
			// duplicate still receives the compiler's CVL4054 diagnostic.
			var allUnits = units
				.OrderByDescending(unit => (unit.NamespaceDeclaration?.Members ?? unit.Members)
					.OfType<StructDeclarationSyntax>().Sum(declaration => declaration.Fields.Count))
				.ThenBy(unit => unit.Context?.FilePath, StringComparer.OrdinalIgnoreCase)
				.ToList();
			foreach (var external in snapshot.ExternalUnits)
			{
				allUnits.Add(external.Unit);
				binder.Context.FileContexts[external.Unit] = external.Unit.Context;
				if (external.Kind == ExternalSemanticUnitKind.ExternalPackageApi)
				{
					binder.Context.ExternalPackageUnits.Add(external.Unit);
					if (external.DefaultabilityCertificates is { Count: > 0 } certificates)
					{
						foreach (var (typeName, cert) in certificates)
							binder.Context.ImportedDefaultCertificates[typeName] = new ImportedDefaultCertificate(cert.State, cert.Reason, cert.Fingerprint);
					}
				}
				else
					binder.Context.PackageTemplateUnits.Add(external.Unit);

				if (external.PackageId is { } externalPackageId && external.Unit.Context is { } externalContext)
					binder.Context.UnitPackageIds[externalContext] = externalPackageId;
			}

			binder.Context.BaseSourcePaths.UnionWith(snapshot.BaseSourceFiles.Select(Path.GetFullPath));

			try
			{
				lock (BindLock)
				{
					binder.Bind(allUnits);
				}
				binderContext = binder.Context;
			}
			catch (NullReferenceException) when (parseDiagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
			{
				// A malformed editor buffer can yield an incomplete syntax graph.
				// Never cache the exception in ProjectSnapshot's Lazy analysis:
				// that would break unrelated semantic-token, inlay-hint, signature,
				// and CodeLens requests for the entire snapshot until the next edit.
				// Do not expose a half-bound context; keep the original parser
				// diagnostics and the independently available syntax-only services.
				binderContext = null;
			}

			if (binderContext is not null)
			{
				binderDiagnostics = binder.Diagnostics;
				if (pass == 0 && AmbiguousDeclarationRecovery.TryRecover(
					snapshot, binderContext, unitsByDocument, parseDiagnostics) is { } repaired)
				{
					(units, unitsByDocument, parseDiagnostics) = repaired;
					continue;
				}
			}
			break;
		}

		allDiagnostics.AddRange(parseDiagnostics);
		// Parser recovery diagnostics own damaged spans. During parse errors
		// publish only provenance-stable duplicate-type binder findings, not
		// synthetic follow-on errors from incomplete statements.
		if (binderContext is not null && binderDiagnostics is not null)
		{
			var semantic = DiagnosticAdapter.ConvertDiagnostics(binderDiagnostics, docByFilePath);
			if (parseDiagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
			{
				// The parser may have repaired a malformed declaration head such as
				// 'Begi\nx = Init();'. Its error must not hide the independent
				// immutability violation on the *real* assignment to x.
				//
				// Retain only diagnostics on a corresponding written assignment
				// target in the final recovered tree. Never publish a semantic
				// error solely because ANTLR synthesized a missing token.
				var writtenTargets = new HashSet<(DocumentId Document, int Start, int Length)>();
				foreach (var (documentId, unit) in unitsByDocument)
				{
					if (unit is null)
						continue;
					foreach (var node in Descendants(unit))
					{
						if (node is BinaryExpressionSyntax
							{
								Operator: "=",
								Left: IdentifierExpressionSyntax target
							} && target.Span.Start >= 0 && target.Span.Length > 0)
							writtenTargets.Add((documentId, target.Span.Start, target.Span.Length));
					}
				}

				allDiagnostics.AddRange(semantic.Where(d =>
					d.Id == DiagnosticIds.DuplicateTypeDefinition
					|| (d.Id == DiagnosticIds.CannotAssignToImmutableVariable
						&& writtenTargets.Contains((
							d.Location.DocumentId,
							d.Location.Span.Start,
							d.Location.Span.Length)))));
			}
			else
				allDiagnostics.AddRange(semantic);
		}

		return new AnalyzedProject
		{
			ResultDiagnostics = allDiagnostics,
			Units = units,
			UnitsByDocument = unitsByDocument,
			BinderContext = binderContext,
		};
	}
	/// <summary>Walk only original/recovered AST nodes, never lexer guesses.</summary>
	private static IEnumerable<SyntaxNode> Descendants(SyntaxNode node)
	{
		yield return node;
		foreach (var child in node.GetChildren())
		{
			foreach (var descendant in Descendants(child))
				yield return descendant;
		}
	}

}
