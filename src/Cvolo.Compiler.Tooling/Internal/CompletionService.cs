using System.Runtime.CompilerServices;
using AnalysisKind = Cvolo.Analysis.Completion.CompletionKind;
using AnalysisContext = Cvolo.Analysis.Completion.CompletionQueryContext;

using Cvolo.Compiler.Tooling.Completion;
using Cvolo.Analysis;
using Cvolo.Analysis.Passes;
using Cvolo.Analysis.Symbols;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using Cvolo.Core.Diagnostics;
using Cvolo.Analysis.Completion;

namespace Cvolo.Compiler.Tooling.Internal;

/// <summary>
/// Orchestrates a completion request: token-level context (span, prefix, comment/literal, type-only
/// position), the shared per-project analysis (parsed units + bound context), the analysis-side
/// semantic query, keyword candidates, and final prefix filtering, type-context narrowing, overload
/// expansion, deterministic ordering, and on-demand resolution of callable candidates.
/// </summary>
internal static class CompletionService
{
	/// <summary>
	/// Maps each binder context (one per distinct analysis) to a stable opaque token so completion
	/// item ids minted against an analysis stay unambiguous and are rejected by any other analysis,
	/// including the probes used for member recovery.
	/// </summary>
	private static readonly ConditionalWeakTable<BindingContext, BinderToken> BinderTokenTable = new();

	private sealed class BinderToken
	{
		public required Guid Value { get; init; }
	}

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
		// type. Offer the full `~Type() { ... }` stub as a structured insertion template with a
		// plain-text fallback, so the developer gets the declaration skeleton in one Tab.
		if (textContext.IsDestructorContext)
		{
			var destructorCandidates = new List<Completion.CompletionCandidate>();
			if (textContext.DestructorTypeName.Length > 0)
			{
				var name = textContext.DestructorTypeName;
				var plainStub = $"~{name}() {{\n    \n}}";
				var plan = new CompletionInsertionPlan(
				[
					new CompletionLiteral($"~{name}() {{"),
					new CompletionLiteral("\n    "),
					new CompletionPlaceholder(""),
					new CompletionLiteral("\n}"),
				]);
				destructorCandidates.Add(new Completion.CompletionCandidate(
					null, $"~{name}()", plainStub, Completion.CompletionKind.Method,
					null, plan, CompletionResolvableFields.None));
			}

			return new CompletionResult(span, destructorCandidates);
		}

		// Inside an attribute list `[...]` offer the compiler's built-in attribute names using the
		// canonical suffix-stripped spelling (`Error`, not `ErrorAttribute`). The attribute surface
		// replaces the general declaration/keyword list, which is noise there.
		if (textContext.IsAttributeContext)
		{
			var attributeCandidates = new List<Completion.CompletionCandidate>();
			foreach (var name in DeclarationPass.IntrinsicAttributeNames)
			{
				if (!name.StartsWith(textContext.Prefix, StringComparison.Ordinal))
					continue;

				attributeCandidates.Add(new Completion.CompletionCandidate(
					null, name, name, Completion.CompletionKind.Type,
					null, null, CompletionResolvableFields.None));
			}

			return new CompletionResult(span, attributeCandidates);
		}

		var analysis = project.GetAnalysis();

		var unit = analysis.UnitsByDocument.TryGetValue(doc.Id, out var parsed)
			? parsed
			: null;

		var queryPosition = position;
		AnalyzedProject? analyzedForCandidates = null;

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
				analyzedForCandidates = analysis;
			}
		}
		else
		{
			analyzedForCandidates = analysis;
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
				analyzedForCandidates = recoveredResult.Project;
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
		var groupedCallableCandidates = queryCandidates
			.Where(candidate => candidate.Kind is AnalysisKind.Function or AnalysisKind.Method && candidate.CallableGroupKey is not null)
			.Select(candidate => (candidate.Label, candidate.Kind))
			.ToHashSet();
		var expandedCallableCandidates = new HashSet<(string Label, AnalysisKind Kind)>();

		foreach (var candidate in queryCandidates)
		{
			if (!candidate.Label.StartsWith(textContext.Prefix, StringComparison.Ordinal))
				continue;

			var kind = MapKind(candidate.Kind);

			// Callable candidates are expanded into one candidate per overload so distinct
			// overloads stay distinct in the editor (LSP-7 supersedes the earlier label+kind
			// collapse for callables). Detail distinguishes them; insertion stays single-source.
			if (candidate.Kind is AnalysisKind.Function or AnalysisKind.Method)
			{
				// If the semantic query supplied an exact callable-group key for this source label,
				// discard any weaker fallback candidate with the same spelling. This protects member
				// completion from returning one rich semantic item plus one legacy/plain duplicate.
				if (candidate.CallableGroupKey is null && groupedCallableCandidates.Contains((candidate.Label, candidate.Kind)))
					continue;

				// A semantic label is expanded at most once. Distinct overloads remain separate inside
				// that exact compiler-selected group; unrelated owners are never merged by short name.
				if (candidate.CallableGroupKey is not null && !expandedCallableCandidates.Add((candidate.Label, candidate.Kind)))
					continue;

				if (!seen.Add((candidate.Label, kind)))
					continue;

				var expanded = ExpandOverloads(analyzedForCandidates?.BinderContext, candidate.CallableGroupKey, candidate.Label, kind, doc.Text, span);
				if (expanded is not null)
				{
					candidates.AddRange(expanded);
					continue;
				}
			}
			else if (!seen.Add((candidate.Label, kind)))
			{
				continue;
			}

			candidates.Add(new Completion.CompletionCandidate(
				null, candidate.Label, candidate.InsertText, kind,
				null, null, CompletionResolvableFields.None));
		}

		foreach (var keyword in CompletionKeywordProvider.For(queryContext))
		{
			if (!keyword.StartsWith(textContext.Prefix, StringComparison.Ordinal))
				continue;

			if (!seen.Add((keyword, Completion.CompletionKind.Keyword)))
				continue;

			candidates.Add(new Completion.CompletionCandidate(
				null, keyword, keyword, Completion.CompletionKind.Keyword,
				null, null, CompletionResolvableFields.None));
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

	/// <summary>
	/// Resolves the lazily-resolvable fields of a callable completion candidate against exactly
	/// <paramref name="snapshot"/>. A null or unknown id (a foreign snapshot's id, a stale token, or
	/// an out-of-range ordinal) deterministically yields no result rather than an error.
	/// </summary>
	internal static CompletionResolvedInfo? Resolve(ProjectSnapshot snapshot, CompletionItemId itemId)
	{
		var analysis = snapshot.GetAnalysis();
		if (analysis.BinderContext is not { } context)
			return null;

		if (!BinderTokenTable.TryGetValue(context, out var token) || token.Value != itemId.SnapshotToken)
			return null;

		var all = EnumerateAll(context);
		if ((uint)itemId.Value >= (uint)all.Count)
			return null;

		var function = all[itemId.Value];
		return new CompletionResolvedInfo(DetailOf(context, function), DocumentationOf(context, function));
	}

	/// <summary>
	/// Turns a deduplicated callable query candidate into one candidate per overload. A single
	/// callable still gets an identity so its documentation and detail stay resolvable; only a null
	/// <paramref name="context"/> or an unknown label keeps the callers' single candidate path.
	/// </summary>
	private static List<Completion.CompletionCandidate>? ExpandOverloads(
		BindingContext? context, string? callableGroupKey, string label, Completion.CompletionKind kind, SourceText text, TextSpan span)
	{
		if (context is null || callableGroupKey is null
			|| !context.OverloadedFunctions.TryGetValue(callableGroupKey, out var functions)
			|| functions.Count == 0)
		{
			return null;
		}

		var all = EnumerateAll(context);
		var token = TokenFor(context);
		var onlyOne = functions.Count == 1;
		var result = new List<Completion.CompletionCandidate>(functions.Count);
		foreach (var function in functions)
		{
			var ordinal = all.IndexOf(function);
			CompletionItemId? itemId = ordinal >= 0 ? new CompletionItemId(token, ordinal) : null;
			var plan = BuildInsertionPlan(label, function, text, span);
			result.Add(new Completion.CompletionCandidate(
				itemId,
				label,
				RenderPlainText(plan, label),
				kind,
				DetailOf(context, function),
				plan,
				CompletionResolvableFields.Detail | CompletionResolvableFields.Documentation));
		}

		return result;
	}

	private static List<FunctionSymbol> EnumerateAll(BindingContext context)
	{
		var result = new List<FunctionSymbol>();
		foreach (var entry in context.OverloadedFunctions.OrderBy(e => e.Key, StringComparer.Ordinal))
			result.AddRange(entry.Value);
		return result;
	}

	private static Guid TokenFor(BindingContext context)
		=> BinderTokenTable.GetValue(context, static _ => new BinderToken { Value = Guid.NewGuid() }).Value;

	/// <summary>
	/// The initial distinguishing detail for a callable candidate: return type, source name, and
	/// parameter types. Computed identically at enumeration and at resolution time so a resolved
	/// value never contradicts the initial response.
	/// </summary>
	private static string DetailOf(BindingContext context, FunctionSymbol function)
	{
		var parameters = function.Parameters
			.Where(parameter => parameter.Name != "this")
			.Select(parameter => $"{parameter.Type.Name} {parameter.Name}");
		return $"{function.ReturnType.Name} {SourceNameOf(context, function)}({string.Join(", ", parameters)})";
	}

	/// <summary>
	/// Builds a structured call template tailored to the exact completion site so punctuation is
	/// never doubled: when the user has just opened the call (`name(` or `name(|`), the plan
	/// supplies the arguments and the final cursor but no leading or trailing parenthesis; when the
	/// call is closed or being edited mid-arguments, no plan is produced.
	/// </summary>

	private static CompletionInsertionPlan? BuildInsertionPlan(string label, FunctionSymbol function, SourceText text, TextSpan span)
	{
		var next = span.End;
		while (next < text.Length && char.IsWhiteSpace(text[next]))
			next++;
		var parenAfterName = next < text.Length && text[next] == '(';

		if (span.Length == 0)
		{
			var prev = span.Start - 1;
			while (prev >= 0 && char.IsWhiteSpace(text[prev]))
				prev--;
			if (prev >= 0 && text[prev] == '(')
			{
				// Freshly opened call: `name(|`. The cursor is right inside the parens with no
				// arguments, so the template fills the arguments without repeating anything.
				var segments = new List<CompletionInsertSegment>();
				AppendParameterPlaceholders(segments, function);
				segments.Add(new CompletionFinalCursor());
				return new CompletionInsertionPlan(segments);
			}

			if (IsInsideOpenCall(text, span.Start))
				return null;
		}

		if (parenAfterName)
		{
			// The open parenthesis was just typed after the name (`name|(`): the replacement span
			// covers the name, so keep the label, supply the arguments, and stop short of the
			// closing parenthesis to avoid doubling what the user may already have typed.
			var segments = new List<CompletionInsertSegment> { new CompletionLiteral(label) };
			AppendParameterPlaceholders(segments, function);
			segments.Add(new CompletionFinalCursor());
			return new CompletionInsertionPlan(segments);
		}

		var fullCall = new List<CompletionInsertSegment> { new CompletionLiteral(label), new CompletionLiteral("(") };
		AppendParameterPlaceholders(fullCall, function);
		fullCall.Add(new CompletionLiteral(")"));
		fullCall.Add(new CompletionFinalCursor());
		return new CompletionInsertionPlan(fullCall);
	}

	/// <summary>
	/// Appends one placeholder per non-receiver parameter of <paramref name="function"/>, separated
	/// by literal ", " segments.
	/// </summary>
	private static void AppendParameterPlaceholders(List<CompletionInsertSegment> segments, FunctionSymbol function)
	{
		var first = true;
		foreach (var parameter in function.Parameters)
		{
			if (parameter.Name == "this")
				continue;

			if (!first)
				segments.Add(new CompletionLiteral(", "));
			first = false;
			segments.Add(new CompletionPlaceholder(parameter.Name));
		}
	}

	private static string RenderPlainText(CompletionInsertionPlan? plan, string fallback)
	{
		if (plan is null)
			return fallback;

		string? label = null;
		var opensCall = false;

		foreach (var segment in plan.SnippetSegments)
		{
			if (segment is not CompletionLiteral literal)
				continue;

			label ??= literal.Text;
			if (literal.Text == "(")
				opensCall = true;
		}

		if (label is null)
			return string.Empty;

		return opensCall ? label + "()" : label;
	}

	/// <summary>
	/// Determines whether <paramref name="position"/> sits inside the arguments of an already-open
	/// call (an unmatched '(' before it with no intervening ')').
	/// </summary>
	private static bool IsInsideOpenCall(SourceText text, int position)
	{
		var depth = 0;
		for (var i = position - 1; i >= 0; i--)
		{
			var ch = text[i];
			if (ch == ')')
				depth++;
			else if (ch == '(')
			{
				if (depth == 0)
					return true;
				depth--;
			}
		}

		return false;
	}

	private static string? DocumentationOf(BindingContext context, FunctionSymbol function)
	{
		if (FindDeclaration(context, function) is not { } found
			|| !context.FileContexts.TryGetValue(found.Unit, out var fileContext))
		{
			return null;
		}

		return ExtractDocumentation(fileContext.Source, found.Node.Span.Start);
	}

	/// <summary>
	/// Locates the declaration node of a bound function by re-deriving the binder's own mangled
	/// names (same arithmetic as the binder, so no occurrence is matched by raw text).
	/// </summary>
	private static (CompilationUnitSyntax Unit, SyntaxNode Node)? FindDeclaration(BindingContext context, FunctionSymbol function)
	{
		foreach (var unit in context.FileContexts.Keys)
		{
			var ns = unit.NamespaceDeclaration?.Name;
			if (unit.NamespaceDeclaration is { } namespaceDeclaration)
			{
				foreach (var member in namespaceDeclaration.Members)
				{
					if (MatchNode(context, member, ns, function) is { } node)
						return (unit, node);
				}
			}

			foreach (var member in unit.Members)
			{
				if (MatchNode(context, member, ns, function) is { } node)
					return (unit, node);
			}
		}

		return null;
	}

	private static SyntaxNode? MatchNode(BindingContext context, SyntaxNode node, string? ns, FunctionSymbol function)
	{
		switch (node)
		{
			case FunctionDeclarationSyntax func:
			{
				var baseName = func.Name is "main" or "Main" ? "main" : context.GetMangledName(func.Name, ns);
				return TryResolveParameters(context, func.Parameters) is { } types
					&& string.Equals(context.GetOverloadedMangledName(baseName, types), function.Name, StringComparison.Ordinal)
					? node
					: null;
			}
			case ExtensionDeclarationSyntax extension:
			{
				var extendedType = context.ResolveType(context.NormalizeGenericName(extension.ExtendedTypeName));
				foreach (var method in extension.Methods)
				{
					var baseName = context.GetMangledName($"{extension.ExtendedTypeName}.{method.Name}", ns);
					if (extendedType is not null && TryResolveParameters(context, method.Parameters) is { } types)
					{
						var withReceiver = new List<TypeSymbol>(types.Count + 1)
						{
							new PointerTypeSymbol(extendedType, isMutable: false),
						};
						withReceiver.AddRange(types);
						if (string.Equals(context.GetOverloadedMangledName(baseName, withReceiver), function.Name, StringComparison.Ordinal))
							return method;
					}
				}

				foreach (var constructor in extension.Constructors)
				{
					var baseName = context.GetMangledName(extension.ExtendedTypeName, ns);
					if (TryResolveParameters(context, constructor.Parameters) is { } types
						&& string.Equals(context.GetOverloadedMangledName(baseName, types), function.Name, StringComparison.Ordinal))
					{
						return constructor;
					}
				}

				return null;
			}
			default:
				return null;
		}
	}

	private static IReadOnlyList<TypeSymbol>? TryResolveParameters(BindingContext context, IReadOnlyList<ParameterSyntax> parameters)
	{
		var types = new List<TypeSymbol>(parameters.Count);
		foreach (var parameter in parameters)
		{
			var type = context.ResolveType(context.NormalizeGenericName(parameter.Type));
			if (type is null)
				return null;

			types.Add(type);
		}

		return types;
	}

	/// <summary>
	/// Extracts the contiguous <c>///</c> documentation block immediately preceding a source
	/// position. Ordinary <c>//</c> comments are intentionally ignored: only Cvolo documentation
	/// comments participate in completion/signature documentation.
	/// </summary>
	private static string? ExtractDocumentation(string source, int position)
	{
		if (position <= 0 || position > source.Length)
			return null;

		// AST declaration/parameter spans start at the first token rather than at indentation.
		// Normalize to the physical line start before scanning the immediately preceding /// block.
		var scan = position;
		while (scan > 0 && source[scan - 1] is not '\r' and not '\n')
			scan--;

		var lines = new Stack<string>();
		while (scan > 0)
		{
			var lineEnd = scan;
			while (lineEnd > 0 && source[lineEnd - 1] is '\r' or '\n')
				lineEnd--;

			var lineStart = lineEnd;
			while (lineStart > 0 && source[lineStart - 1] is not '\r' and not '\n')
				lineStart--;

			var line = source[lineStart..lineEnd].Trim();
			if (line.Length == 0 || !line.StartsWith("///", StringComparison.Ordinal))
				break;

			lines.Push(line[3..].Trim());
			scan = lineStart;
		}

		return lines.Count == 0 ? null : string.Join("\n", lines);
	}

	private static string LeafKey(string name)
	{
		var dot = name.LastIndexOf('.');
		return dot < 0 ? name : name[(dot + 1)..];
	}

	private static string SourceNameOf(BindingContext context, FunctionSymbol function)
	{
		// Prefer the compiler-owned source declaration spelling. Extension method symbols contain
		// receiver information in their mangled symbol name (for example Name_refNamespace_Type),
		// which must never leak into editor-facing completion detail.
		if (FindDeclaration(context, function) is { Node: FunctionDeclarationSyntax declaration })
			return declaration.Name;

		if (FindDeclaration(context, function) is { Node: ConstructorDeclarationSyntax constructor })
			return constructor.StructName;

		var name = LeafKey(function.Name);
		var nonThis = function.Parameters.Where(parameter => parameter.Name != "this").ToList();
		var suffix = string.Concat(nonThis.Select(parameter => "_" + MangleTypeName(parameter.Type.Name)));
		if (suffix.Length == 0)
			suffix = "_void";

		return name.EndsWith(suffix, StringComparison.Ordinal)
			? name[..^suffix.Length]
			: name;
	}

	private static string MangleTypeName(string name) =>
		name.Replace("<", "_")
			.Replace(">", "_")
			.Replace("[", "Arr")
			.Replace("]", "")
			.Replace(" ", "")
			.Replace(",", "_")
			.Replace(".", "_")
			.Replace("*", "Ptr");

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

	private static (AnalyzedProject Project, AnalysisContext Context, IReadOnlyList<Analysis.Completion.CompletionCandidate> Candidates)? TryRecoverMemberQuery(
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
				return (probed, result.Context, result.Candidates);
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