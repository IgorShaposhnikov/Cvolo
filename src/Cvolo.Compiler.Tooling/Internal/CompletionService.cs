using AnalysisKind = Cvolo.Analysis.Completion.CompletionKind;
using AnalysisContext = Cvolo.Analysis.Completion.CompletionQueryContext;

using Cvolo.Compiler.Tooling.Completion;
using Cvolo.Analysis;
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

		// Inside an extension body a `~` can only begin a destructor named after the extended
		// type. Offer the full `~Type() { ... }` stub as a snippet with the caret placed in the
		// body, so the developer gets the declaration skeleton in one Tab.
		if (textContext.IsDestructorContext)
		{
			var destructorCandidates = new List<Completion.CompletionCandidate>();
			if (textContext.DestructorTypeName.Length > 0)
			{
				var name = textContext.DestructorTypeName;
				destructorCandidates.Add(new Completion.CompletionCandidate(
					$"~{name}()",
					$"~{name}() {{\n    $0\n}}",
					Completion.CompletionKind.Method,
					IsSnippet: true));
			}

			return new CompletionResult(span, destructorCandidates);
		}

		var analysis = project.GetAnalysis();

		var unit = analysis.UnitsByDocument.TryGetValue(doc.Id, out var parsed)
			? parsed
			: null;

		var queryPosition = position;

		if (unit is null)
		{
			var recovered = TryRecoverUnit(project, doc, position);
			if (recovered is null)
			{
				// The primary parse produced no unit. A member-shaped request can still be
				// recovered by the probe below; anything else degrades to empty.
				if (!textContext.IsMemberAccessContext)
					return new CompletionResult(span, []);
			}
			else
			{
				analysis = recovered.Value.Project;
				unit = recovered.Value.Unit;
				queryPosition = recovered.Value.Position;
			}
		}

		AnalysisContext queryContext = AnalysisContext.None;
		IReadOnlyList<Analysis.Completion.CompletionCandidate> queryCandidates = [];

		// A parse error at the cursor can leave the whole project unbound (null binder
		// context). That must not suppress a recoverable member request; the probe below
		// runs a parseable snapshot instead.
		if (unit is not null && analysis.BinderContext is { } binderContext)
			(queryContext, queryCandidates) = RunQuery(binderContext, unit, queryPosition);

		// A realistic editor buffer often ends at `receiver.|` / `receiver.mem|` with no
		// following semicolon yet. ANTLR can still return a non-null partial compilation unit,
		// but that unit may omit the MemberAccessExpression node. In that case a normal query
		// would be misclassified as Statement/Expression and leak globals/keywords after '.',
		// e.g. an unknown `v.v|` could suggest `value` / `var`. Lexical context is authoritative
		// only for deciding that this is member-shaped; receiver binding and candidates remain
		// entirely compiler-owned. Probe a parseable snapshot even when the original unit exists.
		if (textContext.IsMemberAccessContext && queryContext != AnalysisContext.Member)
		{
			var recovered = TryRecoverMemberQuery(project, doc, position);
			if (recovered is { } recoveredResult)
			{
				queryContext = recoveredResult.Context;
				queryCandidates = recoveredResult.Candidates;
			}

			// Never degrade a member-shaped request to the general completion surface. If
			// recovery cannot bind a member access, an empty member result is safer and matches
			// the language-server contract for an unresolved receiver.
			if (queryContext != AnalysisContext.Member)
			{
				queryContext = AnalysisContext.Member;
				queryCandidates = [];
			}
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

	private static (AnalysisContext Context, IReadOnlyList<Analysis.Completion.CompletionCandidate> Candidates) RunQuery(
		BindingContext binderContext, CompilationUnitSyntax unit, int position)
	{
		// CompletionQuery temporarily mutates CurrentUnit/CurrentNamespace on the shared binder
		// context to resolve names from the requesting file's perspective; the lock keeps that
		// mutation serialized across concurrent completion requests.
		lock (binderContext)
		{
			var result = CompletionQuery.Compute(binderContext, unit, position);
			return (result.Context, result.Candidates);
		}
	}

	private static (AnalysisContext Context, IReadOnlyList<Analysis.Completion.CompletionCandidate> Candidates)? TryRecoverMemberQuery(
		ProjectSnapshot project, DocumentSnapshot doc, int position)
	{
		foreach (var probeText in CompletionTextContext.GetMemberProbeTexts(doc.Text, position))
		{
			var probed = project.WithDocument(doc.Id, SourceText.From(probeText)).GetAnalysis();
			if (probed.BinderContext is not { } binder)
				continue;

			if (!probed.UnitsByDocument.TryGetValue(doc.Id, out var unit) || unit is null)
				continue;

			var result = RunQuery(binder, unit, position);
			if (result.Context == AnalysisContext.Member)
				return result;
		}

		return null;
	}

	private static (AnalyzedProject Project, CompilationUnitSyntax Unit, int Position)? TryRecoverUnit(
		ProjectSnapshot project, DocumentSnapshot doc, int position)
	{
		foreach (var (probeText, probePosition) in CompletionTextContext.GetProbeTexts(doc.Text, position))
		{
			var probed = project.WithDocument(doc.Id, SourceText.From(probeText)).GetAnalysis();
			if (probed.BinderContext is null ||
				!probed.UnitsByDocument.TryGetValue(doc.Id, out var probedUnit) ||
				probedUnit is null)
			{
				continue;
			}

			return (probed, probedUnit, probePosition);
		}

		return null;
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
