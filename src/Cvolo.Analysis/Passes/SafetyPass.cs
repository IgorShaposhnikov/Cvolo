using Cvolo.Analysis.Passes.Safety;
using Cvolo.Analysis.Symbols;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using Cvolo.Core.AST.Expressions;

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
	private SafetyTraversal? _traversal;
	private FunctionSafetyAnalyzer? _functionSafety;

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
	/// Lazily creates the single safety traversal that coordinates the extracted analyzers while
	/// preserving the original recursive walk and diagnostic ordering.
	/// </summary>
	private SafetyTraversal Traversal => _traversal ??= new SafetyTraversal(
		context,
		Borrows,
		UnsafeContext,
		ReferenceLifetimes,
		Moves,
		Unbound,
		ForEachSafety,
		SafeDelegates,
		ResolveExpressionType,
		GetBaseIdentifierName);

	/// <summary>
	/// Lazily creates the safe-delegate analyzer that owns lambda capture policy, delegate
	/// provenance, and escape checks while reusing the existing recursive safety traversal.
	/// </summary>
	private SafeDelegateAnalyzer SafeDelegates => _safeDelegates ??= new SafeDelegateAnalyzer(
		context,
		Borrows,
		Moves,
		UnsafeContext,
		ResolveExpressionType,
		GetBaseIdentifierName,
		(expr, scope) => Traversal.CheckExpressionSafety(expr, scope),
		(block, scope, func) => Traversal.CheckBlockSafety(block, scope, func));

	/// <summary>
	/// Lazily creates the per-function safety session coordinator that resets analyzer state,
	/// establishes the function tier and scope, and starts the shared traversal.
	/// </summary>
	private FunctionSafetyAnalyzer FunctionSafety => _functionSafety ??= new FunctionSafetyAnalyzer(
		context,
		() => Borrows,
		() => UnsafeContext,
		() => ReferenceLifetimes,
		() => Unbound,
		() => SafeDelegates,
		() => Traversal);

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
					FunctionSafety.Check(func);
				}
				else if (member is ExposeExternBlockSyntax exportBlock)
				{
					foreach (var exportFunc in exportBlock.Functions)
					{
						if (exportFunc.GenericParameters.Count == 0 && !exportFunc.Name.Contains('<'))
							FunctionSafety.Check(exportFunc);
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
						FunctionSafety.Check(method);
					}

					foreach (var ctor in extDecl.Constructors)
					{
						FunctionSafety.Check(ctor.ToFunctionDeclaration());
					}
				}
			}
		}

		// Enforce safety pass on all monomorphized generic functions and extension methods!
		foreach (var instDecl in context.MonomorphizedFunctionDecls)
		{
			FunctionSafety.Check(instDecl);
		}

		foreach (var decl in context.MonomorphizedExtensionDecls)
		{
			if (decl is FunctionDeclarationSyntax func)
			{
				FunctionSafety.Check(func);
			}
			else if (decl is ConstructorDeclarationSyntax ctor)
			{
				FunctionSafety.Check(ctor.ToFunctionDeclaration());
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

	private string? GetBaseIdentifierName(ExpressionSyntax expr)
	{
		if (expr is IdentifierExpressionSyntax id) return id.Name;
		if (expr is MemberAccessExpressionSyntax m) return GetBaseIdentifierName(m.Expression);
		if (expr is IndexExpressionSyntax idx) return GetBaseIdentifierName(idx.Left);
		if (expr is BorrowExpressionSyntax b) return GetBaseIdentifierName(b.Expression);
		return null;
	}
}
