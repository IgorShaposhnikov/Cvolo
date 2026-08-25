using Cvolo.Analysis.Symbols;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Collections;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using Cvolo.Core.AST.Expressions;
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

	public CompilationUnitSyntax? CurrentUnit { get; set; }
	public string? CurrentNamespace { get; set; }

	/// <summary>
	/// Resolves a string type name to its canonical, immutable TypeSymbol object.
	/// </summary>
	public TypeSymbol? ResolveType(string name)
	{
		if (string.IsNullOrEmpty(name)) return null;

		// 1. Check Canonical Type Cache first (Flyweight Pattern)
		if (_typeCache.TryGetValue(name, out var cached))
			return cached;

		// 2. Resolve Pointer/Reference Types (ref / refvar)
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

		// 3. Resolve Static Array Types (e.g., int[5])
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

		// 4. Resolve Dynamic Slice Types (e.g., int[])
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

		// 5. Check Primitives
		var primitive = TypeSymbol.FromName(name);
		if (primitive is not null)
		{
			_typeCache[name] = primitive;
			return primitive;
		}

		// 6. Resolve Namespaced/Imported Structures nominal match
		var candidates = new List<StructTypeSymbol>();
		if (StructTypes.TryGetValue(name, out var exactMatch))
			candidates.Add(exactMatch);

		if (CurrentNamespace != null)
		{
			var localMangled = GetMangledName(name, CurrentNamespace);
			if (StructTypes.TryGetValue(localMangled, out var localStruct))
				candidates.Add(localStruct);
		}

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
			}
		}

		// Resolve Generic Instantiations (e.g., Point<int>)
		if (name.Contains('<'))
		{
			if (_typeCache.TryGetValue(name, out var cachedType)) return cachedType;

			var openBracket = name.IndexOf('<');
			var baseName = name.Substring(0, openBracket);
			var argsPart = name.Substring(openBracket + 1, name.Length - openBracket - 2);

			var baseType = ResolveType(baseName) as StructTypeSymbol;
			if (baseType is not null && GenericStructTemplates.TryGetValue(baseType.Name, out var templateDecl))
			{
				var typeArgs = argsPart.Split(',').Select(s => ResolveType(s.Trim())!).ToList();

				// Pass baseType.Name (which is the fully qualified template name) to the instantiator
				var instantiatedType = InstantiateGenericStruct(templateDecl, baseType.Name, typeArgs!);

				// Register the concrete layout in our global type table!
				StructTypes[name] = instantiatedType;
				_typeCache[name] = instantiatedType;
				return instantiatedType;
			}
		}

		if (candidates.Count == 1)
		{
			_typeCache[name] = candidates[0];
			return candidates[0];
		}

		return null;
	}

	public string GetMangledName(string name, string? namespaceName)
	{
		if (string.IsNullOrEmpty(namespaceName)) return name;
		return $"{namespaceName}.{name}";
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
		for (int i = 0; i < templateDecl.GenericParameters.Count; i++)
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

		// Restore active contexts back to previous state
		CurrentUnit = prevUnit;
		CurrentNamespace = prevNamespace;

		return new StructTypeSymbol(instName, fields);
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
}
