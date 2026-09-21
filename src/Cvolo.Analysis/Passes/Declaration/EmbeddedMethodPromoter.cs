using Cvolo.Analysis.Symbols;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;

namespace Cvolo.Analysis.Passes.Declaration;

/// <summary>
/// Promotes extension methods from embedded struct types onto the outer struct during
/// declaration binding, preserving the existing flat-prefix layout and method-resolution rules.
/// </summary>
internal sealed class EmbeddedMethodPromoter(BindingContext context)
{
	/// <summary>
	/// Registers copies of embedded-type extension methods for every struct that embeds them.
	/// Outer declarations win on signature collisions, and nominal interface conformance remains
	/// non-transitive because this step only populates ordinary function/extension lookup tables.
	/// </summary>
	public void Promote(IEnumerable<CompilationUnitSyntax> units)
	{
		var extensionsByType = new Dictionary<string, List<(CompilationUnitSyntax Unit, ExtensionDeclarationSyntax Decl)>>();
		foreach (var unit in units)
		{
			var members = unit.NamespaceDeclaration is not null ? unit.NamespaceDeclaration.Members : unit.Members;
			foreach (var member in members)
			{
				if (member is not ExtensionDeclarationSyntax extDecl)
					continue;
				if (context.ResolveType(extDecl.ExtendedTypeName) is not StructTypeSymbol targetType)
					continue;

				if (!extensionsByType.TryGetValue(targetType.Name, out var list))
				{
					list = [];
					extensionsByType[targetType.Name] = list;
				}

				list.Add((unit, extDecl));
			}
		}

		var promotedAny = new HashSet<string>();
		foreach (var unit in units)
		{
			var members = unit.NamespaceDeclaration is not null ? unit.NamespaceDeclaration.Members : unit.Members;
			foreach (var member in members)
			{
				if (member is not StructDeclarationSyntax outerDecl || outerDecl.EmbeddedType is null)
					continue;

				var outerName = context.GetMangledName(outerDecl.Name, unit.NamespaceDeclaration?.Name);
				if (context.StructTypes.TryGetValue(outerName, out var outerSym) && outerSym is StructTypeSymbol outerStruct)
					PromoteForStruct(outerStruct, unit, extensionsByType, promotedAny);
			}
		}
	}

	/// <summary>
	/// Walks one outer struct's embedded-type chain and registers each inherited extension
	/// signature against the outer receiver while retaining the source declaration for body reuse.
	/// </summary>
	private void PromoteForStruct(
		StructTypeSymbol outerStruct,
		CompilationUnitSyntax outerUnit,
		Dictionary<string, List<(CompilationUnitSyntax Unit, ExtensionDeclarationSyntax Decl)>> extensionsByType,
		HashSet<string> promotedAny)
	{
		var chain = new List<StructTypeSymbol>();
		var cursor = outerStruct.EmbeddedType;
		while (cursor is not null)
		{
			chain.Add(cursor);
			cursor = cursor.EmbeddedType;
		}

		if (chain.Count == 0)
			return;

		if (!promotedAny.Add(outerStruct.Name))
			return;

		var previousUnit = context.CurrentUnit;
		var previousNamespace = context.CurrentNamespace;
		context.CurrentUnit = outerUnit;
		context.CurrentNamespace = outerUnit.NamespaceDeclaration?.Name;

		var outerBaseNamespace = outerUnit.NamespaceDeclaration?.Name;

		foreach (var baseStruct in chain)
		{
			if (!extensionsByType.TryGetValue(baseStruct.Name, out var extList))
				continue;

			foreach (var (sourceUnit, extDecl) in extList)
			{
				foreach (var method in extDecl.Methods.Concat(extDecl.Destructors.Select(static d => d.ToFunctionDeclaration())))
				{
					// Resolve the embedded method's explicit parameter types against
					// its declaring unit's context (namespace-sensitive types).
					var previousUnit2 = context.CurrentUnit;
					var previousNamespace2 = context.CurrentNamespace;
					context.CurrentUnit = sourceUnit;
					context.CurrentNamespace = sourceUnit.NamespaceDeclaration?.Name;

					var parameters = new List<ParameterSymbol>
					{
						new("this", new PointerTypeSymbol(outerStruct, isMutable: false))
					};
					var paramOk = true;
					foreach (var param in method.Parameters)
					{
						var paramType = context.ResolveType(param.Type);
						if (paramType is null)
						{
							paramOk = false;
							break;
						}

						parameters.Add(new ParameterSymbol(param.Name, paramType));
					}

					var returnType = context.ResolveType(method.ReturnType);
					context.CurrentUnit = previousUnit2;
					context.CurrentNamespace = previousNamespace2;
					if (!paramOk || returnType is null)
						continue;

					// Register under the OUTER struct's method key so `w.Method(...)`
					// resolves through the existing dotted-extension machinery.
					var baseKey = context.GetMangledName($"{outerStruct.Name}.{method.Name}", outerBaseNamespace);
					var overloadedName = context.GetOverloadedMangledName(baseKey, parameters.Select(p => p.Type).ToList());

					if (context.Globals.Lookup(overloadedName) is not null)
						continue; // outer already declares this signature — own method wins

					var newSymbol = new FunctionSymbol(overloadedName, returnType, parameters)
					{
						Visibility = method.Visibility,
						DeclaringUnit = sourceUnit
					};
					context.Globals.Declare(newSymbol);

					if (!context.OverloadedFunctions.TryGetValue(baseKey, out var candidates))
					{
						candidates = [];
						context.OverloadedFunctions[baseKey] = candidates;
					}

					candidates.Add(newSymbol);

					context.SymbolUnits[overloadedName] = outerUnit;

					var copiedDecl = new FunctionDeclarationSyntax(method.Span, method.ReturnType, overloadedName, [], method.Parameters, method.Body, method.Attributes, method.Modifier, visibility: method.Visibility);
					context.MonomorphizedExtensionDecls.Add(copiedDecl);
					context.MonomorphizedExtensionNames[copiedDecl] = overloadedName;
					context.MonomorphizedExtensionExtendedTypes[overloadedName] = outerStruct.Name;
				}
			}
		}

		context.CurrentUnit = previousUnit;
		context.CurrentNamespace = previousNamespace;
	}
}
