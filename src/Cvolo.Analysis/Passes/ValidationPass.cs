using Cvolo.Analysis.Contracts;
using Cvolo.Analysis.Passes.Validation;
using Cvolo.Analysis.Resolution;
using Cvolo.Analysis.Symbols;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using Cvolo.Core.AST.Expressions;
using Cvolo.Core.AST.Statements;
using Cvolo.Core.Diagnostics;

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
		ResolveFunctionTemplateName,
		InstantiateGenericFunction,
		ResolveInterfaceFunctionTemplateName,
		TryResolveInterfaceCall,
		ResolveProtocolFunctionTemplateName,
		TryResolveProtocolCall,
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
		IsMethodGroupReference,
		CheckMustUseDiscard,
		() => Switches);

	private FunctionBodyValidator? _functionBodyValidator;
	/// <summary>Shared ordinary/extension function-body validator.</summary>
	private FunctionBodyValidator FunctionBodies => _functionBodyValidator ??= new FunctionBodyValidator(
		context,
		_validation,
		Statements,
		Intrinsics,
		CheckGenericVisibilityLeak,
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
				if (member is FunctionDeclarationSyntax func)
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
					if (extendedType != null && (context.GenericStructTemplates.ContainsKey(extendedType.Name) || context.GenericUnionTemplates.ContainsKey(extendedType.Name)))
					{
						continue;
					}

					foreach (var method in extDecl.Methods.Concat(extDecl.Destructors.Select(static d => d.ToFunctionDeclaration())))
					{
						FunctionBodies.ValidateExtensionBody(extDecl.ExtendedTypeName, method);
					}

					foreach (var ctorDecl in extDecl.Constructors)
					{
						Constructors.ValidateBody(extDecl.ExtendedTypeName, ctorDecl);
					}
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
					FunctionBodies.ValidateExtensionBody(extendedTypeName, func);
				}
				else if (decl is ConstructorDeclarationSyntax ctor)
				{
					Constructors.ValidateBody(extendedTypeName, ctor);
				}
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

	/// <summary>Validates a value supplied to a delegate-typed target.</summary>
	private void CheckDelegateValueExpression(ExpressionSyntax expr, DelegateTypeSymbol delegateType, SymbolTable scope)
		=> Expressions.CheckDelegateValue(expr, delegateType, scope);

	/// <summary>Validates a lambda against an expected delegate type through the shared expression validator.</summary>
	private void CheckTargetTypedLambda(LambdaExpressionSyntax lambda, DelegateTypeSymbol delegateType, SymbolTable scope)
		=> Expressions.CheckTargetTypedLambda(lambda, delegateType, scope);

	/// <summary>Validates a function or bound-method group conversion through the shared expression validator.</summary>
	private void CheckFunctionGroupConversion(ExpressionSyntax expression, DelegateTypeSymbol delegateType, SymbolTable scope)
		=> Expressions.CheckFunctionGroupConversion(expression, delegateType, scope);

	/// <summary>Returns whether a member access denotes a bound method group rather than data access.</summary>
	private bool IsMethodGroupReference(MemberAccessExpressionSyntax memberAccess, SymbolTable scope)
		=> Expressions.IsMethodGroupReference(memberAccess, scope);

	/// <summary>Returns the root identifier of a nested member/borrow expression when one exists.</summary>
	private string? GetBaseIdentifierName(ExpressionSyntax expr) => Expressions.GetBaseIdentifierName(expr);


	/// <summary>Checks that discarding an expression result does not violate must-use semantics.</summary>
	private void CheckMustUseDiscard(ExpressionSyntax expr, SymbolTable scope) => Expressions.CheckMustUseDiscard(expr, scope);

	private string? ResolveFunctionTemplateName(string name, SymbolTable scope)
	{
		var localMangled = context.GetMangledName(name, context.CurrentNamespace);
		if (context.GenericFunctionTemplates.ContainsKey(localMangled))
			return localMangled;

		if (context.CurrentUnit is not null)
		{
			var activeUsings = context.GetActiveUsings(context.CurrentUnit);

			foreach (var ns in activeUsings)
			{
				var candidateMangled = context.GetMangledName(name, ns);
				if (context.GenericFunctionTemplates.ContainsKey(candidateMangled))
					return candidateMangled;
			}
		}

		if (context.GenericFunctionTemplates.ContainsKey(name))
			return name;
		return null;
	}

	private FunctionSymbol InstantiateGenericFunction(FunctionDeclarationSyntax templateDecl, List<TypeSymbol> typeArgs, SymbolTable scope)
	{
		// Resolve the template's fully qualified mangled name (e.g. BankSystem.IO.PrintAccountInfo)
		var templateMangledName = ResolveFunctionTemplateName(templateDecl.Name, scope)!;
		var rawName = $"{templateMangledName}<{string.Join(",", typeArgs.Select(t => t.Name))}>";
		// Canonical Name
		var instName = context.NormalizeGenericName(rawName);

		if (context.MonomorphizedFunctions.TryGetValue(instName, out var existing))
			return existing;

		var substitutionMap = new Dictionary<string, TypeSymbol>();
		for (var i = 0; i < templateDecl.GenericParameters.Count; i++)
		{
			substitutionMap[templateDecl.GenericParameters[i]] = typeArgs[i];
		}

		TypeSymbol ResolveSubstitutedType(string typeName)
		{
			// Substitute placeholders inside type name strings first
			var substitutedTypeName = typeName;
			foreach (var kv in substitutionMap)
			{
				substitutedTypeName = SubstituteTypeToken(substitutedTypeName, kv.Key, kv.Value.Name);
			}

			if (substitutedTypeName.StartsWith("refvar ") || substitutedTypeName.StartsWith("ref "))
			{
				var isMutable = substitutedTypeName.StartsWith("refvar ");
				var innerName = isMutable ? substitutedTypeName.Substring(7) : substitutedTypeName.Substring(4);
				var innerType = ResolveSubstitutedType(innerName);
				return new PointerTypeSymbol(innerType, isMutable);
			}

			return context.ResolveType(substitutedTypeName)!;
		}

		var returnType = ResolveSubstitutedType(templateDecl.ReturnType);
		var parameters = new List<ParameterSymbol>();
		var instParameters = new List<ParameterSyntax>();

		foreach (var param in templateDecl.Parameters)
		{
			var paramType = ResolveSubstitutedType(param.Type);
			parameters.Add(new ParameterSymbol(param.Name, paramType));
			instParameters.Add(new ParameterSyntax(param.Span, paramType.Name, param.Name));
		}

		var instSymbol = new FunctionSymbol(instName, returnType, parameters)
		{
			Visibility = templateDecl.Visibility,
			SafetyTier = templateDecl.Modifier ?? SafetyTier.Safe
		};
		var templateMangledNameForUnit = ResolveFunctionTemplateName(templateDecl.Name, scope);
		if (templateMangledNameForUnit is not null && context.SymbolUnits.TryGetValue(templateMangledNameForUnit, out var declaringUnit))
			instSymbol.DeclaringUnit = declaringUnit;
		context.MonomorphizedFunctions[instName] = instSymbol;

		var instBody = SubstituteBlockGenerics(templateDecl.Body, substitutionMap);
		var instDecl = new FunctionDeclarationSyntax(templateDecl.Span, returnType.Name, instName, [], instParameters, instBody, modifier: templateDecl.Modifier, visibility: templateDecl.Visibility);

		context.MonomorphizedFunctionDecls.Add(instDecl);

		// Map the monomorphized instance to the template's original file unit
		if (context.SymbolUnits.TryGetValue(templateMangledName, out var templateUnit))
		{
			context.SymbolUnits[instName] = templateUnit;
		}

		// Bind the newly generated function body immediately!
		var localScope = new SymbolTable(context.Globals);
		foreach (var p in parameters)
		{
			localScope.Declare(new VariableSymbol(p.Name, p.Type, p.Type is PointerTypeSymbol { IsMutable: true }) { IsInitialized = true, Origin = OriginKind.Parameter });
		}

		CheckBlock(instBody, localScope, instDecl);

		return instSymbol;
	}

	// ---------------------------------------------------------------------------
	// Interface-parameterized (implicit generic) function dispatch.
	// A function with a nominal-interface-typed parameter is lowered to a template
	// and monomorphized at each call site with the concrete conforming argument
	// type (static-only dispatch; no vtable / fat pointers).
	// ---------------------------------------------------------------------------

	private string? ResolveInterfaceFunctionTemplateName(string name, SymbolTable scope)
	{
		var localMangled = context.GetMangledName(name, context.CurrentNamespace);
		if (context.InterfaceFunctionTemplates.ContainsKey(localMangled))
			return localMangled;

		if (context.CurrentUnit is not null)
		{
			var activeUsings = context.GetActiveUsings(context.CurrentUnit);

			foreach (var ns in activeUsings)
			{
				var candidateMangled = context.GetMangledName(name, ns);
				if (context.InterfaceFunctionTemplates.ContainsKey(candidateMangled))
					return candidateMangled;
			}
		}

		if (context.InterfaceFunctionTemplates.ContainsKey(name))
			return name;
		return null;
	}


	/// <summary>
	/// Resolves a call to an interface-parameterized function by monomorphizing the
	/// template with the concrete conforming argument types. When the callee is an
	/// interface template but the call cannot be instantiated, reports the specific
	/// id-less diagnostic (conformance / argument-count / conflicting-concrete) and
	/// returns null so the caller suppresses the generic "no overload" message.
	/// </summary>
	private FunctionSymbol? TryResolveInterfaceCall(CallExpressionSyntax call, IReadOnlyList<TypeSymbol> argTypes, SymbolTable scope)
	{
		var templateName = ResolveInterfaceFunctionTemplateName(call.FunctionName, scope);
		if (templateName is null)
			return null;

		var templateDecl = context.InterfaceFunctionTemplates[templateName];
		var currentFileContext = context.FileContexts[context.CurrentUnit!];

		if (argTypes.Count != templateDecl.Parameters.Count)
		{
			context.Diagnostics.Report(currentFileContext, call.Span,
				$"Function '{call.FunctionName}' expects {templateDecl.Parameters.Count} argument(s) but received {argTypes.Count}");
			return null;
		}

		// Build the substitution map: each interface-typed parameter maps to its concrete arg type.
		var substitutionMap = new Dictionary<string, TypeSymbol>();
		for (var i = 0; i < templateDecl.Parameters.Count; i++)
		{
			var param = templateDecl.Parameters[i];

			// Unwrap an optional ref/refvar prefix to discover the underlying interface name.
			var isRefParam = param.Type.StartsWith("refvar ", StringComparison.Ordinal)
				|| param.Type.StartsWith("ref ", StringComparison.Ordinal);
			var interfaceTypeName = isRefParam
				? (param.Type.StartsWith("refvar ", StringComparison.Ordinal) ? param.Type[7..] : param.Type[4..])
				: param.Type;

			if (context.ResolveType(interfaceTypeName) is not InterfaceTypeSymbol iface)
				continue;

			// For a ref/refvar interface parameter, the concrete argument arrives as a pointer;
			// substitute the referent type name so the ref/refvar wrapper is supplied by the
			// template's own parameter string (e.g. "refvar IShape" -> "refvar Rect").
			var concreteArg = argTypes[i];
			var concrete = concreteArg is PointerTypeSymbol cPtr ? cPtr.ReferencedType : concreteArg;

			// An argument that is itself interface-typed has no concrete representation to
			// monomorphize against (the interface is an abstract marker, not a value type).
			// Report a dedicated unresolved-concrete-type diagnostic instead of the misleading
			// "does not conform" message.
			if (concrete is InterfaceTypeSymbol)
			{
				context.Diagnostics.Report(currentFileContext, call.Span,
					$"Interface parameter '{param.Name}' of function '{call.FunctionName}' cannot be resolved to a concrete conforming type; argument is abstract interface type '{concrete.Name}'");
				return null;
			}

			if (!Interfaces.Conforms(concrete, iface))
			{
				context.Diagnostics.Report(currentFileContext, call.Span,
					$"Type '{concrete.Name}' does not conform to interface '{iface.Name}' for parameter '{param.Name}'");
				return null;
			}

			if (substitutionMap.TryGetValue(interfaceTypeName, out var existing) && existing.Name != concrete.Name)
			{
				context.Diagnostics.Report(currentFileContext, call.Span,
					$"Interface parameter '{param.Name}' requires a single concrete type, but both '{existing.Name}' and '{concrete.Name}' were passed");
				return null;
			}

			substitutionMap[interfaceTypeName] = concrete;
		}

		if (substitutionMap.Count == 0)
			return null;

		return InstantiateInterfaceFunction(templateDecl, substitutionMap, scope);
	}

	private FunctionSymbol InstantiateInterfaceFunction(
		FunctionDeclarationSyntax templateDecl, Dictionary<string, TypeSymbol> substitutionMap, SymbolTable scope)
	{
		var templateMangledName = ResolveInterfaceFunctionTemplateName(templateDecl.Name, scope)!;
		return InstantiateDispatchFunction(templateDecl, substitutionMap, scope, templateMangledName);
	}

	private FunctionSymbol InstantiateProtocolFunction(
		FunctionDeclarationSyntax templateDecl, Dictionary<string, TypeSymbol> substitutionMap, SymbolTable scope)
	{
		var templateMangledName = ResolveProtocolFunctionTemplateName(templateDecl.Name, scope)!;
		return InstantiateDispatchFunction(templateDecl, substitutionMap, scope, templateMangledName);
	}

	/// <summary>
	/// Shared monomorphization core for interface/protocol-parameterized templates:
	/// substitutes the concrete conforming type names into the signature and body,
	/// registers the instance, and validates the body once with the concrete types.
	/// </summary>
	private static string SubstituteTypeToken(string text, string key, string value)
	{
		// A generic-instantiated key (e.g. "IContainer<int>") can never be matched by a
		// \b...\b regex pattern (a word boundary cannot be asserted after a non-word '>'),
		// so generic keys are substituted as exact type tokens instead.
		if (key.Contains('<'))
			return text.Replace(key, value);

		return System.Text.RegularExpressions.Regex.Replace(text, $@"\b{System.Text.RegularExpressions.Regex.Escape(key)}\b", value);
	}

	private FunctionSymbol InstantiateDispatchFunction(
	FunctionDeclarationSyntax templateDecl, Dictionary<string, TypeSymbol> substitutionMap, SymbolTable scope,
	string templateMangledName)
	{
		var rawName = $"{templateMangledName}<{string.Join(",", substitutionMap.Values.Select(t => t.Name))}>";
		var instName = context.NormalizeGenericName(rawName);

		if (context.MonomorphizedFunctions.TryGetValue(instName, out var existing))
			return existing;

		TypeSymbol ResolveSubstitutedType(string typeName)
		{
			var substitutedTypeName = typeName;
			foreach (var kv in substitutionMap)
			{
				substitutedTypeName = SubstituteTypeToken(substitutedTypeName, kv.Key, kv.Value.Name);
			}

			if (substitutedTypeName.StartsWith("refvar ") || substitutedTypeName.StartsWith("ref "))
			{
				var isMutable = substitutedTypeName.StartsWith("refvar ");
				var innerName = isMutable ? substitutedTypeName.Substring(7) : substitutedTypeName.Substring(4);
				var innerType = ResolveSubstitutedType(innerName);
				return new PointerTypeSymbol(innerType, isMutable);
			}

			return context.ResolveType(substitutedTypeName)!;
		}

		var returnType = ResolveSubstitutedType(templateDecl.ReturnType);
		var parameters = new List<ParameterSymbol>();
		var instParameters = new List<ParameterSyntax>();

		foreach (var param in templateDecl.Parameters)
		{
			var paramType = ResolveSubstitutedType(param.Type);
			parameters.Add(new ParameterSymbol(param.Name, paramType));
			instParameters.Add(new ParameterSyntax(param.Span, paramType.Name, param.Name));
		}

		var instSymbol = new FunctionSymbol(instName, returnType, parameters)
		{
			Visibility = templateDecl.Visibility,
			SafetyTier = templateDecl.Modifier ?? SafetyTier.Safe
		};

		if (context.SymbolUnits.TryGetValue(templateMangledName, out var declaringUnit))
			instSymbol.DeclaringUnit = declaringUnit;
		context.MonomorphizedFunctions[instName] = instSymbol;

		var instBody = SubstituteBlockGenerics(templateDecl.Body, substitutionMap);
		var instDecl = new FunctionDeclarationSyntax(templateDecl.Span, returnType.Name, instName, [], instParameters, instBody, modifier: templateDecl.Modifier, visibility: templateDecl.Visibility);

		context.MonomorphizedFunctionDecls.Add(instDecl);

		if (context.SymbolUnits.TryGetValue(templateMangledName, out var templateUnit))
		{
			context.SymbolUnits[instName] = templateUnit;
		}

		var localScope = new SymbolTable(context.Globals);
		foreach (var p in parameters)
		{
			localScope.Declare(new VariableSymbol(p.Name, p.Type, p.Type is PointerTypeSymbol { IsMutable: true }) { IsInitialized = true, Origin = OriginKind.Parameter });
		}

		CheckBlock(instBody, localScope, instDecl);

		return instSymbol;
	}

	// ---------------------------------------------------------------------------
	// Protocol-parameterized (structural / duck-typed implicit generic) dispatch.
	// A function with a protocol-typed parameter is lowered to a template and
	// monomorphized at each call site with the concrete argument type that
	// structurally conforms to the protocol's canonical member tokens. Conformance
	// is implicit — no `extension T : IProtocol` declaration exists for protocols.
	// ---------------------------------------------------------------------------

	private string? ResolveProtocolFunctionTemplateName(string name, SymbolTable scope)
	{
		var localMangled = context.GetMangledName(name, context.CurrentNamespace);
		if (context.ProtocolFunctionTemplates.ContainsKey(localMangled))
			return localMangled;

		if (context.CurrentUnit is not null)
		{
			var activeUsings = context.GetActiveUsings(context.CurrentUnit);

			foreach (var ns in activeUsings)
			{
				var candidateMangled = context.GetMangledName(name, ns);
				if (context.ProtocolFunctionTemplates.ContainsKey(candidateMangled))
					return candidateMangled;
			}
		}

		if (context.ProtocolFunctionTemplates.ContainsKey(name))
			return name;
		return null;
	}

	/// <summary>
	/// Resolves a call to a protocol-parameterized function by monomorphizing the
	/// template with the concrete structurally conforming argument types. Reports
	/// the specific id-less diagnostic (conformance / argument-count / conflicting
	/// concrete) and returns null so the caller suppresses the generic "no
	/// overload" message.
	/// </summary>
	private FunctionSymbol? TryResolveProtocolCall(CallExpressionSyntax call, IReadOnlyList<TypeSymbol> argTypes, SymbolTable scope)
	{
		var templateName = ResolveProtocolFunctionTemplateName(call.FunctionName, scope);
		if (templateName is null)
			return null;

		var templateDecl = context.ProtocolFunctionTemplates[templateName];
		var currentFileContext = context.FileContexts[context.CurrentUnit!];

		if (argTypes.Count != templateDecl.Parameters.Count)
		{
			context.Diagnostics.Report(currentFileContext, call.Span,
				$"Function '{call.FunctionName}' expects {templateDecl.Parameters.Count} argument(s) but received {argTypes.Count}");
			return null;
		}

		// Conforming concrete types whose protocol defaults must be materialized
		// before the body is validated (so inherited-member calls resolve).
		var conformedPairs = new List<(TypeSymbol Concrete, ProtocolTypeSymbol Proto)>();

		// Build the substitution map: each protocol-typed parameter maps to its concrete arg type.
		var substitutionMap = new Dictionary<string, TypeSymbol>();
		for (var i = 0; i < templateDecl.Parameters.Count; i++)
		{
			var param = templateDecl.Parameters[i];

			// Unwrap an optional ref/refvar prefix to discover the underlying protocol name.
			var isRefParam = param.Type.StartsWith("refvar ", StringComparison.Ordinal)
				|| param.Type.StartsWith("ref ", StringComparison.Ordinal);
			var protocolTypeName = isRefParam
				? (param.Type.StartsWith("refvar ", StringComparison.Ordinal) ? param.Type[7..] : param.Type[4..])
				: param.Type;

			if (context.ResolveType(protocolTypeName) is not ProtocolTypeSymbol proto)
				continue;

			// For a ref/refvar protocol parameter, the concrete argument arrives as a pointer;
			// substitute the referent type name so ref/refvar is supplied by the template's
			// own parameter string (e.g. "refvar IShape" -> "refvar Rect").
			var concreteArg = argTypes[i];
			var concrete = concreteArg is PointerTypeSymbol cPtr ? cPtr.ReferencedType : concreteArg;

			if (concrete is ProtocolTypeSymbol or InterfaceTypeSymbol)
			{
				context.Diagnostics.Report(currentFileContext, call.Span,
					$"Protocol parameter '{param.Name}' of function '{call.FunctionName}' cannot be resolved to a concrete conforming type; argument is abstract protocol type '{concrete.Name}'");
				return null;
			}

			if (!Protocols.Conforms(concrete, proto))
			{
				context.Diagnostics.Report(currentFileContext, call.Span,
					$"Type '{concrete.Name}' does not structurally conform to protocol '{proto.Name}' for parameter '{param.Name}'");
				return null;
			}

			// Protocol `for ...` requires-clause (lazy, at the dispatch call site):
			// the concrete type must itself satisfy the named contract. Missing or
			// unresolvable constraints are treated conservatively as non-conforming.
			if (proto.Constraint is not null && !Protocols.SatisfiesConstraint(concrete, proto.Constraint))
			{
				context.Diagnostics.Report(currentFileContext, call.Span,
					$"Type '{concrete.Name}' does not satisfy the requires-clause '{proto.Constraint}' of protocol '{proto.Name}'.");
				return null;
			}

			conformedPairs.Add((concrete, proto));

			// Ambiguity rule (spec §7.C): a concrete type matching several contracts
			// (declared in different extension namespaces) for the same member
			// signature yields more than one distinct implementation -> error.
			if (Protocols.TryFindAmbiguousMember(concrete, proto, out var ambiguousMember))
			{
				context.Diagnostics.Report(currentFileContext, call.Span,
					$"Ambiguous implementation of '{ambiguousMember}' for protocol '{proto.Name}' on type '{concrete.Name}': multiple extension methods match the required signature.");
				return null;
			}

			if (substitutionMap.TryGetValue(protocolTypeName, out var existing) && existing.Name != concrete.Name)
			{
				context.Diagnostics.Report(currentFileContext, call.Span,
					$"Protocol parameter '{param.Name}' requires a single concrete type, but both '{existing.Name}' and '{concrete.Name}' were passed");
				return null;
			}

			substitutionMap[protocolTypeName] = concrete;
		}

		if (substitutionMap.Count == 0)
			return null;

		// Inherited default implementations (spec §4): materialize a substituted
		// copy of each default onto its conforming concrete type so calls inside
		// the monomorphized body resolve to a real function.
		foreach (var (conformed, conformedProto) in conformedPairs.Distinct())
			ProtocolDefaults.Materialize(conformed, conformedProto);

		return InstantiateProtocolFunction(templateDecl, substitutionMap, scope);
	}

	private BlockStatementSyntax SubstituteBlockGenerics(BlockStatementSyntax block, Dictionary<string, TypeSymbol> substitutionMap)
	{
		var statements = new List<SyntaxNode>();
		foreach (var stmt in block.Statements)
			statements.Add(SubstituteStatementGenerics(stmt, substitutionMap));
		return new BlockStatementSyntax(block.Span, statements);
	}

	private SyntaxNode SubstituteStatementGenerics(SyntaxNode stmt, Dictionary<string, TypeSymbol> substitutionMap)
	{
		switch (stmt)
		{
			case VariableDeclarationSyntax v:
				var newType = v.Type;
				if (newType != null)
				{
					foreach (var kv in substitutionMap)
					{
						newType = SubstituteTypeToken(newType, kv.Key, kv.Value.Name);
					}
				}

				return new VariableDeclarationSyntax(v.Span, v.IsMutable, newType, v.Name, v.Initializer != null ? SubstituteExpressionGenerics(v.Initializer, substitutionMap) : null);

			case BlockStatementSyntax b:
				return SubstituteBlockGenerics(b, substitutionMap);

			case IfStatementSyntax i:
				return new IfStatementSyntax(i.Span, SubstituteExpressionGenerics(i.Condition, substitutionMap), SubstituteStatementGenerics(i.ThenStatement, substitutionMap), i.ElseClause != null ? new ElseClauseSyntax(i.ElseClause.Span, SubstituteBlockGenerics(i.ElseClause.Body, substitutionMap)) : null);

			case WhileStatementSyntax w:
				return new WhileStatementSyntax(w.Span, SubstituteExpressionGenerics(w.Condition, substitutionMap), SubstituteStatementGenerics(w.Body, substitutionMap), w.Label);

			case ForStatementSyntax f:
				return new ForStatementSyntax(f.Span, SubstituteStatementGenerics(f.Initializer, substitutionMap) as VariableDeclarationSyntax ?? f.Initializer, SubstituteExpressionGenerics(f.Condition, substitutionMap), SubstituteExpressionGenerics(f.Increment, substitutionMap), SubstituteStatementGenerics(f.Body, substitutionMap), f.Label);

			case ForEachStatementSyntax forEach:
				{
					var newExplicitType = forEach.ExplicitItemType;
					if (newExplicitType != null)
					{
						foreach (var kv in substitutionMap)
							newExplicitType = SubstituteTypeToken(newExplicitType, kv.Key, kv.Value.Name);
					}
					return new ForEachStatementSyntax(forEach.Span, forEach.BindingKind, newExplicitType, forEach.ItemName, SubstituteExpressionGenerics(forEach.Collection, substitutionMap), SubstituteStatementGenerics(forEach.Body, substitutionMap) as BlockStatementSyntax ?? forEach.Body, forEach.Label);
				}

			case ReturnStatementSyntax r:
				return new ReturnStatementSyntax(r.Span, r.Expression != null ? SubstituteExpressionGenerics(r.Expression, substitutionMap) : null);

			case ExpressionStatementSyntax e:
				return new ExpressionStatementSyntax(e.Span, SubstituteExpressionGenerics(e.Expression, substitutionMap));

			case SwitchStatementSyntax s:
				return new SwitchStatementSyntax(s.Span, SubstituteExpressionGenerics(s.Expression, substitutionMap),
					s.Cases.Select(c => new SwitchCaseSyntax(c.Span, c.VariantName, c.VariableName, c.IsDefault,
						c.Body.Select(st => SubstituteStatementGenerics(st, substitutionMap)).ToList())).ToList());

			case SwitchCaseSyntax c:
				return new SwitchCaseSyntax(c.Span, c.VariantName, c.VariableName, c.IsDefault,
					c.Body.Select(st => SubstituteStatementGenerics(st, substitutionMap)).ToList());

			case LabeledBlockStatementSyntax lb:
				return new LabeledBlockStatementSyntax(lb.Span, lb.Label, SubstituteBlockGenerics(lb.Body, substitutionMap));

			default:
				return stmt;
		}
	}

	private ExpressionSyntax SubstituteExpressionGenerics(ExpressionSyntax expr, Dictionary<string, TypeSymbol> substitutionMap)
	{
		switch (expr)
		{
			case BinaryExpressionSyntax bin:
				return new BinaryExpressionSyntax(bin.Span, SubstituteExpressionGenerics(bin.Left, substitutionMap), bin.Operator, SubstituteExpressionGenerics(bin.Right, substitutionMap));

			case UnaryExpressionSyntax unary:
				var newOp = unary.Operator;
				if (newOp.StartsWith("(") && newOp.EndsWith(")"))
				{
					foreach (var kv in substitutionMap)
					{
						newOp = SubstituteTypeToken(newOp, kv.Key, kv.Value.Name);
					}
				}

				return new UnaryExpressionSyntax(unary.Span, newOp, SubstituteExpressionGenerics(unary.Operand, substitutionMap));

			case IsPatternExpressionSyntax isPat:
				return new IsPatternExpressionSyntax(isPat.Span, SubstituteExpressionGenerics(isPat.Operand, substitutionMap), isPat.VariantName, isPat.BoundName);

			case CallExpressionSyntax call:
				var newTypeArgs = call.TypeArguments.Select(t =>
				{
					var substituted = t;
					foreach (var kv in substitutionMap)
					{
						substituted = SubstituteTypeToken(substituted, kv.Key, kv.Value.Name);
					}

					return substituted;
				}).ToList();
				var newArgs = call.Arguments.Select(a => SubstituteExpressionGenerics(a, substitutionMap)).ToList();
				return new CallExpressionSyntax(call.Span, call.FunctionName, newTypeArgs, newArgs, call.ArgumentListSpan);

			case StructInitializationExpressionSyntax structInit:
				var newTypeName = structInit.StructTypeName;
				foreach (var kv in substitutionMap)
				{
					newTypeName = SubstituteTypeToken(newTypeName, kv.Key, kv.Value.Name);
				}

				var newInits = structInit.Initializers.Select(i => new MemberInitializerSyntax(i.Span, i.MemberName, SubstituteExpressionGenerics(i.Expression, substitutionMap))).ToList();
				return new StructInitializationExpressionSyntax(structInit.Span, newTypeName, newInits);

			case MemberAccessExpressionSyntax m:
				return new MemberAccessExpressionSyntax(m.Span, SubstituteExpressionGenerics(m.Expression, substitutionMap), m.MemberName);

			case IndexExpressionSyntax idx:
				return new IndexExpressionSyntax(idx.Span, SubstituteExpressionGenerics(idx.Left, substitutionMap), SubstituteExpressionGenerics(idx.Index, substitutionMap));

			case BorrowExpressionSyntax b:
				return new BorrowExpressionSyntax(b.Span, SubstituteExpressionGenerics(b.Expression, substitutionMap), b.IsMutable);

			case HeapAllocationExpressionSyntax h:
				return new HeapAllocationExpressionSyntax(h.Span, SubstituteExpressionGenerics(h.Expression, substitutionMap));

			case ArrayInitializationExpressionSyntax arr:
				return new ArrayInitializationExpressionSyntax(arr.Span, arr.Elements.Select(e => SubstituteExpressionGenerics(e, substitutionMap)).ToList());

			case TernaryExpressionSyntax t:
				return new TernaryExpressionSyntax(t.Span, SubstituteExpressionGenerics(t.Condition, substitutionMap), SubstituteExpressionGenerics(t.ThenExpression, substitutionMap), SubstituteExpressionGenerics(t.ElseExpression, substitutionMap));

			case DefaultExpressionSyntax def:
				if (def.TypeName is null)
					return def;

				var newDefaultType = def.TypeName;
				foreach (var kv in substitutionMap)
				{
					newDefaultType = SubstituteTypeToken(newDefaultType, kv.Key, kv.Value.Name);
				}

				return new DefaultExpressionSyntax(def.Span, newDefaultType);

			default:
				return expr;
		}
	}

	/// <summary>
	/// Emits CVL1038 error if a public symbol exposes a generic type instantiation
	/// whose type argument is private or internal.
	/// </summary>
	private void CheckGenericVisibilityLeak(TextSpan span, TypeSymbol? type, string hostName)
	{
		if (type is null)
			return;

		if (type is PointerTypeSymbol ptr)
			type = ptr.ReferencedType;

		if (!type.Name.Contains('<'))
			return;

		var openBracket = type.Name.IndexOf('<');
		var closeBracket = type.Name.LastIndexOf('>');
		if (openBracket <= 0 || closeBracket <= openBracket)
			return;

		var argsPart = type.Name.Substring(openBracket + 1, closeBracket - openBracket - 1);
		foreach (var rawArg in argsPart.Split(','))
		{
			var argType = context.ResolveType(rawArg.Trim());
			if (argType is null)
				continue;

			if (argType is StructTypeSymbol st && st.Visibility < Visibility.Public)
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(
					currentFileContext,
					span,
					$"The visibility of generic type instantiation '{type.Name}' exceeds the visibility of its type argument '{st.Name}'. Upgrade the argument visibility or restrict the parent declaration.",
					DiagnosticIds.GenericVisibilityLeak);
			}
			else if (argType is UnionTypeSymbol ut && ut.Visibility < Visibility.Public)
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(
					currentFileContext,
					span,
					$"The visibility of generic type instantiation '{type.Name}' exceeds the visibility of its type argument '{ut.Name}'. Upgrade the argument visibility or restrict the parent declaration.",
					DiagnosticIds.GenericVisibilityLeak);
			}
			else if (argType is EnumTypeSymbol et && et.Visibility < Visibility.Public)
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(
					currentFileContext,
					span,
					$"The visibility of generic type instantiation '{type.Name}' exceeds the visibility of its type argument '{et.Name}'. Upgrade the argument visibility or restrict the parent declaration.",
					DiagnosticIds.GenericVisibilityLeak);
			}
		}
	}

	private static bool IsFunctionUnsafeBodyOnly(FunctionSymbol func)
	{
		// [UnsafeBody] encapsulates unsafe code safely, so callers in safe code CAN call it.
		// Pure 'unsafe fn' signatures force callers to be in an unsafe context.
		return func.IsUnsafeBody;
	}
}
