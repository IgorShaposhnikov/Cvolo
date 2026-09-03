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
	public Dictionary<string, EnumTypeSymbol> EnumTypes { get; } = [];

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

	// Default implementations for contracts: extension blocks written directly on a
	// protocol definition (mangled protocol name -> (member name, method declaration)).
	// Conforming concrete types inherit these automatically unless they override them.
	public Dictionary<string, List<(string MemberName, FunctionDeclarationSyntax Decl)>> ProtocolDefaults { get; } = [];

	// Module-level dedup of default-implementation materialization
	// ("{ConcreteTypeName}|{ProtocolName}" once a conformer's inherited methods exist).
	public HashSet<string> MaterializedProtocolDefaults { get; } = [];

	// Transitive closure of a protocol's members after `:` base-clause aggregation
	// (mangled protocol name -> (owning protocol mangled name, member declaration)).
	// Built by DeclarationPass Pass 0b; used for conformance checks, dispatch
	// templates, generic protocol instantiation, and default-implementation lookup.
	public Dictionary<string, List<(string OwnerProtocol, ProtocolMethodDeclarationSyntax Member)>> ProtocolEffectiveMembers { get; } = [];


	public CompilationUnitSyntax? CurrentUnit { get; set; }
	public string? CurrentNamespace { get; set; }

	/// <summary>Current safety tier used during attribute validation in DeclarationPass.</summary>
	public SafetyTier CurrentSafetyTier { get; set; }

	/// <summary>
	/// When true (driven by the --legacy-visibility CLI flag), the Binder treats every
	/// declaration as 'public': all visibility diagnostics are suppressed. This restores the
	/// pre-modifier behavior of the v0.2.0-alpha compiler after any upgrade work.
	/// </summary>
	public bool LegacyVisibility { get; set; }

	/// <summary>Dedup set for per-instantiation visibility-leak diagnostics (CVL1038).</summary>
	public HashSet<string> ReportedVisibilityLeaks { get; } = [];

	/// <summary>
	/// Maps a namespace to the set of namespaces directly re-exported via 'expose using'.
	/// e.g. "System.Math" -> ["System.Math.Constants", "System.Math.Double"]
	/// </summary>
	public Dictionary<string, HashSet<string>> NamespaceReExports { get; } = [];

	/// <summary>
	/// All known/declared namespace names across compilation units.
	/// </summary>
	public HashSet<string> DeclaredNamespaces { get; } = [];

	/// <summary>
	/// Resolves a string type name to its canonical, immutable TypeSymbol object.
	/// </summary>
	/// <summary>
	/// Resolves a string type name to its canonical, immutable TypeSymbol object.
	/// </summary>
	public TypeSymbol? ResolveType(string name)
	{
		if (string.IsNullOrEmpty(name))
			return null;

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
			var innerName = isMutable ? name[7..] : name[4..];
			var innerType = ResolveType(innerName);
			if (innerType is null)
				return null;

			var ptrType = new PointerTypeSymbol(innerType, isMutable);
			_typeCache[name] = ptrType;
			return ptrType;
		}

		// 4. Resolve Static Array Types (e.g., int[5] or int[Color.Max + 1])
		if (name.EndsWith(']'))
		{
			var openBracket = name.LastIndexOf('[');
			var sizePart = name.Substring(openBracket + 1, name.Length - openBracket - 2);
			var innerName = name[..openBracket];
			var innerType = ResolveType(innerName);
			if (innerType is not null && TryEvaluateEnumSizeConstant(sizePart) is { } computedSize && computedSize <= int.MaxValue)
			{
				var arrType = new ArrayTypeSymbol(innerType, (int)computedSize);
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
			if (_typeCache.TryGetValue(name, out var cachedType))
				return cachedType;

			var openBracket = name.IndexOf('<');
			var baseName = name[..openBracket];
			var argsPart = name.Substring(openBracket + 1, name.Length - openBracket - 2);

			var baseType = ResolveType(baseName);
			if (baseType is StructTypeSymbol baseStruct && GenericStructTemplates.TryGetValue(baseStruct.Name, out var templateDecl))
			{
				if (!TryResolveTypeArguments(argsPart, out var typeArgs))
					return null;
				if (typeArgs.Count != templateDecl.GenericParameters.Count)
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
				if (unionTypeArgs.Count != unionTemplateDecl.GenericParameters.Count)
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
			if (EnumTypes.TryGetValue(localMangled, out var localEnum))
				candidates.Add(localEnum);
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
			if (EnumTypes.TryGetValue(name, out var exactEnumMatch))
				candidates.Add(exactEnumMatch);
		}

		if (candidates.Count == 1)
		{
			CacheCandidate(name, candidates[0]);
			return candidates[0];
		}
		else if (candidates.Count > 1)
		{
			// Ambiguous match between global/local definitions
			return null;
		}

		// Priority 3: Imported usings (Consulted ONLY IF no local or global match was found)
		if (candidates.Count == 0 && CurrentUnit is not null)
		{
			var activeUsings = GetActiveUsings(CurrentUnit);

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
				if (EnumTypes.TryGetValue(candidateMangled, out var enumMatch))
					candidates.Add(enumMatch);
			}
		}

		if (candidates.Count == 1)
		{
			CacheCandidate(name, candidates[0]);
			return candidates[0];
		}

		return null;
	}

	/// <summary>
	/// Evaluates a static array size expression used inside a type (e.g. "Color.Max + 1").
	/// Supports plain literals and left-to-right integer arithmetic over literals and
	/// enum metaprogramming constants (Min, Max, Count). Returns null when the expression
	/// cannot be resolved to a constant (caller treats the type as unresolvable).
	/// Per spec §5.C: using Min or Max on an enum with negative variant values is forbidden.
	/// </summary>
	private long? TryEvaluateEnumSizeConstant(string text)
	{
		var trimmed = text.Trim();
		var parts = new List<string>();
		var ops = new List<char>();
		var current = new System.Text.StringBuilder();
		foreach (var ch in trimmed)
		{
			if (ch is '+' or '-' or '*' or '/')
			{
				if (current.Length > 0)
				{
					parts.Add(current.ToString());
					current.Clear();
				}

				ops.Add(ch);
			}
			else
			{
				current.Append(ch);
			}
		}

		if (current.Length > 0)
			parts.Add(current.ToString());

		if (parts.Count == 0)
			return null;

		var result = ResolveEnumSizeTerm(parts[0]);
		for (var i = 0; i < ops.Count && result is not null; i++)
		{
			var rhs = ResolveEnumSizeTerm(parts[i + 1]);
			if (rhs is null)
				return null;
			result = ops[i] switch
			{
				'+' => result + rhs,
				'-' => result - rhs,
				'*' => result * rhs,
				'/' when rhs != 0 => result / rhs,
				_ => null,
			};
		}

		return result;
	}

	private long? ResolveEnumSizeTerm(string term)
	{
		var trimmed = term.Trim();
		if (long.TryParse(trimmed, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var literal))
			return literal;

		var dot = trimmed.LastIndexOf('.');
		if (dot <= 0 || dot == trimmed.Length - 1)
			return null;
		var enumName = trimmed[..dot];
		var member = trimmed[(dot + 1)..];
		if (ResolveType(enumName) is not EnumTypeSymbol targetEnum)
			return null;

		var hasNegative = targetEnum.Variants.Any(v => v.Value < 0);
		if (member == "Min")
			return hasNegative ? null : targetEnum.Variants.Min(v => v.Value);
		if (member == "Max")
			return hasNegative ? null : targetEnum.Variants.Max(v => v.Value);
		if (member == "Count")
			return targetEnum.IsFlags
				? targetEnum.Variants.Count(v => v.Value > 0 && IsPowerOfTwo(v.Value))
				: targetEnum.Variants.Count;
		return null;
	}

	private static bool IsPowerOfTwo(long value) => value > 0 && (value & (value - 1)) == 0;
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
		if (string.IsNullOrEmpty(namespaceName))
			return name;
		return $"{namespaceName}.{name}";
	}

	// CVL1038: a generic instantiation must not be visibly 'wider' than any of its type
	// arguments — exporting (or ABI-exposing) a container whose argument is more private
	// lets a less-visible type leak through the public surface. A private argument that is
	// confined to the referencing file is treated as internal: it cannot leak further.
	private void CheckInstantiationVisibilityLeak(string instantiationName, TypeSymbol host, IReadOnlyList<TypeSymbol> args)
	{
		if (LegacyVisibility || args.Count == 0)
			return;

		var hostRank = VisibilityRank(host.Visibility);
		var referencingUnit = CurrentUnit;
		for (var i = 0; i < args.Count; i++)
		{
			var arg = args[i];
			var argVisibility = arg.Visibility;
			if (SymbolUnits.TryGetValue(arg.Name, out var declaringUnit))
			{
				// A private argument that is confined to the referencing file counts as
				// internal: it cannot leak further through this instantiation.
				if (argVisibility == Visibility.Private && ReferenceEquals(declaringUnit, referencingUnit))
					argVisibility = Visibility.Internal;
			}
			else
			{
				// Primitives, slices and other built-in types have no declaring unit and
				// are inherently public at the ABI boundary.
				argVisibility = Visibility.Public;
			}

			if (hostRank > VisibilityRank(argVisibility))
			{
				if (ReportedVisibilityLeaks.Add(instantiationName))
				{
					Diagnostics.Report(
						FileContexts[referencingUnit!],
						default,
						$"The visibility of generic type instantiation '{host.Name}<{string.Join(",", args)}>' exceeds the visibility of its type argument '{arg.Name}'. Upgrade the argument visibility or restrict the parent declaration.",
						DiagnosticIds.GenericVisibilityLeak);
				}

				break;
			}
		}
	}

	private static int VisibilityRank(Visibility visibility) => visibility switch
	{
		Visibility.Public => 2,
		Visibility.Internal => 1,
		_ => 0
	};

	/// <summary>
	/// Updates the canonical type cache for a name. Used when a placeholder
	/// symbol registered for forward/self reference is replaced by its fully
	/// built symbol, so later resolution does not serve a stale placeholder.
	/// </summary>
	public void ReplaceTypeInCache(string name, TypeSymbol symbol)
	{
		_typeCache[name] = symbol;
	}

	/// <summary>
	/// Drops the flyweight cache entries for a bare type name and its namespaced (mangled)
	/// forms. Called when a declaration registers under a name that may collide with a
	/// previously cached — possibly imported — resolution (e.g. a user's global
	/// `union Result&lt;T&gt;` shadowing the stdlib `System.Result&lt;T,E&gt;` that was cached
	/// via a `using` lookup). Without invalidation, the stale cache serves the shadowed
	/// symbol and the local declaration can never be resolved.
	/// </summary>
	public void InvalidateTypeCache(string name)
	{
		var names = new List<string> { name, GetMangledName(name, null) };
		foreach (var key in names)
		{
			_typeCache.Remove(key);
		}
	}

	/// <summary>
	/// Stores a resolved candidate in the flyweight type cache. Only bare-name (unqualified)
	/// resolutions are cached under their own key; a namespaced symbol (whose mangled name
	/// differs from the lookup name) is context-dependent and must not poison the shared
	/// unqualified key — otherwise a `using`-imported `System.Result` cached under `Result`
	/// would shadow a user's global `Result` declared in another namespace context.
	/// </summary>
	private void CacheCandidate(string name, TypeSymbol candidate)
	{
		if (candidate.Name == name)
			_typeCache[name] = candidate;
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

			fields.Add(new StructFieldSymbol(field.Name, fieldType) { Visibility = field.Visibility });
		}

		// Compute effective visibility: min(Template.Visibility, TypeArgs.Visibility)
		var effectiveVisibility = templateDecl.Visibility;
		foreach (var arg in typeArgs)
		{
			var argVis = GetSymbolVisibility(arg);
			if (argVis < effectiveVisibility)
				effectiveVisibility = argVis;
		}

		var isStrictMut = templateDecl.Attributes.Any(a => NormalizeAttributeName(a.Name) == "StrictMutability");

		var instantiatedType = new StructTypeSymbol(instName, fields)
		{
			Visibility = effectiveVisibility,
			IsStrictMutability = isStrictMut
		};

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
				var innerName = isMutable ? substitutedTypeName[7..] : substitutedTypeName[4..];
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
					if (newOp.StartsWith('(') && newOp.EndsWith(')'))
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
				if (returnType is null)
					continue;

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

				var newSymbol = new FunctionSymbol(overloadedName, returnType, parameters)
				{
					SafetyTier = method.Modifier ?? SafetyTier.Safe,
					Visibility = method.Visibility,
					DeclaringUnit = originalUnit
				};

				// Copy UnsafeBody and MustUse attributes to the monomorphized instance
				if (method.Attributes.Any(a => a.Name.Contains("UnsafeBody")))
				{
					newSymbol.IsUnsafeBody = true;
					newSymbol.SafetyTier = SafetyTier.Unsafe; // UnsafeBody promotes tier to Unsafe
				}

				if (method.Attributes.Any(a => a.Name.Contains("MustUse")))
				{
					newSymbol.IsMustUse = true;
				}

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
				var instDecl = new FunctionDeclarationSyntax(
					method.Span,
					returnType.Name,
					overloadedName,
					[],
					instParams,
					instBody,
					method.Attributes,
					method.Modifier,
					method.Receiver,
					method.Visibility);

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
				var ctorSymbol = new FunctionSymbol(ctorOverloadedName, instantiatedType, ctorParameters)
				{
					SafetyTier = SafetyTier.Safe,
					Visibility = ctorDecl.Visibility,
					DeclaringUnit = originalUnit
				};

				if (ctorDecl.Attributes.Any(a => a.Name.Contains("UnsafeBody")))
				{
					ctorSymbol.IsUnsafeBody = true;
					ctorSymbol.SafetyTier = SafetyTier.Unsafe;
				}

				if (ctorDecl.Attributes.Any(a => a.Name.Contains("MustUse")))
				{
					ctorSymbol.IsMustUse = true;
				}

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
				var instDecl = new ConstructorDeclarationSyntax(ctorDecl.Span, instantiatedType.Name, instParams, instBody, ctorDecl.Attributes, ctorDecl.SyntacticVisibility);

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
		for (var i = 0; i < templateDecl.GenericParameters.Count; i++)
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

			fields.Add(new UnionFieldSymbol(field.Name, fieldType, isVoidVariant) { Visibility = field.Visibility });
		}

		var effectiveVisibility = templateDecl.Visibility;
		foreach (var arg in typeArgs)
		{
			var argVis = GetSymbolVisibility(arg);
			if (argVis < effectiveVisibility)
				effectiveVisibility = argVis;
		}

		var instantiatedType = new UnionTypeSymbol(instName, fields)
		{
			Visibility = effectiveVisibility
		};

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

		var (members, canonicalMembers) = GetEffectiveProtocolShape(templateMangledName, argNames, templateDecl);
		return new ProtocolTypeSymbol(instName, members, templateDecl.GenericParameters, templateDecl.Constraint, canonicalMembers, argNames);
	}

	/// <summary>
	/// The member list and canonical tokens of a protocol after `:` base-clause
	/// aggregation. Each member is canonicalized with its OWNER's generic
	/// parameters (the protocol it was declared on), so a parent protocol's
	/// placeholders keep their own width; concrete arguments from the current
	/// instantiation substitute throughout.
	/// </summary>
	private (IReadOnlyList<ProtocolMethodDeclarationSyntax> Members, IReadOnlySet<string> CanonicalMembers) GetEffectiveProtocolShape(
		string protoMangledName, List<string> concreteTypeArguments, ProtocolDeclarationSyntax fallbackDecl)
	{
		if (ProtocolEffectiveMembers.TryGetValue(protoMangledName, out var effective))
		{
			var canonical = new HashSet<string>();
			foreach (var (owner, member) in effective)
			{
				var ownerGenerics = ProtocolTemplates.TryGetValue(owner, out var ownerDecl)
					? ownerDecl.GenericParameters
					: fallbackDecl.GenericParameters;
				canonical.Add(ProtocolCanonicalizer.BuildMemberToken(member, ownerGenerics, this, selfReplacement: null, concreteTypeArguments));
			}

			return (effective.Select(e => e.Member).ToList(), canonical);
		}

		return (fallbackDecl.Members, ProtocolCanonicalizer.BuildCanonicalMembers(fallbackDecl, this, concreteTypeArguments));
	}

	/// <summary>
	/// Returns all transitively re-exported namespaces for a given root namespace (cycle-safe).
	/// </summary>
	public HashSet<string> GetTransitiveReExports(string rootNamespace)
	{
		var result = new HashSet<string>(StringComparer.Ordinal);
		var visited = new HashSet<string>(StringComparer.Ordinal);

		void Walk(string ns)
		{
			if (!visited.Add(ns))
				return;

			if (NamespaceReExports.TryGetValue(ns, out var reExports))
			{
				foreach (var target in reExports)
				{
					result.Add(target);
					Walk(target);
				}
			}
		}

		Walk(rootNamespace);
		return result;
	}

	/// <summary>
	/// Returns all active namespaces imported by the given file, expanding any 'expose using' re-exports.
	/// </summary>
	public List<string> GetActiveUsings(CompilationUnitSyntax? unit)
	{
		if (unit is null)
			return [];

		var directUsings = new HashSet<string>(StringComparer.Ordinal);

		// 1. Collect standard 'using' directives (exclude 'expose using' which only re-exports)
		foreach (var u in unit.Usings.Where(x => !x.IsExposed))
			directUsings.Add(u.NamespaceName);

		if (unit.NamespaceDeclaration is not null)
		{
			foreach (var u in unit.NamespaceDeclaration.Usings.Where(x => !x.IsExposed))
				directUsings.Add(u.NamespaceName);
		}

		// 2. Expand with all transitively re-exported namespaces
		var allUsings = new HashSet<string>(directUsings, StringComparer.Ordinal);
		foreach (var ns in directUsings)
		{
			foreach (var reExported in GetTransitiveReExports(ns))
			{
				allUsings.Add(reExported);
			}
		}

		return [.. allUsings];
	}

	private Visibility GetSymbolVisibility(TypeSymbol type)
	{
		if (type is StructTypeSymbol st) return st.Visibility;
		if (type is UnionTypeSymbol ut) return ut.Visibility;
		if (type is EnumTypeSymbol et) return et.Visibility;
		return Visibility.Public; // Primitives are always public
	}

	private static string NormalizeAttributeName(string attributeName)
	{
		var simple = attributeName.Contains('.') ? attributeName[(attributeName.LastIndexOf('.') + 1)..] : attributeName;
		if (simple.EndsWith("Attribute", StringComparison.Ordinal))
			simple = simple[..^"Attribute".Length];
		return simple;
	}
}
