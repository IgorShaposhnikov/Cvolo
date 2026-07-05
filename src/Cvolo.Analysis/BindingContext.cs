using Cvolo.Analysis.Symbols;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Collections;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Analysis;

public sealed class BindingContext
{
	public DiagnosticBag Diagnostics { get; } = new();
	public SymbolTable Globals { get; } = new();
	public Dictionary<string, StructTypeSymbol> StructTypes { get; } = [];
	public Dictionary<VariableDeclarationSyntax, VariableSymbol> VariableSymbols { get; } = [];

	// Canonical Type Cache (Phase 3: Ensuring structural equality)
	private readonly Dictionary<string, TypeSymbol> _typeCache = [];

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
}
