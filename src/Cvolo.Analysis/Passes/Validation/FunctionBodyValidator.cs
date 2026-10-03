using Cvolo.Analysis.Passes;
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
				context.Diagnostics.Report(currentFileContext, func.NameSpan, $"Function '{func.Name}' must declare a body unless decorated with '[Intrinsic]'.", DiagnosticIds.FunctionMustDeclareABodyUnlessDecoratedWithIntrinsic);
			}

			return;
		}

		var baseUnsafeDepth = validation.UnsafeDepth;
		var baseInUnbound = validation.InUnbound;
		var baseCallableBody = validation.CurrentCallableBody;
		validation.UnsafeDepth = IsUnsafeFunction(func) ? 1 : 0;
		validation.InUnbound = func.Modifier == SafetyTier.Unbound;
		var baseEnclosingFunction = validation.EnclosingFunction;
		validation.EnclosingFunction = func;
		validation.CurrentCallableBody = func;
		var errorCountBefore = ErrorCount;
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
			, DiagnosticIds.FunctionIsDeclaredToReturnButIsMissingAReturnStatement);
		}

		CheckUnsafeBodyEffectiveness(func, errorCountBefore);

		validation.UnsafeDepth = baseUnsafeDepth;
		validation.InUnbound = baseInUnbound;
		validation.EnclosingFunction = baseEnclosingFunction;
		validation.CurrentCallableBody = baseCallableBody;
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

	/// <summary>Number of error-severity diagnostics currently recorded for the compilation.</summary>
	private int ErrorCount =>
		context.Diagnostics.Diagnostics.Count(static d => d.Severity == DiagnosticSeverity.Error);

	/// <summary>
	/// Emits CVL1001 once a <c>[UnsafeBody]</c> body has been fully validated. The decision consumes
	/// the canonical unsafe-usage registry populated by semantic validation: if any operation that
	/// requires unsafe capability was recorded for this exact callable body, the attribute is
	/// effective. The warning is suppressed for incomplete analysis (new blocking errors) so it never
	/// speculates on a body the compiler could not reliably classify.
	/// </summary>
	private void CheckUnsafeBodyEffectiveness(FunctionDeclarationSyntax decl, int errorCountBefore)
	{
		if (decl.Body is null || !IsUnsafeBodyDeclaration(decl))
			return;

		// Blocking errors introduced while validating this body mean unsafe-effect analysis did
		// not complete; do not emit a misleading no-effect warning.
		if (ErrorCount > errorCountBefore)
			return;

		if (SuppressesCvl1001(decl))
			return;

		if (validation.HasUnsafeUsage(decl))
			return;

		// Syntax-level unsafe operations that do not (yet) have a dedicated semantic validation
		// site remain covered by the shared scanner. It is an implementation aid only and never
		// overrides the semantic registry above.
		if (UnsafeOperationScanner.ContainsUnsafeOperations(decl.Body))
			return;

		context.Diagnostics.ReportWarning(
			context.FileContexts[context.CurrentUnit!],
			decl.NameSpan,
			"'[UnsafeBody]' attribute has no effect because function contains no unsafe operations.",
			DiagnosticIds.UnsafeBodyNoEffect);
	}

	/// <summary>Returns whether the declaration carries the <c>[UnsafeBody]</c> attribute.</summary>
	private static bool IsUnsafeBodyDeclaration(FunctionDeclarationSyntax decl) =>
		decl.Attributes.Any(static a => NormalizeAttributeName(a.Name) == "UnsafeBody");

	/// <summary>
	/// Returns whether the declaration suppresses CVL1001 through <c>[SuppressWarning("CVL1001")]</c>.
	/// </summary>
	private static bool SuppressesCvl1001(FunctionDeclarationSyntax decl) =>
		decl.Attributes.Any(static a =>
			NormalizeAttributeName(a.Name) == "SuppressWarning"
			&& a.Arguments.Count > 0
			&& a.Arguments[0] is StringLiteralExpressionSyntax literal
			&& string.Equals(literal.Value, DiagnosticIds.UnsafeBodyNoEffect, StringComparison.OrdinalIgnoreCase));

	/// <summary>
	/// Normalizes a possibly namespace-qualified attribute name by dropping any qualifier and the
	/// optional <c>Attribute</c> suffix.
	/// </summary>
	private static string NormalizeAttributeName(string name)
	{
		var simple = name.Contains('.') ? name[(name.LastIndexOf('.') + 1)..] : name;
		return simple.EndsWith("Attribute", StringComparison.Ordinal) ? simple[..^"Attribute".Length] : simple;
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
			var baseCallableBodyP = validation.CurrentCallableBody;
			validation.CurrentCallableBody = method;
			var errorCountBeforeP = ErrorCount;
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
			CheckUnsafeBodyEffectiveness(method, errorCountBeforeP);
			validation.UnsafeDepth = baseUnsafeDepth;
			validation.InUnbound = baseInUnboundP;
			validation.CurrentCallableBody = baseCallableBodyP;
			return;
		}

		// (EnumTypeSymbol is handled via reflection-like fallback in case it's in another branch)
		if (extendedType is not (StructTypeSymbol or UnionTypeSymbol) && extendedType?.GetType().Name != "EnumTypeSymbol")
			return;

		if (!method.HasBody)
		{
			if (context.CurrentUnit is not null && context.ExternalPackageUnits.Contains(context.CurrentUnit))
				return;

			if (!intrinsics.IsIntrinsicDeclaration(method))
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, method.NameSpan, $"Method '{method.Name}' must declare a body unless decorated with '[Intrinsic]'.", DiagnosticIds.MethodMustDeclareABodyUnlessDecoratedWithIntrinsic);
			}

			return;
		}

		var baseUnsafeDepth2 = validation.UnsafeDepth;
		validation.UnsafeDepth = IsUnsafeFunction(method) ? 1 : 0;
		var baseInUnbound2 = validation.InUnbound;
		validation.InUnbound = method.Modifier == SafetyTier.Unbound;
		var baseCallableBody2 = validation.CurrentCallableBody;
		validation.CurrentCallableBody = method;
		var errorCountBefore2 = ErrorCount;

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
					$"Method '{method.Name}' must declare 'ref this' or 'refvar this' receiver in [StrictMutability] struct '{extendedTypeName}'.", DiagnosticIds.MethodMustDeclareRefThisOrRefvarThisReceiverInStrictMutabili);
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
					$"Extension method '{method.Name}' declares read-only 'ref this' receiver but mutates field(s) of '{extendedTypeName}'.", DiagnosticIds.ExtensionMethodDeclaresReadOnlyRefThisReceiverButMutatesFiel);
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

		if (!isCtorOrDtor && method.ReturnType != "void" && !EndsWithReturn(method.Body!))
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(
				currentFileContext,
				method.NameSpan,
				$"Method '{method.Name}' is declared to return '{method.ReturnType}' but is missing a return statement."
			, DiagnosticIds.MethodIsDeclaredToReturnButIsMissingAReturnStatement);
		}

		CheckUnsafeBodyEffectiveness(method, errorCountBefore2);

		// Restore original depth context
		validation.UnsafeDepth = baseUnsafeDepth2;
		validation.InUnbound = baseInUnbound2;
		validation.CurrentCallableBody = baseCallableBody2;
	}

	/// <summary>
	/// Validates an associated (leading-dot) extension body.
	/// </summary>
	/// <remarks>
	/// An associated function has no receiver, so this path differs from
	/// <see cref="ValidateExtensionBody"/> in three ways: there is no <c>this</c> parameter, the
	/// extended type's fields are NOT lifted into scope as flat locals (that implicit instance scope
	/// is exactly what "no receiver" means), and no receiver mutability contract applies — which also
	/// exempts the function from <c>[StrictMutability]</c> and from the auto-inference warning.
	/// </remarks>
	public void ValidateAssociatedFunctionBody(string extendedTypeName, FunctionDeclarationSyntax method)
	{
		if (method.IsBuiltin)
			return;

		if (!method.HasBody)
		{
			if (context.CurrentUnit is not null && context.ExternalPackageUnits.Contains(context.CurrentUnit))
				return;

			if (!intrinsics.IsIntrinsicDeclaration(method))
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, method.NameSpan, $"Function '{method.Name}' must declare a body unless decorated with '[Intrinsic]'.", DiagnosticIds.FunctionMustDeclareABodyUnlessDecoratedWithIntrinsic);
			}

			return;
		}

		var baseUnsafeDepth = validation.UnsafeDepth;
		validation.UnsafeDepth = IsUnsafeFunction(method) ? 1 : 0;
		var baseInUnbound = validation.InUnbound;
		validation.InUnbound = method.Modifier == SafetyTier.Unbound;
		var baseCallableBody = validation.CurrentCallableBody;
		validation.CurrentCallableBody = method;
		var errorCountBefore = ErrorCount;

		var localScope = new SymbolTable(context.Globals);
		foreach (var param in method.Parameters)
		{
			var paramType = context.ResolveType(param.Type);
			if (paramType is not null)
				localScope.Declare(new VariableSymbol(param.Name, paramType, isMutable: false) { IsInitialized = true, Origin = OriginKind.Parameter });
		}

		// An associated function has no receiver, so naming one of the extended type's instance fields
		// is an error rather than a lifted local. Record the field names so the expression validator can
		// report that specific mistake instead of a generic "undefined variable".
		var previousAssociatedFields = validation.AssociatedInstanceFields;
		validation.AssociatedInstanceFields = AssociatedFieldNames(context.ResolveType(extendedTypeName));

		statements.CheckBlock(method.Body, localScope, method);

		if (method.ReturnType != "void" && !EndsWithReturn(method.Body!))
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(
				currentFileContext,
				method.NameSpan,
				$"Function '{method.Name}' is declared to return '{method.ReturnType}' but is missing a return statement."
			, DiagnosticIds.FunctionIsDeclaredToReturnButIsMissingAReturnStatement);
		}

		CheckUnsafeBodyEffectiveness(method, errorCountBefore);

		validation.UnsafeDepth = baseUnsafeDepth;
		validation.InUnbound = baseInUnbound;
		validation.CurrentCallableBody = baseCallableBody;
		validation.AssociatedInstanceFields = previousAssociatedFields;
	}

	/// <summary>
	/// Names of the instance fields an associated body is forbidden to touch implicitly, or null when
	/// the extended type is not an aggregate with fields.
	/// </summary>
	private static IReadOnlySet<string>? AssociatedFieldNames(TypeSymbol? extendedType)
	{
		var names = new HashSet<string>(StringComparer.Ordinal);
		switch (extendedType)
		{
			case StructTypeSymbol structure:
				foreach (var field in structure.Fields)
					names.Add(field.Name);
				break;
			case UnionTypeSymbol union:
				foreach (var field in union.Fields)
				{
					if (!field.IsVoidVariant)
						names.Add(field.Name);
				}
				break;
		}

		return names.Count == 0 ? null : names;
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
