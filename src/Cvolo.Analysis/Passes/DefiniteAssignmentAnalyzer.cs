using Cvolo.Analysis.Ownership;
using Cvolo.Analysis.Symbols;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Collections;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using Cvolo.Core.AST.Expressions;
using Cvolo.Core.AST.Statements;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Analysis.Passes;

/// <summary>
/// Flow-sensitive definite-assignment analysis. It walks each function body as a control-flow
/// graph, joining the "definitely assigned" projection set at merge points (if/else, switch,
/// ternary, lazy operators and loop backedges) so that a read is only accepted when every path
/// reaching it has initialized the accessed storage. Projections (struct fields and constant array
/// elements) are tracked with <see cref="MovePath"/> identities shared with the ownership pass.
/// </summary>
internal sealed class DefiniteAssignmentAnalyzer(BindingContext context)
{
	// Maps a live 'ref'/'refvar' local name to the move path of the storage it references, so
	// assignments to the reference project onto the underlying owner's path.
	private readonly Dictionary<string, MovePath> _referenceProvenance = [];

	// Guards against reporting the same source location twice when a desugared compound
	// assignment re-walks a shared l-value node.
	private readonly HashSet<(int Start, int End)> _reportedSpans = [];

	// Separate from the DA guard: an inactive-variant read is a different diagnostic and must
	// not be suppressed just because DA already reported an availability problem at that span.
	private readonly HashSet<(int Start, int End)> _reportedVariantSpans = [];

	// Innermost breakable/continuable constructs, used to route break/continue edge states.
	private readonly List<FlowFrameBase> _frames = [];

	/// <summary>
	/// Analyzes a concrete function body, seeding its local scope with initialized parameters.
	/// </summary>
	public void Analyze(FunctionDeclarationSyntax func)
	{
		if (!func.HasBody)
			return;

		_referenceProvenance.Clear();
		_reportedSpans.Clear();
		_reportedVariantSpans.Clear();
		_frames.Clear();

		var scope = new SymbolTable(context.Globals);

		// Parameters are initialized on function entry.
		foreach (var param in func.Parameters)
		{
			var type = context.ResolveType(param.Type);
			if (type != null)
				scope.Declare(new VariableSymbol(param.Name, type, false) { IsInitialized = true, Origin = OriginKind.Parameter });
		}

		AnalyzeBlock(func.Body, scope, new State());
	}

	// Statement flow

	/// <summary>
	/// Analyzes the statements of a block sequentially. Returns the outgoing state, or <c>null</c>
	/// when the block cannot fall through (e.g. it ends in a return).
	/// </summary>
	private State? AnalyzeBlock(BlockStatementSyntax? block, SymbolTable scope, State state)
	{
		if (block is null)
			return state;

		State? current = state;
		foreach (var stmt in block.Statements)
		{
			if (current is null)
				break;
			current = AnalyzeStatement(stmt, scope, current);
		}

		return current;
	}

	private State? AnalyzeStatement(SyntaxNode stmt, SymbolTable scope, State state)
	{
		switch (stmt)
		{
			case VariableDeclarationSyntax v:
				return AnalyzeDeclaration(v, scope, state);

			case ExpressionStatementSyntax e:
				return AnalyzeExpression(e.Expression, scope, state);

			case IfStatementSyntax i:
				return AnalyzeIf(i, scope, state);

			case WhileStatementSyntax w:
				return AnalyzeWhile(w, scope, state);

			case ForStatementSyntax f:
				return AnalyzeFor(f, scope, state);

			case ForEachStatementSyntax fe:
				return AnalyzeForEach(fe, scope, state);

			case SwitchStatementSyntax s:
				return AnalyzeSwitch(s, scope, state);

			case BlockStatementSyntax b:
				return AnalyzeBlock(b, new SymbolTable(scope), state);

			case LabeledBlockStatementSyntax lb:
				return AnalyzeBlock(lb.Body, new SymbolTable(scope), state);

			case UnsafeBlockStatementSyntax ub:
				return AnalyzeBlock(ub.Body, new SymbolTable(scope), state);

			case ReturnStatementSyntax r:
				if (r.Expression != null)
					AnalyzeExpression(r.Expression, scope, state);
				return null;

			case BreakStatementSyntax:
				RegisterBreak(state);
				return null;

			case ContinueStatementSyntax:
				// A continue edge returns to the loop header and, like the backedge, is not
				// iterated; it cannot reach the loop exit without first passing a break.
				return null;

			// Deferred and try/catch bodies run on edges that this pass does not model; their
			// effects are conservatively ignored so they neither hide nor invent assignments.
			case DeferStatementSyntax:
				return state;

			default:
				return state;
		}
	}

	private State? AnalyzeDeclaration(VariableDeclarationSyntax v, SymbolTable scope, State state)
	{
		if (!context.VariableSymbols.TryGetValue(v, out var symbol))
		{
			if (v.Initializer != null)
				state = AnalyzeExpression(v.Initializer, scope, state);
			return state;
		}

		scope.Declare(symbol);

		var declaresReference = v.Type is "ref" or "refvar"
			|| (v.Type is { } typeName
				&& (typeName.StartsWith("refvar ", StringComparison.Ordinal)
					|| typeName.StartsWith("ref ", StringComparison.Ordinal)));

		if (v.Initializer is null)
			return state;

		state = AnalyzeExpression(v.Initializer, scope, state);

		if (declaresReference)
		{
			// Acquiring a reference does not initialize the referent (§15.1); it only records
			// where later writes through the reference should project.
			if (ResolveMovePath(v.Initializer) is { } target)
				_referenceProvenance[symbol.Name] = target;
			return state;
		}

		var path = new MovePath(symbol.Name);
		state = AddAssigned(state, path);

		// A tagged union is initialized when one variant is written. Track that variant here;
		// DA intentionally treats the root as a single unit and does not imply any per-variant fact.
		return ApplyWholeUnionValue(state, path, v.Initializer, scope);
	}

	private State? AnalyzeIf(IfStatementSyntax i, SymbolTable scope, State state)
	{
		var (thenIn, elseIn) = AnalyzeCondition(i.Condition, scope, state);

		var thenOut = thenIn != null ? AnalyzeStatement(i.ThenStatement, scope, thenIn) : null;
		var elseOut = i.ElseClause != null
			? (elseIn != null ? AnalyzeStatement(i.ElseClause.Body, scope, elseIn) : null)
			: elseIn;

		return Join(thenOut, elseOut);
	}

	private State? AnalyzeWhile(WhileStatementSyntax w, SymbolTable scope, State state)
	{
		var (bodyIn, exitIn) = AnalyzeCondition(w.Condition, scope, state);

		var frame = new LoopFrame();
		_frames.Add(frame);
		if (bodyIn != null)
			AnalyzeStatement(w.Body, new SymbolTable(scope), bodyIn);
		_frames.RemoveAt(_frames.Count - 1);

		// Zero-iteration exit plus every 'break' edge. The backedge is not iterated: a variable
		// assigned only inside the body is not considered assigned after the loop. Active-variant
		// facts are different: a backedge may change a tag, so any fact whose storage the body can
		// write must not survive into the exit state.
		if (exitIn != null)
			exitIn = InvalidateFactsWrittenIn(w.Body, exitIn);
		var breaks = frame.Breaks.Select(b => InvalidateFactsWrittenIn(w.Body, b));
		return Join(exitIn, JoinAll(breaks));
	}

	private State? AnalyzeFor(ForStatementSyntax f, SymbolTable scope, State state)
	{
		var loopScope = new SymbolTable(scope);

		var afterInit = AnalyzeStatement(f.Initializer, loopScope, state) ?? state;
		var (bodyIn, exitIn) = AnalyzeCondition(f.Condition, loopScope, afterInit);

		var frame = new LoopFrame();
		_frames.Add(frame);
		if (bodyIn != null)
		{
			var bodyOut = AnalyzeStatement(f.Body, new SymbolTable(loopScope), bodyIn);
			AnalyzeExpression(f.Increment, loopScope, bodyOut ?? bodyIn);
		}

		_frames.RemoveAt(_frames.Count - 1);

		if (exitIn != null)
		{
			exitIn = InvalidateFactsWrittenIn(f.Body, exitIn);
			exitIn = InvalidateFactsWrittenIn(f.Increment, exitIn);
		}

		var breaks = frame.Breaks.Select(b => InvalidateFactsWrittenIn(f.Increment, InvalidateFactsWrittenIn(f.Body, b)));
		return Join(exitIn, JoinAll(breaks));
	}

	private State? AnalyzeForEach(ForEachStatementSyntax fe, SymbolTable scope, State state)
	{
		var entry = AnalyzeExpression(fe.Collection, scope, state);

		var bodyScope = new SymbolTable(scope);
		var itemType = context.ResolveType(fe.ItemBindingTypeName ?? fe.ItemTypeName ?? string.Empty)
			?? TypeSymbol.Int;
		bodyScope.Declare(new VariableSymbol(fe.ItemName, itemType, isMutable: false) { IsInitialized = true });

		var frame = new LoopFrame();
		_frames.Add(frame);
		AnalyzeBlock(fe.Body, bodyScope, entry);
		_frames.RemoveAt(_frames.Count - 1);

		var exit = InvalidateFactsWrittenIn(fe.Body, entry);
		var breaks = frame.Breaks.Select(b => InvalidateFactsWrittenIn(fe.Body, b));
		return Join(exit, JoinAll(breaks));
	}

	private State? AnalyzeSwitch(SwitchStatementSyntax s, SymbolTable scope, State state)
	{
		var afterExpr = AnalyzeExpression(s.Expression, scope, state);
		var switchUnion = GetTaggedUnionType(s.Expression, scope);
		var switchPath = switchUnion is not null ? ResolveMovePath(s.Expression) : null;

		var frame = new SwitchFrame();
		_frames.Add(frame);
		var exits = new List<State?>();
		foreach (var @case in s.Cases)
		{
			var caseScope = new SymbolTable(scope);
			if (@case.VariableName != null)
			{
				var variantType = ResolveCaseVariableType(@case, s, scope) ?? TypeSymbol.Int;
				caseScope.Declare(new VariableSymbol(@case.VariableName, variantType, isMutable: false) { IsInitialized = true });
			}

			// Reuse the branch fact that pattern matching already establishes. A case that
			// contradicts an already-proven fact is unreachable, so it must not overwrite that
			// fact; this keeps a known variant stable across an exhaustive switch.
			var caseState = afterExpr;
			if (switchPath is not null)
			{
				var known = afterExpr.ActiveVariants.TryGetValue(switchPath, out var existing) ? existing : null;
				if (known is not null && known != @case.VariantName)
				{
					caseState = afterExpr;
				}
				else if (switchUnion?.FindField(@case.VariantName) is not null)
				{
					caseState = SetActiveVariant(afterExpr, switchPath, @case.VariantName);
				}
				else
				{
					caseState = ClearActiveVariant(afterExpr, switchPath);
				}
			}

			var bodyOut = AnalyzeBlock(new BlockStatementSyntax(@case.Span, @case.Body), caseScope, caseState);
			if (bodyOut != null)
				exits.Add(bodyOut);
		}

		_frames.RemoveAt(_frames.Count - 1);

		exits.AddRange(frame.Breaks);
		if (!IsExhaustive(s, scope))
			exits.Add(afterExpr);

		return JoinAll(exits);
	}

	// Expressions

	private State AnalyzeExpression(ExpressionSyntax expr, SymbolTable scope, State state)
	{
		switch (expr)
		{
			case IdentifierExpressionSyntax id:
				CheckIdentifierRead(id, scope, state);
				return state;

			case BinaryExpressionSyntax { Operator: "=" } assign:
				return AnalyzeAssignment(assign, scope, state);

			case BinaryExpressionSyntax { Operator: "&&" or "||" } lazy:
				{
					var (t, f) = AnalyzeCondition(lazy, scope, state);
					return Join(t, f) ?? state;
				}

			case BinaryExpressionSyntax bin:
				state = AnalyzeExpression(bin.Left, scope, state);
				return AnalyzeExpression(bin.Right, scope, state);

			case MemberAccessExpressionSyntax m:
				{
					// Every dotted projection is a potential payload read, including nested projections
					// (x.A.Field) and reads hidden in operands of other expressions. This check is kept
					// separate from DA's availability check so an uninitialized root keeps its own error.
					CheckUnionVariantReads(m, scope, state);
					return AnalyzeMemberRead(m, scope, state);
				}

			case IndexExpressionSyntax idx:
				return AnalyzeIndexRead(idx, scope, state);

			case CallExpressionSyntax call:
				return AnalyzeCall(call, scope, state);

			case UnaryExpressionSyntax u:
				return AnalyzeUnary(u, scope, state);

			case BorrowExpressionSyntax b:
				// A borrow of a union payload exposes its storage even when it does not load a
				// value, so it is guarded like a read.
				AnalyzeLValueAddress(b.Expression, scope, state, isVariantSelectionTarget: false);
				return state;

			case TernaryExpressionSyntax tern:
				{
					var (t, f) = AnalyzeCondition(tern.Condition, scope, state);
					var thenOut = t != null ? AnalyzeExpression(tern.ThenExpression, scope, t) : null;
					var elseOut = f != null ? AnalyzeExpression(tern.ElseExpression, scope, f) : null;
					return Join(thenOut, elseOut) ?? state;
				}

			case CoalesceExpressionSyntax co:
				{
					var afterLeft = AnalyzeExpression(co.Left, scope, state);
					var afterRight = AnalyzeExpression(co.Right, scope, afterLeft);
					return Join(afterLeft, afterRight) ?? afterLeft;
				}

			case OptionalChainExpressionSyntax oc:
				return AnalyzeOptionalChain(oc, scope, state);

			case IsPatternExpressionSyntax ip:
				{
					var (t, f) = AnalyzeCondition(ip, scope, state);
					return Join(t, f) ?? state;
				}

			case StructInitializationExpressionSyntax si:
				foreach (var init in si.Initializers)
					state = AnalyzeExpression(init.Expression, scope, state);
				return state;

			case ParenthesizedStructInitializerExpressionSyntax psi:
				foreach (var init in psi.Initializers)
					state = AnalyzeExpression(init.Expression, scope, state);
				return state;

			case ArrayInitializationExpressionSyntax ai:
				foreach (var element in ai.Elements)
					state = AnalyzeExpression(element, scope, state);
				return state;

			case ArrayReplicationExpressionSyntax ar:
				state = AnalyzeExpression(ar.Value, scope, state);
				return AnalyzeExpression(ar.Count, scope, state);

			case HeapAllocationExpressionSyntax ha:
				return AnalyzeExpression(ha.Expression, scope, state);

			case HeapArrayAllocationExpressionSyntax haa:
				return AnalyzeExpression(haa.CountExpression, scope, state);

			case InterpolatedStringExpressionSyntax it:
				foreach (var segment in it.Segments)
					if (segment.Expression != null)
						state = AnalyzeExpression(segment.Expression, scope, state);
				return state;

			case CatchExpressionSyntax ce:
				state = AnalyzeExpression(ce.Operand, scope, state);
				if (ce.Fallback != null)
					state = AnalyzeExpression(ce.Fallback, scope, state);
				return state;

			case AsmExpressionSyntax asm:
				foreach (var operand in asm.Operands)
					if (!operand.IsOutput)
						state = AnalyzeExpression(operand.Expression, scope, state);
				return state;

			// nameof/typeof/sizeof/alignof/offsetof name symbols rather than values, and lambda
			// bodies execute on an unrelated edge; none contribute reads on this path.
			default:
				return state;
		}
	}

	private State AnalyzeUnary(UnaryExpressionSyntax u, SymbolTable scope, State state)
	{
		switch (u.Operator)
		{
			// Read-modify-write: the operand's old value is read, then it is definitely assigned.
			case "++_prefix" or "--_prefix" or "++_postfix" or "--_postfix":
				state = AnalyzeExpression(u.Operand, scope, state);
				return MarkAssigned(u.Operand, scope, state) ?? state;

			case "!":
				{
					var (t, f) = AnalyzeCondition(u, scope, state);
					return Join(t, f) ?? state;
				}

			case "&":
				// A reference to a union payload exposes its storage, so it is a read of that
				// variant even though no value is loaded here.
				AnalyzeLValueAddress(u.Operand, scope, state, isVariantSelectionTarget: false);
				return state;

			case "*":
				// The pointer is read; the pointee is untracked storage.
				return AnalyzeExpression(u.Operand, scope, state);

			default:
				// Cast "(T)" and arithmetic unary operators evaluate their operand.
				return AnalyzeExpression(u.Operand, scope, state);
		}
	}

	private State AnalyzeMemberRead(MemberAccessExpressionSyntax m, SymbolTable scope, State state)
	{
		var path = ResolveMovePath(m);
		var type = path != null ? ResolvePathType(path, scope) : null;

		// A stable path projects directly onto a tracked storage location; its sub-expressions are
		// pure address arithmetic and carry no reads of their own.
		if (path != null && type != null)
		{
			if (!IsRootInitialized(path.Root, scope) && !IsDefinitelyAssigned(path, type, state))
				ReportUninitialized(m.Span, path);
			return state;
		}

		return AnalyzeExpression(m.Expression, scope, state);
	}

	private State AnalyzeIndexRead(IndexExpressionSyntax idx, SymbolTable scope, State state)
	{
		if (MovePathBuilder.TryGetConstantIndex(idx.Index, out _))
		{
			var path = ResolveMovePath(idx);
			var type = path != null ? ResolvePathType(path, scope) : null;
			if (path != null && type != null)
			{
				if (!IsRootInitialized(path.Root, scope) && !IsDefinitelyAssigned(path, type, state))
					ReportUninitialized(idx.Span, path);
				return state;
			}
		}

		// Dynamic index: the accessed element cannot be identified, so element-level definite
		// assignment is not enforced. The index expression is still read; the base of the access
		// only has its address computed.
		AnalyzeLValueAddress(idx.Left, scope, state);
		return AnalyzeExpression(idx.Index, scope, state);
	}

	private State AnalyzeCall(CallExpressionSyntax call, SymbolTable scope, State state)
	{
		context.ResolvedCalls.TryGetValue(call, out var func);

		for (var i = 0; i < call.Arguments.Count; i++)
		{
			var argument = call.Arguments[i];
			var mutableReference = func != null
				&& i < func.Parameters.Count
				&& func.Parameters[i].Type is PointerTypeSymbol { IsMutable: true };

			if (mutableReference)
			{
				// A mutable reference argument is an address, not a value read. It is also not
				// treated as initializing the source: callees never make a local definite.
				AnalyzeLValueAddress(argument, scope, state, isVariantSelectionTarget: false);
				if (ResolveMovePath(argument) is { } mutablePath)
				{
					state = InvalidateActiveFacts(state, mutablePath);
				}
			}
			else
			{
				state = AnalyzeExpression(argument, scope, state);
			}
		}

		// A callee may also mutate any global union storage; locals passed by value cannot be
		// changed by the call and keep their proven facts.
		return InvalidateGlobalActiveFacts(state, scope);
	}

	private State AnalyzeOptionalChain(OptionalChainExpressionSyntax oc, SymbolTable scope, State state)
	{
		var afterReceiver = AnalyzeExpression(oc.Receiver, scope, state);

		if (!oc.IsInvocation)
			return afterReceiver;

		var afterArgs = afterReceiver;
		foreach (var argument in oc.Arguments!)
			afterArgs = AnalyzeExpression(argument, scope, afterArgs);

		// The arguments only evaluate on the 'Some' path; the 'None' path skips them.
		return Join(afterReceiver, afterArgs) ?? afterReceiver;
	}

	private State AnalyzeAssignment(BinaryExpressionSyntax bin, SymbolTable scope, State state)
	{
		// Evaluate the destination address (which may read index expressions), then the source,
		// then record the assignment. The destination's own value is not read by a plain '='.
		AnalyzeLValueAddress(bin.Left, scope, state);
		state = AnalyzeExpression(bin.Right, scope, state);
		return MarkAssigned(bin.Left, scope, state, bin.Right) ?? state;
	}

	// Lazy boolean conditions

	/// <summary>
	/// Evaluates a condition, returning the states on the true and false branches (<c>null</c> when
	/// a branch is unreachable, e.g. a literal <c>true</c>). Short-circuit operators only evaluate
	/// their right operand on the path where it actually executes.
	/// </summary>
	private (State? True, State? False) AnalyzeCondition(ExpressionSyntax expr, SymbolTable scope, State state)
	{
		switch (expr)
		{
			case BooleanLiteralExpressionSyntax b:
				return b.Value ? (state, null) : (null, state);

			case UnaryExpressionSyntax { Operator: "!" } not:
				{
					var (t, f) = AnalyzeCondition(not.Operand, scope, state);
					return (f, t);
				}

			case BinaryExpressionSyntax { Operator: "&&" } and:
				{
					var (leftTrue, leftFalse) = AnalyzeCondition(and.Left, scope, state);
					State? rightTrue = null;
					State? rightFalse = null;
					if (leftTrue != null)
						(rightTrue, rightFalse) = AnalyzeCondition(and.Right, scope, leftTrue);
					return (rightTrue, Join(leftFalse, rightFalse));
				}

			case BinaryExpressionSyntax { Operator: "||" } or:
				{
					var (leftTrue, leftFalse) = AnalyzeCondition(or.Left, scope, state);
					State? rightTrue = null;
					State? rightFalse = null;
					if (leftFalse != null)
						(rightTrue, rightFalse) = AnalyzeCondition(or.Right, scope, leftFalse);
					return (Join(leftTrue, rightTrue), rightFalse);
				}

			case IsPatternExpressionSyntax ip:
				{
					var evaluated = AnalyzeExpression(ip.Operand, scope, state);
					var testedUnion = GetTaggedUnionType(ip.Operand, scope);
					var testedPath = testedUnion is not null ? ResolveMovePath(ip.Operand) : null;
					if (testedPath is not null && testedUnion?.FindField(ip.VariantName) is not null)
					{
						// The true edge proves the tested variant; the false edge only rules it out,
						// leaving an already-known different variant intact.
						var trueState = SetActiveVariant(evaluated, testedPath, ip.VariantName);
						var falseState = ExcludeActiveVariant(evaluated, testedPath, ip.VariantName);
						if (ip.BoundName != null)
							trueState = AddAssigned(trueState, new MovePath(ip.BoundName));
						return (trueState, falseState);
					}

					if (ip.BoundName != null)
						return (AddAssigned(evaluated, new MovePath(ip.BoundName)), evaluated);
					return (evaluated, evaluated);
				}

			default:
				{
					var evaluated = AnalyzeExpression(expr, scope, state);
					return (evaluated, evaluated);
				}
		}
	}

	// Reads and assignments
	private void CheckIdentifierRead(IdentifierExpressionSyntax id, SymbolTable scope, State state)
	{
		if (scope.Lookup(id.Name) is not VariableSymbol symbol)
			return;
		if (symbol.IsInitialized || symbol.Origin == OriginKind.Global)
			return;

		var path = ResolveMovePath(id) ?? new MovePath(id.Name);
		if (!IsDefinitelyAssigned(path, symbol.Type, state))
			ReportUninitialized(id.Span, path);
	}

	/// <summary>
	/// Walks the address computation of an assignment target. Identifiers contribute no read;
	/// index expressions are evaluated (their index is a real read) and a raw-pointer dereference
	/// reads the pointer.
	/// </summary>
	private void AnalyzeLValueAddress(ExpressionSyntax lvalue, SymbolTable scope, State state, bool isVariantSelectionTarget = true)
	{
		switch (lvalue)
		{
			case IdentifierExpressionSyntax:
				return;

			case MemberAccessExpressionSyntax m:
				// `x.A = value` selects the variant and is not a read of the old payload. Any
				// projection inside its receiver (and every other l-value use) still is.
				if (!isVariantSelectionTarget)
				{
					CheckUnionVariantReads(m, scope, state);
				}
				else
				{
					CheckUnionVariantReads(m.Expression, scope, state);
				}
				AnalyzeLValueAddress(m.Expression, scope, state, isVariantSelectionTarget: false);
				return;

			case IndexExpressionSyntax idx:
				AnalyzeLValueAddress(idx.Left, scope, state);
				AnalyzeExpression(idx.Index, scope, state);
				return;

			case UnaryExpressionSyntax { Operator: "*" } deref:
				AnalyzeExpression(deref.Operand, scope, state);
				return;

			case BorrowExpressionSyntax b:
				AnalyzeLValueAddress(b.Expression, scope, state, isVariantSelectionTarget);
				return;

			default:
				AnalyzeExpression(lvalue, scope, state);
				return;
		}
	}

	private State? MarkAssigned(ExpressionSyntax lvalue, SymbolTable scope, State state, ExpressionSyntax? source = null)
	{
		// Writing a union variant initializes the whole union value; the discriminant makes any
		// per-variant tracking unsound, so the union root is the unit of assignment (§16).
		if (lvalue is MemberAccessExpressionSyntax member
			&& GetTaggedUnionType(member.Expression, scope) is { } union
			&& ResolveMovePath(member.Expression) is { } unionRoot
			&& union.FindField(member.MemberName) is not null)
		{
			state = AddAssigned(state, unionRoot);
			return SetActiveVariant(state, unionRoot, member.MemberName);
		}

		var path = ResolveMovePath(lvalue);
		if (path is null)
			return null;

		state = AddAssigned(state, path);

		// A whole-union store carries the source's active variant when we can see it, and makes
		// the destination unknown otherwise. Payload writes leave the tag unchanged.
		return GetTaggedUnionType(lvalue, scope) is null
			? state
			: ApplyWholeUnionValue(state, path, source, scope);
	}

	private bool IsDefinitelyAssigned(MovePath path, TypeSymbol? type, State state)
	{
		if (state.Assigned.Contains(path))
			return true;

		foreach (var assigned in state.Assigned)
			if (assigned.IsStrictAncestorOf(path))
				return true;

		return IsAggregateFullyAssigned(path, type, state);
	}

	/// <summary>
	/// True when an aggregate is initialized field-by-field (or element-by-element) even though the
	/// whole value was never assigned in one step.
	/// </summary>
	private bool IsAggregateFullyAssigned(MovePath path, TypeSymbol? type, State state)
	{
		switch (type)
		{
			case StructTypeSymbol structType:
				if (structType.Fields.Count == 0)
					return false;
				foreach (var field in structType.Fields)
					if (!IsDefinitelyAssigned(path.Append(ProjectionStep.Field(field.Name)), field.Type, state))
						return false;
				return true;

			case ArrayTypeSymbol arrayType:
				if (arrayType.Size == 0)
					return false;
				for (var i = 0; i < arrayType.Size; i++)
					if (!IsDefinitelyAssigned(path.Append(ProjectionStep.FixedIndex(i)), arrayType.ElementType, state))
						return false;
				return true;

			default:
				return false;
		}
	}

	private bool IsRootInitialized(string root, SymbolTable scope)
	{
		if (scope.Lookup(root) is VariableSymbol variable)
			return variable.IsInitialized || variable.Origin == OriginKind.Global;
		return context.ResolveGlobalReference(root, out _) != null;
	}

	private bool IsExhaustive(SwitchStatementSyntax s, SymbolTable scope)
	{
		if (s.Cases.Any(c => c.IsDefault))
			return true;

		return ResolveType(s.Expression, scope) switch
		{
			EnumTypeSymbol enumType when !enumType.IsFlags =>
				enumType.Variants.All(v => s.Cases.Any(c => c.VariantName == v.Name)),
			UnionTypeSymbol unionType =>
				unionType.Fields.All(f => s.Cases.Any(c => c.VariantName == f.Name)),
			_ => false
		};
	}

	private TypeSymbol? ResolveCaseVariableType(SwitchCaseSyntax @case, SwitchStatementSyntax s, SymbolTable scope)
		=> ResolveType(s.Expression, scope) switch
		{
			UnionTypeSymbol unionType => unionType.FindField(@case.VariantName)?.Type,
			EnumTypeSymbol enumType => enumType.StorageType,
			_ => null
		};

	// Active variant facts
	//
	// These facts intentionally live beside definite assignment: both are flow-sensitive
	// properties of a canonical storage path. They answer a different question, though — DA says
	// whether the root can be read at all; these facts say which tagged-union variant it holds.

	/// <summary>
	/// Returns the tagged union type of an expression, or null for values without a tag
	/// (including raw <c>unsafe union</c>, whose projection rules are unchanged).
	/// </summary>
	private UnionTypeSymbol? GetTaggedUnionType(ExpressionSyntax expr, SymbolTable scope)
	{
		var type = ResolveType(expr, scope);
		if (type is PointerTypeSymbol pointer)
			type = pointer.ReferencedType;
		return type is UnionTypeSymbol { IsUnsafe: false } union ? union : null;
	}

	/// <summary>
	/// Returns the variant a union value is known to hold, from either a flow fact or a visible
	/// aggregate initializer. Null means unknown and is never treated as proof of any variant.
	/// </summary>
	private string? TryGetKnownActiveVariant(ExpressionSyntax expr, SymbolTable scope, State state)
	{
		if (TryGetInitializerVariant(expr, out var initializerVariant))
			return initializerVariant;

		var path = ResolveMovePath(expr);
		return path is not null && state.ActiveVariants.TryGetValue(path, out var active)
			? active
			: null;
	}

	private bool TryGetInitializerVariant(ExpressionSyntax expr, out string? variant)
	{
		variant = null;
		IReadOnlyList<MemberInitializerSyntax>? initializers = expr switch
		{
			StructInitializationExpressionSyntax si => si.Initializers,
			ParenthesizedStructInitializerExpressionSyntax paren => paren.Initializers,
			_ => null
		};

		if (initializers is null || initializers.Count != 1)
			return false;

		var typeName = expr switch
		{
			StructInitializationExpressionSyntax si => si.StructTypeName,
			ParenthesizedStructInitializerExpressionSyntax { ResolvedStructTypeName: { } name } => name,
			_ => null
		};

		if (typeName is null || context.ResolveType(typeName) is not UnionTypeSymbol { IsUnsafe: false } union)
			return false;
		if (union.FindField(initializers[0].MemberName) is null)
			return false;

		variant = initializers[0].MemberName;
		return true;
	}

	/// <summary>
	/// Applies the active-variant effects of a value assigned to a union storage path. Nested
	/// aggregate literals are walked as well, so <c>Holder { Root: Option { Some: ... } }</c>
	/// establishes the fact for <c>holder.Root</c>.
	/// </summary>
	private State ApplyWholeUnionValue(State state, MovePath path, ExpressionSyntax? value, SymbolTable scope)
	{
		if (GetTaggedUnionTypeForPath(path, scope) is not null)
		{
			var known = value is not null ? TryGetKnownActiveVariant(value, scope, state) : null;
			return known is not null
				? SetActiveVariant(state, path, known)
				: ClearActiveVariant(state, path);
		}

		if (value is not (StructInitializationExpressionSyntax or ParenthesizedStructInitializerExpressionSyntax))
			return state;

		var initializers = value is StructInitializationExpressionSyntax si
			? si.Initializers
			: ((ParenthesizedStructInitializerExpressionSyntax)value).Initializers;
		foreach (var initializer in initializers)
		{
			var fieldPath = path.Append(ProjectionStep.Field(initializer.MemberName));
			state = ApplyWholeUnionValue(state, fieldPath, initializer.Expression, scope);
		}

		return state;
	}

	private UnionTypeSymbol? GetTaggedUnionTypeForPath(MovePath path, SymbolTable scope)
		=> ResolvePathType(path, scope) is UnionTypeSymbol { IsUnsafe: false } union ? union : null;

	/// <summary>
	/// Rejects every dotted projection of a tagged union whose variant is not proven active.
	/// The syntax walk is what makes <c>x.A.Field</c> report the <c>x.A</c> access itself rather
	/// than only the outer struct field.
	/// </summary>
	private void CheckUnionVariantReads(ExpressionSyntax expr, SymbolTable scope, State state)
	{
		if (expr is not MemberAccessExpressionSyntax member)
			return;

		CheckUnionVariantReads(member.Expression, scope, state);

		if (GetTaggedUnionType(member.Expression, scope) is not { } unionType)
			return;
		if (unionType.FindField(member.MemberName) is null)
			return; // The validator already reports unknown variants.

		var unionPath = ResolveMovePath(member.Expression);
		if (unionPath is not null
			&& !IsRootInitialized(unionPath.Root, scope)
			&& !IsDefinitelyAssigned(unionPath, unionType, state))
			return; // Root availability is DA's diagnostic, not an inactive-variant error.

		var active = TryGetKnownActiveVariant(member.Expression, scope, state);
		if (string.Equals(active, member.MemberName, StringComparison.Ordinal))
			return;

		ReportInactiveVariantRead(member, unionType, active);
	}

	private void ReportInactiveVariantRead(MemberAccessExpressionSyntax member, UnionTypeSymbol unionType, string? active)
	{
		// Try/catch lowering creates payload projections with an empty synthetic span. They are
		// compiler-internal reads with no user source to point at and are already tag-checked by
		// the lowering; they must not produce editor diagnostics at file position 1:1.
		if (member.Span.Length == 0)
			return;
		if (!_reportedVariantSpans.Add((member.Span.Start, member.Span.End)))
			return;
		if (context.CurrentUnit is not { } unit)
			return;

		var message = active is null
			? $"Cannot read variant '{member.MemberName}' of union '{unionType.Name}': the active variant is not known at this point. Use 'is' or 'switch'."
			: $"Cannot read variant '{member.MemberName}' of union '{unionType.Name}': its active variant is '{active}'. Use 'is' or 'switch' to check the variant.";
		context.Diagnostics.Report(context.FileContexts[unit], member.Span, message, DiagnosticIds.InactiveUnionVariantRead);
	}

	private static State SetActiveVariant(State state, MovePath path, string variant)
	{
		// A tag change also invalidates any facts about storage nested inside the old payload.
		var variants = new Dictionary<MovePath, string>(state.ActiveVariants);
		foreach (var tracked in variants.Keys.Where(path.IsPrefixOf).ToList())
			variants.Remove(tracked);
		variants[path] = variant;
		return WithVariants(state, variants);
	}

	private static State ClearActiveVariant(State state, MovePath path)
	{
		return InvalidateActiveFacts(state, path);
	}

	private static State ExcludeActiveVariant(State state, MovePath path, string variant)
	{
		return state.ActiveVariants.TryGetValue(path, out var active) && active == variant
				? InvalidateActiveFacts(state, path)
				: state;
	}

	/// <summary>
	/// Invalidates facts for the written path and every path that contains it (in either
	/// direction), because a write to a payload can change the tag of its union ancestor.
	/// </summary>
	private static State InvalidateActiveFacts(State state, MovePath path)
	{
		var variants = new Dictionary<MovePath, string>(state.ActiveVariants);
		foreach (var tracked in variants.Keys
			.Where(key => key.IsPrefixOf(path) || path.IsPrefixOf(key))
			.ToList())
			variants.Remove(tracked);
		return WithVariants(state, variants);
	}

	private State InvalidateGlobalActiveFacts(State state, SymbolTable scope)
	{
		var globals = state.ActiveVariants.Keys
			.Where(path => context.ResolveGlobalReference(path.Root, out _) is not null
				|| scope.Lookup(path.Root) is VariableSymbol { Origin: OriginKind.Global })
			.ToList();
		if (globals.Count == 0)
			return state;

		var variants = new Dictionary<MovePath, string>(state.ActiveVariants);
		foreach (var path in globals)
			variants.Remove(path);
		return WithVariants(state, variants);
	}

	/// <summary>
	/// Loop backedges may change tags, so no fact about storage written in the loop body may
	/// survive to the loop exit. Assignment state is deliberately not iterated; only the facts
	/// need this invalidation.
	/// </summary>
	private State InvalidateFactsWrittenIn(SyntaxNode body, State state)
	{
		var paths = new List<MovePath>();
		CollectAssignedPaths(body, paths);
		foreach (var path in paths)
			state = InvalidateActiveFacts(state, path);
		return state;
	}

	private void CollectAssignedPaths(SyntaxNode node, List<MovePath> paths)
	{
		switch (node)
		{
			case BinaryExpressionSyntax { Operator: "=" } assign
				when ResolveMovePath(assign.Left) is { } assigned:
				paths.Add(assigned);
				break;
			case UnaryExpressionSyntax u when u.Operator.StartsWith("++", StringComparison.Ordinal)
				|| u.Operator.StartsWith("--", StringComparison.Ordinal):
				if (ResolveMovePath(u.Operand) is { } incremented)
					paths.Add(incremented);
				break;
		}

		foreach (var child in node.GetChildren())
			CollectAssignedPaths(child, paths);
	}

	private static State WithVariants(State state, Dictionary<MovePath, string> variants)
	{
		return new([.. state.Assigned], variants);
	}

	// Move paths and types

	/// <summary>
	/// Builds the move path for an expression, resolving the root of a borrowed 'ref'/'refvar'
	/// local to the storage it references so reads and writes project onto the owner.
	/// </summary>
	private MovePath? ResolveMovePath(ExpressionSyntax expr)
	{
		var path = MovePathBuilder.TryBuild(expr);
		if (path is null)
			return null;

		if (_referenceProvenance.TryGetValue(path.Root, out var target))
		{
			var resolved = target;
			foreach (var step in path.Steps)
				resolved = resolved.Append(step);
			return resolved;
		}

		return path;
	}

	private TypeSymbol? ResolveType(ExpressionSyntax expr, SymbolTable scope)
	{
		switch (expr)
		{
			case IdentifierExpressionSyntax id:
				if (scope.Lookup(id.Name) is VariableSymbol variable)
					return variable.Type;
				return context.ResolveGlobalReference(id.Name, out _)?.Type;

			case MemberAccessExpressionSyntax m:
				return ResolveType(m.Expression, scope) switch
				{
					StructTypeSymbol s => s.FindField(m.MemberName)?.Type,
					UnionTypeSymbol u => u.FindField(m.MemberName)?.Type,
					PointerTypeSymbol p when p.ReferencedType is StructTypeSymbol ps => ps.FindField(m.MemberName)?.Type,
					_ => null
				};

			case IndexExpressionSyntax idx:
				return ResolveType(idx.Left, scope) switch
				{
					ArrayTypeSymbol array => array.ElementType,
					SliceTypeSymbol slice => slice.ElementType,
					_ => null
				};

			case BorrowExpressionSyntax b:
				return ResolveType(b.Expression, scope);

			case UnaryExpressionSyntax { Operator: "*" } u:
				return ResolveType(u.Operand, scope) switch
				{
					PointerTypeSymbol pointer => pointer.ReferencedType,
					RawPointerTypeSymbol raw => raw.ElementType,
					_ => null
				};

			default:
				return null;
		}
	}

	private TypeSymbol? ResolvePathType(MovePath path, SymbolTable scope)
	{
		var type = scope.Lookup(path.Root) is VariableSymbol variable
			? variable.Type
			: context.ResolveGlobalReference(path.Root, out _)?.Type;

		if (type is null)
			return null;

		foreach (var step in path.Steps)
		{
			type = step.Kind == ProjectionStepKind.Field
				? type switch
				{
					StructTypeSymbol s => s.FindField(step.Name!)?.Type,
					UnionTypeSymbol u => u.FindField(step.Name!)?.Type,
					_ => null
				}
				: type switch
				{
					ArrayTypeSymbol array => array.ElementType,
					SliceTypeSymbol slice => slice.ElementType,
					_ => null
				};

			if (type is null)
				return null;
		}

		return type;
	}

	// Frames, joins, diagnostics

	private void RegisterBreak(State state)
	{
		for (var i = _frames.Count - 1; i >= 0; i--)
		{
			switch (_frames[i])
			{
				case SwitchFrame sw:
					sw.Breaks.Add(state);
					return;
				case LoopFrame loop:
					loop.Breaks.Add(state);
					return;
			}
		}
	}

	private static State AddAssigned(State state, MovePath path)
	{
		var assigned = new HashSet<MovePath>(state.Assigned) { path };
		return new State(assigned, new Dictionary<MovePath, string>(state.ActiveVariants));
	}

	/// <summary>
	/// Merges two outgoing states. <c>null</c> denotes an unreachable predecessor, which does not
	/// constrain the merge; when both are reachable the result keeps only the projections that are
	/// definitely assigned on every incoming path.
	/// </summary>
	private static State? Join(State? a, State? b)
	{
		if (a is null)
			return b;
		if (b is null)
			return a;

		var assigned = new HashSet<MovePath>(a.Assigned);
		assigned.IntersectWith(b.Assigned);

		// Keep a variant fact only when every reachable predecessor agrees on it.
		var variants = new Dictionary<MovePath, string>(a.ActiveVariants);
		foreach (var (path, variant) in variants.ToList())
		{
			if (!b.ActiveVariants.TryGetValue(path, out var other) || other != variant)
			{
				variants.Remove(path);
			}
		}

		return new State(assigned, variants);
	}

	private static State? JoinAll(IEnumerable<State?> states)
	{
		State? accumulator = null;
		foreach (var state in states)
			accumulator = Join(accumulator, state);
		return accumulator;
	}

	private void ReportUninitialized(TextSpan span, MovePath path)
	{
		if (!_reportedSpans.Add((span.Start, span.End)))
			return;

		var unit = context.CurrentUnit;
		if (unit is null)
			return;

		var message = path.Depth == 0
			? $"Variable '{path.Root}' may be uninitialized on this path."
			: $"'{path}' may be uninitialized on this path.";
		context.Diagnostics.Report(context.FileContexts[unit], span, message, DiagnosticIds.UseOfPossiblyUninitializedVariable);
	}

	// Support types

	private abstract class FlowFrameBase;

	private sealed class LoopFrame : FlowFrameBase
	{
		public List<State> Breaks { get; } = [];
	}

	private sealed class SwitchFrame : FlowFrameBase
	{
		public List<State> Breaks { get; } = [];
	}

	/// <summary>
	/// Immutable flow state: definitely assigned projections plus the known active variant for
	/// each tagged-union storage location. Both are intersected at control-flow joins.
	/// </summary>
	private sealed class State(HashSet<MovePath> assigned, Dictionary<MovePath, string> activeVariants)
	{
		public HashSet<MovePath> Assigned { get; } = assigned;

		/// <summary>Path to a tagged union -> name of its definitely active variant.</summary>
		public IReadOnlyDictionary<MovePath, string> ActiveVariants { get; } = activeVariants;

		public State() : this([], []) { }
	}
}
