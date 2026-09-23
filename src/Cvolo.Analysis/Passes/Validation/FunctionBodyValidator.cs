using Cvolo.Analysis.Symbols;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using Cvolo.Core.AST.Expressions;
using Cvolo.Core.AST.Statements;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Analysis.Passes.Validation;

/// <summary>
/// Establishes function-level validation state and validates ordinary and extension function bodies.
/// </summary>
/// <remarks>
/// The validator owns function entry/exit state, parameter and receiver scope construction, extension
/// receiver mutability rules, and required-return checks. Statement traversal remains in
/// <see cref="StatementValidator"/> so each function body is traversed only once.
/// </remarks>
internal sealed class FunctionBodyValidator(
	BindingContext context,
	ValidationContext validation,
	StatementValidator statements,
	IntrinsicValidator intrinsics,
	VisibilityValidator visibility,
	Func<ExpressionSyntax, string?> getBaseIdentifierName)
{
	/// <summary>
	/// Validates one ordinary function body and establishes its function-local validation state.
	/// </summary>
	public void ValidateFunctionBody(FunctionDeclarationSyntax func)
	{
		// 0. Check for generic parameter visibility leaks (CVL1038) on public functions
		if (func.Visibility == Visibility.Public)
		{
			var retType = context.ResolveType(func.ReturnType);
			visibility.CheckGenericVisibilityLeak(func.NameSpan, retType);

			foreach (var param in func.Parameters)
			{
				var paramType = context.ResolveType(param.Type);
				visibility.CheckGenericVisibilityLeak(param.Span, paramType);
			}
		}

		// 1. Guard for bodyless functions: must have [Intrinsic]
		if (!func.HasBody)
		{
			if (context.CurrentUnit is not null && context.ExternalPackageUnits.Contains(context.CurrentUnit))
				return;

			if (!intrinsics.IsIntrinsicDeclaration(func))
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, func.NameSpan, $"Function '{func.Name}' must declare a body unless decorated with '[Intrinsic]'.");
			}

			return;
		}

		var baseUnsafeDepth = validation.UnsafeDepth;
		var baseInUnbound = validation.InUnbound;
		validation.UnsafeDepth = IsUnsafeFunction(func) ? 1 : 0;
		validation.InUnbound = func.Modifier == SafetyTier.Unbound;
		var baseEnclosingFunction = validation.EnclosingFunction;
		validation.EnclosingFunction = func;
		var localScope = new SymbolTable(context.Globals);

		foreach (var param in func.Parameters)
		{
			var paramType = context.ResolveType(param.Type);
			if (paramType is not null)
			{
				var varSymbol = new VariableSymbol(param.Name, paramType, isMutable: false)
				{
					IsInitialized = true,
					Origin = OriginKind.Parameter
				};
				localScope.Declare(varSymbol);
			}
		}

		statements.CheckBlock(func.Body!, localScope, func);

		// Guard: Ensure non-void functions end with a return statement
		if (func.ReturnType != "void" && !EndsWithReturn(func.Body!))
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(
				currentFileContext,
				func.NameSpan,
				$"Function '{func.Name}' is declared to return '{func.ReturnType}' but is missing a return statement."
			);
		}

		validation.UnsafeDepth = baseUnsafeDepth;
		validation.InUnbound = baseInUnbound;
		validation.EnclosingFunction = baseEnclosingFunction;
	}

	/// <summary>
	/// Returns whether a function body executes under the existing unsafe validation tier.
	/// </summary>
	private static bool IsUnsafeFunction(FunctionDeclarationSyntax func)
	{
		return func.Modifier == SafetyTier.Unsafe ||
		func.CallingConvention is not null ||
		func.Attributes.Any(static a => string.Equals(a.Name, "UnsafeBody", StringComparison.OrdinalIgnoreCase));
	}

	/// <summary>
	/// Returns whether the supplied statement shape is known to terminate with a return.
	/// </summary>
	public static bool EndsWithReturn(SyntaxNode s) => s switch
	{
		BlockStatementSyntax b => b.Statements.Count > 0 && EndsWithReturn(b.Statements[^1]),
		LabeledBlockStatementSyntax lb => lb.Body.Statements.Count > 0 && EndsWithReturn(lb.Body.Statements[^1]),
		ReturnStatementSyntax => true,
		TryStatementSyntax t => EndsWithReturn(t.Body) && t.CatchClauses.All(c => EndsWithReturn(c.Body)),
		_ => false,
	};

	/// <summary>
	/// Validates an extension/default/destructor body and its receiver mutability contract.
	/// </summary>
	public void ValidateExtensionBody(string extendedTypeName, FunctionDeclarationSyntax method, bool forceMutableThis = false)
	{
		var extendedType = context.ResolveType(extendedTypeName);
		if (extendedType != null && extendedType.GetType().Name == "ProtocolTypeSymbol")
		{
			// PROTOCOL DEFAULT BODIES: validated once at declaration against a
			// this-free scope (no receiver object or flat struct fields exist).
			// A default body may only reference globals/functions and its own
			// explicit parameters.
			var baseUnsafeDepth = validation.UnsafeDepth;
			validation.UnsafeDepth = IsUnsafeFunction(method) ? 1 : 0;
			var baseInUnboundP = validation.InUnbound;
			validation.InUnbound = method.Modifier == SafetyTier.Unbound;
			var protoScope = new SymbolTable(context.Globals);
			foreach (var p in method.Parameters)
			{
				var pt = context.ResolveType(p.Type);
				if (pt != null)
				{
					protoScope.Declare(new VariableSymbol(p.Name, pt, isMutable: false) { IsInitialized = true, Origin = OriginKind.Parameter });
				}
			}

			statements.CheckBlock(method.Body, protoScope, method);
			validation.UnsafeDepth = baseUnsafeDepth;
			validation.InUnbound = baseInUnboundP;
			return;
		}

		// (EnumTypeSymbol is handled via reflection-like fallback in case it's in another branch)
		if (extendedType is not (StructTypeSymbol or UnionTypeSymbol) && extendedType?.GetType().Name != "EnumTypeSymbol")
			return;

		var baseUnsafeDepth2 = validation.UnsafeDepth;
		validation.UnsafeDepth = IsUnsafeFunction(method) ? 1 : 0;
		var baseInUnbound2 = validation.InUnbound;
		validation.InUnbound = method.Modifier == SafetyTier.Unbound;

		var structType = extendedType as StructTypeSymbol;
		bool isMutating;
		var isCtorOrDtor = method.Name.Contains('~') || method.Name == extendedTypeName || method.Name.EndsWith($".{extendedTypeName}");

		// 1. StrictMutability Enforcement:
		// When [StrictMutability] is present, auto-inference is disabled.
		// Every method (excluding constructors and destructors) must explicitly declare 'ref this' or 'refvar this'.
		if (structType?.IsStrictMutability == true && !isCtorOrDtor)
		{
			if (method.Receiver == ReceiverContract.None)
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, method.NameSpan,
					$"Method '{method.Name}' must declare 'ref this' or 'refvar this' receiver in [StrictMutability] struct '{extendedTypeName}'.");
			}
		}

		// 2. Mutability Determination & Verification
		if (method.Receiver == ReceiverContract.Refvar)
		{
			isMutating = true;
		}
		else if (method.Receiver == ReceiverContract.Ref)
		{
			isMutating = false;
			// Guard: 'ref this' methods are strictly forbidden from mutating fields
			if (structType != null && DetectFieldMutation(method.Body, structType))
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, method.NameSpan,
					$"Extension method '{method.Name}' declares read-only 'ref this' receiver but mutates field(s) of '{extendedTypeName}'.");
			}
		}
		else
		{
			// Fallback: Auto-inference runs only when [StrictMutability] is NOT active
			isMutating = forceMutableThis || (structType != null && DetectFieldMutation(method.Body, structType));

			// CVL1011 Warning: Notify developer when auto-inference infers mutability
			if (isMutating && !forceMutableThis && !isCtorOrDtor && structType?.IsStrictMutability != true)
			{
				// Check if this method symbol suppresses CVL1011
				var baseMangledNameForLookup = context.GetMangledName($"{extendedTypeName}.{method.Name}", context.CurrentNamespace);
				var lookupThisTypeForWarn = new PointerTypeSymbol(extendedType!, isMutable: false);
				var lookupParamsForWarn = new List<TypeSymbol> { lookupThisTypeForWarn };
				foreach (var p in method.Parameters)
					lookupParamsForWarn.Add(context.ResolveType(p.Type)!);
				var overloadedNameForWarn = context.GetOverloadedMangledName(baseMangledNameForLookup, lookupParamsForWarn);
				var targetFuncSymbol = context.Globals.Lookup(overloadedNameForWarn) as FunctionSymbol;

				if (targetFuncSymbol == null || !targetFuncSymbol.SuppressedWarnings.Contains(DiagnosticIds.AutoInferMutationWarning))
				{
					context.Diagnostics.ReportWarning(context.FileContexts[context.CurrentUnit!], method.NameSpan,
						$"Auto-inference chose mutability for method '{method.Name}'. Explicitly mark 'refvar this' to silence this warning.",
						DiagnosticIds.AutoInferMutationWarning);
				}
			}
		}

		// 3. Locate the registered function symbol using the base registration
		var baseMangledName = context.GetMangledName($"{extendedTypeName}.{method.Name}", context.CurrentNamespace);
		var lookupThisType = new PointerTypeSymbol(extendedType!, isMutable: false);
		var lookupParams = new List<TypeSymbol> { lookupThisType };
		foreach (var p in method.Parameters)
		{
			lookupParams.Add(context.ResolveType(p.Type)!);
		}

		var lookupOverloadedName = context.GetOverloadedMangledName(baseMangledName, lookupParams);
		var funcSymbol = context.Globals.Lookup(lookupOverloadedName) as FunctionSymbol;

		if (funcSymbol is not null)
		{
			// Upgrade the registered symbol's "this" parameter mutability
			if (funcSymbol.Parameters[0].Type is PointerTypeSymbol thisParamType)
			{
				thisParamType.IsMutable = isMutating;
			}
		}

		// 4. Populate local scope with fields/variants so they can be written as flat local variables
		var localScope = new SymbolTable(context.Globals);

		// EXPLICITLY DECLARE 'this' in the local scope!
		var thisPtrType = new PointerTypeSymbol(extendedType!, isMutable: isMutating);
		localScope.Declare(new VariableSymbol("this", thisPtrType, isMutable: false) { IsInitialized = true });

		if (extendedType is StructTypeSymbol st)
		{
			foreach (var field in st.Fields)
			{
				localScope.Declare(new VariableSymbol(field.Name, field.Type, isMutable: true) { IsInitialized = true });
			}
		}
		else if (extendedType is UnionTypeSymbol ut)
		{
			foreach (var field in ut.Fields)
			{
				localScope.Declare(new VariableSymbol(field.Name, field.Type, isMutable: true) { IsInitialized = true });
			}
		}
		else if (extendedType?.GetType().Name == "EnumTypeSymbol")
		{
			// Restore the E7 logic: make enum variants accessible unqualified
			dynamic enumType = extendedType;
			foreach (var variant in enumType.Variants)
			{
				localScope.Declare(new VariableSymbol((string)variant.Name, extendedType, isMutable: false) { IsInitialized = true });
			}
		}

		foreach (var param in method.Parameters)
		{
			var paramType = context.ResolveType(param.Type);
			if (paramType is not null)
			{
				localScope.Declare(new VariableSymbol(param.Name, paramType, isMutable: false) { IsInitialized = true, Origin = OriginKind.Parameter });
			}
		}

		statements.CheckBlock(method.Body, localScope, method);

		// Restore original depth context
		validation.UnsafeDepth = baseUnsafeDepth2;
		validation.InUnbound = baseInUnbound2;
	}

	/// <summary>
	/// Returns whether an extension body assigns to a field of the extended struct.
	/// </summary>
	private bool DetectFieldMutation(SyntaxNode node, StructTypeSymbol structType)
	{
		if (node is BinaryExpressionSyntax bin && bin.Operator == "=")
		{
			var baseName = getBaseIdentifierName(bin.Left);
			if (baseName != null && structType.FindField(baseName) != null)
			{
				return true; // Detected a field assignment!
			}
		}

		foreach (var child in node.GetChildren())
		{
			if (DetectFieldMutation(child, structType))
				return true;
		}

		return false;
	}
}
