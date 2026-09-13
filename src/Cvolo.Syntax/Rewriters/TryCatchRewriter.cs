using Cvolo.Core.AST;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using Cvolo.Core.AST.Expressions;
using Cvolo.Core.AST.Statements;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Syntax.Rewriters;

/// <summary>
/// Lowers every <c>try</c> statement and <c>catch</c> expression into the state-flag framework
/// described in <c>problems/Inc - Non-Allocating State-Flag Try-Catch and Catch-Expressions
/// Framework.md</c>. Runs BEFORE the defer rewriter so defers written inside try/catch bodies are
/// expanded on their final, post-lowering positions.
/// </summary>
/// <remarks>
/// Try lowering (per <c>try { ... } catch (E.V) {...}</c>):
/// — A <c>try</c> block becomes a labeled block with a compiler-generated label
///   (<c>__try_N: { ... }</c>). An error exit is a <c>break __try_N;</c> carrying the payload
///   through three kinds of state declared in front of the block: a fail flag
///   (<c>bool __try_failed_N</c>), a small integer discriminator (<c>int __try_tag_N</c>), and
///   one payload slot (<c>var E __try_err_N = default(E);</c>) per distinct error type.
/// — Every <c>Result</c>-producing step is lowered linearly inside the labeled block; no
///   if-gate cascade is emitted because a failing step <c>break</c>s out of the block.
///   <c>int x = F();</c> becomes <c>int x = default(int); var __try_res_N = F(); if
///   (__try_res_N is Err) { __try_err_E = __try_res_N.Err; __try_tag_N = i; __try_failed_N =
///   true; break __try_N; } x = __try_res_N.Ok;</c>.
/// — After the block, <c>if (__try_failed_N) { ... }</c> dispatches on <c>__try_tag_N</c> against
///   the clauses in source order; value patterns additionally compare the payload slot.
///   A type pattern with a binding reads the slot into the bound name. A bare <c>catch</c>
///   becomes the trailing else.
/// — Clauses must cover every error type the block can emit (CVL1057); no implicit bubbling is
///   performed. Duplicate clauses are CVL1053, subsumed clauses CVL1054, non-<c>[Error]</c>
///   pattern types CVL1059, and value patterns over non-<c>enum</c> types CVL1067.
/// — <c>defer</c> declared inside the <c>try</c> binds to the labeled block and runs exactly once
///   on its single exit (fallthrough, error break, user break/continue, or return) via the
///   existing defer machinery.
/// Catch-expression lowering (only at statement seams — a whole initializer, a whole assignment
/// right-hand side, a whole return operand, or a whole expression statement):
///   <c>var __catch_res_N = X; if (__catch_res_N is Err e) { handler } else { dst = __catch_res_N.Ok; }</c>
/// where the handler either evaluates the literal fallback or runs the arrow lambda. A lambda
/// body must end with an explicit <c>return</c> (CVL1058); that return yields the recovered value.
/// Nested catch expressions anywhere else are rejected with CVL1051. The pass is idempotent
/// (its output contains no try/catch nodes).
/// </remarks>
public sealed class TryCatchRewriter(
	DiagnosticBag diagnostics,
	CompilationContext fileContext,
	DeclarationIndex declarationIndex) : AstRewriterBase
{
	private readonly DiagnosticBag _diagnostics = diagnostics;
	private readonly CompilationContext _fileContext = fileContext;
	private readonly DeclarationIndex _declarationIndex = declarationIndex;

	private int _tempCounter;

	private const string CatchRequiresResultMessage =
		"The `catch` operator requires a valid left-hand expression or a `try` block yielding a `Result` shape.";
	private const string CatchLambdaMissingReturnMessage =
		"Lambda catch body must end with a `return` statement.";

	public override SyntaxNode Rewrite(SyntaxNode node)
	{
		if (node is BlockStatementSyntax block)
			return RewriteBlock(block);

		if (node is TryStatementSyntax tryStmt)
			return LowerTryStatement(tryStmt);

		// Statement-seam catch expressions are consumed here; a catch reaching the generic
		// rewrite path below is nested inside a larger expression and is not supported.
		if (TryBuildSeamRewriter(node) is { } seam)
			return seam;

		if (node is CatchExpressionSyntax nestedCatcher)
		{
			Report(nestedCatcher.Span, CatchRequiresResultMessage, DiagnosticIds.CatchRequiresResult);
			return node;
		}

		return base.Rewrite(node);
	}

	private BlockStatementSyntax RewriteBlock(BlockStatementSyntax block)
	{
		var rewrittenStatements = new List<SyntaxNode>();
		foreach (var stmt in block.Statements)
			rewrittenStatements.AddRange(RewriteStatements(stmt));
		return new BlockStatementSyntax(block.Span, rewrittenStatements);
	}

	/// <summary>
	/// Rewrites a single statement and splices any <see cref="SplicedStatementListSyntax"/>
	/// result (used by the declaration seam so the hoisted target variable is emitted at
	/// block scope, not nested inside the dispatch block).
	/// </summary>
	private IEnumerable<SyntaxNode> RewriteStatements(SyntaxNode stmt)
	{
		var result = Rewrite(stmt);
		return result is SplicedStatementListSyntax list ? list.Statements : [result];
	}

	/// <summary>Marker node expanded by <see cref="RewriteStatements"/>; never reaches later passes.</summary>
	private sealed class SplicedStatementListSyntax(TextSpan span, IReadOnlyList<SyntaxNode> statements) : SyntaxNode(span)
	{
		public override SyntaxKind Kind => SyntaxKind.BlockStatement;
		public IReadOnlyList<SyntaxNode> Statements { get; } = statements;
		public override IEnumerable<SyntaxNode> GetChildren() => Statements;
	}

	/// <summary>
	/// Routes a statement whose expression contains a catch at a supported seam (the whole
	/// initializer, assignment right-hand side, return operand, or expression statement).
	/// </summary>
	private SyntaxNode? TryBuildSeamRewriter(SyntaxNode node)
	{
		switch (node)
		{
			case VariableDeclarationSyntax vd when vd.Initializer is CatchExpressionSyntax:
				return LowerCatchVarDecl(vd);
			case ExpressionStatementSyntax es when es.Expression is CatchExpressionSyntax:
				return LowerCatchWholeExprStatement(es);
			case ExpressionStatementSyntax es when es.Expression is BinaryExpressionSyntax { Operator: "=" } bin && bin.Right is CatchExpressionSyntax:
				return LowerCatchAssign(bin);
			case ReturnStatementSyntax rt when rt.Expression is CatchExpressionSyntax:
				return LowerCatchReturn(rt);
			default:
				return null;
		}
	}

	// ── try statement lowering

	private SyntaxNode LowerTryStatement(TryStatementSyntax tryStmt)
	{
		var span = tryStmt.Span;
		var failName = Fresh("__try_failed_");
		var tagName = Fresh("__try_tag_");
		var tryLabel = Fresh("__try_");
		var slotNames = new Dictionary<string, string>(StringComparer.Ordinal);
		var tagIndices = new Dictionary<string, int>(StringComparer.Ordinal);
		var slotDecls = new List<string>();

		var patterns = AnalyzePatterns(tryStmt, span);
		if (patterns.Count == 0)
		{
			// No usable clause; report and leave the node for the binder to judge.
			Report(span, CatchRequiresResultMessage, DiagnosticIds.CatchRequiresResult);
			return new BlockStatementSyntax(span, [RewriteBlock(tryStmt.Body)]);
		}

		// 1. Coverage: every error type the steps can emit must appear in a clause (CVL1057).
		var emitted = new HashSet<string>(StringComparer.Ordinal);
		var emittedSpans = new Dictionary<string, TextSpan>(StringComparer.Ordinal);
		foreach (var rawStmt in tryStmt.Body.Statements)
		{
			if (TryGetStepCall(rawStmt, out var stepCall) && ResolveStepErrorType(stepCall) is { } err)
			{
				emitted.Add(err);
				emittedSpans.TryAdd(err, rawStmt.Span);
			}
		}

		var coveredTypes = new HashSet<string>(StringComparer.Ordinal);
		foreach (var pattern in patterns)
		{
			if (pattern.ErrorType is { } pt)
				coveredTypes.Add(pt);
		}

		var reported = new HashSet<string>(StringComparer.Ordinal);
		foreach (var err in emitted)
		{
			if (reported.Add(err) && !patterns.Any(p => p.IsBare) && !coveredTypes.Contains(err))
			{
				Report(emittedSpans[err],
					$"Error type `{err}` emitted inside this `try` block has no matching `catch` clause.",
					DiagnosticIds.CatchUnhandledErrorType);
			}
		}

		// 2. One payload slot + one tag index per distinct error type.
		foreach (var pattern in patterns)
		{
			if (pattern.ErrorType is { } t)
				EnsureTypeSlot(t, slotNames, tagIndices, slotDecls);
		}
		foreach (var err in emitted)
			EnsureTypeSlot(err, slotNames, tagIndices, slotDecls);

		// 3. State scalars declared in front of the labeled block.
		var scalarDecls = new List<SyntaxNode>
		{
			new VariableDeclarationSyntax(span, true, "bool", failName, new BooleanLiteralExpressionSyntax(span, false)),
			new VariableDeclarationSyntax(span, true, "int", tagName, new IntegerLiteralExpressionSyntax(span, 0, "int")),
		};
		foreach (var errType in slotDecls)
			scalarDecls.Add(new VariableDeclarationSyntax(span, true, errType, slotNames[errType], new DefaultExpressionSyntax(span, errType)));

		// 4. The linear labeled-block body.
		var bodyStatements = new List<SyntaxNode>();
		foreach (var rawStmt in tryStmt.Body.Statements)
			bodyStatements.AddRange(LowerBodyStatement(rawStmt, tryLabel, failName, tagName, slotNames, tagIndices, span));

		var labeledBlock = new LabeledBlockStatementSyntax(span, tryLabel, new BlockStatementSyntax(span, bodyStatements));
		var cascade = BuildCascade(span, failName, tagName, slotNames, tagIndices, patterns);

		var statements = new List<SyntaxNode>();
		statements.AddRange(scalarDecls);
		statements.Add(labeledBlock);
		statements.Add(cascade);
		return new BlockStatementSyntax(span, statements);
	}

	/// <summary>
	/// Analyzes the clause patterns: classification, CVL1053/1054/1059/1067 checks.
	/// </summary>
	private List<ClausePattern> AnalyzePatterns(TryStatementSyntax tryStmt, TextSpan span)
	{
		var patterns = new List<ClausePattern>();
		var seenKeys = new HashSet<string>(StringComparer.Ordinal);
		var coveredTypes = new HashSet<string>(StringComparer.Ordinal);
		var bareSeen = false;

		foreach (var clause in tryStmt.CatchClauses)
		{
			string? errorType = null;
			string? variant = null;

			if (!clause.IsBare)
			{
				var text = clause.ErrorTypeName!;
				var segments = text.Split('.');
				if (clause.BindingName is null && segments.Length >= 2
					&& _declarationIndex.TryGetTypeKind(string.Join('.', segments[..^1]), out var kind))
				{
					var receiver = string.Join('.', segments[..^1]);
					errorType = receiver;
					variant = segments[^1];
					if (kind != "enum")
					{
						Report(clause.Span,
							$"Value-pattern `catch ({receiver}.{segments[^1]})` requires `{receiver}` to be an `enum`; `{receiver}` is a `{kind}`.",
							DiagnosticIds.CatchValuePatternOnNonEnum);
					}
					else if (!_declarationIndex.HasErrorAttribute(receiver))
					{
						Report(clause.Span,
							$"`catch` pattern references type `{receiver}` which is not marked `[Error]`.",
							DiagnosticIds.CatchPatternNotErrorAttribute);
					}
				}
				else
				{
					errorType = text;
					if (_declarationIndex.TryGetTypeKind(text, out _) && !_declarationIndex.HasErrorAttribute(text))
					{
						Report(clause.Span,
							$"`catch` pattern references type `{text}` which is not marked `[Error]`.",
							DiagnosticIds.CatchPatternNotErrorAttribute);
					}
				}
			}

			// Duplicate pattern check (CVL1053).
			var key = clause.IsBare ? "__bare__"
				: variant is not null ? $"V:{errorType}.{variant}"
				: $"T:{errorType}";
			if (!seenKeys.Add(key))
			{
				Report(clause.Span,
					$"Duplicate `catch` block clause detected for pattern `{FormatPattern(clause, errorType, variant)}` within this try statement scope.",
					DiagnosticIds.DuplicateCatchClause);
			}

			// Unreachable clause check (CVL1054): a new clause covered by an earlier one.
			var unreachable = bareSeen || (!clause.IsBare && errorType is { } et && coveredTypes.Contains(et));
			if (unreachable)
			{
				Report(clause.Span,
					$"`catch` clause `{FormatPattern(clause, errorType, variant)}` is unreachable: an earlier clause already covers it.",
					DiagnosticIds.CatchUnreachableClause);
			}

			if (clause.IsBare)
				bareSeen = true;
			else if (variant is null && errorType is { } coveredType)
				coveredTypes.Add(coveredType);

			patterns.Add(new ClausePattern(clause, errorType, variant, clause.BindingName, clause.IsBare, !unreachable));
		}

		return patterns;
	}

	private static string FormatPattern(CatchClauseSyntax clause, string? errorType, string? variant) =>
		clause.IsBare ? "{ }"
		: clause.BindingName is not null ? $"{errorType} {clause.BindingName}"
		: variant is not null ? $"{errorType}.{variant}"
		: $"{errorType}";

	/// <summary>
	/// Returns true when <paramref name="stmt"/> is shaped like a Result-producing step.
	/// </summary>
	private bool TryGetStepCall(SyntaxNode stmt, out CallExpressionSyntax? stepCall)
	{
		stepCall = null;
		switch (stmt)
		{
			case VariableDeclarationSyntax { Initializer: CallExpressionSyntax call }:
				stepCall = call;
				return true;
			case ExpressionStatementSyntax { Expression: CallExpressionSyntax call }:
				stepCall = call;
				return true;
			case ExpressionStatementSyntax { Expression: BinaryExpressionSyntax { Operator: "=", Right: CallExpressionSyntax call } }:
				stepCall = call;
				return true;
			default:
				return false;
		}
	}

	/// <summary>
	/// Looks up the Result type arguments of a call in the index.
	/// </summary>
	private (string OkType, string ErrorType)? ResolveStepResultTypes(CallExpressionSyntax call)
	{
		var name = call.FunctionName;
		return _declarationIndex.ResultTypes(name)
			?? _declarationIndex.ResultTypes(name.Split('.').Last());
	}

	/// <summary>
	/// Looks up the error type of a <c>Result&lt;T, E&gt;</c>-returning call in the index.
	/// </summary>
	private string? ResolveStepErrorType(CallExpressionSyntax call) =>
		ResolveStepResultTypes(call)?.ErrorType;

	private void EnsureTypeSlot(
		string errorType,
		Dictionary<string, string> slotNames,
		Dictionary<string, int> tagIndices,
		List<string> slotDecls)
	{
		if (slotNames.ContainsKey(errorType))
			return;
		slotNames[errorType] = Fresh("__try_err_");
		tagIndices[errorType] = slotDecls.Count;
		slotDecls.Add(errorType);
	}

	/// <summary>
	/// Lowers a single statement inside the labeled try block. Typed Result-call declarations
	/// and expression statements that call (or assign the result of) a Result function become
	/// checked steps; everything else is rewritten linearly without gates.
	/// </summary>
	private IEnumerable<SyntaxNode> LowerBodyStatement(
		SyntaxNode rawStmt,
		string tryLabel,
		string failName,
		string tagName,
		Dictionary<string, string> slotNames,
		Dictionary<string, int> tagIndices,
		TextSpan span)
	{
		switch (rawStmt)
		{
			case VariableDeclarationSyntax vd when vd.Initializer is CallExpressionSyntax call:
				{
					var resolved = ResolveStepResultTypes(call);
					if (resolved is not { } types)
						return [Rewrite(vd)];

					var dstType = vd.Type;
					if (dstType is null)
					{
						// "var x = F();" infers the Ok type from the indexed Result signature.
						dstType = types.OkType;
						if (string.IsNullOrEmpty(dstType))
						{
							// Result step without a usable Ok type: dispatch-only, skip the binding.
							return ResultStep(span, tryLabel, failName, tagName, slotNames, tagIndices, call, types.ErrorType, null);
						}
					}

					return ResultStepTargetedDecl(span, tryLabel, failName, tagName, slotNames, tagIndices, dstType, vd.Name, call, types.ErrorType);
				}
			case ExpressionStatementSyntax es when es.Expression is CallExpressionSyntax stepCall:
				{
					var exprErr = ResolveStepErrorType(stepCall);
					return exprErr is { }
						? ResultStep(span, tryLabel, failName, tagName, slotNames, tagIndices, stepCall, exprErr, null)
						: [Rewrite(es)];
				}
			case ExpressionStatementSyntax es when es.Expression is BinaryExpressionSyntax { Operator: "=", Right: CallExpressionSyntax assignCall } bin:
				{
					var assignErr = ResolveStepErrorType(assignCall);
					return assignErr is { }
						? ResultStep(span, tryLabel, failName, tagName, slotNames, tagIndices, assignCall, assignErr, bin.Left)
						: [Rewrite(es)];
				}
			default:
				return [Rewrite(rawStmt)];
		}
	}

	private List<SyntaxNode> ResultStepTargetedDecl(
		TextSpan span,
		string tryLabel,
		string failName,
		string tagName,
		Dictionary<string, string> slotNames,
		Dictionary<string, int> tagIndices,
		string dstType,
		string dstName,
		ExpressionSyntax operand,
		string errType)
	{
		var statements = new List<SyntaxNode>
		{
			new VariableDeclarationSyntax(span, true, dstType, dstName, new DefaultExpressionSyntax(span, dstType)),
		};
		statements.AddRange(ResultStep(span, tryLabel, failName, tagName, slotNames, tagIndices, operand, errType, Identifier(span, dstName)));
		return statements;
	}

	/// <summary>
	/// Emits <c>var __try_res_N = call; if (__try_res_N is Err) { slot = res.Err; tag = i;
	/// failed = true; break __try_N; } [dst = __try_res_N.Ok;]</c>. When <paramref name="dst"/>
	/// is null (a standalone call statement) the Ok branch is omitted.
	/// </summary>
	private List<SyntaxNode> ResultStep(
		TextSpan span,
		string tryLabel,
		string failName,
		string tagName,
		Dictionary<string, string> slotNames,
		Dictionary<string, int> tagIndices,
		ExpressionSyntax operand,
		string errType,
		ExpressionSyntax? dst)
	{
		var resName = Fresh("__try_res_");
		var slotName = slotNames[errType];
		var tagIndex = tagIndices[errType];

		var errorStatements = new List<SyntaxNode>
		{
			AssignStatement(span, slotName, MemberField(resName, "Err")),
			AssignStatement(span, tagName, new IntegerLiteralExpressionSyntax(span, (ulong)tagIndex, "int")),
			AssignStatement(span, failName, new BooleanLiteralExpressionSyntax(span, true)),
			new BreakStatementSyntax(span, tryLabel),
		};

		ElseClauseSyntax? elseClause = null;
		if (dst is not null)
		{
			var okAssign = new ExpressionStatementSyntax(span,
				new BinaryExpressionSyntax(span, dst, "=", MemberField(resName, "Ok")));
			elseClause = new ElseClauseSyntax(span, new BlockStatementSyntax(span, [okAssign]));
		}

		return
		[
			new VariableDeclarationSyntax(span, false, null, resName, operand),
			new IfStatementSyntax(span,
				new IsPatternExpressionSyntax(span, Identifier(resName), "Err", null),
				new BlockStatementSyntax(span, errorStatements),
				elseClause),
		];
	}

	/// <summary>
	/// Builds the post-block dispatch <c>if (__try_failed_N) { ... }</c>. Clauses are evaluated
	/// in source order as an else-if chain; value patterns also compare the payload slot, type
	/// patterns only the tag, and the first bare catch becomes the trailing else.
	/// </summary>
	private SyntaxNode BuildCascade(
		TextSpan span,
		string failName,
		string tagName,
		Dictionary<string, string> slotNames,
		Dictionary<string, int> tagIndices,
		List<ClausePattern> patterns)
	{
		BlockStatementSyntax? bareBody = null;
		foreach (var pattern in patterns)
		{
			if (pattern.IsBare)
			{
				bareBody = RewriteBlock(pattern.Clause.Body);
				break;
			}
		}

		SyntaxNode? chain = null;
		for (var i = patterns.Count - 1; i >= 0; i--)
		{
			var pattern = patterns[i];
			if (!pattern.Reachable || pattern.IsBare)
				continue;

			var cond = pattern.Variant is not null
				? new BinaryExpressionSyntax(pattern.Clause.Span,
					new BinaryExpressionSyntax(pattern.Clause.Span, Identifier(tagName), "==",
						new IntegerLiteralExpressionSyntax(pattern.Clause.Span, (ulong)tagIndices[pattern.ErrorType!], "int")),
					"&&",
					new BinaryExpressionSyntax(pattern.Clause.Span, Identifier(slotNames[pattern.ErrorType!]), "==",
						VariantPathExpression(pattern.Clause.Span, $"{pattern.ErrorType}.{pattern.Variant}".Split('.'))))
				: new BinaryExpressionSyntax(pattern.Clause.Span, Identifier(tagName), "==",
					new IntegerLiteralExpressionSyntax(pattern.Clause.Span, (ulong)tagIndices[pattern.ErrorType!], "int"));

			var body = BuildClauseBody(pattern, slotNames);
			chain = new IfStatementSyntax(pattern.Clause.Span, cond, body,
				chain is null
					? (bareBody is null ? null : new ElseClauseSyntax(pattern.Clause.Span, bareBody))
					: new ElseClauseSyntax(pattern.Clause.Span, new BlockStatementSyntax(pattern.Clause.Span, [chain])));
		}

		if (chain is null)
			chain = bareBody ?? new BlockStatementSyntax(span, []);

		return new IfStatementSyntax(span, Identifier(failName), new BlockStatementSyntax(span, [chain]), null);
	}

	private BlockStatementSyntax BuildClauseBody(ClausePattern pattern, Dictionary<string, string> slotNames)
	{
		var body = RewriteBlock(pattern.Clause.Body);
		if (pattern.Binding is { } binding)
		{
			var statements = new List<SyntaxNode>
			{
				new VariableDeclarationSyntax(pattern.Clause.Span, false, pattern.ErrorType!, binding,
					Identifier(slotNames[pattern.ErrorType!])),
			};
			statements.AddRange(body.Statements);
			return new BlockStatementSyntax(pattern.Clause.Span, statements);
		}
		return body;
	}

	private static ExpressionSyntax VariantPathExpression(TextSpan span, string[] segments)
	{
		ExpressionSyntax current = new IdentifierExpressionSyntax(span, segments[0]);
		for (var i = 1; i < segments.Length; i++)
			current = new MemberAccessExpressionSyntax(span, current, segments[i]);
		return current;
	}

	// ── catch expression lowering
	private SyntaxNode LowerCatchVarDecl(VariableDeclarationSyntax vd)
	{
		var span = vd.Span;
		var catcher = (CatchExpressionSyntax)vd.Initializer!;
		var statements = new List<SyntaxNode>();

		var dstName = vd.Name;
		if (vd.Type is not null)
			statements.Add(new VariableDeclarationSyntax(span, true, vd.Type, dstName, null));
		else
			Report(span, CatchRequiresResultMessage, DiagnosticIds.CatchRequiresResult);

		statements.Add(LowerCatchDispatchCore(span, dstName, catcher));
		return new SplicedStatementListSyntax(span, statements);
	}

	private SyntaxNode LowerCatchAssign(BinaryExpressionSyntax assignment)
	{
		var span = assignment.Span;
		var catcher = (CatchExpressionSyntax)assignment.Right;
		return LowerCatchDispatchCore(span, ExpressionTargetName(assignment.Left), catcher);
	}

	private SyntaxNode LowerCatchWholeExprStatement(ExpressionStatementSyntax es)
	{
		var span = es.Span;
		var catcher = (CatchExpressionSyntax)es.Expression;
		return LowerCatchDispatchCore(span, null, catcher);
	}

	/// <summary>
	/// Declares the freshly-named result temp initialized from the operand and emits the dispatch.
	/// Shared by the declaration/assignment/whole-statement seams so each variant writes into an
	/// already-existing destination slot (or none) without re-declaring it.
	/// </summary>
	private SyntaxNode LowerCatchDispatchCore(TextSpan span, string? dstName, CatchExpressionSyntax catcher)
	{
		var resName = Fresh("__catch_res_");
		return new BlockStatementSyntax(span, new List<SyntaxNode>
		{
			new VariableDeclarationSyntax(span, false, null, resName, catcher.Operand),
			BuildCatchDispatch(span, dstName, catcher, resName),
		});
	}

	private SyntaxNode LowerCatchReturn(ReturnStatementSyntax rt)
	{
		var span = rt.Span;
		var catcher = (CatchExpressionSyntax)rt.Expression!;
		var resName = Fresh("__catch_res_");

		var errStatements = catcher.Lambda is { } lambda
			? LambdaBodyStatements(lambda.Body, span, tailExpr => new ReturnStatementSyntax(span, tailExpr), dropTail: false)
			: [new ReturnStatementSyntax(span, catcher.Fallback)];

		var errBranch = new BlockStatementSyntax(span, errStatements);
		var okValue = MemberField(resName, "Ok");

		// The seam must be spliced at block scope so the trailing `return` is the LAST
		// statement of the enclosing body and EndsWithReturn recognizes it.
		return new SplicedStatementListSyntax(span,
		[
			new VariableDeclarationSyntax(span, false, null, resName, catcher.Operand),
			new IfStatementSyntax(span,
				new IsPatternExpressionSyntax(span, Identifier(resName), "Err", catcher.Lambda?.ErrorName),
				errBranch,
				null),
			new ReturnStatementSyntax(span, okValue),
		]);
	}

	/// <summary>
	/// Builds the dispatch <c>if (res is Err [e]) { handler } else { dst = res.Ok; }</c>. With a
	/// null destination (a whole expression statement) the Ok branch is omitted and the handler
	/// runs purely for its side effects.
	/// </summary>
	private SyntaxNode BuildCatchDispatch(TextSpan span, string? dstName, CatchExpressionSyntax catcher, string resName)
	{
		var errStatements = catcher.Lambda is { } lambda
			? LambdaBodyStatements(lambda.Body, span,
				dstName is not null
					? tailExpr => AssignStatement(span, dstName, tailExpr)
					: tailExpr => new ExpressionStatementSyntax(span, tailExpr),
				dropTail: dstName is null)
			: dstName is not null
				? [AssignStatement(span, dstName, catcher.Fallback!)]
				: catcher.Fallback is not null ? [new ExpressionStatementSyntax(span, catcher.Fallback)] : [];

		var errBranch = new BlockStatementSyntax(span, errStatements);
		var elseClause = dstName is null
			? null
			: new ElseClauseSyntax(span,
				new BlockStatementSyntax(span, [AssignStatement(span, dstName, MemberField(resName, "Ok"))]));

		return new IfStatementSyntax(span,
			new IsPatternExpressionSyntax(span, Identifier(resName), "Err", catcher.Lambda?.ErrorName),
			errBranch,
			elseClause);
	}

	/// <summary>
	/// Rewrites a lambda body and projects its trailing <c>return</c> per the tail transformer.
	/// A body that is empty or does not end with a <c>return</c> statement is reported as CVL1058;
	/// when <paramref name="dropTail"/> is set (side-effect seam) the projected value statement is
	/// omitted and the body runs purely for its effects.
	/// </summary>
	private List<SyntaxNode> LambdaBodyStatements(
		BlockStatementSyntax body,
		TextSpan span,
		Func<ExpressionSyntax, SyntaxNode> tailTransform,
		bool dropTail)
	{
		var cloned = new List<SyntaxNode>(body.Statements.Select(Rewrite));
		if (cloned.Count == 0 || cloned[^1] is not ReturnStatementSyntax { Expression: { } tailExpr })
		{
			Report(body.Span, CatchLambdaMissingReturnMessage, DiagnosticIds.CatchLambdaMissingReturn);
			return cloned.Where(s => s is not ReturnStatementSyntax).ToList();
		}

		cloned.RemoveAt(cloned.Count - 1);
		if (!dropTail)
			cloned.Add(tailTransform(tailExpr));
		return cloned;
	}

	private static string? ExpressionTargetName(ExpressionSyntax expr) =>
		expr is IdentifierExpressionSyntax id ? id.Name : null;

	// node factories
	private static IdentifierExpressionSyntax Identifier(string name) => new(new TextSpan(), name);

	private static IdentifierExpressionSyntax Identifier(TextSpan span, string name) => new(span, name);

	private static MemberAccessExpressionSyntax MemberField(string baseName, string fieldName)
	{
		var span = new TextSpan();
		return new MemberAccessExpressionSyntax(span, new IdentifierExpressionSyntax(span, baseName), fieldName);
	}

	private static ExpressionStatementSyntax AssignStatement(TextSpan span, string target, ExpressionSyntax value) =>
		new(span, new BinaryExpressionSyntax(span, new IdentifierExpressionSyntax(span, target), "=", value));

	private string Fresh(string prefix) => $"{prefix}{_tempCounter++}";

	private void Report(TextSpan span, string message, string diagnosticId)
	{
		_diagnostics.Report(_fileContext, span, message, diagnosticId);
	}

	private sealed record ClausePattern(
		CatchClauseSyntax Clause,
		string? ErrorType,
		string? Variant,
		string? Binding,
		bool IsBare,
		bool Reachable);
}
