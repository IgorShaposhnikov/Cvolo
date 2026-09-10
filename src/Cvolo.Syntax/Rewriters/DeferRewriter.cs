using Cvolo.Core.AST;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using Cvolo.Core.AST.Expressions;
using Cvolo.Core.AST.Statements;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Syntax.Rewriters;

/// <summary>
/// Lowers every <c>defer</c> statement into explicit LIFO splice sites. The rewriter walks
/// function bodies top-down with a live scope registry stack, so any control transfer
/// (return, break, continue, labeled multi-level jumps) splices exactly the defers of the
/// scopes it actually leaves — nothing more, nothing less.
/// </summary>
/// <remarks>
/// Semantics:
/// — A block's own defers fire in reverse registration order when the block exits naturally
///   or via any terminator inside it.
/// — <c>return</c> fires every registry from the jump site up to the function-body root.
/// — An unlabeled <c>break</c> inside a switch case fires every registry from the jump site
///   up to the switch's case scope (C-style: it exits the switch, not a surrounding loop).
/// — An unlabeled <c>break</c>/<c>continue</c> inside a loop body fires every registry from
///   the jump site up to and including the nearest enclosing loop's body scope.
/// — A labeled <c>break(label)</c>/<c>continue(label)</c> fires every registry from the jump
///   site up to and including the targeted loop's body scope (the spec's cross-iteration
///   cleanups: innermost mutation buffers first, then the parent row scope).
/// Registered actions are cloned into the splice sites; the natural fall-through path of a
/// crossed scope never runs after a jump, so nothing can fire twice.
/// </remarks>
public sealed class DeferRewriter(DiagnosticBag diagnostics, CompilationContext fileContext) : AstRewriterBase
{
	// Active block-scope registries, innermost last (top of the "stack").
	private readonly List<ScopeRegistry> _registryStack = [];

	// Enclosing loop contexts used for break/continue defer splicing.
	private readonly Stack<LoopContext> _loopStack = [];

	// Enclosing switch splice bounds: each entry is the registry index the switch's case
	// bodies occupy. `break;` inside a switch case splices the case-local defers but leaves
	// the switch's enclosing block (and any outer loop) untouched.
	private readonly Stack<int> _switchFrames = [];

	private readonly DiagnosticBag _diagnostics = diagnostics;
	private readonly CompilationContext _fileContext = fileContext;

	private sealed class ScopeRegistry
	{
		public List<SyntaxNode> Defers { get; } = [];
	}

	private sealed class LoopContext(string? label, int bodyRegistryIndex)
	{
		public string? Label { get; } = label;

		// Index into (_registryStack) of the registry that forms this loop's body;
		// break/continue splice registries from the jump site down through this scope.
		public int BodyRegistryIndex { get; } = bodyRegistryIndex;
	}

	public override SyntaxNode Rewrite(SyntaxNode node)
	{
		if (node is BlockStatementSyntax block)
			return RewriteBlock(block);

		if (node is ForStatementSyntax forStmt)
		{
			var ctx = new LoopContext(forStmt.Label, _registryStack.Count);
			_loopStack.Push(ctx);
			var body = RewriteBranch(forStmt.Body);
			_loopStack.Pop();
			var init = (VariableDeclarationSyntax)Rewrite(forStmt.Initializer);
			var cond = (ExpressionSyntax)Rewrite(forStmt.Condition);
			var inc = (ExpressionSyntax)Rewrite(forStmt.Increment);
			return new ForStatementSyntax(forStmt.Span, init, cond, inc, body, forStmt.Label);
		}

		if (node is WhileStatementSyntax whileStmt)
		{
			var ctx = new LoopContext(whileStmt.Label, _registryStack.Count);
			_loopStack.Push(ctx);
			var body = RewriteBranch(whileStmt.Body);
			_loopStack.Pop();
			var cond = (ExpressionSyntax)Rewrite(whileStmt.Condition);
			return new WhileStatementSyntax(whileStmt.Span, cond, body, whileStmt.Label);
		}

		if (node is IfStatementSyntax ifStmt)
		{
			var cond = (ExpressionSyntax)Rewrite(ifStmt.Condition);
			var then = RewriteBranch(ifStmt.ThenStatement);
			var elseClause = ifStmt.ElseClause != null
				? new ElseClauseSyntax(ifStmt.ElseClause.Span, (BlockStatementSyntax)RewriteBranch(ifStmt.ElseClause.Body))
				: null;
			return new IfStatementSyntax(ifStmt.Span, cond, then, elseClause);
		}

		if (node is SwitchStatementSyntax sw)
		{
			var expr = (ExpressionSyntax)Rewrite(sw.Expression);

			// Case bodies form a distinct registry layer: their defers fire on natural
			// exit or on a case-local break, but a break never runs the defers of the
			// switch's enclosing block.
			_switchFrames.Push(_registryStack.Count);
			var cases = new List<SwitchCaseSyntax>();
			try
			{
				foreach (var c in sw.Cases)
				{
					cases.Add(new SwitchCaseSyntax(c.Span, c.VariantName, c.VariableName, c.IsDefault,
						RewriteSwitchCaseBody(c.Body, c.Span)));
				}
			}
			finally
			{
				_switchFrames.Pop();
			}

			return new SwitchStatementSyntax(sw.Span, expr, cases);
		}

		return base.Rewrite(node);
	}

	/// <summary>
	/// Rewrites a switch case body (a bare statement list) as a block scope so its defers
	/// are registered and spliced exactly like any other scope.
	/// </summary>
	private IReadOnlyList<SyntaxNode> RewriteSwitchCaseBody(IReadOnlyList<SyntaxNode>? body, TextSpan span)
	{
		if (body is null || body.Count == 0)
			return [];

		return RewriteBlock(new BlockStatementSyntax(span, body.ToList())).Statements;
	}

	/// <summary>
	/// Rewrites a loop body or if-branch, normalizing non-block single statements into a
	/// block so terminators nested inside them still get defer splicing.
	/// </summary>
	private SyntaxNode RewriteBranch(SyntaxNode body)
	{
		var rewritten = Rewrite(body);
		if (rewritten is BlockStatementSyntax block)
			return block;

		return RewriteBlock(new BlockStatementSyntax(rewritten.Span, [rewritten]));
	}

	private BlockStatementSyntax RewriteBlock(BlockStatementSyntax block)
	{
		var registry = new ScopeRegistry();
		_registryStack.Add(registry);
		var result = new List<SyntaxNode>();

		try
		{
			foreach (var rawStmt in block.Statements)
			{
				var stmt = Rewrite(rawStmt);

				if (stmt is DeferStatementSyntax defer)
				{
					var body = Rewrite(defer.Body);
					if (defer.Label is { } label)
					{
						var target = FindLoopContext(label);
						if (target is null)
						{
							Report(defer.Span,
								$"Labeled branch target '{label}' could not be resolved inside the active iteration scope ancestry chain.",
								DiagnosticIds.LabeledBranchTargetNotFound);
							registry.Defers.Add(body);
						}
						else
						{
							// CVL1071: a targeted defer may only anchor to the innermost active
							// loop — any nested loop between the defer and its target would
							// re-register an outer cleanup on every inner iteration.
							if (target != _loopStack.Peek())
							{
								Report(defer.Span,
									$"Targeted defer expression '{label}' violates the cross-iteration loop barrier. Cannot target an outer loop structure across internal nested loops.",
									DiagnosticIds.CrossIterationDeferBarrier);
							}

							// Anchor the body to the targeted loop's body scope: it fires when
							// that scope exits (via fall-through or any jump out of it), never
							// when an intermediate bare block is left.
							_registryStack[target.BodyRegistryIndex].Defers.Add(body);
						}
					}
					else
					{
						registry.Defers.Add(body);
					}

					continue;
				}

				if (stmt is ReturnStatementSyntax ret)
				{
					// A return exits every scope up to the function body root (index 0).
					result.AddRange(SpliceDefersForExit(ret, 0));
					continue;
				}

				if (stmt is BreakStatementSyntax or ContinueStatementSyntax)
				{
					result.AddRange(SpliceControlExit(stmt));
					continue;
				}

				result.Add(stmt);
			}
		}
		finally
		{
			_registryStack.RemoveAt(_registryStack.Count - 1);
		}

		// Natural fall-through: run this block's own defers in reverse order. Skipped when the
		// block definitively ends in a control transfer (its fall-through is unreachable).
		if (registry.Defers.Count > 0 && (result.Count == 0 || !EndsInTerminator(result[^1])))
		{
			var defers = registry.Defers.ToList();
			defers.Reverse();
			result.Add(new BlockStatementSyntax(block.Span, defers.Select(CloneNode).ToList()));
		}

		return new BlockStatementSyntax(block.Span, result);
	}

	private List<SyntaxNode> SpliceControlExit(SyntaxNode stmt)
	{
		// An unlabeled break exits the innermost switch case (C-style); a continue always
		// targets a loop. Labeled jumps target the matching loop in the ancestor chain. If
		// no target exists (a binder error — CVL1063/CVL1070) emit the raw jump so the
		// pipeline still completes for analysis.
		if (stmt is BreakStatementSyntax { Label: null })
		{
			if (_switchFrames.Count > 0)
				return SpliceDefersForExit(stmt, _switchFrames.Peek());

			return _loopStack.Count > 0 ? SpliceDefersForExit(stmt, _loopStack.Peek().BodyRegistryIndex) : [stmt];
		}

		if (stmt is ContinueStatementSyntax { Label: null })
			return _loopStack.Count > 0 ? SpliceDefersForExit(stmt, _loopStack.Peek().BodyRegistryIndex) : [stmt];

		if (stmt is BreakStatementSyntax { Label: { } breakLabel })
			return SpliceToLoop(stmt, FindLoopContext(breakLabel));

		if (stmt is ContinueStatementSyntax { Label: { } continueLabel })
			return SpliceToLoop(stmt, FindLoopContext(continueLabel));

		return [stmt];
	}

	private List<SyntaxNode> SpliceToLoop(SyntaxNode stmt, LoopContext? target)
	{
		return target is null ? [stmt] : SpliceDefersForExit(stmt, target.BodyRegistryIndex);
	}

	private void Report(TextSpan span, string message, string diagnosticId)
	{
		_diagnostics.Report(_fileContext, span, message, diagnosticId);
	}

	private LoopContext? FindLoopContext(string label)
	{
		foreach (var ctx in _loopStack)
		{
			if (ctx.Label == label)
				return ctx;
		}

		return null;
	}

	/// <summary>
	/// Collects, in LIFO order, the defers of every active registry from the innermost scope
	/// down through <paramref name="minRegistryIndex"/>, and returns them as clones followed by
	/// <paramref name="exitStmt"/> (flat — the terminator stays a direct statement so flow
	/// analysis and the emitter can still see the transfer).
	/// </summary>
	private List<SyntaxNode> SpliceDefersForExit(SyntaxNode exitStmt, int minRegistryIndex)
	{
		var spliced = new List<SyntaxNode>();
		for (var i = _registryStack.Count - 1; i >= minRegistryIndex; i--)
		{
			var defers = _registryStack[i].Defers;
			if (defers.Count == 0)
				continue;

			var reversed = defers.ToList();
			reversed.Reverse();
			spliced.AddRange(reversed.Select(CloneNode));
		}

		spliced.Add(exitStmt);
		return spliced;
	}

	private static bool EndsInTerminator(SyntaxNode stmt)
	{
		return stmt switch
		{
			ReturnStatementSyntax or BreakStatementSyntax or ContinueStatementSyntax => true,
			BlockStatementSyntax b when b.Statements.Count > 0 => EndsInTerminator(b.Statements[^1]),
			UnsafeBlockStatementSyntax ub => EndsInTerminator(ub.Body),
			_ => false,
		};
	}

	private SyntaxNode CloneNode(SyntaxNode node)
	{
		return node switch
		{
			ExpressionStatementSyntax exprStmt => new ExpressionStatementSyntax(exprStmt.Span, CloneExpression(exprStmt.Expression)),
			ReturnStatementSyntax ret => new ReturnStatementSyntax(ret.Span, ret.Expression != null ? CloneExpression(ret.Expression) : null),
			BlockStatementSyntax block => new BlockStatementSyntax(block.Span, block.Statements.Select(CloneNode).ToList()),
			IfStatementSyntax ifStmt => CloneIf(ifStmt),
			WhileStatementSyntax whileStmt => new WhileStatementSyntax(whileStmt.Span, CloneExpression(whileStmt.Condition), CloneNode(whileStmt.Body), whileStmt.Label),
			ForStatementSyntax forStmt => new ForStatementSyntax(forStmt.Span, CloneVarDecl(forStmt.Initializer), CloneExpression(forStmt.Condition), CloneExpression(forStmt.Increment), CloneNode(forStmt.Body), forStmt.Label),
			UnsafeBlockStatementSyntax unsafeBlock => new UnsafeBlockStatementSyntax(unsafeBlock.Span, (BlockStatementSyntax)CloneNode(unsafeBlock.Body)),
			SwitchStatementSyntax sw => CloneSwitch(sw),
			VariableDeclarationSyntax varDecl => CloneVarDecl(varDecl),
			BreakStatementSyntax brk => new BreakStatementSyntax(brk.Span, brk.Label),
			ContinueStatementSyntax cont => new ContinueStatementSyntax(cont.Span, cont.Label),
			_ => node,
		};
	}

	private IfStatementSyntax CloneIf(IfStatementSyntax ifStmt)
	{
		var then = CloneNode(ifStmt.ThenStatement);
		var elseClause = ifStmt.ElseClause != null
			? new ElseClauseSyntax(ifStmt.ElseClause.Span, (BlockStatementSyntax)CloneNode(ifStmt.ElseClause.Body))
			: null;
		return new IfStatementSyntax(ifStmt.Span, CloneExpression(ifStmt.Condition), then, elseClause);
	}

	private SwitchStatementSyntax CloneSwitch(SwitchStatementSyntax sw)
	{
		var cases = sw.Cases.Select(c =>
		{
			var stmts = c.Body.Select(CloneNode).ToList();
			return new SwitchCaseSyntax(c.Span, c.VariantName, c.VariableName, c.IsDefault, stmts);
		}).ToList();
		return new SwitchStatementSyntax(sw.Span, sw.Expression, cases);
	}

	private VariableDeclarationSyntax CloneVarDecl(VariableDeclarationSyntax varDecl)
	{
		return new VariableDeclarationSyntax(varDecl.Span, varDecl.IsMutable, varDecl.Type, varDecl.Name,
			varDecl.Initializer != null ? CloneExpression(varDecl.Initializer) : null);
	}

	private MemberInitializerSyntax CloneMemberInit(MemberInitializerSyntax init)
	{
		return new MemberInitializerSyntax(init.Span, init.MemberName, CloneExpression(init.Expression));
	}

	private ExpressionSyntax CloneExpression(ExpressionSyntax expr)
	{
		return expr switch
		{
			CallExpressionSyntax call => new CallExpressionSyntax(call.Span, call.FunctionName, call.TypeArguments,
				call.Arguments.Select(CloneExpression).Cast<ExpressionSyntax>().ToList()),
			BinaryExpressionSyntax bin => new BinaryExpressionSyntax(bin.Span, CloneExpression(bin.Left), bin.Operator, CloneExpression(bin.Right)),
			UnaryExpressionSyntax unary => new UnaryExpressionSyntax(unary.Span, unary.Operator, CloneExpression(unary.Operand)),
			IntegerLiteralExpressionSyntax lit => new IntegerLiteralExpressionSyntax(lit.Span, lit.Value, lit.LiteralType),
			DoubleLiteralExpressionSyntax lit => new DoubleLiteralExpressionSyntax(lit.Span, lit.Value, lit.IsFloat),
			BooleanLiteralExpressionSyntax lit => new BooleanLiteralExpressionSyntax(lit.Span, lit.Value),
			StringLiteralExpressionSyntax lit => new StringLiteralExpressionSyntax(lit.Span, lit.Value),
			CharacterLiteralExpressionSyntax lit => new CharacterLiteralExpressionSyntax(lit.Span, lit.Value),
			NullLiteralExpressionSyntax lit => new NullLiteralExpressionSyntax(lit.Span),
			VoidLiteralExpressionSyntax lit => new VoidLiteralExpressionSyntax(lit.Span),
			IdentifierExpressionSyntax id => new IdentifierExpressionSyntax(id.Span, id.Name),
			MemberAccessExpressionSyntax member => new MemberAccessExpressionSyntax(member.Span, CloneExpression(member.Expression), member.MemberName),
			IndexExpressionSyntax index => new IndexExpressionSyntax(index.Span, CloneExpression(index.Left), CloneExpression(index.Index)),
			BorrowExpressionSyntax borrow => new BorrowExpressionSyntax(borrow.Span, CloneExpression(borrow.Expression), borrow.IsMutable),
			HeapAllocationExpressionSyntax heap => new HeapAllocationExpressionSyntax(heap.Span, CloneExpression(heap.Expression)),
			HeapArrayAllocationExpressionSyntax heapArr => new HeapArrayAllocationExpressionSyntax(heapArr.Span, heapArr.ElementTypeName, CloneExpression(heapArr.CountExpression)),
			ArrayInitializationExpressionSyntax arrInit => new ArrayInitializationExpressionSyntax(arrInit.Span, arrInit.Elements.Select(CloneExpression).Cast<ExpressionSyntax>().ToList()),
			ArrayReplicationExpressionSyntax arrRep => new ArrayReplicationExpressionSyntax(arrRep.Span, CloneExpression(arrRep.Value), CloneExpression(arrRep.Count)),
			StructInitializationExpressionSyntax structInit => new StructInitializationExpressionSyntax(structInit.Span, structInit.StructTypeName, structInit.Initializers.Select(CloneMemberInit).ToList()),
			ParenthesizedStructInitializerExpressionSyntax parenInit => new ParenthesizedStructInitializerExpressionSyntax(parenInit.Span, parenInit.Initializers.Select(CloneMemberInit).ToList()),
			TernaryExpressionSyntax ternary => new TernaryExpressionSyntax(ternary.Span, CloneExpression(ternary.Condition), CloneExpression(ternary.ThenExpression), CloneExpression(ternary.ElseExpression)),
			InterpolatedStringExpressionSyntax interp => new InterpolatedStringExpressionSyntax(interp.Span, interp.RawText),
			IsPatternExpressionSyntax isPat => new IsPatternExpressionSyntax(isPat.Span, CloneExpression(isPat.Operand), isPat.VariantName, isPat.BoundName),
			DefaultExpressionSyntax def => new DefaultExpressionSyntax(def.Span, def.TypeName),
			_ => expr,
		};
	}
}
