using Cvolo.Analysis.Symbols;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;

namespace Cvolo.Analysis.Passes.Safety;

/// <summary>
/// Owns the lifecycle of one function safety-analysis session: resets per-function analyzer state,
/// establishes the function safety tier and local scope, registers parameters, and starts the shared traversal.
/// </summary>
internal sealed class FunctionSafetyAnalyzer(
	BindingContext context,
	Func<BorrowTracker> getBorrows,
	Func<UnsafeContextValidator> getUnsafeContext,
	Func<ReferenceLifetimeAnalyzer> getReferenceLifetimes,
	Func<UnboundValidator> getUnbound,
	Func<SafeDelegateAnalyzer> getSafeDelegates,
	Func<SafetyTraversal> getTraversal)
{
	/// <summary>
	/// Runs the existing safety checks for one concrete function body without adding another syntax traversal.
	/// </summary>
	public void Check(FunctionDeclarationSyntax func)
	{
		if (!func.HasBody)
			return;

		getBorrows().Reset();
		getReferenceLifetimes().Reset();
		getUnbound().Reset();
		getSafeDelegates().Reset(func);

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

		getUnsafeContext().Reset(tier);

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
				getSafeDelegates().TrackParameter(param.Name, type);
			}
		}

		// Unbound tier: relaxed checks (skip borrow exclusivity, but still do basic flow)
		getTraversal().CheckBlockSafety(func.Body, scope, func);
	}
}
