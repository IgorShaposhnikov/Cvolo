using Cvolo.Analysis.Symbols;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Borrowing;
using Cvolo.Analysis.Symbols.Collections;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using Cvolo.Core.AST.Expressions;
using Cvolo.Core.AST.Statements;
using Cvolo.Core.Diagnostics;
using Cvolo.Analysis.VisibilityChecks;

namespace Cvolo.Analysis.Passes;

public sealed class SafetyPass(BindingContext context)
{
	private readonly List<BorrowSymbol> _activeBorrows = [];
	private readonly Dictionary<string, (string BorrowedName, bool IsMutable, int LastUseEnd, TextSpan DeclSpan)> _activeRefs = [];
	private readonly Dictionary<string, HashSet<string>> _parentLocks = []; // parentVar -> set of refVar names
	private readonly Dictionary<string, HashSet<string>> _structRefTargets = []; // structVar -> set of variable names that ref fields point to
	private readonly Stack<SafetyTier> _currentTierStack = [];
	private readonly HashSet<string> _localRefsInUnboundScope = []; // refvar/ref variables declared inside the current unbound scope (including nested unsafe blocks)
	private readonly Dictionary<string, string> _refVarTargets = []; // ref/refvar (and nullable-reference-option) variable -> base identifier it currently points to
	private readonly HashSet<string> _heapVariables = []; // local variables initialized with `heap ...` (their storage outlives the function — heap-relative provenance)
	private ClassificationAnalyzer? _classification;

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

	private ClassificationAnalyzer Classification => _classification ??= new ClassificationAnalyzer(context);
	private SafetyTier CurrentTier => _currentTierStack.Count > 0 ? _currentTierStack.Peek() : SafetyTier.Safe;

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

		_activeBorrows.Clear();
		_activeRefs.Clear();
		_parentLocks.Clear();
		_structRefTargets.Clear();
		_localRefsInUnboundScope.Clear();
		_refVarTargets.Clear();
		_heapVariables.Clear();
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

		_currentTierStack.Clear();
		_currentTierStack.Push(tier);

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

		var borrowCountBefore = _activeBorrows.Count;
		var refsAtEntry = new HashSet<string>(_activeRefs.Keys);
		var stmts = block.Statements;
		for (var i = 0; i < stmts.Count; i++)
		{
			var stmt = stmts[i];
			ReleaseExpiredBorrows(stmt.Span.Start, block, i);
			CheckStatementSafety(stmt, scope, func);
		}

		// Release all borrows and refs taken in this block at block exit.
		if (_activeBorrows.Count > borrowCountBefore)
			_activeBorrows.RemoveRange(borrowCountBefore, _activeBorrows.Count - borrowCountBefore);
		var refsToRemove = _activeRefs.Keys.Where(k => !refsAtEntry.Contains(k)).ToList();
		foreach (var name in refsToRemove)
		{
			_activeRefs.Remove(name);
			ReleaseParentLock(name);
			_structRefTargets.Remove(name);
			_refVarTargets.Remove(name);
		}
	}

	/// <summary>
	/// For each active ref, check if it has any uses in statements from startIndex onward.
	/// If a ref has no uses in remaining statements, release its borrow early.
	/// </summary>
	private void ReleaseExpiredBorrows(int currentStatementStart, BlockStatementSyntax block, int startIndex)
	{
		var stmts = block.Statements;
		var toRelease = new List<string>();

		foreach (var kv in _activeRefs)
		{
			var refName = kv.Key;
			var hasUseAfterCurrent = false;

			for (var j = startIndex; j < stmts.Count; j++)
			{
				if (NodeContainsRefUse(stmts[j], refName))
				{
					hasUseAfterCurrent = true;
					break;
				}
			}

			if (!hasUseAfterCurrent)
				toRelease.Add(refName);
		}

		foreach (var refName in toRelease)
		{
			_activeRefs.Remove(refName);
			_activeBorrows.RemoveAll(b => b.BorrowerName == refName);
			ReleaseParentLock(refName);
		}
	}

	/// <summary>
	/// Check whether a syntax node (recursively, including branches and loops)
	/// contains any use of the given identifier name.
	/// </summary>
	private static bool NodeContainsRefUse(SyntaxNode node, string refName)
	{
		return node switch
		{
			BlockStatementSyntax block => block.Statements.Any(s => NodeContainsRefUse(s, refName)),
			IfStatementSyntax ifStmt =>
				NodeContainsRefUse(ifStmt.Condition, refName) ||
				NodeContainsRefUse(ifStmt.ThenStatement, refName) ||
				(ifStmt.ElseClause != null && NodeContainsRefUse(ifStmt.ElseClause.Body, refName)),
			WhileStatementSyntax whileStmt =>
				NodeContainsRefUse(whileStmt.Condition, refName) ||
				NodeContainsRefUse(whileStmt.Body, refName),
			ForStatementSyntax forStmt =>
				(forStmt.Initializer != null && NodeContainsRefUse(forStmt.Initializer, refName)) ||
				NodeContainsRefUse(forStmt.Condition, refName) ||
				NodeContainsRefUse(forStmt.Increment, refName) ||
				NodeContainsRefUse(forStmt.Body, refName),
			ForEachStatementSyntax forEach =>
				NodeContainsRefUse(forEach.Collection, refName) ||
				NodeContainsRefUse(forEach.Body, refName),
			ReturnStatementSyntax ret => ret.Expression != null && ExprContainsRefUse(ret.Expression, refName),
			ExpressionStatementSyntax exprStmt => ExprContainsRefUse(exprStmt.Expression, refName),
			VariableDeclarationSyntax varDecl => varDecl.Initializer != null && ExprContainsRefUse(varDecl.Initializer, refName),
			ExpressionSyntax expr => ExprContainsRefUse(expr, refName),
			_ => false
		};
	}

	/// <summary>
	/// Check whether an expression (recursively) references the given identifier name.
	/// </summary>
	private static bool ExprContainsRefUse(ExpressionSyntax expr, string refName)
	{
		return expr switch
		{
			IdentifierExpressionSyntax id => id.Name == refName,
			MemberAccessExpressionSyntax m => ExprContainsRefUse(m.Expression, refName),
			IndexExpressionSyntax idx => ExprContainsRefUse(idx.Left, refName) || ExprContainsRefUse(idx.Index, refName),
			BorrowExpressionSyntax borrow => ExprContainsRefUse(borrow.Expression, refName),
			CallExpressionSyntax call => call.Arguments.Any(a => ExprContainsRefUse(a, refName)),
			BinaryExpressionSyntax bin => ExprContainsRefUse(bin.Left, refName) || ExprContainsRefUse(bin.Right, refName),
			StructInitializationExpressionSyntax init => init.Initializers.Any(f => ExprContainsRefUse(f.Expression, refName)),
			_ => false
		};
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
						EmitLargeCopyWarningIfNeeded(v.Initializer, scope);

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
							_heapVariables.Add(v.Name);

						// Track what ref/refvar and nullable-reference-option variables point to,
						// so return-time provenance can resolve through reference chains.
						if (sym.Type is PointerTypeSymbol || (sym.Type is UnionTypeSymbol optU && optU.IsOption && optU.IsNpoEligible))
						{
							var targetBase = TryGetPayloadBase(v.Initializer, scope);
							if (targetBase != null)
								_refVarTargets[v.Name] = targetBase;
						}

						// Track refvar/ref declarations inside unbound scope for CVL1008
						// Uses stack check (not CurrentTier) so nested unsafe blocks inside unbound are still tracked
						if (v.Type is not null && v.Type.StartsWith("ref") && _currentTierStack.Contains(SafetyTier.Unbound))
							_localRefsInUnboundScope.Add(v.Name);

						// Track ref field targets for struct variables (§3C)
						if (v.Type is not "ref" and not "refvar" && sym.Type is StructTypeSymbol)
							TrackStructRefTargets(v.Name, v.Initializer, scope);

						// Track delegate provenance so escape rules can be enforced later (§13)
						if (sym.Type is DelegateTypeSymbol && v.Initializer != null)
							_delegateProvenances[v.Name] = GetDelegateProvenance(v.Initializer, scope);
					}

					// CVL1005: Raw pointer variables cannot be declared outside unsafe
					if (sym.Type is RawPointerTypeSymbol && CurrentTier != SafetyTier.Unsafe)
					{
						context.Diagnostics.Report(context.CurrentUnit!.Context, v.Span,
							"Raw pointer variables cannot be declared outside unsafe context.");
					}

					VerifyBorrowRules(v, scope);
				}

				break;

			case SwitchStatementSyntax sw:
				CheckSwitchStatementSafety(sw, scope, func);
				break;

			case ReturnStatementSyntax r:
				if (r.Expression != null) CheckExpressionSafety(r.Expression, scope);
				if (r.Expression != null && IsDelegateValueExpr(r.Expression, scope))
					CheckDelegateEscape(r.Expression, scope, r.Expression.Span, isReturn: true);
				VerifyReturnLifetime(r, func, scope);
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
				_currentTierStack.Push(SafetyTier.Unsafe);
				CheckBlockSafety(unsafeBlock.Body, new SymbolTable(scope), func);
				_currentTierStack.Pop();
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
				if (ret.Expression is not null && ExprContainsRefUse(ret.Expression, itemName))
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
				if (lhsBase is not null && !safeTargets.Contains(lhsBase) && ExprContainsRefUse(assign.Right, itemName))
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
					if (callee.Parameters[i].Type is PointerTypeSymbol && ExprContainsRefUse(call.Arguments[i], itemName))
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
				if ((scope.Lookup(id.Name) as VariableSymbol ?? context.ResolveGlobalReference(id.Name, out _)) is { IsMoved: true })
					context.Diagnostics.Report(context.CurrentUnit!.Context, id.Span, $"Use of moved variable '{id.Name}'");
				break;

			case NullLiteralExpressionSyntax:
				// CVL1104: the null literal is only meaningful as a null pointer / empty
				// option. In safe or unbound code it is always an error.
				if (CurrentTier != SafetyTier.Unsafe)
					context.Diagnostics.Report(context.CurrentUnit!.Context, expr.Span,
						"null is not allowed in safe code. Use Option.None instead.",
						DiagnosticIds.NullForOptionalType);
				break;

			case MemberAccessExpressionSyntax m:
				CheckExpressionSafety(m.Expression, scope);
				ReportUnboundRefFieldVisibilityLeak(m.Expression, m.MemberName, m.Span, scope);
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
				// CVL1006: Dereference only in unsafe
				if (u.Operator == "*" && CurrentTier != SafetyTier.Unsafe)
					context.Diagnostics.Report(context.CurrentUnit!.Context, u.Span, "Cannot dereference outside unsafe context.");
				// CVL1007: Address-of only in unsafe
				if (u.Operator == "&" && CurrentTier != SafetyTier.Unsafe)
					context.Diagnostics.Report(context.CurrentUnit!.Context, u.Span, "Cannot take address outside unsafe context.");
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
					HandleByValueArgument(arg, scope);
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
                             && Classification.Classify(capturedSym.Type) == CopyKind.ResourceMove)
                    {
                        // §8.2 default mode copies a snapshot; move-only values cannot be copied.
                        context.Diagnostics.Report(context.CurrentUnit!.Context, lam.Span,
                            $"Cannot capture '{name}' in default mode: '{capturedSym.Type.Name}' is move-only and cannot be copied; use 'move' or 'ref' capture.",
                            DiagnosticIds.DefaultModeCaptureOfMoveOnly);
                    }

                    if (lam.CaptureMode == LambdaCaptureMode.Move
                        && Classification.Classify(capturedSym.Type) == CopyKind.ResourceMove)
                    {
                        // §8.3 'move' capture moves the move-only source: it becomes unavailable.
                        capturedSym.IsMoved = true;
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
				var savedTier = _currentTierStack.Count > 0 ? _currentTierStack.Pop() : SafetyTier.Safe;
				_currentTierStack.Push(SafetyTier.Safe);
				try
				{
					if (lam.ExpressionBody != null)
						CheckExpressionSafety(lam.ExpressionBody, lambdaScope);
					else if (lam.BlockBody != null)
						CheckBlockSafety(lam.BlockBody, lambdaScope, _enclosingFunc!);
				}
				finally
				{
					_currentTierStack.Pop();
					if (savedTier != SafetyTier.Safe) _currentTierStack.Push(savedTier);
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
						VerifyBorrowLock(bin.Left, scope, "reassign");
						leftSymbol.IsMoved = false;
						HandleCopyAssignment(bin.Right, scope);

						// CVL1008: Escape prevention — local refvar cannot escape unbound scope to globals
						if (_currentTierStack.Contains(SafetyTier.Unbound) && leftSymbol.IsGlobal && IsLocalUnboundRef(bin.Right, scope))
						{
							context.Diagnostics.Report(context.CurrentUnit!.Context, bin.Span,
								$"Reference cannot escape unbound scope: cannot assign local reference to global variable '{leftId.Name}'");
						}

						// Track ref field targets for struct reassignment (§3C)
						if (leftSymbol.Type is StructTypeSymbol)
							TrackStructRefTargets(leftId.Name, bin.Right, scope);

						// Track ref/refvar and nullable-reference-option reassignment for return-time provenance
						if (leftSymbol.Type is PointerTypeSymbol || (leftSymbol.Type is UnionTypeSymbol optU && optU.IsOption && optU.IsNpoEligible))
						{
							var targetBase = TryGetPayloadBase(bin.Right, scope);
							if (targetBase != null)
								_refVarTargets[leftId.Name] = targetBase;
							else
								_refVarTargets.Remove(leftId.Name);
						}

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

					// Structural Field-Mutation Isolation (§2 Rule 7): safe code must not directly write
					// to a struct's `refvar`/`ref` reference fields; it may only read or traverse them.
					// Modifying a structural reference field requires an `unbound` block or function.
					// Raw-pointer fields are unaffected (they are not references and remain freely writable).
					if (bin.Operator == "="
						&& bin.Left is MemberAccessExpressionSyntax fieldWrite
						&& CurrentTier != SafetyTier.Unbound
						&& GetRefFieldName(fieldWrite.Expression, fieldWrite.MemberName, scope) is { } mutRefField)
					{
						var baseName = GetBaseIdentifierName(fieldWrite.Expression) ?? "?";
						context.Diagnostics.Report(context.CurrentUnit!.Context, bin.Span,
							$"Cannot assign to reference field '{mutRefField}' of variable '{baseName}' in safe code. Use an 'unbound' block or function to modify structural reference fields.");
					}

					// CVL1035: The unbound sandbox suspends the borrow checker but NOT visibility.
					// A ref/refvar field that is private/internal and declared in another compilation
					// unit cannot be structurally mutated (or traversed, handled above) from here.
					if (bin.Operator == "=" && _currentTierStack.Contains(SafetyTier.Unbound) &&
						bin.Left is MemberAccessExpressionSyntax unboundWrite)
					{
						ReportUnboundRefFieldVisibilityLeak(unboundWrite.Expression, unboundWrite.MemberName, bin.Span, scope);
					}

					// CVL1008: Escape prevention for reference-field stores. A local reference declared
					// inside unbound scope must not escape into a reference field of an external (non-local)
					// struct object (a parameter or a global), which outlives the unbound scope and would
					// otherwise dangle.
					if (bin.Operator == "="
						&& _currentTierStack.Contains(SafetyTier.Unbound)
						&& IsLocalUnboundRef(bin.Right, scope)
						&& bin.Left is MemberAccessExpressionSyntax member
						&& IsExternalEscapeBase(member.Expression, scope)
						&& GetRefFieldName(member.Expression, member.MemberName, scope) is { } refField)
					{
						var baseName = GetBaseIdentifierName(member.Expression) ?? "?";
						context.Diagnostics.Report(context.CurrentUnit!.Context, bin.Span,
							$"Reference cannot escape unbound scope: cannot assign local reference to reference field '{refField}' of non-local variable '{baseName}'");
					}
				}

				break;
		}
	}

	private void HandleByValueArgument(ExpressionSyntax arg, SymbolTable scope)
	{
		if (arg is StructInitializationExpressionSyntax or BorrowExpressionSyntax)
			return;

		var type = ResolveExpressionType(arg, scope);

		if (type is StructTypeSymbol or UnionTypeSymbol)
		{
			var kind = Classification.Classify(type);
			switch (kind)
			{
				case CopyKind.ResourceMove:
					if (arg is IdentifierExpressionSyntax aid && scope.Lookup(aid.Name) is VariableSymbol av)
					{
						VerifyBorrowLock(arg, scope, "move");
						av.IsMoved = true;
					}
					break;
				case CopyKind.LargeCopy:
					var size = Classification.CalculateByteSize(type);
					context.Diagnostics.ReportWarning(
						context.CurrentUnit!.Context, arg.Span,
						$"'{type.Name}' is {size} bytes. Copying by value duplicates the payload. Consider passing by 'ref'.",
						DiagnosticIds.LargeCopyWarning);
					break;
			}
		}
		else if (type is SliceTypeSymbol)
		{
			if (arg is IdentifierExpressionSyntax sid && scope.Lookup(sid.Name) is VariableSymbol sv)
			{
				VerifyBorrowLock(arg, scope, "move");
				sv.IsMoved = true;
			}
		}
	}

	private void EmitLargeCopyWarningIfNeeded(ExpressionSyntax expr, SymbolTable scope)
	{
		if (expr is StructInitializationExpressionSyntax)
			return;

		var type = ResolveExpressionType(expr, scope);
		if (type is StructTypeSymbol st)
		{
			var kind = Classification.Classify(st);
			if (kind == CopyKind.LargeCopy)
			{
				var size = Classification.CalculateByteSize(st);
				context.Diagnostics.ReportWarning(
					context.CurrentUnit!.Context, expr.Span,
					$"'{st.Name}' is {size} bytes. Copying by value duplicates the payload. Consider passing by 'ref'.",
					DiagnosticIds.LargeCopyWarning);
			}
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

	private void HandleCopyAssignment(ExpressionSyntax rightExpr, SymbolTable scope)
	{
		if (rightExpr is not IdentifierExpressionSyntax rightId)
			return;

		var rightSymbol = scope.Lookup(rightId.Name) as VariableSymbol;
		if (rightSymbol == null || rightSymbol.Type is not StructTypeSymbol rightStruct)
			return;

		var kind = Classification.Classify(rightStruct);
		if (kind == CopyKind.LargeCopy)
		{
			var size = Classification.CalculateByteSize(rightStruct);
			context.Diagnostics.ReportWarning(
				context.CurrentUnit!.Context, rightId.Span,
				$"'{rightId.Name}' is {size} bytes. Copying by value duplicates the payload. Consider passing by 'ref'.",
				DiagnosticIds.LargeCopyWarning);
		}
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
		_activeRefs[lockName] = (name, false, span.End, span);
		_activeBorrows.Add(new BorrowSymbol(lockName, name, false, span));
		RegisterParentLock(name, lockName);
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

	private void VerifyBorrowRules(VariableDeclarationSyntax varDecl, SymbolTable scope)
	{
		// Borrow exclusivity checks are disabled in unbound and unsafe tiers
		if (CurrentTier != SafetyTier.Safe)
			return;

		if ((varDecl.Type == "refvar" || varDecl.Type == "ref") && varDecl.Initializer is BorrowExpressionSyntax borrow)
		{
			var borrowedName = GetBaseIdentifierName(borrow.Expression);
			if (borrowedName != null)
			{
				var isMutable = varDecl.Type == "refvar";

				// Array Index Locking: borrowing any element blocks all other element borrows
				var isIndexBorrow = borrow.Expression is IndexExpressionSyntax;
				if (isIndexBorrow && _parentLocks.ContainsKey(borrowedName))
				{
					context.Diagnostics.Report(context.CurrentUnit!.Context, varDecl.Span,
						$"'{borrowedName}' is already borrowed; cannot borrow multiple elements of the same array");
				}

				// Exclusive Mutability: check parent-level conflicts
				var conflicts = _activeBorrows.Where(b => b.BorrowedName == borrowedName).ToList();
				if (conflicts.Count > 0)
				{
					if (isMutable || conflicts.Any(c => c.IsMutable))
					{
						context.Diagnostics.Report(context.CurrentUnit!.Context, varDecl.Span, $"Cannot borrow '{borrowedName}' because an incompatible borrow is already active");
					}
				}

				_activeBorrows.Add(new BorrowSymbol(varDecl.Name, borrowedName, isMutable, varDecl.Span));
				_activeRefs[varDecl.Name] = (borrowedName, isMutable, varDecl.Span.End, varDecl.Span);
				RegisterParentLock(borrowedName, varDecl.Name);
			}
		}
	}

	private void VerifyReturnLifetime(ReturnStatementSyntax ret, FunctionDeclarationSyntax func, SymbolTable scope)
	{
		if (ret.Expression == null) return;

		// Lifetime checks are disabled in unsafe tier
		if (CurrentTier == SafetyTier.Unsafe)
			return;

		// Case 1: return ref expr; — BorrowExpressionSyntax wrapping an identifier
		if (ret.Expression is BorrowExpressionSyntax borrow && borrow.Expression is IdentifierExpressionSyntax bid)
		{
			if (IsDanglingTarget(bid.Name, scope))
			{
				context.Diagnostics.Report(context.CurrentUnit!.Context, ret.Expression.Span, $"Cannot return reference to local variable '{bid.Name}' (dangling reference)");
			}
			return;
		}

		// Case 2: return r; where r is a ref/refvar variable (PointerTypeSymbol)
		if (ret.Expression is IdentifierExpressionSyntax id)
		{
			if (scope.Lookup(id.Name) is VariableSymbol idSym && idSym.Type is PointerTypeSymbol && IsDanglingTarget(id.Name, scope))
			{
				context.Diagnostics.Report(context.CurrentUnit!.Context, ret.Expression.Span, $"Cannot return reference to local variable '{id.Name}' (dangling reference)");
			}

			// Case 3: return by value of a variable whose fields are currently borrowed
			if (_parentLocks.ContainsKey(id.Name))
			{
				context.Diagnostics.Report(context.CurrentUnit!.Context, ret.Expression.Span,
					$"Cannot return '{id.Name}' by value while a field borrow is still active");
			}

			// Case 4: return by value of a struct whose ref fields point to locals (§3C)
			if (scope.Lookup(id.Name) is VariableSymbol retSym && retSym.Type is StructTypeSymbol retStruct)
			{
				VerifyStructByValueReturn(retStruct, id.Name, ret.Expression.Span, scope);
			}

			return;
		}

		// Case 5: return a pointer-bearing value constructed inline (struct literal, non-nullable
		// reference option literal, or a reference-field member access). Every reachable reference
		// payload must ultimately point at heap, global, or parameter storage; a non-heap stack local
		// would dangle once the caller takes ownership of the returned graph (heap-relative provenance).
		if (ResolveExpressionType(ret.Expression, scope) is { } returnType && TypeTransitivelyHasRefs(returnType))
			VerifyHeapRelativeReturn(ret.Expression, returnType, ret.Expression.Span, scope);
	}

	/// <summary>
	/// Verify that a struct being returned by value doesn't have ref fields pointing to local-origin variables (§3C).
	/// Uses cycle detection to handle self-referential structs.
	/// </summary>
	private void VerifyStructByValueReturn(StructTypeSymbol structType, string varName, TextSpan span, SymbolTable scope)
	{
		VerifyStructByValueReturnCore(structType, varName, span, [], scope);
	}

	private void VerifyStructByValueReturnCore(StructTypeSymbol structType, string varName, TextSpan span, HashSet<string> visited, SymbolTable scope)
	{
		if (!visited.Add(structType.Name))
			return; // cycle-cut: already visited this type, stop recursion

		foreach (var field in structType.Fields)
		{
			if (field.IsCycleCut) continue;

			if (field.Type is PointerTypeSymbol ptr && ptr.ReferencedType is StructTypeSymbol innerStruct)
			{
				// Ref field pointing to a struct: recurse into that struct's fields
				if (_structRefTargets.TryGetValue(varName, out var targets))
				{
					foreach (var target in targets)
					{
						if (IsDanglingTarget(target, scope))
						{
							context.Diagnostics.Report(context.CurrentUnit!.Context, span,
								$"Cannot return '{varName}' by value: reference field '{field.Name}' targets local variable '{target}' (dangling reference)");
							return;
						}
					}
				}

				VerifyStructByValueReturnCore(innerStruct, varName, span, visited, scope);
			}
			else if (field.Type is PointerTypeSymbol ptrScalar && ptrScalar.ReferencedType is not StructTypeSymbol)
			{
				// Ref field pointing to a scalar: check tracked targets
				if (_structRefTargets.TryGetValue(varName, out var targets))
				{
					foreach (var target in targets)
					{
						if (IsDanglingTarget(target, scope))
						{
							context.Diagnostics.Report(context.CurrentUnit!.Context, span,
								$"Cannot return '{varName}' by value: reference field '{field.Name}' targets local variable '{target}' (dangling reference)");
							return;
						}
					}
				}
			}
		}
	}

	/// <summary>
	/// For a ref/refvar or nullable-reference-option initializer, return the base identifier the
	/// reference ultimately points at (the payload expression for a reference option literal).
	/// </summary>
	private string? TryGetPayloadBase(ExpressionSyntax expr, SymbolTable scope)
	{
		if (expr is StructInitializationExpressionSyntax init && init.Initializers.Count == 1)
		{
			if (ResolveExpressionType(init, scope) is UnionTypeSymbol ut)
			{
				var variant = ut.FindField(init.Initializers[0].MemberName);
				if (variant?.Type is PointerTypeSymbol)
					return GetBaseIdentifierName(init.Initializers[0].Expression);
				return null;
			}
		}

		return GetBaseIdentifierName(expr);
	}

	/// <summary>
	/// Resolve a variable name through reference chains to the concrete variable whose storage the
	/// reference ultimately points at (heap-relative provenance). Cycle-safe.
	/// </summary>
	private string? ResolveUltimateTarget(string name, SymbolTable scope)
	{
		var visited = new HashSet<string>();
		var current = name;
		while (current != null && visited.Add(current) && _refVarTargets.TryGetValue(current, out var next))
			current = next;
		return current;
	}

	/// <summary>
	/// True when a reference target points at a non-heap stack-local variable that would dangle once
	/// ownership of the returned graph transfers to a caller. Heap allocations, globals, and parameters
	/// all outlive the function and are therefore safe escape targets.
	/// </summary>
	private bool IsDanglingTarget(string targetName, SymbolTable scope)
	{
		var ultimate = ResolveUltimateTarget(targetName, scope) ?? targetName;
		if (_heapVariables.Contains(ultimate)) return false;
		if (scope.Lookup(ultimate) is not VariableSymbol symbol) return false;
		return symbol.Origin == OriginKind.Local;
	}

	private static bool TypeTransitivelyHasRefs(TypeSymbol type)
	{
		return type switch
		{
			PointerTypeSymbol => true,
			StructTypeSymbol st => st.Fields.Any(f => TypeTransitivelyHasRefs(f.Type)),
			UnionTypeSymbol ut => ut.Fields.Any(f => !f.IsVoidVariant && TypeTransitivelyHasRefs(f.Type)),
			ArrayTypeSymbol arr => TypeTransitivelyHasRefs(arr.ElementType),
			SliceTypeSymbol sl => TypeTransitivelyHasRefs(sl.ElementType),
			_ => false
		};
	}

	/// <summary>
	/// Verify a pointer-bearing value returned by value: every reachable reference payload must not
	/// dangle. Collects the base identifiers of all reference payloads in the expression, then checks
	/// each one against heap/global/parameter provenance.
	/// </summary>
	private void VerifyHeapRelativeReturn(ExpressionSyntax retExpr, TypeSymbol retType, TextSpan span, SymbolTable scope)
	{
		var targets = new HashSet<string>();
		CollectPointerPayloadBases(retExpr, retType, scope, targets, []);

		foreach (var target in targets)
		{
			if (IsDanglingTarget(target, scope))
			{
				context.Diagnostics.Report(context.CurrentUnit!.Context, span,
					$"Cannot return value: reference '{target}' targets local variable '{ResolveUltimateTarget(target, scope)}' (dangling reference)");
				return;
			}
		}
	}

	private void CollectPointerPayloadBases(ExpressionSyntax expr, TypeSymbol type, SymbolTable scope,
		HashSet<string> targets, HashSet<string> visited)
	{
		if (type is PointerTypeSymbol)
		{
			var baseId = GetBaseIdentifierName(expr);
			if (baseId != null)
				targets.Add(baseId);
			return;
		}

		if (expr is StructInitializationExpressionSyntax init)
		{
			if (type is StructTypeSymbol st)
			{
				if (!visited.Add("S:" + st.Name)) return;
				foreach (var memberInit in init.Initializers)
				{
					var field = st.FindField(memberInit.MemberName);
					if (field == null) continue;
					if (field.Type is PointerTypeSymbol)
					{
						var baseId = GetBaseIdentifierName(memberInit.Expression);
						if (baseId != null)
							targets.Add(baseId);
					}
					else if (field.Type is StructTypeSymbol or UnionTypeSymbol)
					{
						CollectPointerPayloadBases(memberInit.Expression, field.Type, scope, targets, visited);
					}
				}
			}
			else if (type is UnionTypeSymbol ut && init.Initializers.Count == 1)
			{
				var variant = ut.FindField(init.Initializers[0].MemberName);
				if (variant == null || variant.IsVoidVariant) return;
				if (variant.Type is PointerTypeSymbol)
				{
					var baseId2 = GetBaseIdentifierName(init.Initializers[0].Expression);
					if (baseId2 != null)
						targets.Add(baseId2);
				}
				else if (variant.Type is StructTypeSymbol or UnionTypeSymbol)
				{
					CollectPointerPayloadBases(init.Initializers[0].Expression, variant.Type, scope, targets, visited);
				}
			}
			return;
		}

		// Non-literal pointer-bearing expression (reference-field member access, graph-handle call, or
		// a reference/option variable): fall back to the base identifier; chains resolve via _refVarTargets.
		var baseId3 = GetBaseIdentifierName(expr);
		if (baseId3 != null)
			targets.Add(baseId3);
	}

	/// <summary>
	/// When a struct variable is initialized (struct literal or function call),
	/// scan ref fields and record what each ref field points to in _structRefTargets.
	/// </summary>
	private void TrackStructRefTargets(string varName, ExpressionSyntax initializer, SymbolTable scope)
	{
		var type = ResolveExpressionType(initializer, scope);
		if (type is not StructTypeSymbol structType) return;

		var refTargets = new HashSet<string>();
		CollectRefTargets(structType, initializer, scope, refTargets, []);

		if (refTargets.Count > 0)
			_structRefTargets[varName] = refTargets;
	}

	private void CollectRefTargets(StructTypeSymbol structType, ExpressionSyntax expr, SymbolTable scope,
		HashSet<string> targets, HashSet<string> visited)
	{
		if (!visited.Add(structType.Name)) return; // cycle-cut

		if (expr is StructInitializationExpressionSyntax init)
		{
			foreach (var memberInit in init.Initializers)
			{
				var field = structType.FindField(memberInit.MemberName);
				if (field == null || field.Type is not PointerTypeSymbol ptrType) continue;

				var fieldExpr = memberInit.Expression;
				var borrowedName = GetBaseIdentifierName(fieldExpr);
				if (borrowedName != null)
					targets.Add(borrowedName);

				// Recurse into nested struct fields
				if (ptrType.ReferencedType is StructTypeSymbol innerStruct && fieldExpr is StructInitializationExpressionSyntax innerInit)
					CollectRefTargets(innerStruct, innerInit, scope, targets, visited);
			}
		}
		else if (expr is CallExpressionSyntax call && context.ResolvedCalls.TryGetValue(call, out var callee))
		{
			// Function call returning a struct: we can't track per-field origins without interprocedural analysis.
			// Record the function parameters as potential ref targets (conservative).
			for (var i = 0; i < call.Arguments.Count && i < callee.Parameters.Count; i++)
			{
				if (callee.Parameters[i].Type is PointerTypeSymbol)
				{
					var argName = GetBaseIdentifierName(call.Arguments[i]);
					if (argName != null)
						targets.Add(argName);
				}
			}
		}
	}

	private void RegisterParentLock(string parentName, string refName)
	{
		if (!_parentLocks.TryGetValue(parentName, out var refs))
		{
			refs = [];
			_parentLocks[parentName] = refs;
		}
		refs.Add(refName);
	}

	private void ReleaseParentLock(string refName)
	{
		var parentKeys = _parentLocks.Where(kv => kv.Value.Contains(refName)).Select(kv => kv.Key).ToList();
		foreach (var parent in parentKeys)
		{
			_parentLocks[parent].Remove(refName);
			if (_parentLocks[parent].Count == 0)
				_parentLocks.Remove(parent);
		}
	}

	private void VerifyBorrowLock(ExpressionSyntax expr, SymbolTable scope, string verb)
	{
		var name = GetBaseIdentifierName(expr);
		if (name != null && _parentLocks.ContainsKey(name))
		{
			context.Diagnostics.Report(context.CurrentUnit!.Context, expr.Span,
				$"Cannot {verb} '{name}' while a field borrow is still active");
		}
	}

	private string? GetBaseIdentifierName(ExpressionSyntax expr)
	{
		if (expr is IdentifierExpressionSyntax id) return id.Name;
		if (expr is MemberAccessExpressionSyntax m) return GetBaseIdentifierName(m.Expression);
		if (expr is IndexExpressionSyntax idx) return GetBaseIdentifierName(idx.Left);
		if (expr is BorrowExpressionSyntax b) return GetBaseIdentifierName(b.Expression);
		return null;
	}

	/// <summary>
	/// Returns true if the expression resolves to a refvar/ref variable declared locally inside the current unbound scope.
	/// </summary>
	private bool IsLocalUnboundRef(ExpressionSyntax expr, SymbolTable scope)
	{
		var name = GetBaseIdentifierName(expr);
		if (name == null) return false;
		if (!_localRefsInUnboundScope.Contains(name)) return false;
		return scope.Lookup(name) is VariableSymbol sym && sym.Type is PointerTypeSymbol;
	}

	/// <summary>
	/// Returns true when the base of a member access resolves to a non-local ("external") variable: a function
	/// parameter or a global. Such objects outlive the surrounding unbound scope, so a local unbound reference
	/// stored into one of their reference fields would escape and dangle (CVL1008).
	/// </summary>
	private bool IsExternalEscapeBase(ExpressionSyntax baseExpr, SymbolTable scope)
	{
		var name = GetBaseIdentifierName(baseExpr);
		if (name == null) return false;
		return scope.Lookup(name) is VariableSymbol sym && (sym.IsGlobal || sym.Origin == OriginKind.Parameter);
	}

	/// <summary>
	/// Resolves the named field on the struct type of the given base expression and returns its name if and only if
	/// it is a reference (ref/refvar) field. Returns null for value fields or when the type cannot be resolved.
	/// </summary>
	private string? GetRefFieldName(ExpressionSyntax baseExpr, string fieldName, SymbolTable scope)
	{
		var (_, field) = ResolveStructField(baseExpr, fieldName, scope);
		return field is { Type: PointerTypeSymbol } ? field.Name : null;
	}

	private (StructTypeSymbol? Container, StructFieldSymbol? Field) ResolveStructField(ExpressionSyntax baseExpr, string fieldName, SymbolTable scope)
	{
		var name = GetBaseIdentifierName(baseExpr);
		if (name == null || scope.Lookup(name) is not VariableSymbol sym) return (null, null);

		var structType = sym.Type switch
		{
			StructTypeSymbol s => s,
			PointerTypeSymbol p when p.ReferencedType is StructTypeSymbol s => s,
			_ => null
		};
		return (structType, structType?.FindField(fieldName));
	}

	/// <summary>
	/// CVL1035: the unbound sandbox suspends access checks? No — it suspends the borrow checker only.
	/// A private ref/refvar field declared in another compilation unit cannot be structurally mutated
	/// or traversed from an unbound scope. Internal fields are always reachable within a single module.
	/// </summary>
	private void ReportUnboundRefFieldVisibilityLeak(ExpressionSyntax baseExpr, string fieldName, TextSpan span, SymbolTable scope)
	{
		if (context.LegacyVisibility || !_currentTierStack.Contains(SafetyTier.Unbound))
			return;

		var (container, field) = ResolveStructField(baseExpr, fieldName, scope);
		if (container is null || field is null || field.Type is not PointerTypeSymbol)
			return;
		if (field.Visibility == Visibility.Public)
			return;

		CompilationUnitSyntax? declaringUnit = null;
		if (context.SymbolUnits.TryGetValue(container.Name, out var unit))
			declaringUnit = unit;
		if (declaringUnit is null || VisibilityChecker.IsAccessible(field.Visibility, context.CurrentUnit, declaringUnit))
			return;

		context.Diagnostics.Report(context.CurrentUnit!.Context, span,
			$"The 'unbound' sandbox cannot suspend access restrictions. Structural mutation of refvar field '{field.Name}' is blocked because it is not visible to this compilation scope.",
			DiagnosticIds.UnboundVisibilityLeak);
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
				_activeBorrows.Add(new BorrowSymbol(c.VariableName!, parentName, isMutable, c.Span));
				_activeRefs[c.VariableName!] = (parentName, isMutable, c.Span.End, c.Span);
				RegisterParentLock(parentName, c.VariableName!);
			}

			CheckBlockSafety(new BlockStatementSyntax(c.Span, c.Body), new SymbolTable(scope), func);

			if (hasRefPromotion && parentName is not null)
			{
				_activeRefs.Remove(c.VariableName!);
				_activeBorrows.RemoveAll(b => b.BorrowerName == c.VariableName);
				ReleaseParentLock(c.VariableName!);
			}
		}
	}
}
