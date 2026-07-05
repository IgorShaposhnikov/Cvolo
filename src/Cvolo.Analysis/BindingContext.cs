using System;
using System.Collections.Generic;
using System.Text;
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
	// Maps a variable declaration in the code to its calculated Symbol
	public Dictionary<VariableDeclarationSyntax, VariableSymbol> VariableSymbols { get; } = [];

	// State for the current file being processed
	public CompilationUnitSyntax? CurrentUnit { get; set; }
	public string? CurrentNamespace { get; set; }

	/// <summary>
	/// Logic moved from Binder.ResolveType to be accessible by all passes.
	/// </summary>
	public TypeSymbol? ResolveType(string name)
	{
		if (name.StartsWith("refvar "))
		{
			var innerName = name.Substring(7);
			var innerType = ResolveType(innerName);
			if (innerType is null) return null;
			return new PointerTypeSymbol(innerType, isMutable: true);
		}

		if (name.StartsWith("ref "))
		{
			var innerName = name.Substring(4);
			var innerType = ResolveType(innerName);
			if (innerType is null) return null;
			return new PointerTypeSymbol(innerType, isMutable: false);
		}

		if (name.EndsWith(']'))
		{
			var openBracket = name.LastIndexOf('[');
			var sizePart = name.Substring(openBracket + 1, name.Length - openBracket - 2);
			var innerName = name.Substring(0, openBracket);
			var innerType = ResolveType(innerName);
			if (innerType is not null && int.TryParse(sizePart, out var size))
				return new ArrayTypeSymbol(innerType, size);
		}

		if (name.EndsWith("[]") && !name.StartsWith("ref"))
		{
			var inner = name[..^2];
			var innerType = ResolveType(inner);
			if (innerType is not null)
				return new StructTypeSymbol(name, []);
		}

		var primitive = TypeSymbol.FromName(name);
		if (primitive is not null) return primitive;

		// Resolve namespaced type lookups dynamically
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

		if (candidates.Count == 1) return candidates[0];

		return null;
	}

	public string GetMangledName(string name, string? namespaceName)
	{
		if (string.IsNullOrEmpty(namespaceName)) return name;
		return $"{namespaceName}.{name}";
	}
}
