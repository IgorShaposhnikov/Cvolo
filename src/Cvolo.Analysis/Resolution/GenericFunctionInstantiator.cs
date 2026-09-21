using Cvolo.Analysis.Contracts;
using Cvolo.Analysis.Symbols;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using Cvolo.Core.AST.Expressions;
using Cvolo.Core.AST.Statements;

namespace Cvolo.Analysis.Resolution;

/// <summary>
/// Resolves and materializes explicit generic functions and static interface/protocol dispatch specializations.
/// </summary>
/// <remarks>
/// This service owns monomorphization mechanics and AST type-token substitution. It deliberately does not
/// perform an independent AST pass: newly materialized bodies are handed back to the active validation traversal
/// through the supplied block-validation callback. Nominal and structural conformance remain delegated to the
/// dedicated contract services.
/// </remarks>
/// <remarks>
/// Creates a monomorphization service over the active binding context and shared contract services.
/// </remarks>
internal sealed class GenericFunctionInstantiator(
	BindingContext context,
	InterfaceConformance interfaces,
	ProtocolConformance protocols,
	ProtocolDefaultMaterializer protocolDefaults,
	Action<BlockStatementSyntax?, SymbolTable, FunctionDeclarationSyntax> validateBlock)
{

	/// <summary>
	/// Resolves an explicit generic function template name through the current namespace, active
	/// using directives, and finally the already-qualified template registry.
	/// </summary>
	public string? ResolveFunctionTemplateName(string name, SymbolTable scope)
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

	/// <summary>
	/// Materializes and validates one explicit generic function specialization for concrete type arguments.
	/// </summary>
	public FunctionSymbol InstantiateGenericFunction(FunctionDeclarationSyntax templateDecl, List<TypeSymbol> typeArgs, SymbolTable scope)
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

		// Resolves one template signature type after applying the current substitution map.
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

		validateBlock(instBody, localScope, instDecl);

		return instSymbol;
	}

	// ---------------------------------------------------------------------------
	// Interface-parameterized (implicit generic) function dispatch.
	// A function with a nominal-interface-typed parameter is lowered to a template
	// and monomorphized at each call site with the concrete conforming argument
	// type (static-only dispatch; no vtable / fat pointers).
	// ---------------------------------------------------------------------------

	/// <summary>
	/// Resolves the registered template name for a function whose implicit generic parameters are nominal interfaces.
	/// </summary>
	public string? ResolveInterfaceFunctionTemplateName(string name, SymbolTable scope)
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
	public FunctionSymbol? TryResolveInterfaceCall(CallExpressionSyntax call, IReadOnlyList<TypeSymbol> argTypes, SymbolTable scope)
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

			if (!interfaces.Conforms(concrete, iface))
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

	/// <summary>
	/// Instantiates an interface-parameterized dispatch template using the concrete substitution map.
	/// </summary>
	private FunctionSymbol InstantiateInterfaceFunction(
		FunctionDeclarationSyntax templateDecl, Dictionary<string, TypeSymbol> substitutionMap, SymbolTable scope)
	{
		var templateMangledName = ResolveInterfaceFunctionTemplateName(templateDecl.Name, scope)!;
		return InstantiateDispatchFunction(templateDecl, substitutionMap, scope, templateMangledName);
	}

	/// <summary>
	/// Instantiates a protocol-parameterized dispatch template using the concrete substitution map.
	/// </summary>
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

	/// <summary>
	/// Registers, substitutes, and validates the shared monomorphized body used by interface and protocol dispatch.
	/// </summary>
	private FunctionSymbol InstantiateDispatchFunction(
		FunctionDeclarationSyntax templateDecl, Dictionary<string, TypeSymbol> substitutionMap, SymbolTable scope,
	string templateMangledName)
	{
		var rawName = $"{templateMangledName}<{string.Join(",", substitutionMap.Values.Select(t => t.Name))}>";
		var instName = context.NormalizeGenericName(rawName);

		if (context.MonomorphizedFunctions.TryGetValue(instName, out var existing))
			return existing;

		// Resolves one dispatch signature type after applying the current substitution map.
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

		validateBlock(instBody, localScope, instDecl);

		return instSymbol;
	}

	// ---------------------------------------------------------------------------
	// Protocol-parameterized (structural / duck-typed implicit generic) dispatch.
	// A function with a protocol-typed parameter is lowered to a template and
	// monomorphized at each call site with the concrete argument type that
	// structurally conforms to the protocol's canonical member tokens. Conformance
	// is implicit — no `extension T : IProtocol` declaration exists for protocols.
	// ---------------------------------------------------------------------------

	/// <summary>
	/// Resolves the registered template name for a function whose implicit generic parameters are structural protocols.
	/// </summary>
	public string? ResolveProtocolFunctionTemplateName(string name, SymbolTable scope)
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
	public FunctionSymbol? TryResolveProtocolCall(CallExpressionSyntax call, IReadOnlyList<TypeSymbol> argTypes, SymbolTable scope)
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

			if (!protocols.Conforms(concrete, proto))
			{
				context.Diagnostics.Report(currentFileContext, call.Span,
					$"Type '{concrete.Name}' does not structurally conform to protocol '{proto.Name}' for parameter '{param.Name}'");
				return null;
			}

			// Protocol `for ...` requires-clause (lazy, at the dispatch call site):
			// the concrete type must itself satisfy the named contract. Missing or
			// unresolvable constraints are treated conservatively as non-conforming.
			if (proto.Constraint is not null && !protocols.SatisfiesConstraint(concrete, proto.Constraint))
			{
				context.Diagnostics.Report(currentFileContext, call.Span,
					$"Type '{concrete.Name}' does not satisfy the requires-clause '{proto.Constraint}' of protocol '{proto.Name}'.");
				return null;
			}

			conformedPairs.Add((concrete, proto));

			// Ambiguity rule (spec §7.C): a concrete type matching several contracts
			// (declared in different extension namespaces) for the same member
			// signature yields more than one distinct implementation -> error.
			if (protocols.TryFindAmbiguousMember(concrete, proto, out var ambiguousMember))
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
			protocolDefaults.Materialize(conformed, conformedProto);

		return InstantiateProtocolFunction(templateDecl, substitutionMap, scope);
	}

	/// <summary>
	/// Clones a block while substituting generic or contract type tokens throughout contained statements.
	/// </summary>
	private BlockStatementSyntax SubstituteBlockGenerics(BlockStatementSyntax block, Dictionary<string, TypeSymbol> substitutionMap)
	{
		var statements = new List<SyntaxNode>();
		foreach (var stmt in block.Statements)
			statements.Add(SubstituteStatementGenerics(stmt, substitutionMap));
		return new BlockStatementSyntax(block.Span, statements);
	}

	/// <summary>
	/// Clones one statement with generic or contract type-token substitutions applied recursively.
	/// </summary>
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

	/// <summary>
	/// Clones one expression with generic or contract type-token substitutions applied recursively.
	/// </summary>
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

}
