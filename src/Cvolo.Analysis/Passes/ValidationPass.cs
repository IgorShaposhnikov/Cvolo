using Cvolo.Analysis.Contracts;
using Cvolo.Analysis.Passes.Validation;
using Cvolo.Analysis.Resolution;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using Cvolo.Core.AST.Expressions;
using Cvolo.Core.AST.Statements;

namespace Cvolo.Analysis.Passes;

public sealed class ValidationPass(BindingContext context)
{
	private ClassificationAnalyzer? _classification;
	private ClassificationAnalyzer Classification => _classification ??= new ClassificationAnalyzer(context);

	private OverloadResolver? _overloadResolver;
	/// <summary>Shared overload candidate discovery and signature scoring service.</summary>
	private OverloadResolver Overloads => _overloadResolver ??= new OverloadResolver(context);
	private CallResolver? _callResolver;
	/// <summary>Shared callable-target resolver for ordinary, synthetic enum, and delegate-value calls.</summary>
	private CallResolver Calls => _callResolver ??= new CallResolver(context, Overloads);

	private InlineAsmValidator? _inlineAsmValidator;
	/// <summary>Shared inline-assembly semantic validator.</summary>
	private InlineAsmValidator InlineAsm => _inlineAsmValidator ??= new InlineAsmValidator(
		context,
		_validation,
		CheckExpression,
		GetExpressionType);

	private IntrinsicValidator? _intrinsicValidator;
	/// <summary>Shared compiler-intrinsic declaration and expression validator.</summary>
	private IntrinsicValidator Intrinsics => _intrinsicValidator ??= new IntrinsicValidator(
		context,
		CheckExpression,
		GetExpressionType,
		GetBaseIdentifierName);

	private ExpressionValidator? _expressionValidator;
	/// <summary>Shared expression semantic validator used by the single validation traversal.</summary>
	private ExpressionValidator Expressions => _expressionValidator ??= new ExpressionValidator(
		context,
		_validation,
		Classification,
		Overloads,
		Calls,
		InlineAsm,
		Intrinsics,
		CheckBlock,
		Generics,
		FunctionBodyValidator.EndsWithReturn);

	private SwitchValidator? _switchValidator;
	/// <summary>Shared enum/union switch semantic validator.</summary>
	private SwitchValidator Switches => _switchValidator ??= new SwitchValidator(
		context,
		_validation,
		CheckExpression,
		GetExpressionType,
		CheckBlock);

	private StatementValidator? _statementValidator;
	/// <summary>Shared statement validator used by the single validation traversal.</summary>
	private StatementValidator Statements => _statementValidator ??= new StatementValidator(
		context,
		_validation,
		Classification,
		Overloads,
		Calls,
		CheckExpression,
		GetExpressionType,
		CheckTargetTypedLambda,
		CheckFunctionGroupConversion,
		CheckDelegateValue,
		IsMethodGroupReference,
		CheckMustUseDiscard,
		() => Switches);

	private VisibilityValidator? _visibilityValidator;
	/// <summary>Shared declaration-level visibility validator.</summary>
	private VisibilityValidator Visibility => _visibilityValidator ??= new VisibilityValidator(context);

	private FunctionBodyValidator? _functionBodyValidator;
	/// <summary>Shared ordinary/extension function-body validator.</summary>
	private FunctionBodyValidator FunctionBodies => _functionBodyValidator ??= new FunctionBodyValidator(
		context,
		_validation,
		Statements,
		Intrinsics,
		Visibility,
		GetBaseIdentifierName);

	private InterfaceConformance? _interfaceConformance;
	/// <summary>Shared nominal interface-conformance service.</summary>
	private InterfaceConformance Interfaces => _interfaceConformance ??= new InterfaceConformance(context);
	private ProtocolConformance? _protocolConformance;
	/// <summary>Shared structural protocol-conformance and ambiguity service.</summary>
	private ProtocolConformance Protocols => _protocolConformance ??= new ProtocolConformance(context, Interfaces);
	private ProtocolDefaultMaterializer? _protocolDefaults;
	/// <summary>Shared materializer for inherited protocol default implementations.</summary>
	private ProtocolDefaultMaterializer ProtocolDefaults => _protocolDefaults ??= new ProtocolDefaultMaterializer(context);

	private GenericFunctionInstantiator? _genericFunctionInstantiator;
	/// <summary>Shared explicit-generic and contract-dispatch monomorphization service.</summary>
	private GenericFunctionInstantiator Generics => _genericFunctionInstantiator ??= new GenericFunctionInstantiator(
		context,
		Interfaces,
		Protocols,
		ProtocolDefaults,
		CheckBlock);

	private ConstructorValidator? _constructorValidator;
	/// <summary>Constructor-specific validation service used by the single validation traversal.</summary>
	private ConstructorValidator Constructors => _constructorValidator ??= new ConstructorValidator(
		context,
		_validation,
		Overloads,
		(extendedTypeName, method, forceMutableThis) => FunctionBodies.ValidateExtensionBody(extendedTypeName, method, forceMutableThis),
		CheckExpression,
		GetExpressionType);

	/// <summary>
	/// Mutable traversal state for the validation run. The state object is kept separate from the
	/// semantic services owned by <see cref="BindingContext"/> so later validators can share the same
	/// single-pass traversal state without depending on <see cref="ValidationPass"/> itself.
	/// </summary>
	private readonly ValidationContext _validation = new();

	public void Process(IEnumerable<CompilationUnitSyntax> units)
	{
		_validation.Units = units as IReadOnlyList<CompilationUnitSyntax> ?? units.ToList();
		foreach (var unit in _validation.Units)
		{
			context.CurrentUnit = unit;
			context.CurrentNamespace = unit.NamespaceDeclaration?.Name;

			var members = context.CurrentNamespace != null ? unit.NamespaceDeclaration!.Members : unit.Members;
			foreach (var member in members)
			{
				// Global constant expressions are emitted without a function frame, but their binary
				// operations still belong to the same semantic authority. DeclarationPass only performs
				// syntax-level constant-shape checks; bind binary nodes here, after the complete declaration
				// universe (including cross-file operators) is stable.
				if (member is GlobalVariableDeclarationSyntax { IsForeign: false, Initializer: not null } global
					&& context.GlobalVariables.Any(entry => ReferenceEquals(entry.Node, global)))
				{
					BindGlobalInitializerBinaries(global.Initializer);
				}
				else if (member is FunctionDeclarationSyntax func)
				{
					var isTemplate = func.GenericParameters.Count > 0 && func.GenericParameters.Any(p => context.ResolveType(p) == null);

					// Interface-parameterized functions are implicit generic templates: their bodies are
					// validated at each call site (monomorphized), never here with an abstract interface type.
					var ifaceTemplateName = context.GetMangledName(func.Name, context.CurrentNamespace);
					var isInterfaceTemplate = context.InterfaceFunctionTemplates.ContainsKey(ifaceTemplateName);

					if (isInterfaceTemplate)
						continue;

					// Protocol-parameterized functions are likewise implicit templates: a protocol name has
					// no value representation, so the body is validated when monomorphized at a call site.
					var isProtocolTemplate = context.ProtocolFunctionTemplates.ContainsKey(ifaceTemplateName);

					if (isProtocolTemplate)
						continue;

					if (!isTemplate)
					{
						// For explicit template specializations, validate the registered monomorphized version
						if (func.GenericParameters.Count > 0)
						{
							var mangledName = context.GetMangledName(func.Name, context.CurrentNamespace);
							var instName = $"{mangledName}<{string.Join(", ", func.GenericParameters)}>";
							var instDecl = context.MonomorphizedFunctionDecls.First(d => d.Name == instName);
							FunctionBodies.ValidateFunctionBody(instDecl);
						}
						else
						{
							FunctionBodies.ValidateFunctionBody(func);
						}
					}
				}
				else if (member is ExposeExternBlockSyntax exportBlock)
				{
					foreach (var exportFunc in exportBlock.Functions)
					{
						var isTemplate = exportFunc.GenericParameters.Count > 0 && exportFunc.GenericParameters.Any(p => context.ResolveType(p) == null);
						var ifaceTemplateName = context.GetMangledName(exportFunc.Name, context.CurrentNamespace);
						var isInterfaceTemplate = context.InterfaceFunctionTemplates.ContainsKey(ifaceTemplateName);
						var isProtocolTemplate = context.ProtocolFunctionTemplates.ContainsKey(ifaceTemplateName);
						if (isTemplate || isInterfaceTemplate || isProtocolTemplate)
							continue;

						// Exposed functions are never generic; fall back to direct body validation.
						FunctionBodies.ValidateFunctionBody(exportFunc);
					}
				}
				else if (member is ExtensionDeclarationSyntax extDecl)
				{
					var extendedType = context.ResolveType(extDecl.ExtendedTypeName);
					// The declaration itself is the primary authority for an open extension scope: if the AST
					// carries T/E/... on `extension Result<T, E>`, those names must resolve while its body is
					// validated regardless of how an imported short owner name re-resolves at this later stage.
					// Registration/type lookup remain defensive fallbacks for rewritten/template-owned blocks.
					var genericExtendedType = extDecl.GenericParameters.Count > 0
						|| context.GenericExtensionTemplateUnits.ContainsKey(extDecl)
						|| (extendedType != null
							&& (context.GenericStructTemplates.ContainsKey(extendedType.Name)
								|| context.GenericUnionTemplates.ContainsKey(extendedType.Name)));

					// A body that extends an open generic type can still be bound: the extension's own
					// type parameters name the same parameters the extended declaration uses, so pushing
					// their scope lets the members be checked instead of silently skipped.
					var extensionScopeDepth = context.GenericParameterScopes.Count;
					if (genericExtendedType)
						context.PushGenericScope("extension", extDecl.ExtendedTypeName, extDecl.GenericParameters);

					foreach (var method in extDecl.Methods
						.Concat(extDecl.Destructors.Select(static d => d.ToFunctionDeclaration()))
						.Concat(extDecl.Operators.Select(static o => o.ToFunctionDeclaration())))
					{
						// A method's own type parameters are not registered while the pass walks the
						// declaration, so push their scope before deciding whether the body is still a
						// template. Only parameters inherited from an unresolved enclosing template keep
						// the body uninstantiable (and therefore skipped); a method that introduces its
						// own parameters can still be bound and checked.
						var genericScopeDepth = context.GenericParameterScopes.Count;
						if (method.GenericParameters.Count > 0)
							context.PushGenericScope("method", method.Name, method.GenericParameters);

						try
						{
							var isMethodTemplate = method.GenericParameters.Count > 0 && method.GenericParameters.Any(p => context.ResolveType(p) == null);
							if (isMethodTemplate)
								continue;

							// Associated functions are receiverless: no `this`, no lifted fields, and no
							// mutability contract, so they take a separate body-validation path.
							if (method.IsAssociated)
								FunctionBodies.ValidateAssociatedFunctionBody(extDecl.ExtendedTypeName, method);
							else
								FunctionBodies.ValidateExtensionBody(extDecl.ExtendedTypeName, method);
						}
						finally
						{
							while (context.GenericParameterScopes.Count > genericScopeDepth)
								context.PopGenericScope();
						}
					}

					foreach (var ctorDecl in extDecl.Constructors)
					{
						Constructors.ValidateBody(extDecl.ExtendedTypeName, ctorDecl);
					}

					while (context.GenericParameterScopes.Count > extensionScopeDepth)
						context.PopGenericScope();
				}
			}
		}

		// Validate monomorphized extension methods and constructors
		var validatedMonomorphized = new HashSet<string>();
		while (true)
		{
			var pending = context.MonomorphizedExtensionDecls.Where(d =>
			{
				var name = context.MonomorphizedExtensionNames[d];
				return !validatedMonomorphized.Contains(name);
			}).ToList();

			if (pending.Count == 0)
				break;

			foreach (var decl in pending)
			{
				var emitName = context.MonomorphizedExtensionNames[decl];
				validatedMonomorphized.Add(emitName);

				if (context.SymbolUnits.TryGetValue(emitName, out var unit))
				{
					context.CurrentUnit = unit;
					context.CurrentNamespace = unit.NamespaceDeclaration?.Name;
				}

				var extendedTypeName = context.MonomorphizedExtensionExtendedTypes[emitName];

				if (decl is FunctionDeclarationSyntax func)
				{
					if (func.IsAssociated)
						FunctionBodies.ValidateAssociatedFunctionBody(extendedTypeName, func);
					else
						FunctionBodies.ValidateExtensionBody(extendedTypeName, func);
				}
				else if (decl is ConstructorDeclarationSyntax ctor)
				{
					Constructors.ValidateBody(extendedTypeName, ctor);
				}
			}
		}
	}

	/// <summary>
	/// Binds binary operations that occur in the restricted compile-time global-initializer grammar.
	/// This deliberately runs in ValidationPass, never DeclarationPass, so an incomplete early probe
	/// cannot poison the final binary-binding cache before operator declarations are registered.
	/// </summary>
	private void BindGlobalInitializerBinaries(ExpressionSyntax expression)
	{
		var globalScope = new SymbolTable();
		Bind(expression);

		void Bind(ExpressionSyntax current)
		{
			switch (current)
			{
				case BinaryExpressionSyntax binary:
					Expressions.Check(binary, globalScope);
					break;
				case UnaryExpressionSyntax { Operator: "-" } unary:
					Bind(unary.Operand);
					break;
				case StructInitializationExpressionSyntax structInitializer:
					foreach (var member in structInitializer.Initializers)
						Bind(member.Expression);
					break;
			}
		}
	}

	/// <summary>Delegates block validation to the shared statement validator.</summary>
	private void CheckBlock(BlockStatementSyntax? block, SymbolTable scope, FunctionDeclarationSyntax currentFunction)
		=> Statements.CheckBlock(block, scope, currentFunction);

	/// <summary>Validates one expression through the shared expression semantic service.</summary>
	private void CheckExpression(ExpressionSyntax expr, SymbolTable scope) => Expressions.Check(expr, scope);

	/// <summary>Returns the semantic type of an expression through the shared expression semantic service.</summary>
	private TypeSymbol? GetExpressionType(ExpressionSyntax expr, SymbolTable scope) => Expressions.GetType(expr, scope);


	/// <summary>Validates a lambda against an expected delegate type through the shared expression validator.</summary>
	private void CheckTargetTypedLambda(LambdaExpressionSyntax lambda, DelegateTypeSymbol delegateType, SymbolTable scope)
		=> Expressions.CheckTargetTypedLambda(lambda, delegateType, scope);

	/// <summary>Validates a function or bound-method group conversion through the shared expression validator.</summary>
	private void CheckFunctionGroupConversion(ExpressionSyntax expression, DelegateTypeSymbol delegateType, SymbolTable scope)
		=> Expressions.CheckFunctionGroupConversion(expression, delegateType, scope);

	/// <summary>Validates an expression against an expected delegate value type.</summary>
	private void CheckDelegateValue(ExpressionSyntax expression, DelegateTypeSymbol delegateType, SymbolTable scope)
		=> Expressions.CheckDelegateValue(expression, delegateType, scope);

	/// <summary>Returns whether a member access denotes a bound method group rather than data access.</summary>
	private bool IsMethodGroupReference(MemberAccessExpressionSyntax memberAccess, SymbolTable scope)
		=> Expressions.IsMethodGroupReference(memberAccess, scope);

	/// <summary>Returns the root identifier of a nested member/borrow expression when one exists.</summary>
	private string? GetBaseIdentifierName(ExpressionSyntax expr) => Expressions.GetBaseIdentifierName(expr);


	/// <summary>Checks that discarding an expression result does not violate must-use semantics.</summary>
	private void CheckMustUseDiscard(ExpressionSyntax expr, SymbolTable scope) => Expressions.CheckMustUseDiscard(expr, scope);
}