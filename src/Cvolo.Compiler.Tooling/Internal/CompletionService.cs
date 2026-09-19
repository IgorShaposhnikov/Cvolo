using AnalysisKind = Cvolo.Analysis.Completion.CompletionKind;
using AnalysisContext = Cvolo.Analysis.Completion.CompletionQueryContext;

using Cvolo.Compiler.Tooling.Completion;
using Cvolo.Core.AST.Base;
using Cvolo.Analysis.Completion;

namespace Cvolo.Compiler.Tooling.Internal;

/// <summary>
/// Orchestrates a completion request: token-level context (span, prefix, comment/literal, type-only
/// position), the shared per-project analysis (parsed units + bound context), the analysis-side
/// semantic query, keyword candidates, and final prefix filtering, type-context narrowing, dedupe and
/// deterministic ordering.
/// </summary>
internal static class CompletionService
{
	/// <summary>
	/// Computes the completion result for <paramref name="doc"/> at <paramref name="position"/> using
	/// <paramref name="project"/>'s already-established analysis. Never throws for recoverable editor
	/// input: comments, literals and unrecoverable parse contexts yield an empty candidate list with a
	/// valid replacement span.
	/// </summary>
	public static CompletionResult Compute(ProjectSnapshot project, DocumentSnapshot doc, int position)
	{
		var textContext = CompletionTextContext.Analyze(doc.Text, position);
		var span = textContext.ReplacementSpan;

		if (textContext.IsCommentOrLiteral)
			return new CompletionResult(span, []);

		var analysis = project.GetAnalysis();

		var unit = analysis.UnitsByDocument.TryGetValue(doc.Id, out var parsed)
			? parsed
			: null;

		if (unit is null)
		{
			var recovered = TryRecoverUnit(project, doc, position);
			if (recovered is null)
				return new CompletionResult(span, []);

			analysis = recovered.Value.Project;
			unit = recovered.Value.Unit;
		}

		if (analysis.BinderContext is null)
			return new CompletionResult(span, []);

		AnalysisContext queryContext;
		IReadOnlyList<Analysis.Completion.CompletionCandidate> queryCandidates;

		// CompletionQuery temporarily mutates CurrentUnit/CurrentNamespace on the shared binder
		// context to resolve names from the requesting file's perspective; the lock keeps that
		// mutation serialized across concurrent completion requests.
		lock (analysis.BinderContext)
		{
			var result = CompletionQuery.Compute(analysis.BinderContext, unit, position);
			queryContext = result.Context;
			queryCandidates = result.Candidates;
		}

		var candidates = new List<Completion.CompletionCandidate>();
		var seen = new HashSet<(string Label, Completion.CompletionKind Kind)>();

		foreach (var candidate in queryCandidates)
		{
			if (!candidate.Label.StartsWith(textContext.Prefix, StringComparison.Ordinal))
				continue;

			var kind = MapKind(candidate.Kind);
			if (!seen.Add((candidate.Label, kind)))
				continue;

			candidates.Add(new Completion.CompletionCandidate(candidate.Label, candidate.InsertText, kind));
		}

		foreach (var keyword in CompletionKeywordProvider.For(queryContext))
		{
			if (!keyword.StartsWith(textContext.Prefix, StringComparison.Ordinal))
				continue;

			if (!seen.Add((keyword, Completion.CompletionKind.Keyword)))
				continue;

			candidates.Add(new Completion.CompletionCandidate(keyword, keyword, Completion.CompletionKind.Keyword));
		}

		if (textContext.IsTypeContext)
		{
			candidates.RemoveAll(candidate =>
				candidate.Kind != Completion.CompletionKind.Type &&
				candidate.Kind != Completion.CompletionKind.Namespace &&
				!(candidate.Kind == Completion.CompletionKind.Keyword && CompletionKeywordProvider.IsTypeKeyword(candidate.Label)));
		}

		return new CompletionResult(span, candidates);
	}

	private static (AnalyzedProject Project, CompilationUnitSyntax Unit)? TryRecoverUnit(
		ProjectSnapshot project, DocumentSnapshot doc, int position)
	{
		if (!CompletionTextContext.TryGetProbeText(doc.Text, position, out var probeText))
			return null;

		var probed = project.WithDocument(doc.Id, SourceText.From(probeText)).GetAnalysis();
		if (probed.BinderContext is null ||
			!probed.UnitsByDocument.TryGetValue(doc.Id, out var probedUnit) ||
			probedUnit is null)
		{
			return null;
		}

		return (probed, probedUnit);
	}

	internal static Completion.CompletionKind MapKind(AnalysisKind kind) => kind switch
	{
		AnalysisKind.Local => Completion.CompletionKind.Local,
		AnalysisKind.Parameter => Completion.CompletionKind.Parameter,
		AnalysisKind.Global => Completion.CompletionKind.Global,
		AnalysisKind.Function => Completion.CompletionKind.Function,
		AnalysisKind.Method => Completion.CompletionKind.Method,
		AnalysisKind.Type => Completion.CompletionKind.Type,
		AnalysisKind.Namespace => Completion.CompletionKind.Namespace,
		AnalysisKind.StructField => Completion.CompletionKind.StructField,
		AnalysisKind.UnionVariant => Completion.CompletionKind.UnionVariant,
		AnalysisKind.EnumVariant => Completion.CompletionKind.EnumVariant,
		AnalysisKind.EnumMetadata => Completion.CompletionKind.EnumMetadata,
		AnalysisKind.ArrayLength => Completion.CompletionKind.ArrayLength,
		_ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unhandled analysis CompletionKind.")
	};
}
