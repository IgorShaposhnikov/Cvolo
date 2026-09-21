using Cvolo.Analysis.Symbols;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Analysis.Passes.Safety;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using Cvolo.Core.AST.Expressions;
using Cvolo.Core.AST.Statements;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Analysis.Passes;

public sealed class SafetyPass(BindingContext context)
{
	private BorrowTracker? _borrows;
	private UnsafeContextValidator? _unsafeContext;
	private ReferenceLifetimeAnalyzer? _referenceLifetimes;
	private MoveAnalyzer? _moves;
	private UnboundValidator? _unbound;

	/// <summary>
	/// Lazily creates the unsafe-context validator that owns tier-stack transitions and
	/// diagnostics for unsafe-only raw-pointer operations.
	/// </summary>
	private UnsafeContextValidator UnsafeContext => _unsafeContext ??= new UnsafeContextValidator(context);

	/// <summary>
	/// Lazily creates the per-function borrow tracker that owns borrow exclusivity, parent locks,
	/// and early-release bookkeeping while move analysis and delegate semantics live in dedicated layers.
	/// </summary>
	private BorrowTracker Borrows => _borrows ??= new BorrowTracker(
		context,
		GetBaseIdentifierName,
		() => UnsafeContext.CurrentTier);

	/// <summary>
	/// Lazily creates the reference-lifetime service while borrow-lock state is owned by
	/// <see cref="BorrowTracker"/> and tier state is supplied by <see cref="UnsafeContextValidator"/>.
	/// </summary>
	private ReferenceLifetimeAnalyzer ReferenceLifetimes => _referenceLifetimes ??= new ReferenceLifetimeAnalyzer(
		context,
		ResolveExpressionType,
		GetBaseIdentifierName,
		Borrows.HasParentLock,
		() => UnsafeContext.CurrentTier);

	/// <summary>
	/// Lazily creates the value-move service that owns moved-state checks, by-value ownership
	/// transfer, and large-copy diagnostics while delegate capture policy remains in this pass.
	/// </summary>
	private MoveAnalyzer Moves => _moves ??= new MoveAnalyzer(
		context,
		Borrows,
		ResolveExpressionType);

	/// <summary>
	/// Lazily creates the unbound-sandbox validator that owns structural reference-field mutation,
	/// visibility preservation, and local-reference escape checks while tier state comes from
	/// <see cref="UnsafeContextValidator"/>.
	/// </summary>
	private UnboundValidator Unbound => _unbound ??= new UnboundValidator(
		context,
		GetBaseIdentifierName,
		() => UnsafeContext.CurrentTier,
		() => UnsafeContext.IsInsideUnbound);

	// — Safe Delegates & Borrowed Closures pass state (todo 7) —
	/// <summary>The function whose body is currently being walked (used for lambda block bodies).</summary>
	private FunctionDeclarationSyntax? _enclosingFunc;

	/// <summary>
	/// Delegate provenance records for delegate-typed variables declared so far in the current
	/// function: the context the delegate value carries (static/free, closure env, ref borrow,
	/// or a bound receiver). Copy assignments propagate provenance.
	/// </summary>
	private readonly Dictionary<string, DelegateProvenance> _delegateProvenances = [];

	/// <summary>Names of non-escaping safe-delegate parameters of the current function (§13.0).</summary>
	private readonly HashSet<string> _delegateParams = [];

	private enum DelegateProvenanceKind
	{
		Free,     // context == null (free function / non-capturing lambda): may escape
		Capturing,// context == &closureEnv: may NOT escape (CVL1318)
		RefBorrow,// context == &closureEnv holding ref borrows: may NOT escape (CVL1316)
		BoundMethod, // context == &receiver: may escape only as far as the receiver (CVL1321/CVL1319)
	}

	private sealed record DelegateProvenance(
		DelegateProvenanceKind Kind,
		IReadOnlyList<string> CapturedNames,
		string? ReceiverBase);

	/// <summary>Names of delegate-typed locals/globals whose context must not escape (capturing/ref/bound).</summary>
	private readonly Dictionary<string, DelegateProvenance> _nonEscapingDelegates = [];

	/// <summary>Variables holding an active immutable ref-lambda borrow (reassignment/move blocked).</summary>
	private readonly HashSet<string> _refCapturedVars = [];

	/// <summary>Captured-variable sets of the lambdas currently being checked (stack for CVL1312/CVL1313).</summary>
	private readonly Stack<HashSet<string>> _lambdaCaptureSets = [];


	public void Process(IEnumerable<CompilationUnitSyntax> units)
	{
		foreach (var unit in units)
		{
			context.CurrentUnit = unit;
			context.CurrentNamespace = unit.NamespaceDeclaration?.Name;
			var members = context.CurrentNamespace != null ? unit.NamespaceDeclaration!.Members : unit.Members;

			foreach (var member in members)
			{
				if (member is FunctionDeclarationSyntax func && func.GenericParameters.Count == 0 && !func.Name.Contains('<'))
				{
					CheckFunctionSafety(func);
				}
				else if (member is ExposeExternBlockSyntax exportBlock)
				{
					foreach (var exportFunc in exportBlock.Functions)
					{
						if (exportFunc.GenericParameters.Count == 0 && !exportFunc.Name.Contains('<'))
							CheckFunctionSafety(exportFunc);
					}
				}
				else if (member is ExtensionDeclarationSyntax extDecl)
				{
					// Skip generic templates; their monomorphized concrete instances are checked below
					if (context.GenericStructTemplates.ContainsKey(extDecl.ExtendedTypeName) ||
						context.GenericUnionTemplates.ContainsKey(extDecl.ExtendedTypeName))
					{
						continue;
					}

					foreach (var method in extDecl.Methods
						.Concat(extDecl.Destructors.Select(static d => d.ToFunctionDeclaration())))
					{
						CheckFunctionSafety(method);
					}

					foreach (var ctor in extDecl.Constructors)
					{
						CheckFunctionSafety(ctor.ToFunctionDeclaration());
					}
				}
			}
		}

		// Enforce safety pass on all monomorphized generic functions and extension methods!
		foreach (var instDecl in context.MonomorphizedFunctionDecls)
		{
			CheckFunctionSafety(instDecl);
		}

		foreach (var decl in context.MonomorphizedExtensionDecls)
		{
			if (decl is FunctionDeclarationSyntax func)
			{
				CheckFunctionSafety(func);
			}
			else if (decl is ConstructorDeclarationSyntax ctor)
			{
				CheckFunctionSafety(ctor.ToFunctionDeclaration());
			}
		}
	}

	private void CheckFunctionSafety(FunctionDeclarationSyntax func)
	{
		if (!func.HasBody)
			return;

		Borrows.Reset();
		ReferenceLifetimes.Reset();
		Unbound.Reset();
		_delegateProvenances.Clear();
		_delegateParams.Clear();
		_refCapturedVars.Clear();
		_lambdaCaptureSets.Clear();
		_enclosingFunc = func;

		// Look up the resolved function symbol to get the actual tier
		var baseName = func.Name == "main" ? "main" : context.GetMangledName(func.Name, context.CurrentNamespace);
		var paramTypes = func.Parameters.Select(p => context.ResolveType(p.Type) ?? TypeSymbol.Int).ToList();
		var overloadedName = context.GetOverloadedMangledName(baseName, paramTypes);
		var funcSymbol = context.Globals.Lookup(overloadedName) as FunctionSymbol;
		// Determine tier from attributes ([UnsafeBody]), modifier, or global symbol table
		var isUnsafeBody = func.Attributes.Any(a => string.Equals(a.Name, "UnsafeBody", StringComparison.OrdinalIgnoreCase) ||
													string.Equals(a.Name, "System.UnsafeBody", StringComparison.OrdinalIgnoreCase) ||
													string.Equals(a.Name, "UnsafeBodyAttribute", StringComparison.OrdinalIgnoreCase));

		var tier = isUnsafeBody || func.Modifier == SafetyTier.Unsafe
			? SafetyTier.Unsafe
			: (func.Modifier == SafetyTier.Unbound ? SafetyTier.Unbound : SafetyTier.Safe);

		UnsafeContext.Reset(tier);

		// Unsafe tier: skip all safety checks entirely
		if (tier == SafetyTier.Unsafe)
			return;

		var scope = new SymbolTable(context.Globals);
		foreach (var param in func.Parameters)
		{
			var type = context.ResolveType(param.Type);
			if (type != null)
			{
				scope.Declare(new VariableSymbol(param.Name, type, false) { IsInitialized = true, Origin = OriginKind.Parameter });
				if (type is DelegateTypeSymbol)
					_delegateParams.Add(param.Name);
			}
		}

		// Unbound tier: relaxed checks (skip borrow exclusivity, but still do basic flow)
		CheckBlockSafety(func.Body, scope, func);
	}

	private void CheckBlockSafety(BlockStatementSyntax block, SymbolTable scope, FunctionDeclarationSyntax func)
	{
		if (block is null)
			return;

		var borrowState = Borrows.CaptureBlockState();
		var stmts = block.Statements;
		for (var i = 0; i < stmts.Count; i++)
		{
			var stmt = stmts[i];
			Borrows.ReleaseExpiredBorrows(stmt.Span.Start, block, i);
			CheckStatementSafety(stmt, scope, func);
		}

		// Release all borrows and refs taken in this block at block exit.
		foreach (var name in Borrows.ExitBlock(borrowState))
			ReferenceLifetimes.RemoveVariable(name);
	}

	private void CheckStatementSafety(SyntaxNode stmt, SymbolTable scope, FunctionDeclarationSyntax func)
	{
		if (stmt is null)
			return;

		switch (stmt)
		{
			case VariableDeclarationSyntax v:
				if (context.VariableSymbols.TryGetValue(v, out var sym))
				{
					scope.Declare(sym);
					if (v.Initializer != null)
					{
						CheckExpressionSafety(v.Initializer, scope);
						Moves.EmitLargeCopyWarningIfNeeded(v.Initializer, scope);

						// Propagate origin through ref/refvar declarations
						if (v.Type is "ref" or "refvar" && v.Initializer is BorrowExpressionSyntax borrowExpr)
						{
							var borrowedName = GetBaseIdentifierName(borrowExpr.Expression);
							if (borrowedName != null && scope.Lookup(borrowedName) is VariableSymbol borrowed)
								sym.Origin = borrowed.Origin;
						}

						// Heap-relative provenance: variables initialized with `heap ...` own
						// heap-allocated storage that deliberately outlives the function.
						if (v.Initializer is HeapAllocationExpressionSyntax)
							ReferenceLifetimes.MarkHeapVariable(v.Name);

						// Track what ref/refvar and nullable-reference-option variables point to,
						// so return-time provenance can resolve through reference chains.
						if (sym.Type is PointerTypeSymbol || (sym.Type is UnionTypeSymbol optU && optU.IsOption && optU.IsNpoEligible))
							ReferenceLifetimes.TrackReferenceTarget(v.Name, v.Initializer, scope, clearWhenMissing: false);

						Unbound.TrackLocalReferenceDeclaration(v);

						// Track ref field targets for struct variables (§3C)
						if (v.Type is not "ref" and not "refvar" && sym.Type is StructTypeSymbol)
							ReferenceLifetimes.TrackStructRefTargets(v.Name, v.Initializer, scope);

						// Track delegate provenance so escape rules can be enforced later (§13)
						if (sym.Type is DelegateTypeSymbol && v.Initializer != null)
							_delegateProvenances[v.Name] = GetDelegateProvenance(v.Initializer, scope);
					}

					// CVL1005: Raw pointer variables cannot be declared outside unsafe
					UnsafeContext.ValidateRawPointerDeclaration(sym, v.Span);

					Borrows.VerifyDeclarationBorrow(v);
				}

				break;

			case SwitchStatementSyntax sw:
				CheckSwitchStatementSafety(sw, scope, func);
				break;

			case ReturnStatementSyntax r:
				if (r.Expression != null) CheckExpressionSafety(r.Expression, scope);
				if (r.Expression != null && IsDelegateValueExpr(r.Expression, scope))
					CheckDelegateEscape(r.Expression, scope, r.Expression.Span, isReturn: true);
				ReferenceLifetimes.VerifyReturnLifetime(r, func, scope);
				break;

			case ExpressionStatementSyntax e:
				CheckExpressionSafety(e.Expression, scope);
				break;

			case IfStatementSyntax i:
				CheckExpressionSafety(i.Condition, scope);
				CheckStatementSafety(i.ThenStatement, scope, func);
				if (i.ElseClause != null) CheckStatementSafety(i.ElseClause.Body, scope, func);
				break;

			case BlockStatementSyntax b:
				CheckBlockSafety(b, new SymbolTable(scope), func);
				break;

			case LabeledBlockStatementSyntax lb:
				CheckBlockSafety(lb.Body, new SymbolTable(scope), func);
				break;

			case WhileStatementSyntax w:
				CheckExpressionSafety(w.Condition, scope);
				if (w.Body is BlockStatementSyntax wBlock)
					CheckBlockSafety(wBlock, new SymbolTable(scope), func);
				else
					CheckStatementSafety(w.Body, scope, func);
				break;

			case ForStatementSyntax f:
				if (f.Initializer != null) CheckStatementSafety(f.Initializer, scope, func);
				CheckExpressionSafety(f.Condition, scope);
				CheckExpressionSafety(f.Increment, scope);
				if (f.Body is BlockStatementSyntax fBlock)
					CheckBlockSafety(fBlock, new SymbolTable(scope), func);
				else
					CheckStatementSafety(f.Body, scope, func);
				break;

			case ForEachStatementSyntax fe:
				CheckExpressionSafety(fe.Collection, scope);
				if (fe.Body is BlockStatementSyntax feBlock)
				{
					CheckForEachContractViolations(fe, feBlock);
					CheckBlockSafety(feBlock, new SymbolTable(scope), func);
				}
				else
					CheckStatementSafety(fe.Body, scope, func);
				break;

			case UnsafeBlockStatementSyntax unsafeBlock:
				UnsafeContext.Push(SafetyTier.Unsafe);
				CheckBlockSafety(unsafeBlock.Body, new SymbolTable(scope), func);
				UnsafeContext.Pop();
				break;

			case BreakStatementSyntax:
			case ContinueStatementSyntax:
				break;
		}
	}

	/// <summary>
	/// Structural pre-pass over a foreach body enforcing the Hardened Iteration contract:
	/// - §2.B / CVL1088: a reference loop variable (refvar binding, or val over a ref-returning
	///   Current) may not cross the lexical boundary of the loop block.
	/// - §4.D: the collection identifier is under an immutable borrow contract for the whole loop;
	///   structural topology mutation (array reallocation, mutator calls, field writes on the
	///   collection) is blocked. Only element slot data via the loop variable may change.
	/// </summary>
	private void CheckForEachContractViolations(ForEachStatementSyntax fe, BlockStatementSyntax body)
	{
		var itemName = fe.ItemName;
		var collectionBase = GetBaseIdentifierName(fe.Collection);

		// Targets that outlive nothing beyond the body are safe: the loop variable itself (write-through
		// to the current element slot) and any variable declared anywhere within the body subtree.
		var safeTargets = new HashSet<string> { itemName };
		CollectLocalDeclNames(body, safeTargets);

		if (fe.IsReferenceBinding)
		{
			// CVL1088: returning the item (or a borrow of it) leaks the synthesized reference past the body.
			foreach (var ret in EnumerateNodes<ReturnStatementSyntax>(body))
			{
				if (ret.Expression is not null && BorrowTracker.ExpressionContainsRefUse(ret.Expression, itemName))
				{
					context.Diagnostics.Report(context.CurrentUnit!.Context, ret.Span,
						$"Escape Boundary Violation: reference loop variable '{itemName}' uses an internal stack provenance exception and cannot cross the lexical boundary of the loop block.",
						DiagnosticIds.ForeachEscapeBoundary);
				}
			}

			// CVL1088: storing the item into any location that outlives the loop block.
			foreach (var assign in EnumerateNodes<BinaryExpressionSyntax>(body))
			{
				if (assign.Operator != "=")
					continue;
				var lhsBase = GetBaseIdentifierName(assign.Left);
				if (lhsBase is not null && !safeTargets.Contains(lhsBase) && BorrowTracker.ExpressionContainsRefUse(assign.Right, itemName))
				{
					context.Diagnostics.Report(context.CurrentUnit!.Context, assign.Span,
						$"Escape Boundary Violation: reference loop variable '{itemName}' uses an internal stack provenance exception and cannot cross the lexical boundary of the loop block.",
						DiagnosticIds.ForeachEscapeBoundary);
				}
			}

			// CVL1088: passing the item into a reference parameter leaks it to the callee's frame,
			// which is only conditionally allowed and cannot be proved safe here.
			foreach (var call in EnumerateNodes<CallExpressionSyntax>(body))
			{
				if (!context.ResolvedCalls.TryGetValue(call, out var callee))
					continue;
				for (var i = 0; i < call.Arguments.Count && i < callee.Parameters.Count; i++)
				{
					if (callee.Parameters[i].Type is PointerTypeSymbol && BorrowTracker.ExpressionContainsRefUse(call.Arguments[i], itemName))
					{
						context.Diagnostics.Report(context.CurrentUnit!.Context, call.Arguments[i].Span,
							$"Escape Boundary Violation: reference loop variable '{itemName}' uses an internal stack provenance exception and cannot cross the lexical boundary of the loop block.",
							DiagnosticIds.ForeachEscapeBoundary);
					}
				}
			}
		}

		// §4.D immutable borrow contract on the collection identifier.
		if (collectionBase is not null)
		{
			foreach (var assign in EnumerateNodes<BinaryExpressionSyntax>(body))
			{
				if (assign.Operator == "=" && GetBaseIdentifierName(assign.Left) == collectionBase)
				{
					context.Diagnostics.Report(context.CurrentUnit!.Context, assign.Span,
						$"'{collectionBase}' is under an immutable borrow contract while it is being iterated: structural mutation is not allowed inside the 'foreach' body.");
				}
			}

			foreach (var call in EnumerateNodes<CallExpressionSyntax>(body))
			{
				var dot = call.FunctionName.IndexOf('.');
				if (dot > 0 && call.FunctionName.AsSpan(0, dot).SequenceEqual(collectionBase))
				{
					context.Diagnostics.Report(context.CurrentUnit!.Context, call.Span,
						$"'{collectionBase}' is under an immutable borrow contract while it is being iterated: mutating method calls are not allowed inside the 'foreach' body.");
				}
			}
		}
	}

	private static void CollectLocalDeclNames(SyntaxNode root, HashSet<string> into)
	{
		if (root is VariableDeclarationSyntax vd)
			into.Add(vd.Name);
		foreach (var child in root.GetChildren())
			CollectLocalDeclNames(child, into);
	}

	private static IEnumerable<TSyntax> EnumerateNodes<TSyntax>(SyntaxNode root) where TSyntax : SyntaxNode
	{
		if (root is TSyntax match)
			yield return match;
		foreach (var child in root.GetChildren())
			foreach (var nested in EnumerateNodes<TSyntax>(child))
				yield return nested;
	}

	private void CheckExpressionSafety(ExpressionSyntax expr, SymbolTable scope)
	{
		switch (expr)
		{
			case IdentifierExpressionSyntax id:
				Moves.VerifyReadable(id, scope);
				break;

			case NullLiteralExpressionSyntax:
				UnsafeContext.ValidateNullLiteral(expr.Span);
				break;

			case MemberAccessExpressionSyntax m:
				CheckExpressionSafety(m.Expression, scope);
				Unbound.ValidateMemberAccess(m, scope);
				break;

			case IndexExpressionSyntax idx:
				CheckExpressionSafety(idx.Left, scope);
				CheckExpressionSafety(idx.Index, scope);
				break;

			case BorrowExpressionSyntax b:
				CheckExpressionSafety(b.Expression, scope);
				break;

			case UnaryExpressionSyntax u:
				CheckExpressionSafety(u.Operand, scope);
				UnsafeContext.ValidateUnaryOperation(u);
				break;

			case StructInitializationExpressionSyntax init:
				foreach (var member in init.Initializers)
					CheckExpressionSafety(member.Expression, scope);
				break;

			case AsmExpressionSyntax asm:
				foreach (var operand in asm.Operands)
					CheckExpressionSafety(operand.Expression, scope);
				break;

			case CallExpressionSyntax call:
				foreach (var arg in call.Arguments)
				{
					CheckExpressionSafety(arg, scope);
					Moves.HandleByValueArgument(arg, scope);
				}

				break;

			case LambdaExpressionSyntax lam:
				// §8.7 capture sources are outer locals + by-value parameters only; borrowed-ref
				// lexical bindings are never capture sources (§8.8). Compute the set from the
				// enclosing scope: any identifier in the body resolving to a non-global variable.
				var capturedNames = ComputeCapturedNames(lam, scope);

				// Capture-policy validation (§8.2/8.3/8.5/8.7/8.9)
				foreach (var name in capturedNames)
				{
					if (scope.Lookup(name) is not VariableSymbol capturedSym) continue;

					if (capturedSym.Type is PointerTypeSymbol)
					{
						// §8.8 borrowed-ref lexical bindings are never capture sources in any mode.
						context.Diagnostics.Report(context.CurrentUnit!.Context, lam.Span,
							$"Cannot capture reference binding '{name}' in a lambda; capture sources must be by-value locals and parameters.",
							DiagnosticIds.RefBindingCaptureUnsupported);
					}
					else if (DelegateTypeHelpers.ContainsMutableBorrowCapability(capturedSym.Type))
					{
						// §8.9 the captured value carries a mutable-borrow capability.
						context.Diagnostics.Report(context.CurrentUnit!.Context, lam.Span,
							$"Cannot capture '{name}' of type '{capturedSym.Type.Name}': the type carries a mutable-borrow capability (refvar/slice/aggregate) which may not be captured.",
							DiagnosticIds.MutableBorrowCapabilityCapture);
					}
					else if (lam.CaptureMode == LambdaCaptureMode.Default
							 && Moves.IsMoveOnly(capturedSym.Type))
					{
						// §8.2 default mode copies a snapshot; move-only values cannot be copied.
						context.Diagnostics.Report(context.CurrentUnit!.Context, lam.Span,
							$"Cannot capture '{name}' in default mode: '{capturedSym.Type.Name}' is move-only and cannot be copied; use 'move' or 'ref' capture.",
							DiagnosticIds.DefaultModeCaptureOfMoveOnly);
					}

					if (lam.CaptureMode == LambdaCaptureMode.Move
						&& Moves.IsMoveOnly(capturedSym.Type))
					{
						// §8.3 'move' capture moves the move-only source: it becomes unavailable.
						Moves.MarkMoved(capturedSym);
					}

					if (lam.CaptureMode == LambdaCaptureMode.Ref)
					{
						// §8.6 active immutable-borrow lock while the lambda may be invoked:
						// mutation, move, and incompatible mutable borrows of the captured
						// variable are blocked while the borrow is live.
						RegisterRefCaptureLock(name, lam.Span);
					}
				}

				// Lambda bodies are validated as safe-callable bodies regardless of the
				// enclosing tier (§21.3). Check the body inside a child scope holding the
				// lambda parameters, pushing the capture set for captured-field checks.
				var lambdaScope = new SymbolTable(scope);
				if (context.ResolvedLambdas.TryGetValue(lam, out var lamInfo))
				{
					for (var i = 0; i < lam.Parameters.Count && i < lamInfo.ParameterTypes.Count; i++)
						lambdaScope.Declare(new VariableSymbol(lam.Parameters[i].Name, lamInfo.ParameterTypes[i], false) { IsInitialized = true, Origin = OriginKind.Parameter });
				}
				else
				{
					foreach (var p in lam.Parameters)
						lambdaScope.Declare(new VariableSymbol(p.Name, TypeSymbol.Int, false) { IsInitialized = true, Origin = OriginKind.Parameter });
				}

				_lambdaCaptureSets.Push(capturedNames);
				var savedTier = UnsafeContext.PopOrSafe();
				UnsafeContext.Push(SafetyTier.Safe);
				try
				{
					if (lam.ExpressionBody != null)
						CheckExpressionSafety(lam.ExpressionBody, lambdaScope);
					else if (lam.BlockBody != null)
						CheckBlockSafety(lam.BlockBody, lambdaScope, _enclosingFunc!);
				}
				finally
				{
					UnsafeContext.Pop();
					if (savedTier != SafetyTier.Safe) UnsafeContext.Push(savedTier);
					_lambdaCaptureSets.Pop();
				}
				break;

			case BinaryExpressionSyntax bin:
				CheckExpressionSafety(bin.Right, scope);
				if (bin.Operator == "=")
				{
					// §8.2 captured variables (immutable snapshots / borrows) may not be assigned
					// or mutated while the enclosing lambda body is being checked (CVL1312/1313).
					if (_lambdaCaptureSets.Count > 0)
					{
						var lhsBase = GetBaseIdentifierName(bin.Left);
						if (lhsBase is not null && _lambdaCaptureSets.Peek().Contains(lhsBase))
						{
							context.Diagnostics.Report(context.CurrentUnit!.Context, bin.Span,
								$"Cannot assign to captured variable '{lhsBase}': captured variables are immutable within the lambda body.",
								bin.Left is MemberAccessExpressionSyntax
									? DiagnosticIds.CapturedFieldMutation
									: DiagnosticIds.CapturedFieldAssignment);
						}
					}

					// §13.3 store to longer-lived storage: a capturing/ref lambda or a bound method
					// may not be stored into long-lived (global or parameter-origin) storage.
					if (IsLongLivedStoreDestination(bin.Left, scope) && IsDelegateValueExpr(bin.Right, scope))
						CheckDelegateEscape(bin.Right, scope, bin.Span, isReturn: false);

					// Propagate delegate provenance through reassignment of a delegate-typed variable.
					if (GetBaseIdentifierName(bin.Left) is { } lhsName
						&& scope.Lookup(lhsName) is VariableSymbol reSym
						&& reSym.Type is DelegateTypeSymbol)
					{
						_delegateProvenances[lhsName] = GetDelegateProvenance(bin.Right, scope);
					}
				}
				if (bin.Operator == "=" && bin.Left is IdentifierExpressionSyntax leftId)
				{
					var leftSymbol = scope.Lookup(leftId.Name) as VariableSymbol
						?? context.ResolveGlobalReference(leftId.Name, out _);
					if (leftSymbol is not null)
					{
						Borrows.VerifyUnlocked(bin.Left, "reassign");
						Moves.ResetMoved(leftSymbol);
						Moves.HandleCopyAssignment(bin.Right, scope);

						Unbound.ValidateGlobalAssignmentEscape(bin, leftSymbol, scope);

						// Track ref field targets for struct reassignment (§3C)
						if (leftSymbol.Type is StructTypeSymbol)
							ReferenceLifetimes.TrackStructRefTargets(leftId.Name, bin.Right, scope);

						// Track ref/refvar and nullable-reference-option reassignment for return-time provenance
						if (leftSymbol.Type is PointerTypeSymbol || (leftSymbol.Type is UnionTypeSymbol optU && optU.IsOption && optU.IsNpoEligible))
							ReferenceLifetimes.TrackReferenceTarget(leftId.Name, bin.Right, scope, clearWhenMissing: true);

						// Propagate origin on ref/refvar reassignment
						if (leftSymbol.Type is PointerTypeSymbol)
						{
							if (bin.Right is BorrowExpressionSyntax rb)
							{
								var rightName = GetBaseIdentifierName(rb.Expression);
								if (rightName != null && scope.Lookup(rightName) is VariableSymbol rightSym)
								{
									leftSymbol.Origin = rightSym.Origin;

									// §3F Global Lifetime Inequality: only global-origin refs may be stored in globals
									if (leftSymbol.IsGlobal && rightSym.Origin != OriginKind.Global)
									{
										context.Diagnostics.Report(context.CurrentUnit!.Context, bin.Span,
											$"Cannot assign {rightSym.Origin.ToString().ToLower()}-origin reference to global variable '{leftId.Name}': only global-origin references may be stored in globals");
									}
								}
							}
							else if (bin.Right is IdentifierExpressionSyntax rightId && scope.Lookup(rightId.Name) is VariableSymbol rightSym2 && rightSym2.Type is PointerTypeSymbol)
							{
								leftSymbol.Origin = rightSym2.Origin;

								// §3F Global Lifetime Inequality
								if (leftSymbol.IsGlobal && rightSym2.Origin != OriginKind.Global)
								{
									context.Diagnostics.Report(context.CurrentUnit!.Context, bin.Span,
										$"Cannot assign {rightSym2.Origin.ToString().ToLower()}-origin reference to global variable '{leftId.Name}': only global-origin references may be stored in globals");
								}
							}
						}
					}
				}
				else
				{
					CheckExpressionSafety(bin.Left, scope);

					Unbound.ValidateReferenceFieldAssignment(bin, scope);
				}

				break;
		}
	}

	private TypeSymbol? ResolveExpressionType(ExpressionSyntax expr, SymbolTable scope)
	{
		return expr switch
		{
			IdentifierExpressionSyntax id => scope.Lookup(id.Name) is VariableSymbol v ? v.Type : null,
			CallExpressionSyntax call => context.ResolvedCalls.TryGetValue(call, out var func) ? func.ReturnType : null,
			StructInitializationExpressionSyntax init => context.ResolveType(init.StructTypeName),
			BorrowExpressionSyntax borrow => new PointerTypeSymbol(ResolveExpressionType(borrow.Expression, scope) ?? TypeSymbol.Int, borrow.IsMutable),

			_ => null
		};
	}

	/// <summary>
	/// Compute the set of by-value outer locals and parameters referenced by a lambda body.
	/// References to globals or to identities shadowed inside the body are not captures.
	/// </summary>
	private HashSet<string> ComputeCapturedNames(LambdaExpressionSyntax lam, SymbolTable scope)
	{
		var captured = new HashSet<string>();
		var body = lam.BlockBody is not null ? (SyntaxNode)lam.BlockBody : lam.ExpressionBody!;
		if (body is null) return captured;

		foreach (var id in EnumerateNodes<IdentifierExpressionSyntax>(body))
		{
			if (captured.Contains(id.Name)) continue;
			if (scope.Lookup(id.Name) is VariableSymbol v && !v.IsGlobal)
				captured.Add(id.Name);
		}
		return captured;
	}

	/// <summary>
	/// §8.6/§8.9 ref-lambda capture: the captured variable is under an immutable-borrow lock for
	/// as long as the lambda may be invoked. Reassignment, movement, and incompatible mutable
	/// borrows of the captured variable are blocked (surface via the existing borrow machinery).
	/// </summary>
	private void RegisterRefCaptureLock(string name, TextSpan span)
	{
		var lockName = "$refλ:" + name;
		Borrows.RegisterBorrow(lockName, name, false, span);
	}

	/// <summary>
	/// Compute the delegate provenance (context) of a delegate value expression. Used to enforce
	/// escape rules for delegation: a capturing/ref lambda's closure environment may not escape its
	/// owner block, and a bound method may not outlive its receiver.
	/// </summary>
	private DelegateProvenance GetDelegateProvenance(ExpressionSyntax expr, SymbolTable scope)
	{
		switch (expr)
		{
			case LambdaExpressionSyntax lam:
				var captures = ComputeCapturedNames(lam, scope);
				if (captures.Count == 0) return new DelegateProvenance(DelegateProvenanceKind.Free, [], null);
				return new DelegateProvenance(
					lam.CaptureMode == LambdaCaptureMode.Ref ? DelegateProvenanceKind.RefBorrow : DelegateProvenanceKind.Capturing,
					[.. captures], null);
			case MemberAccessExpressionSyntax ma:
				// A delegate field or method group bound to a receiver: context == &receiver.
				// Only classify as a bound delegate value when the accessed member is genuinely a
				// delegate-typed field or a method group; a plain member read (e.g. 's.Id' where
				// Id is an int field) is not a delegate value.
				var memberReceiverType = ResolveExpressionType(ma.Expression, scope);
				if (memberReceiverType is PointerTypeSymbol memberReceiverPtr)
					memberReceiverType = memberReceiverPtr.ReferencedType;
				if (memberReceiverType is StructTypeSymbol receiverStruct && receiverStruct.FindField(ma.MemberName)?.Type is DelegateTypeSymbol)
					return new DelegateProvenance(DelegateProvenanceKind.BoundMethod, [], GetBaseIdentifierName(ma.Expression));
				if (memberReceiverType is UnionTypeSymbol receiverUnion && receiverUnion.FindField(ma.MemberName)?.Type is DelegateTypeSymbol)
					return new DelegateProvenance(DelegateProvenanceKind.BoundMethod, [], GetBaseIdentifierName(ma.Expression));
				if (memberReceiverType is not null &&
					context.GetExtensionMethodCandidates(memberReceiverType, context.CurrentUnit, ma.MemberName).Count > 0)
					return new DelegateProvenance(DelegateProvenanceKind.BoundMethod, [], GetBaseIdentifierName(ma.Expression));
				return new DelegateProvenance(DelegateProvenanceKind.Free, [], null);
			case IdentifierExpressionSyntax id when scope.Lookup(id.Name) is VariableSymbol dv && dv.Type is not DelegateTypeSymbol:
				return new DelegateProvenance(DelegateProvenanceKind.Free, [], null);
			case IdentifierExpressionSyntax id when _delegateProvenances.TryGetValue(id.Name, out var prior):
				return prior;
			default:
				return new DelegateProvenance(DelegateProvenanceKind.Free, [], null);
		}
	}

	/// <summary>
	/// §13 escape rules for a delegate value crossing a lifetime boundary (returned from the
	/// function, or stored into long-lived storage such as a global). Free delegates (context==null)
	/// may escape; closure environments and borrowed receivers may not.
	/// </summary>
	private void CheckDelegateEscape(ExpressionSyntax expr, SymbolTable scope, TextSpan span, bool isReturn)
	{
		if (expr is not (LambdaExpressionSyntax or MemberAccessExpressionSyntax or IdentifierExpressionSyntax))
			return;

		// Delegate parameters are non-escaping (§13.0): they may be invoked, copied to call-local
		// storage, or forwarded to another non-escaping parameter — never returned or stored long-lived.
		if (expr is IdentifierExpressionSyntax id && _delegateParams.Contains(id.Name))
		{
			context.Diagnostics.Report(context.CurrentUnit!.Context, span,
				isReturn
					? $"Cannot return delegate parameter '{id.Name}': safe-delegate parameters are non-escaping."
					: $"Cannot store delegate parameter '{id.Name}' into long-lived storage: safe-delegate parameters are non-escaping.",
				isReturn ? DiagnosticIds.ParameterReturnedUnsupported : DiagnosticIds.DelegateParameterEscapes);
			return;
		}

		var provenance = GetDelegateProvenance(expr, scope);
		switch (provenance.Kind)
		{
			case DelegateProvenanceKind.Capturing:
				context.Diagnostics.Report(context.CurrentUnit!.Context, span,
					isReturn
						? $"Captured closure environment cannot escape: the lambda captures '{string.Join(", ", provenance.CapturedNames)}' and may not cross the return boundary."
						: $"Captured closure environment cannot escape: the lambda captures '{string.Join(", ", provenance.CapturedNames)}' and may not be stored into long-lived storage.",
					DiagnosticIds.ClosureEnvironmentEscapes);
				break;
			case DelegateProvenanceKind.RefBorrow:
				context.Diagnostics.Report(context.CurrentUnit!.Context, span,
					isReturn
						? $"Reference lambda borrow cannot escape: the lambda borrows '{string.Join(", ", provenance.CapturedNames)}' and may not cross the return boundary."
						: $"Reference lambda borrow cannot escape: the lambda borrows '{string.Join(", ", provenance.CapturedNames)}' and may not be stored into long-lived storage.",
					DiagnosticIds.RefLambdaBorrowEscapes);
				break;
			case DelegateProvenanceKind.BoundMethod:
				var receiver = provenance.ReceiverBase;
				if (receiver is null) return;
				if (scope.Lookup(receiver) is VariableSymbol recvSym)
				{
					if (recvSym.IsGlobal) return; // global receiver outlives everything
					context.Diagnostics.Report(context.CurrentUnit!.Context, span,
						$"Bound method on receiver '{receiver}' cannot escape: the receiver does not outlive the delegate.",
						DiagnosticIds.BoundReceiverLifetimeEscapes);
				}
				break;
		}
	}

	/// <summary>
	/// True when the expression is a delegate value: a lambda, a member-access bound method/
	/// delegate field, or an identifier resolving to a delegate-typed variable.
	/// </summary>
	private bool IsDelegateValueExpr(ExpressionSyntax expr, SymbolTable scope)
	{
		return expr switch
		{
			LambdaExpressionSyntax => true,
			MemberAccessExpressionSyntax ma =>
				GetDelegateProvenance(ma, scope).Kind == DelegateProvenanceKind.BoundMethod,
			IdentifierExpressionSyntax id => scope.Lookup(id.Name) is VariableSymbol { Type: DelegateTypeSymbol },
			_ => false,
		};
	}

	/// <summary>
	/// §13.0.3 write-through escape destination: a global variable, or storage reachable through a
	/// parameter (parameter-origin). Delegates stored there must be provenance-free.
	/// </summary>
	private bool IsLongLivedStoreDestination(ExpressionSyntax lhs, SymbolTable scope)
	{
		var baseName = GetBaseIdentifierName(lhs);
		if (baseName is null) return false;
		if (scope.Lookup(baseName) is not VariableSymbol dest) return false;
		if (dest.IsGlobal) return true;
		if (dest.Origin == OriginKind.Parameter)
		{
			// Optional reference parameters and writable aggregate parameters with a delegate in their
			// projection are escaping destinations; a by-value non-delegate parameter may be a store.
			return DelegateTypeHelpers.ContainsSafeDelegate(dest.Type);
		}
		return false;
	}

	private string? GetBaseIdentifierName(ExpressionSyntax expr)
	{
		if (expr is IdentifierExpressionSyntax id) return id.Name;
		if (expr is MemberAccessExpressionSyntax m) return GetBaseIdentifierName(m.Expression);
		if (expr is IndexExpressionSyntax idx) return GetBaseIdentifierName(idx.Left);
		if (expr is BorrowExpressionSyntax b) return GetBaseIdentifierName(b.Expression);
		return null;
	}


	private void CheckSwitchStatementSafety(SwitchStatementSyntax sw, SymbolTable scope, FunctionDeclarationSyntax func)
	{
		CheckExpressionSafety(sw.Expression, scope);
		var parentName = GetBaseIdentifierName(sw.Expression);

		foreach (var c in sw.Cases)
		{
			var targetType = ResolveExpressionType(sw.Expression, scope);
			var hasRefPromotion = c.VariableName is not null && targetType is PointerTypeSymbol;

			if (hasRefPromotion && parentName is not null)
			{
				var isMutable = targetType is PointerTypeSymbol targetPtr && targetPtr.IsMutable;
				Borrows.RegisterBorrow(c.VariableName!, parentName, isMutable, c.Span);
			}

			CheckBlockSafety(new BlockStatementSyntax(c.Span, c.Body), new SymbolTable(scope), func);

			if (hasRefPromotion && parentName is not null)
			{
				Borrows.RemoveBorrower(c.VariableName!);
			}
		}
	}
}
