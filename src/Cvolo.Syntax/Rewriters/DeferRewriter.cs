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
///   or via any terminator inside it. A loop body is one such block, so its defers fire at
///   the end of every iteration.
/// — <c>defer label { ... }</c> anchors the action to the nearest enclosing block labeled
///   <c>label</c> (blocks and labeled loops alike); it fires when that scope exits.
/// — Free variables referenced by a defer body are captured by value at registration time:
///   a <c>val __defer_cap_N = x;</c> binding is emitted at the defer's original position and
///   the body reads the snapshot.
/// — <c>return</c> fires every registry from the jump site up to the function-body root. A
///   non-trivial return expression is materialized into a <c>val __defer_ret_N</c> temp so
///   the defers cannot observe it mid-evaluation.
/// — An unlabeled <c>break</c> inside a switch case fires every registry from the jump site
///   up to the switch's case scope (C-style: it exits the switch, not a surrounding loop).
/// — An unlabeled <c>break</c>/<c>continue</c> inside a loop body fires every registry from
///   the jump site up to and including the nearest enclosing loop's body scope.
/// — <c>break label;</c> fires every registry from the jump site up to and including the
///   targeted block/loop's body scope (LIFO), then transfers past the label.
/// Registered actions are cloned into the splice sites; the natural fall-through path of a
/// crossed scope never runs after a jump, so nothing can fire twice. The pass is idempotent.
/// </remarks>
public sealed class DeferRewriter(DiagnosticBag diagnostics, CompilationContext fileContext) : AstRewriterBase
{
	// Active block-scope registries, innermost last (top of the "stack"). Loop bodies and
	// labeled blocks carry their label here so label resolution is one lexical walk.
	private readonly List<ScopeRegistry> _registryStack = [];

	// Enclosing loop contexts used for break/continue defer splicing.
	private readonly Stack<LoopContext> _loopStack = [];

	// Enclosing switch splice bounds: each entry is the registry index the switch's case
	// bodies occupy. `break;` inside a switch case splices the case-local defers but leaves
	// the switch's enclosing block (and any outer loop) untouched.
	private readonly Stack<int> _switchFrames = [];

	private readonly DiagnosticBag _diagnostics = diagnostics;
	private readonly CompilationContext _fileContext = fileContext;

	private int _tempCounter;

	private sealed class ScopeRegistry
	{
		public string? Label { get; set; }

		public bool IsLoopBody { get; set; }

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
		if (node is LabeledBlockStatementSyntax labeledBlock)
			return new LabeledBlockStatementSyntax(labeledBlock.Span, labeledBlock.Label, RewriteBlock(labeledBlock.Body, labeledBlock.Label));

		if (node is BlockStatementSyntax block)
			return RewriteBlock(block);

		if (node is ForStatementSyntax forStmt)
		{
			var ctx = new LoopContext(forStmt.Label, _registryStack.Count);
			_loopStack.Push(ctx);
			var body = RewriteBranch(forStmt.Body, forStmt.Label, isLoopBody: true);
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
			var body = RewriteBranch(whileStmt.Body, whileStmt.Label, isLoopBody: true);
			_loopStack.Pop();
			var cond = (ExpressionSyntax)Rewrite(whileStmt.Condition);
			return new WhileStatementSyntax(whileStmt.Span, cond, body, whileStmt.Label);
		}

		if (node is ForEachStatementSyntax forEach)
		{
			var ctx = new LoopContext(forEach.Label, _registryStack.Count);
			_loopStack.Push(ctx);
			var body = RewriteBranch(forEach.Body, forEach.Label, isLoopBody: true);
			_loopStack.Pop();
			var collection = (ExpressionSyntax)Rewrite(forEach.Collection);
			return new ForEachStatementSyntax(forEach.Span, forEach.BindingKind,
				forEach.ExplicitItemType, forEach.ItemName, collection,
				(BlockStatementSyntax)body, forEach.Label);
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

		if (node is DeferStatementSyntax)
		{
			// A defer appearing where a single statement is expected (e.g. `if (x) defer f();`):
			// process it inside its own scope so capture bindings and splicing behave normally.
			var blk = RewriteBlock(new BlockStatementSyntax(node.Span, [node]));
			return blk.Statements.Count == 1 ? blk.Statements[0] : blk;
		}

		if (node is ReturnStatementSyntax ret)
		{
			var spliced = SpliceReturn(ret);
			return spliced.Count == 1 ? spliced[0] : WrapSpliced(ret.Span, spliced);
		}

		if (node is BreakStatementSyntax or ContinueStatementSyntax)
		{
			var spliced = SpliceControlExit(node);
			return spliced.Count == 1 ? spliced[0] : WrapSpliced(node.Span, spliced);
		}

		return base.Rewrite(node);
	}

	/// <summary>
	/// Produces a block statement carrying a multi-node splice (e.g. capture bindings or
	/// deferred clones followed by a terminator) so a single-statement position can hold it.
	/// </summary>
	private static SyntaxNode WrapSpliced(TextSpan span, List<SyntaxNode> statements) =>
		new BlockStatementSyntax(span, statements);

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
	private BlockStatementSyntax RewriteBranch(SyntaxNode body, string? label = null, bool isLoopBody = false)
	{
		if (body is BlockStatementSyntax block)
			return RewriteBlock(block, label, isLoopBody);

		return RewriteBlock(new BlockStatementSyntax(body.Span, [body]), label, isLoopBody);
	}

	private BlockStatementSyntax RewriteBlock(BlockStatementSyntax block, string? label = null, bool isLoopBody = false)
	{
		var registry = new ScopeRegistry
		{
			Label = label,
			IsLoopBody = isLoopBody,
		};
		_registryStack.Add(registry);
		var result = new List<SyntaxNode>();

		try
		{
			foreach (var rawStmt in block.Statements)
			{
				// Defer is consumed in-place: it registers the action into a scope and emits
				// its capture bindings into this block at the position where it appeared.
				if (rawStmt is DeferStatementSyntax defer)
				{
					ProcessDefer(defer, registry, result);
					continue;
				}

				// Returns and control-exits that are direct statements are spliced flat into
				// this block (not wrapped), so terminal-position checks like `EndsWithReturn`
				// keep seeing the terminator as the block's last statement.
				if (rawStmt is ReturnStatementSyntax ret)
				{
					result.AddRange(SpliceReturn(ret));
					continue;
				}

				if (rawStmt is BreakStatementSyntax or ContinueStatementSyntax)
				{
					result.AddRange(SpliceControlExit(rawStmt));
					continue;
				}

				var stmt = Rewrite(rawStmt);
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

	/// <summary>
	/// Registers the body of a debugger <c>defer</c> statement: validates the allowed body
	/// forms, captures free variables by value, and anchors the result to the current block
	/// (unlabeled) or the nearest block/loop carrying the target label.
	/// </summary>
	private void ProcessDefer(DeferStatementSyntax defer, ScopeRegistry currentRegistry, List<SyntaxNode> result)
	{
		var anchorIndex = _registryStack.Count - 1;
		if (defer.TargetLabel is { } label)
		{
			var resolved = ResolveLabelIndex(label);
			if (resolved is null)
			{
				Report(defer.Span,
					$"`defer {label} {{ ... }}` refers to a label `{label}` not in scope.",
					DiagnosticIds.LabelNotFoundInScope);
			}
			else
			{
				anchorIndex = resolved.Value;
			}
		}

		// The synthesized `finally` defer is compiler-internal: its body is the already-lowered
		// finally block (nested try/catch/finally carry internal `break __try_N;` jumps and
		// nested cleanup defers that a source-body walk would misreport). Control-flow
		// restrictions were already validated against the raw source body by TryCatchRewriter.
		if (!defer.IsLexicalCapture)
			ValidateDeferBody(defer.Body);

		// Capture free variables by value. The `val __defer_cap_N = x;` bindings are emitted
		// at the defer's original position (they run at registration time); the body reads
		// the snapshots, so later mutations of the originals are invisible to the action.
		// The synthesized `finally` defer opts out (spec §4.4): it keeps plain lexical
		// references and observes the bindings' live state when the cleanup actually runs.
		SyntaxNode substitutedBody;
		if (defer.IsLexicalCapture)
		{
			substitutedBody = defer.Body;
		}
		else
		{
			var captureRewriter = new CaptureRewriter(declaredNames: CollectDeclaredNames(defer.Body),
				nextCaptureName: () => $"__defer_cap_{_tempCounter++}", captureDecls: []);
			substitutedBody = (SyntaxNode)captureRewriter.Rewrite(defer.Body);
			result.AddRange(captureRewriter.CaptureDeclarations);
		}

		_registryStack[anchorIndex].Defers.Add(substitutedBody);
	}

	private void ValidateDeferBody(SyntaxNode body)
	{
		switch (body)
		{
			case ReturnStatementSyntax ret:
				Report(ret.Span, "Control flow cannot leave a `defer` body.", DiagnosticIds.DeferControlFlowLeak);
				break;
			case BreakStatementSyntax brk:
			case ContinueStatementSyntax cont:
				Report(body.Span, "Control flow cannot leave a `defer` body.", DiagnosticIds.DeferControlFlowLeak);
				break;
			case DeferStatementSyntax nestedDefer:
				Report(body.Span, "`defer` cannot be nested inside another `defer`.", DiagnosticIds.NestedDefer);
				ValidateDeferBody(nestedDefer.Body);
				break;
			case BlockStatementSyntax block:
				foreach (var child in block.Statements)
					ValidateDeferBody(child);
				break;
			case IfStatementSyntax ifStmt:
				ValidateDeferBody(ifStmt.ThenStatement);
				if (ifStmt.ElseClause is { } elseClause)
					ValidateDeferBody(elseClause.Body);
				break;
			case WhileStatementSyntax whileStmt:
				ValidateDeferBody(whileStmt.Body);
				break;
			case ForStatementSyntax forStmt:
				ValidateDeferBody(forStmt.Body);
				break;
			case ForEachStatementSyntax forEach:
				ValidateDeferBody(forEach.Body);
				break;
			case UnsafeBlockStatementSyntax unsafeBlock:
				ValidateDeferBody(unsafeBlock.Body);
				break;
			case SwitchStatementSyntax sw:
				foreach (var c in sw.Cases)
				{
					foreach (var child in c.Body)
						ValidateDeferBody(child);
				}
				break;
			case LabeledBlockStatementSyntax labeled:
				ValidateDeferBody(labeled.Body);
				break;
		}
	}

	private static HashSet<string> CollectDeclaredNames(SyntaxNode body)
	{
		var declared = new HashSet<string>();
		CollectDeclaredNames(body, declared);
		return declared;
	}

	private static void CollectDeclaredNames(SyntaxNode node, HashSet<string> declared)
	{
		switch (node)
		{
			case VariableDeclarationSyntax varDecl:
				declared.Add(varDecl.Name);
				break;
			case ForEachStatementSyntax forEach when forEach.ItemName is { } itemName:
				declared.Add(itemName);
				break;
			case IsPatternExpressionSyntax isPattern when isPattern.BoundName is { } boundName:
				declared.Add(boundName);
				break;
			case SwitchCaseSyntax switchCase when switchCase.VariableName is { } caseVar:
				declared.Add(caseVar);
				break;
			case BlockStatementSyntax block:
				foreach (var child in block.Statements)
					CollectDeclaredNames(child, declared);
				break;
			case IfStatementSyntax ifStmt:
				CollectDeclaredNames(ifStmt.ThenStatement, declared);
				if (ifStmt.ElseClause is { } elseClause)
					CollectDeclaredNames(elseClause.Body, declared);
				break;
			case WhileStatementSyntax whileStmt:
				CollectDeclaredNames(whileStmt.Body, declared);
				break;
			case ForStatementSyntax forStmt:
				CollectDeclaredNames(forStmt.Body, declared);
				break;
			case ForEachStatementSyntax forEach:
				CollectDeclaredNames(forEach.Body, declared);
				break;
			case UnsafeBlockStatementSyntax unsafeBlock:
				CollectDeclaredNames(unsafeBlock.Body, declared);
				break;
			case SwitchStatementSyntax sw:
				foreach (var c in sw.Cases)
				{
					CollectDeclaredNames(c, declared);
					foreach (var child in c.Body)
						CollectDeclaredNames(child, declared);
				}
				break;
			case LabeledBlockStatementSyntax labeled:
				CollectDeclaredNames(labeled.Body, declared);
				break;
			case ReturnStatementSyntax ret when ret.Expression != null:
				CollectDeclaredNames(ret.Expression, declared);
				break;
			case ExpressionStatementSyntax exprStmt:
				CollectDeclaredNames(exprStmt.Expression, declared);
				break;
			case CallExpressionSyntax call:
				foreach (var arg in call.Arguments)
					CollectDeclaredNames(arg, declared);
				break;
			case BinaryExpressionSyntax bin:
				CollectDeclaredNames(bin.Left, declared);
				CollectDeclaredNames(bin.Right, declared);
				break;
			case TernaryExpressionSyntax ternary:
				CollectDeclaredNames(ternary.Condition, declared);
				CollectDeclaredNames(ternary.ThenExpression, declared);
				CollectDeclaredNames(ternary.ElseExpression, declared);
				break;
			case StructInitializationExpressionSyntax structInit:
				foreach (var init in structInit.Initializers)
					CollectDeclaredNames(init.Expression, declared);
				break;
			case ParenthesizedStructInitializerExpressionSyntax parenInit:
				foreach (var init in parenInit.Initializers)
					CollectDeclaredNames(init.Expression, declared);
				break;
			case ArrayInitializationExpressionSyntax arrInit:
				foreach (var element in arrInit.Elements)
					CollectDeclaredNames(element, declared);
				break;
		}
	}

	private List<SyntaxNode> SpliceControlExit(SyntaxNode stmt)
	{
		// An unlabeled break exits the innermost switch case (C-style); a continue always
		// targets a loop. Labeled jumps target the matching scope in the ancestor chain. If
		// no target exists (a binder error — CVL1061/CVL1070) emit the raw jump so the
		// pipeline still completes for analysis.
		if (stmt is BreakStatementSyntax { TargetLabel: null })
		{
			if (_switchFrames.Count > 0)
				return SpliceDefersForExit(stmt, _switchFrames.Peek());

			return _loopStack.Count > 0 ? SpliceDefersForExit(stmt, _loopStack.Peek().BodyRegistryIndex) : [stmt];
		}

		if (stmt is ContinueStatementSyntax { Label: null })
			return _loopStack.Count > 0 ? SpliceDefersForExit(stmt, _loopStack.Peek().BodyRegistryIndex) : [stmt];

		if (stmt is BreakStatementSyntax { TargetLabel: { } breakLabel })
		{
			var target = ResolveLabelIndex(breakLabel);
			return target is not null ? SpliceDefersForExit(stmt, target.Value) : [stmt];
		}

		if (stmt is ContinueStatementSyntax { Label: { } continueLabel })
		{
			// `continue` may only target a loop body; a labeled block is not iterable.
			var target = ResolveLoopLabelIndex(continueLabel);
			return target is not null ? SpliceDefersForExit(stmt, target.Value) : [stmt];
		}

		return [stmt];
	}

	/// <summary>
	/// Binds a non-trivial return expression into a temp so deferred actions run strictly
	/// after the value is produced, then builds [temp decl, defers (LIFO), return temp].
	/// Void, identifier, and literal-null returns need no temp.
	/// </summary>
	private List<SyntaxNode> SpliceReturn(ReturnStatementSyntax ret)
	{
		// Fast path: with no deferred actions registered anywhere in the ancestry the
		// return statement must survive byte-for-byte (no temp binding, no reordering),
		// so a source without `defer` is a strict syntactic no-op.
		var anyDefers = _registryStack.Any(static s => s.Defers.Count > 0);
		if (!anyDefers)
			return [ret];

		var spliced = new List<SyntaxNode>();
		var preDecls = new List<SyntaxNode>();
		ReturnStatementSyntax exit = ret;

		if (ret.Expression is not null
			&& ret.Expression is not IdentifierExpressionSyntax
			&& ret.Expression is not NullLiteralExpressionSyntax
			&& ret.Expression is not VoidLiteralExpressionSyntax)
		{
			var exprSpan = ret.Expression.Span;
			var tempName = $"__defer_ret_{_tempCounter++}";
			preDecls.Add(new VariableDeclarationSyntax(exprSpan, false, null, tempName, ret.Expression));
			exit = new ReturnStatementSyntax(ret.Span, new IdentifierExpressionSyntax(exprSpan, tempName));
		}

		// LIFO: innermost scope first, down to the function-body root (index 0).
		for (var i = _registryStack.Count - 1; i >= 0; i--)
		{
			var defers = _registryStack[i].Defers;
			if (defers.Count == 0)
				continue;

			var reversed = defers.ToList();
			reversed.Reverse();
			spliced.AddRange(reversed.Select(CloneNode));
		}

		spliced.Add(exit);

		if (preDecls.Count > 0)
		{
			preDecls.AddRange(spliced);
			return preDecls;
		}

		return spliced;
	}

	private void Report(TextSpan span, string message, string diagnosticId)
	{
		_diagnostics.Report(_fileContext, span, message, diagnosticId);
	}

	/// <summary>
	/// Returns the registry index (innermost-first) whose label matches, or null. Blocks,
	/// labeled blocks, and loop bodies all contribute; the first hit walking the stack from
	/// the top is the lexically nearest scope.
	/// </summary>
	private int? ResolveLabelIndex(string label)
	{
		for (var i = _registryStack.Count - 1; i >= 0; i--)
		{
			if (_registryStack[i].Label == label)
				return i;
		}

		return null;
	}

	/// <summary>
	/// Like <see cref="ResolveLabelIndex"/> but only matches loop-body registries, for
	/// <c>continue label;</c> which can never target a labeled block.
	/// </summary>
	private int? ResolveLoopLabelIndex(string label)
	{
		for (var i = _registryStack.Count - 1; i >= 0; i--)
		{
			if (_registryStack[i] is { Label: var registryLabel, IsLoopBody: true } && registryLabel == label)
				return i;
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
			LabeledBlockStatementSyntax lb when lb.Body.Statements.Count > 0 => EndsInTerminator(lb.Body.Statements[^1]),
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
			ForEachStatementSyntax forEach => new ForEachStatementSyntax(forEach.Span, forEach.BindingKind, forEach.ExplicitItemType, forEach.ItemName, CloneExpression(forEach.Collection), (BlockStatementSyntax)CloneNode(forEach.Body), forEach.Label),
			UnsafeBlockStatementSyntax unsafeBlock => new UnsafeBlockStatementSyntax(unsafeBlock.Span, (BlockStatementSyntax)CloneNode(unsafeBlock.Body)),
			SwitchStatementSyntax sw => CloneSwitch(sw),
			VariableDeclarationSyntax varDecl => CloneVarDecl(varDecl),
			BreakStatementSyntax brk => new BreakStatementSyntax(brk.Span, brk.TargetLabel),
			ContinueStatementSyntax cont => new ContinueStatementSyntax(cont.Span, cont.Label),
			LabeledBlockStatementSyntax labeledBlock => new LabeledBlockStatementSyntax(labeledBlock.Span, labeledBlock.Label, (BlockStatementSyntax)CloneNode(labeledBlock.Body)),
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

	/// <summary>
	/// Walks a defer body substituting value-position identifiers with capture snapshots.
	/// Identifiers declared inside the body are left alone; identifiers in type/symbol
	/// positions (call callee names, member names, struct/type names, nameof/typeof/default,
	/// interpolation holes) are not captured. Recomposes the expression nodes that
	/// <see cref="AstRewriterBase"/> does not itself walk.
	/// </summary>
	private sealed class CaptureRewriter(
		HashSet<string> declaredNames,
		Func<string> nextCaptureName,
		List<VariableDeclarationSyntax> captureDecls) : AstRewriterBase
	{
		private readonly Dictionary<string, string> _substituted = [];

		public IReadOnlyList<VariableDeclarationSyntax> CaptureDeclarations => captureDecls;

		public override SyntaxNode Rewrite(SyntaxNode node)
		{
			switch (node)
			{
				case IdentifierExpressionSyntax id when !declaredNames.Contains(id.Name):
					if (!_substituted.TryGetValue(id.Name, out var temp))
					{
						temp = nextCaptureName();
						_substituted[id.Name] = temp;
						captureDecls.Add(new VariableDeclarationSyntax(id.Span, false, null, temp,
							new IdentifierExpressionSyntax(id.Span, id.Name)));
					}

					return new IdentifierExpressionSyntax(id.Span, temp);

				case MemberAccessExpressionSyntax member:
					return new MemberAccessExpressionSyntax(member.Span, (ExpressionSyntax)Rewrite(member.Expression), member.MemberName);
				case IndexExpressionSyntax index:
					return new IndexExpressionSyntax(index.Span, (ExpressionSyntax)Rewrite(index.Left), (ExpressionSyntax)Rewrite(index.Index));
				case BorrowExpressionSyntax borrow:
					return new BorrowExpressionSyntax(borrow.Span, (ExpressionSyntax)Rewrite(borrow.Expression), borrow.IsMutable);
				case HeapAllocationExpressionSyntax heap:
					return new HeapAllocationExpressionSyntax(heap.Span, (ExpressionSyntax)Rewrite(heap.Expression));
				case HeapArrayAllocationExpressionSyntax heapArr:
					return new HeapArrayAllocationExpressionSyntax(heapArr.Span, heapArr.ElementTypeName, (ExpressionSyntax)Rewrite(heapArr.CountExpression));
				case ArrayInitializationExpressionSyntax arrInit:
					return new ArrayInitializationExpressionSyntax(arrInit.Span, arrInit.Elements.Select(e => (ExpressionSyntax)Rewrite(e)).Cast<ExpressionSyntax>().ToList());
				case ArrayReplicationExpressionSyntax arrRep:
					return new ArrayReplicationExpressionSyntax(arrRep.Span, (ExpressionSyntax)Rewrite(arrRep.Value), (ExpressionSyntax)Rewrite(arrRep.Count));
				case StructInitializationExpressionSyntax structInit:
					return new StructInitializationExpressionSyntax(structInit.Span, structInit.StructTypeName,
						structInit.Initializers.Select(i => new MemberInitializerSyntax(i.Span, i.MemberName, (ExpressionSyntax)Rewrite(i.Expression))).ToList());
				case ParenthesizedStructInitializerExpressionSyntax parenInit:
					return new ParenthesizedStructInitializerExpressionSyntax(parenInit.Span,
						parenInit.Initializers.Select(i => new MemberInitializerSyntax(i.Span, i.MemberName, (ExpressionSyntax)Rewrite(i.Expression))).ToList());
				case AsmExpressionSyntax asm:
					return new AsmExpressionSyntax(asm.Span, asm.Template,
						asm.Operands.Select(o => new AsmOperandSyntax(o.Span, o.Name, o.Constraint, (ExpressionSyntax)Rewrite(o.Expression))).ToList(),
						asm.Clobbers.ToList(), asm.Options, asm.ResultType);
				case LabeledBlockStatementSyntax labeledBlock:
					return new LabeledBlockStatementSyntax(labeledBlock.Span, labeledBlock.Label, (BlockStatementSyntax)Rewrite(labeledBlock.Body));
				case NameofExpressionSyntax or TypeofExpressionSyntax or DefaultExpressionSyntax or InterpolatedStringExpressionSyntax:
					return node;
			}

			return base.Rewrite(node);
		}
	}
}