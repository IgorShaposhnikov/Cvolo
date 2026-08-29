using Cvolo.Analysis.Symbols;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Collections;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using Cvolo.Core.AST.Expressions;
using Cvolo.Core.AST.Statements;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Analysis;

public sealed class BindingContext
{
	public DiagnosticBag Diagnostics { get; } = new();
	public SymbolTable Globals { get; } = new();
	public Dictionary<string, StructTypeSymbol> StructTypes { get; } = [];
	public Dictionary<VariableDeclarationSyntax, VariableSymbol> VariableSymbols { get; } = [];
	public Dictionary<CompilationUnitSyntax, CompilationContext> FileContexts { get; } = [];
	// Store generic struct templates (e.g. "Point<T>")
	public Dictionary<string, StructDeclarationSyntax> GenericStructTemplates { get; } = [];
	// Store generic function templates (e.g., "Swap<T>")
	public Dictionary<string, FunctionDeclarationSyntax> GenericFunctionTemplates { get; } = [];

	// Store monomorphized concrete instantiations (e.g., "Swap<int>")
	public Dictionary<string, FunctionSymbol> MonomorphizedFunctions { get; } = [];
	public List<FunctionDeclarationSyntax> MonomorphizedFunctionDecls { get; } = [];

	// Canonical Type Cache (Phase 3: Ensuring structural equality)
	private readonly Dictionary<string, TypeSymbol> _typeCache = [];
	public Dictionary<string, CompilationUnitSyntax> SymbolUnits { get; } = [];
	public Dictionary<string, List<FunctionSymbol>> OverloadedFunctions { get; } = [];
	public Dictionary<CallExpressionSyntax, FunctionSymbol> ResolvedCalls { get; } = [];
	// Destructors registered via '~T()' extension members, keyed by the extended type name
	public Dictionary<string, FunctionSymbol> Destructors { get; } = [];
	// Data-segment globals in declaration order
	public List<(GlobalVariableDeclarationSyntax Node, VariableSymbol Symbol)> GlobalVariables { get; } = [];
	// Constructors registered via 'T(...)' extension members, keyed by the struct type name
	public Dictionary<string, List<FunctionSymbol>> Constructors { get; } = [];
	// Store generic extension templates, keyed by the extended template struct name (mangled)
	public Dictionary<string, List<ExtensionDeclarationSyntax>> GenericExtensionTemplates { get; } = [];

	// Store monomorphized extension method/constructor declarations
	public List<SyntaxNode> MonomorphizedExtensionDecls { get; } = [];

	// Maps monomorphized extension function/constructor name to the concrete extended type name (e.g., Pair<int, double>)
	public Dictionary<string, string> MonomorphizedExtensionExtendedTypes { get; } = [];

	// Maps monomorphized extension node to its unique overloaded mangled name
	public Dictionary<SyntaxNode, string> MonomorphizedExtensionNames { get; } = [];
	public Dictionary<string, UnionTypeSymbol> UnionTypes { get; } = [];
	public Dictionary<string, UnionDeclarationSyntax> GenericUnionTemplates { get; } = [];

	// Nominal interface declarations (mangled name -> declaration) and types.
	public Dictionary<string, InterfaceDeclarationSyntax> InterfaceTemplates { get; } = [];
	public Dictionary<string, InterfaceTypeSymbol> InterfaceTypes { get; } = [];

	// Concrete type name (mangled) -> set of interface names (mangled) it conforms to.
	public Dictionary<string, HashSet<string>> Conformance { get; } = [];

	// Interface-parameterized function templates (implicit generics, e.g. void Draw(IWidget w)),
	// keyed by the mangled base name. Monomorphized per call site with a concrete conforming type.
	public Dictionary<string, FunctionDeclarationSyntax> InterfaceFunctionTemplates { get; } = [];

	// Structural protocol declarations (mangled name -> declaration) and types.
	public Dictionary<string, ProtocolDeclarationSyntax> ProtocolTemplates { get; } = [];
	public Dictionary<string, ProtocolTypeSymbol> ProtocolTypes { get; } = [];

	// Protocol-parameterized function templates (implicit generics, e.g. void Draw(IPrintable p)),
	// keyed by the mangled base name. Monomorphized per call site with a structurally conforming type.
	public Dictionary<string, FunctionDeclarationSyntax> ProtocolFunctionTemplates { get; } = [];


	public CompilationUnitSyntax? CurrentUnit { get; set; }
	public string? CurrentNamespace { get; set; }

	/// <summary>Current safety tier used during attribute validation in DeclarationPass.</summary>
	public SafetyTier CurrentSafetyTier { get; set; }

	/// <summary>
	/// Resolves a string type name to its canonical, immutable TypeSymbol object.
	/// </summary>
	/// <summary>
	/// Resolves a string type name to its canonical, immutable TypeSymbol object.
	/// </summary>
	public TypeSymbol? ResolveType(string name)
	{
		if (string.IsNullOrEmpty(name)) return null;

		// 1. Check Canonical Type Cache first (Flyweight Pattern)
		if (_typeCache.TryGetValue(name, out var cached))
			return cached;

		// 2. Resolve Raw Pointer Types (e.g., int*, char*) — must check before ref/refvar
		if (name.EndsWith('*') && !name.StartsWith("ref"))
		{
			var innerName = name[..^1];
			var innerType = ResolveType(innerName);
			if (innerType is not null)
			{
				var ptrType = new RawPointerTypeSymbol(innerType);
				_typeCache[name] = ptrType;
				return ptrType;
			}
		}

		// 3. Resolve Pointer/Reference Types (ref / refvar)
		if (name.StartsWith("refvar ") || name.StartsWith("ref "))
		{
			var isMutable = name.StartsWith("refvar ");
			var innerName = isMutable ? name.Substring(7) : name.Substring(4);
			var innerType = ResolveType(innerName);
			if (innerType is null) return null;

			var ptrType = new PointerTypeSymbol(innerType, isMutable);
			_typeCache[name] = ptrType;
			return ptrType;
		}

		// 4. Resolve Static Array Types (e.g., int[5])
		if (name.EndsWith(']'))
		{
			var openBracket = name.LastIndexOf('[');
			var sizePart = name.Substring(openBracket + 1, name.Length - openBracket - 2);
			var innerName = name.Substring(0, openBracket);
			var innerType = ResolveType(innerName);
			if (innerType is not null && int.TryParse(sizePart, out var size))
			{
				var arrType = new ArrayTypeSymbol(innerType, size);
				_typeCache[name] = arrType;
				return arrType;
			}
		}

		// 5. Resolve Dynamic Slice Types (e.g., int[])
		if (name.EndsWith("[]") && !name.StartsWith("ref"))
		{
			var inner = name[..^2];
			var innerType = ResolveType(inner);
			if (innerType is not null)
			{
				var sliceType = new SliceTypeSymbol(innerType);
				_typeCache[name] = sliceType;
				return sliceType;
			}
		}

		// 5b. Resolve Generic Instantiations (e.g., Point<int>, Option<int>)
		if (name.Contains('<'))
		{
			if (_typeCache.TryGetValue(name, out var cachedType)) return cachedType;

			var openBracket = name.IndexOf('<');
			var baseName = name.Substring(0, openBracket);
			var argsPart = name.Substring(openBracket + 1, name.Length - openBracket - 2);

			var baseType = ResolveType(baseName);
			if (baseType is StructTypeSymbol baseStruct && GenericStructTemplates.TryGetValue(baseStruct.Name, out var templateDecl))
			{
				if (!TryResolveTypeArguments(argsPart, out var typeArgs))
					return null;
				var instantiatedType = InstantiateGenericStruct(templateDecl, baseType.Name, typeArgs);
				StructTypes[name] = instantiatedType;
				_typeCache[name] = instantiatedType;
				return instantiatedType;
			}
			else if (baseType is UnionTypeSymbol baseUnion && GenericUnionTemplates.TryGetValue(baseUnion.Name, out var unionTemplateDecl))
			{
				if (!TryResolveTypeArguments(argsPart, out var unionTypeArgs))
					return null;
				var instantiatedType = InstantiateGenericUnion(unionTemplateDecl, baseUnion.Name, unionTypeArgs);
				UnionTypes[name] = instantiatedType;
				_typeCache[name] = instantiatedType;
				return instantiatedType;
			}
			else if (baseType is ProtocolTypeSymbol baseProtocol && ProtocolTemplates.TryGetValue(baseProtocol.Name, out var protocolDecl))
			{
				if (!TryResolveTypeArguments(argsPart, out var protocolTypeArgs))
					return null;
				if (protocolTypeArgs.Count != baseProtocol.GenericParameters.Count)
					return null;
				var instantiatedProtocol = InstantiateGenericProtocol(protocolDecl, baseProtocol.Name, protocolTypeArgs);
				_typeCache[name] = instantiatedProtocol;
				return instantiatedProtocol;
			}
		}

		// 5c. Check Primitives
		var primitive = TypeSymbol.FromName(name);
		if (primitive is not null)
		{
			_typeCache[name] = primitive;
			return primitive;
		}

		// 6. Resolve Namespaced/Imported Structures/Unions nominal match
		// 6. Resolve Namespaced/Imported Structures/Unions nominal match (Hierarchical Scoping)
		var candidates = new List<TypeSymbol>();

		// Priority 1: Local Namespace Match (Highest Priority)
		if (CurrentNamespace != null)
		{
			var localMangled = GetMangledName(name, CurrentNamespace);
			if (StructTypes.TryGetValue(localMangled, out var localStruct))
				candidates.Add(localStruct);
			if (UnionTypes.TryGetValue(localMangled, out var localUnion))
				candidates.Add(localUnion);
			if (InterfaceTypes.TryGetValue(localMangled, out var localInterface))
				candidates.Add(localInterface);
			if (ProtocolTypes.TryGetValue(localMangled, out var localProtocol))
				candidates.Add(localProtocol);
		}

		// Priority 2: Exact/Global Match (Only used if no local namespace match is found)
		if (candidates.Count == 0)
		{
			if (StructTypes.TryGetValue(name, out var exactMatch))
				candidates.Add(exactMatch);
			if (UnionTypes.TryGetValue(name, out var exactUnionMatch))
				candidates.Add(exactUnionMatch);
			if (InterfaceTypes.TryGetValue(name, out var exactInterfaceMatch))
				candidates.Add(exactInterfaceMatch);
			if (ProtocolTypes.TryGetValue(name, out var exactProtocolMatch))
				candidates.Add(exactProtocolMatch);
		}

		if (candidates.Count == 1)
		{
			_typeCache[name] = candidates[0];
			return candidates[0];
		}
		else if (candidates.Count > 1)
		{
			// Ambiguous local declaration match
			return null;
		}

		// Priority 3: Imported usings (Consulted only if no local or global match was found)
		if (CurrentUnit is not null)
		{
			var activeUsings = new List<string>(CurrentUnit.Usings.Select(u => u.NamespaceName));
			if (CurrentUnit.NamespaceDeclaration is not null)
				activeUsings.AddRange(CurrentUnit.NamespaceDeclaration.Usings.Select(u => u.NamespaceName));

			foreach (var ns in activeUsings)
			{
				var candidateMangled = GetMangledName(name, ns);
				if (StructTypes.TryGetValue(candidateMangled, out var match))
					candidates.Add(match);
				if (UnionTypes.TryGetValue(candidateMangled, out var unionMatch))
					candidates.Add(unionMatch);
				if (InterfaceTypes.TryGetValue(candidateMangled, out var interfaceMatch))
					candidates.Add(interfaceMatch);
				if (ProtocolTypes.TryGetValue(candidateMangled, out var protocolMatch))
					candidates.Add(protocolMatch);
			}
		}

		if (candidates.Count == 1)
		{
			_typeCache[name] = candidates[0];
			return candidates[0];
		}

		return null;
	}

	/// <summary>
	/// Resolves a comma-separated generic type-argument list (e.g. "ref Node").
	/// Returns false with a diagnostic if any argument cannot be resolved, so a
	/// null type is never smuggled into generic instantiation (which would NRE).
	/// </summary>
	private bool TryResolveTypeArguments(string argsPart, out List<TypeSymbol> typeArgs)
	{
		typeArgs = [];
		foreach (var rawArg in argsPart.Split(','))
		{
			var argType = ResolveType(rawArg.Trim());
			if (argType is null)
			{
				var currentFileContext = FileContexts[CurrentUnit!];
				Diagnostics.Report(currentFileContext, new TextSpan(0, 0), $"Unknown type argument '{rawArg.Trim()}'");
				return false;
			}
			typeArgs.Add(argType);
		}
		return true;
	}

	public string GetMangledName(string name, string? namespaceName)
	{
		if (string.IsNullOrEmpty(namespaceName)) return name;
		return $"{namespaceName}.{name}";
	}

	/// <summary>
	/// Updates the canonical type cache for a name. Used when a placeholder
	/// symbol registered for forward/self reference is replaced by its fully
	/// built symbol, so later resolution does not serve a stale placeholder.
	/// </summary>
	public void ReplaceTypeInCache(string name, TypeSymbol symbol)
	{
		_typeCache[name] = symbol;
	}

	private StructTypeSymbol InstantiateGenericStruct(StructDeclarationSyntax templateDecl, string templateMangledName, List<TypeSymbol> typeArgs)
	{
		var instName = $"{templateMangledName}<{string.Join(", ", typeArgs.Select(t => t.Name))}>";

		// Save current active contexts
		var prevUnit = CurrentUnit;
		var prevNamespace = CurrentNamespace;

		// Restore the template's original namespace and file context
		var originalUnit = SymbolUnits.TryGetValue(templateMangledName, out var u) ? u : null;
		CurrentUnit = originalUnit;
		CurrentNamespace = originalUnit?.NamespaceDeclaration?.Name;

		// Map placeholders to concrete type arguments (e.g., T -> int)
		var substitutionMap = new Dictionary<string, TypeSymbol>();
		for (var i = 0; i < templateDecl.GenericParameters.Count; i++)
		{
			substitutionMap[templateDecl.GenericParameters[i]] = typeArgs[i];
		}

		var fields = new List<StructFieldSymbol>();
		foreach (var field in templateDecl.Fields)
		{
			// 1. Substitute placeholders inside type name strings
			var substitutedTypeName = field.Type;
			foreach (var kv in substitutionMap)
			{
				substitutedTypeName = System.Text.RegularExpressions.Regex.Replace(substitutedTypeName, $@"\b{kv.Key}\b", kv.Value.Name);
			}

			// 2. Resolve the concrete, substituted type safely
			var fieldType = ResolveType(substitutedTypeName);
			if (fieldType is null)
			{
				var currentFileContext = FileContexts[CurrentUnit!];
				Diagnostics.Report(currentFileContext, field.Span, $"Could not resolve field type '{substitutedTypeName}' during generic instantiation of '{instName}'");
				continue;
			}

			fields.Add(new StructFieldSymbol(field.Name, fieldType));
		}

		var instantiatedType = new StructTypeSymbol(instName, fields);

		// Restore active contexts back to previous state
		CurrentUnit = prevUnit;
		CurrentNamespace = prevNamespace;

		// CRITICAL: Register in cache BEFORE calling the monomorphizer to break recursion cycles
		StructTypes[instName] = instantiatedType;
		_typeCache[instName] = instantiatedType;

		MonomorphizeExtensionsForType(instantiatedType, typeArgs, templateMangledName);

		return instantiatedType;
	}

	public string NormalizeGenericName(string name)
	{
		// Remove all whitespace to ensure "Point<int>" matches "Point< int >"
		return name.Replace(" ", "").Trim();
	}

	/// <summary>
	/// Generates a unique name for an overloaded function based on its parameter types.
	/// </summary>
	public string GetOverloadedMangledName(string baseName, IReadOnlyList<TypeSymbol> parameterTypes)
	{
		if (baseName == "main" || baseName == "Main")
			return "main";

		var signature = string.Join("_", parameterTypes.Select(t => NormalizeTypeNameForMangling(t.Name)));
		return string.IsNullOrEmpty(signature) ? $"{baseName}_void" : $"{baseName}_{signature}";
	}

	private static string NormalizeTypeNameForMangling(string name)
	{
		return name.Replace("<", "_")
				   .Replace(">", "_")
				   .Replace("[", "Arr")
				   .Replace("]", "")
				   .Replace(" ", "")
				   .Replace(",", "_")
				   .Replace(".", "_")
				   .Replace("*", "Ptr")
				   .Replace("refvar", "refvar")
				   .Replace("ref", "ref");
	}

	public void MonomorphizeExtensionsForType(TypeSymbol instantiatedType, List<TypeSymbol> typeArgs, string templateMangledName)
	{
		if (!GenericExtensionTemplates.TryGetValue(templateMangledName, out var templates))
			return;

		IReadOnlyList<string> genericParams;
		if (GenericStructTemplates.TryGetValue(templateMangledName, out var structTemplate))
		{
			genericParams = structTemplate.GenericParameters;
		}
		else if (GenericUnionTemplates.TryGetValue(templateMangledName, out var unionTemplate))
		{
			genericParams = unionTemplate.GenericParameters;
		}
		else
		{
			return;
		}

		var substitutionMap = new Dictionary<string, TypeSymbol>();
		for (var i = 0; i < genericParams.Count; i++)
		{
			substitutionMap[genericParams[i]] = typeArgs[i];
		}

		TypeSymbol ResolveSubstitutedType(string typeName)
		{
			var substitutedTypeName = typeName;
			foreach (var kv in substitutionMap)
			{
				substitutedTypeName = System.Text.RegularExpressions.Regex.Replace(substitutedTypeName, $@"\b{kv.Key}\b", kv.Value.Name);
			}

			if (substitutedTypeName.StartsWith("refvar ") || substitutedTypeName.StartsWith("ref "))
			{
				var isMutable = substitutedTypeName.StartsWith("refvar ");
				var innerName = isMutable ? substitutedTypeName.Substring(7) : substitutedTypeName.Substring(4);
				var innerType = ResolveSubstitutedType(innerName);
				return new PointerTypeSymbol(innerType, isMutable);
			}

			return ResolveType(substitutedTypeName)!;
		}

		BlockStatementSyntax SubstituteBlockGenerics(BlockStatementSyntax block)
		{
			var statements = new List<SyntaxNode>();
			foreach (var stmt in block.Statements)
				statements.Add(SubstituteStatementGenerics(stmt));
			return new BlockStatementSyntax(block.Span, statements);
		}

		SyntaxNode SubstituteStatementGenerics(SyntaxNode stmt)
		{
			switch (stmt)
			{
				case VariableDeclarationSyntax v:
					var newType = v.Type;
					if (newType != null)
					{
						foreach (var kv in substitutionMap)
						{
							newType = System.Text.RegularExpressions.Regex.Replace(newType, $@"\b{kv.Key}\b", kv.Value.Name);
						}
					}
					return new VariableDeclarationSyntax(v.Span, v.IsMutable, newType, v.Name, v.Initializer != null ? SubstituteExpressionGenerics(v.Initializer) : null);

				case BlockStatementSyntax b:
					return SubstituteBlockGenerics(b);

				case IfStatementSyntax i:
					return new IfStatementSyntax(i.Span, SubstituteExpressionGenerics(i.Condition), SubstituteStatementGenerics(i.ThenStatement), i.ElseClause != null ? new ElseClauseSyntax(i.ElseClause.Span, SubstituteBlockGenerics(i.ElseClause.Body)) : null);

				case WhileStatementSyntax w:
					return new WhileStatementSyntax(w.Span, SubstituteExpressionGenerics(w.Condition), SubstituteStatementGenerics(w.Body));

				case ForStatementSyntax f:
					return new ForStatementSyntax(f.Span, SubstituteStatementGenerics(f.Initializer) as VariableDeclarationSyntax ?? f.Initializer, SubstituteExpressionGenerics(f.Condition), SubstituteExpressionGenerics(f.Increment), SubstituteStatementGenerics(f.Body));

				case ReturnStatementSyntax r:
					return new ReturnStatementSyntax(r.Span, r.Expression != null ? SubstituteExpressionGenerics(r.Expression) : null);

				case ExpressionStatementSyntax e:
					return new ExpressionStatementSyntax(e.Span, SubstituteExpressionGenerics(e.Expression));

				default:
					return stmt;
			}
		}

		ExpressionSyntax SubstituteExpressionGenerics(ExpressionSyntax expr)
		{
			switch (expr)
			{
				case BinaryExpressionSyntax bin:
					return new BinaryExpressionSyntax(bin.Span, SubstituteExpressionGenerics(bin.Left), bin.Operator, SubstituteExpressionGenerics(bin.Right));

				case UnaryExpressionSyntax unary:
					var newOp = unary.Operator;
					if (newOp.StartsWith("(") && newOp.EndsWith(")"))
					{
						foreach (var kv in substitutionMap)
						{
							newOp = System.Text.RegularExpressions.Regex.Replace(newOp, $@"\b{kv.Key}\b", kv.Value.Name);
						}
					}
					return new UnaryExpressionSyntax(unary.Span, newOp, SubstituteExpressionGenerics(unary.Operand));

				case CallExpressionSyntax call:
					var newTypeArgs = call.TypeArguments.Select(t =>
					{
						var substituted = t;
						foreach (var kv in substitutionMap)
						{
							substituted = System.Text.RegularExpressions.Regex.Replace(substituted, $@"\b{kv.Key}\b", kv.Value.Name);
						}
						return substituted;
					}).ToList();
					var newArgs = call.Arguments.Select(a => SubstituteExpressionGenerics(a)).ToList();
					return new CallExpressionSyntax(call.Span, call.FunctionName, newTypeArgs, newArgs);

				case StructInitializationExpressionSyntax structInit:
					var newTypeName = structInit.StructTypeName;
					foreach (var kv in substitutionMap)
					{
						newTypeName = System.Text.RegularExpressions.Regex.Replace(newTypeName, $@"\b{kv.Key}\b", kv.Value.Name);
					}
					var newInits = structInit.Initializers.Select(i => new MemberInitializerSyntax(i.Span, i.MemberName, SubstituteExpressionGenerics(i.Expression))).ToList();
					return new StructInitializationExpressionSyntax(structInit.Span, newTypeName, newInits);

				case MemberAccessExpressionSyntax m:
					return new MemberAccessExpressionSyntax(m.Span, SubstituteExpressionGenerics(m.Expression), m.MemberName);

				case IndexExpressionSyntax idx:
					return new IndexExpressionSyntax(idx.Span, SubstituteExpressionGenerics(idx.Left), SubstituteExpressionGenerics(idx.Index));

				case BorrowExpressionSyntax b:
					return new BorrowExpressionSyntax(b.Span, SubstituteExpressionGenerics(b.Expression), b.IsMutable);

				case HeapAllocationExpressionSyntax h:
					return new HeapAllocationExpressionSyntax(h.Span, SubstituteExpressionGenerics(h.Expression));

				case ArrayInitializationExpressionSyntax arr:
					return new ArrayInitializationExpressionSyntax(arr.Span, arr.Elements.Select(e => SubstituteExpressionGenerics(e)).ToList());

				case TernaryExpressionSyntax t:
					return new TernaryExpressionSyntax(t.Span, SubstituteExpressionGenerics(t.Condition), SubstituteExpressionGenerics(t.ThenExpression), SubstituteExpressionGenerics(t.ElseExpression));

				default:
					return expr;
			}
		}

		foreach (var extDecl in templates)
		{
			SymbolUnits.TryGetValue(templateMangledName, out var originalUnit);

			// Monomorphize Methods & Destructors
			foreach (var method in extDecl.Methods.Concat(extDecl.Destructors.Select(static d => d.ToFunctionDeclaration())))
			{
				var substitutedReturnTypeName = method.ReturnType;
				foreach (var kv in substitutionMap)
				{
					substitutedReturnTypeName = System.Text.RegularExpressions.Regex.Replace(substitutedReturnTypeName, $@"\b{kv.Key}\b", kv.Value.Name);
				}

				var returnType = ResolveSubstitutedType(substitutedReturnTypeName);
				if (returnType is null) continue;

				var baseMangledName = $"{instantiatedType.Name}.{method.Name}";

				var thisParamType = new PointerTypeSymbol(instantiatedType, isMutable: false);
				var thisParam = new ParameterSymbol("this", thisParamType);

				var parameters = new List<ParameterSymbol> { thisParam };
				var instParams = new List<ParameterSyntax>();

				foreach (var param in method.Parameters)
				{
					var paramType = ResolveSubstitutedType(param.Type);
					parameters.Add(new ParameterSymbol(param.Name, paramType));
					instParams.Add(new ParameterSyntax(param.Span, paramType.Name, param.Name));
				}

				var overloadedName = GetOverloadedMangledName(baseMangledName, parameters.Select(p => p.Type).ToList());

				var newSymbol = new FunctionSymbol(overloadedName, returnType, parameters);
				Globals.Declare(newSymbol);

				if (!OverloadedFunctions.TryGetValue(baseMangledName, out var candidates))
				{
					candidates = [];
					OverloadedFunctions[baseMangledName] = candidates;
				}
				candidates.Add(newSymbol);

				if (originalUnit is not null)
				{
					SymbolUnits[overloadedName] = originalUnit;
				}

				if (method.Name.StartsWith('~'))
				{
					Destructors[instantiatedType.Name] = newSymbol;
				}

				var instBody = SubstituteBlockGenerics(method.Body);
				var instDecl = new FunctionDeclarationSyntax(method.Span, returnType.Name, overloadedName, [], instParams, instBody, method.Attributes, method.Modifier);

				MonomorphizedExtensionDecls.Add(instDecl);
				MonomorphizedExtensionExtendedTypes[overloadedName] = instantiatedType.Name;
				MonomorphizedExtensionNames[instDecl] = overloadedName;
			}

			// Monomorphize Constructors
			foreach (var ctorDecl in extDecl.Constructors)
			{
				var baseMangledName = instantiatedType.Name;

				var ctorThisParamType = new PointerTypeSymbol(instantiatedType, isMutable: true);
				var ctorParameters = new List<ParameterSymbol> { new ParameterSymbol("this", ctorThisParamType) };
				var instParams = new List<ParameterSyntax>();

				foreach (var param in ctorDecl.Parameters)
				{
					var paramType = ResolveSubstitutedType(param.Type);
					ctorParameters.Add(new ParameterSymbol(param.Name, paramType));
					instParams.Add(new ParameterSyntax(param.Span, paramType.Name, param.Name));
				}

				var ctorOverloadedName = GetOverloadedMangledName(baseMangledName, ctorParameters.Select(p => p.Type).ToList());
				var ctorSymbol = new FunctionSymbol(ctorOverloadedName, instantiatedType, ctorParameters);
				Globals.Declare(ctorSymbol);

				if (!OverloadedFunctions.TryGetValue(baseMangledName, out var ctorCandidates))
				{
					ctorCandidates = [];
					OverloadedFunctions[baseMangledName] = ctorCandidates;
				}
				ctorCandidates.Add(ctorSymbol);

				if (originalUnit is not null)
				{
					SymbolUnits[ctorOverloadedName] = originalUnit;
				}

				if (!Constructors.TryGetValue(instantiatedType.Name, out var registeredCtors))
				{
					registeredCtors = [];
					Constructors[instantiatedType.Name] = registeredCtors;
				}
				registeredCtors.Add(ctorSymbol);

				var instBody = SubstituteBlockGenerics(ctorDecl.Body);
				var instDecl = new ConstructorDeclarationSyntax(ctorDecl.Span, instantiatedType.Name, instParams, instBody, ctorDecl.Attributes);

				MonomorphizedExtensionDecls.Add(instDecl);
				MonomorphizedExtensionExtendedTypes[ctorOverloadedName] = instantiatedType.Name;
				MonomorphizedExtensionNames[instDecl] = ctorOverloadedName;
			}
		}
	}

	private UnionTypeSymbol InstantiateGenericUnion(UnionDeclarationSyntax templateDecl, string templateMangledName, List<TypeSymbol> typeArgs)
	{
		var instName = $"{templateMangledName}<{string.Join(", ", typeArgs.Select(t => t.Name))}>";

		var prevUnit = CurrentUnit;
		var prevNamespace = CurrentNamespace;

		var originalUnit = SymbolUnits.TryGetValue(templateMangledName, out var u) ? u : null;
		CurrentUnit = originalUnit;
		CurrentNamespace = originalUnit?.NamespaceDeclaration?.Name;

		var substitutionMap = new Dictionary<string, TypeSymbol>();
		for (int i = 0; i < templateDecl.GenericParameters.Count; i++)
		{
			substitutionMap[templateDecl.GenericParameters[i]] = typeArgs[i];
		}

		var fields = new List<UnionFieldSymbol>();
		foreach (var field in templateDecl.Fields)
		{
			var substitutedTypeName = field.Type;
			foreach (var kv in substitutionMap)
			{
				substitutedTypeName = System.Text.RegularExpressions.Regex.Replace(substitutedTypeName, $@"\b{kv.Key}\b", kv.Value.Name);
			}

			TypeSymbol fieldType;
			var isVoidVariant = substitutedTypeName == "void";
			if (isVoidVariant)
			{
				fieldType = TypeSymbol.Void;
			}
			else
			{
				fieldType = ResolveType(substitutedTypeName);
				if (fieldType is null)
				{
					var currentFileContext = FileContexts[CurrentUnit!];
					Diagnostics.Report(currentFileContext, field.Span, $"Could not resolve field type '{substitutedTypeName}' during generic instantiation of '{instName}'");
					continue;
				}
			}

			fields.Add(new UnionFieldSymbol(field.Name, fieldType, isVoidVariant));
		}

		var instantiatedType = new UnionTypeSymbol(instName, fields);

		CurrentUnit = prevUnit;
		CurrentNamespace = prevNamespace;

		// CRITICAL: Register in cache BEFORE calling the monomorphizer to break recursion cycles
		UnionTypes[instName] = instantiatedType;
		_typeCache[instName] = instantiatedType;

MonomorphizeExtensionsForType(instantiatedType, typeArgs, templateMangledName);

		return instantiatedType;
	}

	/// <summary>
	/// Instantiates a generic protocol (e.g. IContainer&lt;int&gt;): the template's
	/// canonical member tokens get each positional $T placeholder substituted with
	/// its concrete argument name, keeping structural pre-matching O(1). The
	/// declared type parameters are retained (width-lock arity), and the concrete
	/// argument list is recorded for deferred semantic re-validation.
	/// </summary>
	private ProtocolTypeSymbol InstantiateGenericProtocol(ProtocolDeclarationSyntax templateDecl, string templateMangledName, List<TypeSymbol> typeArgs)
	{
		var instName = $"{templateMangledName}<{string.Join(", ", typeArgs.Select(t => t.Name))}>";
		var argNames = typeArgs.Select(t => t.Name).ToList();

		var canonicalMembers = ProtocolCanonicalizer.BuildCanonicalMembers(templateDecl, this, argNames);
		return new ProtocolTypeSymbol(instName, templateDecl.Members, templateDecl.GenericParameters, templateDecl.Constraint, canonicalMembers, argNames);
	}
}
