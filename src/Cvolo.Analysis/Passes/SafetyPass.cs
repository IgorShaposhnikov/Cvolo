using Cvolo.Analysis.Passes.Safety;
using Cvolo.Analysis.Symbols;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using Cvolo.Core.AST.Expressions;
using Cvolo.Core.AST.Statements;

namespace Cvolo.Analysis.Passes;

public sealed class SafetyPass(BindingContext context)
{
	private BorrowTracker? _borrows;
	private UnsafeContextValidator? _unsafeContext;
	private ReferenceLifetimeAnalyzer? _referenceLifetimes;
	private MoveAnalyzer? _moves;
	private UnboundValidator? _unbound;
	private ForEachSafetyValidator? _forEachSafety;
	private SafeDelegateAnalyzer? _safeDelegates;

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
	/// transfer, and large-copy diagnostics while delegate capture policy lives in its dedicated analyzer.
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

	/// <summary>
	/// Lazily creates the foreach safety validator that enforces reference-item escape boundaries
	/// and the immutable collection contract for the duration of each loop body.
	/// </summary>
	private ForEachSafetyValidator ForEachSafety => _forEachSafety ??= new ForEachSafetyValidator(
		context,
		GetBaseIdentifierName);

	/// <summary>
	/// Lazily creates the safe-delegate analyzer that owns lambda capture policy, delegate
	/// provenance, and escape checks while reusing this pass's existing recursive traversal.
	/// </summary>
	private SafeDelegateAnalyzer SafeDelegates => _safeDelegates ??= new SafeDelegateAnalyzer(
		context,
		Borrows,
		Moves,
		UnsafeContext,
		ResolveExpressionType,
		GetBaseIdentifierName,
		CheckExpressionSafety,
		CheckBlockSafety);

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
		SafeDelegates.Reset(func);

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
				SafeDelegates.TrackParameter(param.Name, type);
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

						ReferenceLifetimes.TrackDeclaration(v, sym, scope);

						Unbound.TrackLocalReferenceDeclaration(v);

						SafeDelegates.TrackDeclaration(v, sym, scope);
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
				if (r.Expression != null)
					SafeDelegates.ValidateReturn(r.Expression, scope);
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
					ForEachSafety.Validate(fe, feBlock);
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
				SafeDelegates.ValidateLambda(lam, scope);
				break;

			case BinaryExpressionSyntax bin:
				CheckExpressionSafety(bin.Right, scope);
				if (bin.Operator == "=")
				{
					SafeDelegates.ValidateAssignment(bin, scope);
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
						ReferenceLifetimes.TrackAssignment(leftId, leftSymbol, bin.Right, bin.Span, scope);

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
