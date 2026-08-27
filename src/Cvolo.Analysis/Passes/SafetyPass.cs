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

namespace Cvolo.Analysis.Passes;

public sealed class SafetyPass(BindingContext context)
{
	private readonly List<BorrowSymbol> _activeBorrows = [];
	private readonly Dictionary<string, (string BorrowedName, bool IsMutable, int LastUseEnd, TextSpan DeclSpan)> _activeRefs = [];
	private readonly Dictionary<string, HashSet<string>> _parentLocks = []; // parentVar -> set of refVar names
	private readonly Dictionary<string, HashSet<string>> _structRefTargets = []; // structVar -> set of variable names that ref fields point to
	private readonly Stack<SafetyTier> _currentTierStack = [];
	private readonly HashSet<string> _localRefsInUnboundScope = []; // refvar/ref variables declared inside the current unbound scope (including nested unsafe blocks)
	private ClassificationAnalyzer? _classification;

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
				if (member is FunctionDeclarationSyntax func && func.GenericParameters.Count == 0)
					CheckFunctionSafety(func);
		}
	}

	private void CheckFunctionSafety(FunctionDeclarationSyntax func)
	{
		_activeBorrows.Clear();
		_activeRefs.Clear();
		_parentLocks.Clear();
		_structRefTargets.Clear();
		_localRefsInUnboundScope.Clear();

		// Look up the resolved function symbol to get the actual tier
		var baseName = func.Name == "main" ? "main" : context.GetMangledName(func.Name, context.CurrentNamespace);
		var paramTypes = func.Parameters.Select(p => context.ResolveType(p.Type) ?? TypeSymbol.Int).ToList();
		var overloadedName = context.GetOverloadedMangledName(baseName, paramTypes);
		var funcSymbol = context.Globals.Lookup(overloadedName) as FunctionSymbol;
		var tier = funcSymbol?.SafetyTier ?? SafetyTier.Safe;

		_currentTierStack.Clear();
		_currentTierStack.Push(tier);

		// Unsafe tier: skip all safety checks entirely
		if (tier == SafetyTier.Unsafe)
			return;

		var scope = new SymbolTable(context.Globals);
		foreach (var param in func.Parameters)
		{
			var type = context.ResolveType(param.Type);
			if (type != null) scope.Declare(new VariableSymbol(param.Name, type, false) { IsInitialized = true, Origin = OriginKind.Parameter });
		}

		// Unbound tier: relaxed checks (skip borrow exclusivity, but still do basic flow)
		CheckBlockSafety(func.Body, scope, func);
	}

	private void CheckBlockSafety(BlockStatementSyntax block, SymbolTable scope, FunctionDeclarationSyntax func)
	{
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

						// Track refvar/ref declarations inside unbound scope for CVL1008
						// Uses stack check (not CurrentTier) so nested unsafe blocks inside unbound are still tracked
						if (v.Type is not null && v.Type.StartsWith("ref") && _currentTierStack.Contains(SafetyTier.Unbound))
							_localRefsInUnboundScope.Add(v.Name);

						// Track ref field targets for struct variables (§3C)
						if (v.Type is not "ref" and not "refvar" && sym.Type is StructTypeSymbol)
							TrackStructRefTargets(v.Name, v.Initializer, scope);
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

			case UnsafeBlockStatementSyntax unsafeBlock:
				_currentTierStack.Push(SafetyTier.Unsafe);
				CheckBlockSafety(unsafeBlock.Body, new SymbolTable(scope), func);
				_currentTierStack.Pop();
				break;
		}
	}

	private void CheckExpressionSafety(ExpressionSyntax expr, SymbolTable scope)
	{
		switch (expr)
		{
			case IdentifierExpressionSyntax id:
				if (scope.Lookup(id.Name) is VariableSymbol symbol && symbol.IsMoved)
					context.Diagnostics.Report(context.CurrentUnit!.Context, id.Span, $"Use of moved variable '{id.Name}'");
				break;

			case MemberAccessExpressionSyntax m:
				CheckExpressionSafety(m.Expression, scope);
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

			case CallExpressionSyntax call:
				foreach (var arg in call.Arguments)
				{
					CheckExpressionSafety(arg, scope);
					HandleByValueArgument(arg, scope);
				}

				break;

			case BinaryExpressionSyntax bin:
				CheckExpressionSafety(bin.Right, scope);
				if (bin.Operator == "=" && bin.Left is IdentifierExpressionSyntax leftId)
				{
					if (scope.Lookup(leftId.Name) is VariableSymbol leftSymbol)
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
			if (scope.Lookup(bid.Name) is VariableSymbol sym && sym.Origin == OriginKind.Local)
			{
				context.Diagnostics.Report(context.CurrentUnit!.Context, ret.Expression.Span, $"Cannot return reference to local variable '{bid.Name}' (dangling reference)");
			}
			return;
		}

		// Case 2: return r; where r is a ref/refvar variable (PointerTypeSymbol)
		if (ret.Expression is IdentifierExpressionSyntax id)
		{
			if (scope.Lookup(id.Name) is VariableSymbol idSym && idSym.Type is PointerTypeSymbol && idSym.Origin == OriginKind.Local)
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
		}
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
						if (scope.Lookup(target) is VariableSymbol targetSym && targetSym.Origin == OriginKind.Local)
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
						if (scope.Lookup(target) is VariableSymbol targetSym && targetSym.Origin == OriginKind.Local)
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
