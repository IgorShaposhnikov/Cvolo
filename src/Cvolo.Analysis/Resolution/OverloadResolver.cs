using Cvolo.Analysis.Symbols;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Collections;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.AST.Expressions;

namespace Cvolo.Analysis.Resolution;

/// <summary>
/// Discovers overload candidates and scores callable signatures without depending on validation traversal.
/// </summary>
/// <remarks>
/// The resolver preserves the overload-selection behavior previously embedded in <c>ValidationPass</c>:
/// namespace/using lookup, extension receiver adjustment, constructor receiver insertion, variadic scoring,
/// implicit reference/array/string conversions, and deferred delegate target typing all remain unchanged.
/// </remarks>
/// <remarks>
/// Creates an overload resolver over the shared semantic binding state.
/// </remarks>
/// <param name="context">Binding context that owns overload registries, namespaces, and extension candidates.</param>
internal sealed class OverloadResolver(BindingContext context)
{

	/// <summary>
	/// Semantic sentinel used while a lambda or function group awaits target typing from the selected
	/// delegate-typed parameter.
	/// </summary>
	public static TypeSymbol DeferredCallableArgument { get; } = new TypeSymbol("<lambda-argument>");

	/// <summary>
	/// Resolves the best ordinary function, constructor, or extension-method overload for a call shape.
	/// </summary>
	/// <param name="name">Source-level callable name.</param>
	/// <param name="argumentTypes">Semantic argument types already computed at the call site.</param>
	/// <param name="scope">Lexical scope used to discover dotted receivers and implicit <c>this</c>.</param>
	/// <param name="call">Optional call syntax used to reconstruct generic constructor names.</param>
	/// <returns>The highest-scoring compatible function, or <see langword="null"/> when none matches.</returns>
	public FunctionSymbol? Resolve(string name, IReadOnlyList<TypeSymbol> argumentTypes, SymbolTable scope, CallExpressionSyntax? call = null)
	{
		var candidates = new List<FunctionSymbol>();
		var baseName = name;
		var adjustedArgumentTypes = new List<TypeSymbol>(argumentTypes);

		var isDottedExtension = false;
		TypeSymbol? dottedReceiverType = null;
		string? dottedMemberName = null;

		if (name.Contains('.'))
		{
			var parts = name.Split('.');
			var receiverName = parts[0];
			var methodName = parts[1];

			var receiverSymbol = scope.Lookup(receiverName) as VariableSymbol
				?? context.ResolveGlobalReference(receiverName, out _);
			if (receiverSymbol is not null)
			{
				var receiverType = receiverSymbol.Type;
				if (receiverType is PointerTypeSymbol pointer)
					receiverType = pointer.ReferencedType;

				if (receiverType is StructTypeSymbol or UnionTypeSymbol or EnumTypeSymbol)
				{
					isDottedExtension = true;
					dottedReceiverType = receiverType;
					dottedMemberName = methodName;
					baseName = $"{receiverType.Name}.{methodName}";
					adjustedArgumentTypes.Insert(0, new PointerTypeSymbol(receiverType, isMutable: receiverSymbol.IsMutable));
				}
			}
		}

		if (!isDottedExtension)
		{
			var constructorName = name;
			if (call is not null && call.TypeArguments.Count > 0 && !name.Contains('<'))
			{
				var concreteTypeName = $"{name}<{string.Join(", ", call.TypeArguments)}>";
				var resolvedType = context.ResolveType(concreteTypeName);
				if (resolvedType is not null)
					constructorName = resolvedType.Name;
			}

			var shortConstructorName = constructorName;
			if (shortConstructorName.Contains('.'))
				shortConstructorName = shortConstructorName[(shortConstructorName.LastIndexOf('.') + 1)..];

			if (context.Constructors.ContainsKey(constructorName) || context.Constructors.ContainsKey(shortConstructorName))
			{
				baseName = context.Constructors.ContainsKey(constructorName) ? constructorName : shortConstructorName;
				var constructorType = context.ResolveType(baseName);
				if (constructorType is not null)
					adjustedArgumentTypes.Insert(0, new PointerTypeSymbol(constructorType, isMutable: true));
			}
			else if (scope.Lookup("this") is VariableSymbol enclosingThis
					 && enclosingThis.Type is PointerTypeSymbol thisPointer
					 && thisPointer.ReferencedType is StructTypeSymbol or UnionTypeSymbol or EnumTypeSymbol)
			{
				var enclosingType = thisPointer.ReferencedType;
				if (context.OverloadedFunctions.ContainsKey($"{enclosingType.Name}.{name}"))
				{
					baseName = $"{enclosingType.Name}.{name}";
					adjustedArgumentTypes.Insert(0, new PointerTypeSymbol(enclosingType, isMutable: thisPointer.IsMutable));
				}
			}
		}

		if (isDottedExtension && dottedReceiverType is not null && dottedMemberName is not null)
		{
			candidates.AddRange(context
				.GetExtensionMethodCandidates(dottedReceiverType, context.CurrentUnit, dottedMemberName)
				.Select(candidate => candidate.Function));
		}
		else
		{
			GatherCandidates(baseName, candidates);
		}

		FunctionSymbol? bestMatch = null;
		var bestScore = -1;
		foreach (var candidate in candidates)
		{
			var parameterTypes = candidate.Parameters.Select(parameter => parameter.Type).ToList();
			var score = CompareSignature(parameterTypes, adjustedArgumentTypes, candidate.IsVariadic);
			if (score > bestScore)
			{
				bestScore = score;
				bestMatch = candidate;
			}
		}

		return bestScore >= 0 ? bestMatch : null;
	}

	/// <summary>
	/// Appends all overloads visible for a name through exact, current-namespace, and active-using lookup.
	/// Duplicate physical symbols are suppressed while preserving discovery order.
	/// </summary>
	public void GatherCandidates(string name, List<FunctionSymbol> targetList)
	{
		if (context.OverloadedFunctions.TryGetValue(name, out var directMatches))
			AddRangeUnique(targetList, directMatches);

		var localMangled = context.GetMangledName(name, context.CurrentNamespace);
		if (context.OverloadedFunctions.TryGetValue(localMangled, out var localMatches))
			AddRangeUnique(targetList, localMatches);

		if (context.CurrentUnit is null)
			return;

		foreach (var importedNamespace in context.GetActiveUsings(context.CurrentUnit))
		{
			var candidateMangled = context.GetMangledName(name, importedNamespace);
			if (context.OverloadedFunctions.TryGetValue(candidateMangled, out var matches))
				AddRangeUnique(targetList, matches);
		}
	}

	/// <summary>
	/// Returns whether at least one overload candidate is visible for a callable name.
	/// </summary>
	public bool HasCandidates(string name)
	{
		var candidates = new List<FunctionSymbol>();
		GatherCandidates(name, candidates);
		return candidates.Count > 0;
	}

	/// <summary>
	/// Scores two same-length signatures using the legacy constructor-initializer compatibility rules.
	/// </summary>
	/// <remarks>
	/// The caller remains responsible for checking arity before invoking this helper.
	/// </remarks>
	public int CompareSignatureExactly(IReadOnlyList<TypeSymbol> parameterTypes, IReadOnlyList<TypeSymbol> argumentTypes)
	{
		var score = 0;
		for (var i = 0; i < parameterTypes.Count; i++)
		{
			if (parameterTypes[i].Equals(argumentTypes[i]))
				score += 4;
			else if (argumentTypes[i].Equals(TypeSymbol.Null)
					 && (parameterTypes[i] is RawPointerTypeSymbol || parameterTypes[i] is UnionTypeSymbol { IsOption: true }))
				score += 3;
			else if (TypeSymbol.IsIntegerType(parameterTypes[i]) && TypeSymbol.IsIntegerType(argumentTypes[i]))
				score += 1;
			else if (TypeSymbol.IsFloatingPointType(parameterTypes[i]) && TypeSymbol.IsIntegerType(argumentTypes[i]))
				score += 1;
		}

		return score;
	}

	/// <summary>
	/// Scores a candidate signature against call-site argument types and returns <c>-1</c> when the
	/// candidate is incompatible.
	/// </summary>
	public int CompareSignature(IReadOnlyList<TypeSymbol> parameterTypes, IReadOnlyList<TypeSymbol> argumentTypes, bool isVariadic)
	{
		if (!isVariadic && parameterTypes.Count != argumentTypes.Count)
			return -1;
		if (isVariadic && argumentTypes.Count < parameterTypes.Count)
			return -1;

		var score = 0;
		for (var i = 0; i < parameterTypes.Count; i++)
		{
			var parameter = parameterTypes[i];
			var argument = argumentTypes[i];

			if (argument == DeferredCallableArgument)
			{
				if (parameter is DelegateTypeSymbol)
				{
					score += 4;
					continue;
				}

				return -1;
			}

			if (parameter.Equals(argument))
			{
				score += 4;
			}
			else if (argument.Equals(TypeSymbol.Null)
					 && (parameter is RawPointerTypeSymbol || parameter is UnionTypeSymbol { IsOption: true }))
			{
				score += 3;
			}
			else if (parameter is PointerTypeSymbol parameterPointer
					 && argument is PointerTypeSymbol argumentPointer
					 && parameterPointer.ReferencedType is SliceTypeSymbol sliceType
					 && argumentPointer.ReferencedType is ArrayTypeSymbol arrayType
					 && sliceType.ElementType.Equals(arrayType.ElementType))
			{
				if (parameterPointer.IsMutable && !argumentPointer.IsMutable)
					return -1;
				score += parameterPointer.IsMutable == argumentPointer.IsMutable ? 3 : 2;
			}
			else if (parameter is PointerTypeSymbol pointerParameter
					 && argument is PointerTypeSymbol pointerArgument
					 && pointerParameter.ReferencedType.Equals(pointerArgument.ReferencedType))
			{
				if (pointerParameter.IsMutable && !pointerArgument.IsMutable)
					return -1;
				score += pointerParameter.IsMutable == pointerArgument.IsMutable ? 3 : 2;
			}
			else if (parameter is SliceTypeSymbol slice && argument is ArrayTypeSymbol array
					 && slice.ElementType.Equals(array.ElementType))
			{
				score += 3;
			}
			else if (parameter is PointerTypeSymbol pointer && pointer.ReferencedType.Equals(argument))
			{
				score += 2;
			}
			else if (argument is PointerTypeSymbol dereferencedArgumentPointer && parameter.Equals(dereferencedArgumentPointer.ReferencedType))
			{
				score += 2;
			}
			else if (parameter.Equals(TypeSymbol.Double) && argument.Equals(TypeSymbol.Int))
			{
				score += 1;
			}
			else if (TypeSymbol.IsNumericIntegerType(parameter) && TypeSymbol.IsNumericIntegerType(argument) && !parameter.Equals(argument))
			{
				score += 1;
			}
			else if (parameter.Name == "string"
					 && ((argument is ArrayTypeSymbol charArray && charArray.ElementType.Name == "char")
						 || (argument is SliceTypeSymbol charSlice && charSlice.ElementType.Name == "char")))
			{
				score += 1;
			}
			else
			{
				return -1;
			}
		}

		if (isVariadic)
			score += 1;
		return score;
	}

	/// <summary>
	/// Adds candidates by identity without introducing duplicates into the target list.
	/// </summary>
	private static void AddRangeUnique(List<FunctionSymbol> targetList, IReadOnlyList<FunctionSymbol> candidates)
	{
		foreach (var candidate in candidates)
		{
			if (!targetList.Contains(candidate))
				targetList.Add(candidate);
		}
	}
}
