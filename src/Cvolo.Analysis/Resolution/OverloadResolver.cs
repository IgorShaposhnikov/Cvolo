using Cvolo.Analysis.Symbols;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Collections;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.AST.Declarations;
using Cvolo.Core.AST.Expressions;

namespace Cvolo.Analysis.Resolution;

/// <summary>
/// Which of the two dotted-call modes a call expression is in.
/// </summary>
internal enum DottedCallForm
{
	/// <summary>
	/// Not a dotted call, or the receiver is neither a value nor a resolvable type.
	/// </summary>
	None,
	/// <summary>
	/// <c>value.Member(...)</c> — instance extension dispatch with a synthetic receiver.
	/// </summary>
	ValueReceiver,

	/// <summary>
	/// <c>Type.Member(...)</c> — associated dispatch, receiverless.
	/// </summary>
	TypeReceiver
}

/// <summary>
/// A dotted call whose receiver form contradicts the declared callable kind. Reported separately
/// from "no overload matches" so the diagnostic can name the correct call syntax.
/// </summary>
internal enum DottedCallMismatch
{
	/// <summary>
	/// The call form matches the declared callable kind.
	/// </summary>
	None,
	/// <summary>
	/// Only a receiverless associated function exists but the call went through a value.
	/// </summary>
	AssociatedCalledThroughValue,
	/// <summary>
	/// Only a receiver-backed instance method exists but the call went through the type.
	/// </summary>
	InstanceCalledThroughType
}

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
		=> Resolve(name, argumentTypes, scope, call, out _, out _);

	/// <summary>
	/// Resolves a call and additionally reports how a dotted call form disagreed with the declared
	/// callable kind, so the caller can explain "called through the value" versus "called through the
	/// type" instead of falling back to a generic "no overload matches" message.
	/// </summary>
	/// <param name="mismatch">
	/// Set when a receiver of one kind names only callables of the other kind: a value receiver that
	/// only matches associated functions, or a type receiver that only matches instance methods.
	/// </param>
	/// <param name="mismatchOwnerName">
	/// The owner type the mismatched call resolved to, so diagnostics can name the type rather than
	/// whatever receiver expression happened to be written at the call site.
	/// </param>
	/// <param name="allowTypeReceiverInstanceFallback">
	/// Lets a qualified name whose prefix is a type fall back to the plain overload tables when no
	/// associated function carries the member name. This is for the compiler's own protocol queries
	/// (the <c>GetEnumerator</c>/<c>MoveNext</c>/<c>Current</c> triple, which are always addressed by
	/// qualified name and are receiver-backed instance methods), never for a user call site: there a
	/// type receiver naming only instance methods is the "called through the type" error.
	/// </param>
	public FunctionSymbol? Resolve(
		string name,
		IReadOnlyList<TypeSymbol> argumentTypes,
		SymbolTable scope,
		CallExpressionSyntax? call,
		out DottedCallMismatch mismatch,
		out string? mismatchOwnerName,
		bool allowTypeReceiverInstanceFallback = false)
	{
		var candidates = new List<FunctionSymbol>();
		var baseName = name;
		var adjustedArgumentTypes = new List<TypeSymbol>(argumentTypes);
		mismatch = DottedCallMismatch.None;
		mismatchOwnerName = null;

		var form = DottedCallForm.None;
		TypeSymbol? ownerType = null;
		string? memberName = null;
		List<FunctionSymbol>? implicitThisCandidates = null;

		if (name.Contains('.') && TrySplitDottedReceiver(name, scope, out var receiver))
		{
			if (receiver.IsValue)
			{
				var receiverType = receiver.ValueType!;
				if (receiverType is PointerTypeSymbol receiverPointer)
					receiverType = receiverPointer.ReferencedType;

				// Aggregate values carry ordinary extension receivers. An open generic type parameter
				// can also carry a receiver when its declaration has a protocol/interface bound: during
				// template binding that bound is the complete callable surface promised for the value.
				if (receiverType is StructTypeSymbol or UnionTypeSymbol or EnumTypeSymbol or TypeParameterSymbol)
				{
					form = DottedCallForm.ValueReceiver;
					ownerType = receiverType;
					memberName = receiver.MemberName;
					baseName = $"{receiverType.Name}.{receiver.MemberName}";
					adjustedArgumentTypes.Insert(0, new PointerTypeSymbol(receiverType, isMutable: receiver.IsMutable));
				}
			}
			else
			{
				// A type receiver has no value to pass, so no receiver argument is inserted and the
				// constructor / implicit-`this` fallbacks below do not apply.
				form = DottedCallForm.TypeReceiver;
				memberName = receiver.MemberName;
				context.TryResolveTypeByLookupName(
					name[..(name.Length - receiver.MemberName.Length - 1)],
					context.CurrentUnit,
					out ownerType);
			}
		}

		if (form == DottedCallForm.None)
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

				// An unqualified call inside an extension body (for example IsOk() -> IsErr())
				// is an implicit-this instance call. Do not probe OverloadedFunctions with the raw
				// "Type.Member" spelling here: extension registrations may be namespace-qualified,
				// so that probe misses perfectly valid members and later reports CVL1078. Reuse the
				// same owner-aware lookup used by dotted dispatch instead.
				implicitThisCandidates = [];
				AddRangeUnique(implicitThisCandidates, context
					.GetExtensionMethodCandidates(enclosingType, context.CurrentUnit, name)
					.Select(candidate => candidate.Function)
					.ToList());

				// Generic owner extensions are intentionally parked as templates and therefore have no
				// OverloadedFunctions entries until a concrete owner is instantiated. Validation still binds
				// the open template body, so an unqualified sibling call such as Result<T,E>.IsErr() ->
				// IsOk() must be able to see non-generic sibling declarations directly from the enclosing
				// extension. Build temporary semantic candidates for that open-template-only case.
				AddRangeUnique(implicitThisCandidates, GetOpenGenericSiblingCandidates(enclosingType, name));

				if (implicitThisCandidates.Count > 0)
					adjustedArgumentTypes.Insert(0, new PointerTypeSymbol(enclosingType, isMutable: thisPointer.IsMutable));
			}
		}

		if (form == DottedCallForm.ValueReceiver && ownerType is not null && memberName is not null)
		{
			// Value receiver: ordinary aggregates use registered extension methods. An open generic
			// parameter uses the receiver-backed members guaranteed by its declared contract bound.
			if (ownerType is TypeParameterSymbol typeParameter)
			{
				candidates.AddRange(GetConstrainedGenericReceiverCandidates(typeParameter, memberName, receiverIsMutable: adjustedArgumentTypes[0] is PointerTypeSymbol { IsMutable: true }));
			}
			else
			{
				candidates.AddRange(context
					.GetExtensionMethodCandidates(ownerType, context.CurrentUnit, memberName)
					.Select(candidate => candidate.Function));

				if (candidates.Count == 0
					&& context.GetAssociatedFunctionCandidates(ownerType, context.CurrentUnit, memberName).Count > 0)
				{
					mismatch = DottedCallMismatch.AssociatedCalledThroughValue;
					mismatchOwnerName = ownerType.Name;
				}
			}
		}
		else if (form == DottedCallForm.TypeReceiver && ownerType is not null && memberName is not null)
		{
			// Type receiver: only receiverless associated functions are callable this way, and the
			// declared arguments alone participate in scoring.
			candidates.AddRange(context
				.GetAssociatedFunctionCandidates(ownerType, context.CurrentUnit, memberName)
				.Select(candidate => candidate.Function));

			if (candidates.Count == 0)
			{
				if (context.GetExtensionMethodCandidates(ownerType, context.CurrentUnit, memberName).Count > 0)
				{
					mismatch = DottedCallMismatch.InstanceCalledThroughType;
					mismatchOwnerName = ownerType.Name;
				}

				if (allowTypeReceiverInstanceFallback)
					GatherCandidates(baseName, candidates);
			}
		}
		else if (form == DottedCallForm.None)
		{
			if (implicitThisCandidates is { Count: > 0 })
				candidates.AddRange(implicitThisCandidates);
			else
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
	/// Resolves the longest prefix of a dotted call path that denotes either a value (instance
	/// dispatch) or a type (associated dispatch). Values win, matching the language rule that
	/// <c>value.Member()</c> is always instance dispatch and only a name that is not a value can be
	/// read as an owner type. The prefix search is longest-first so namespace-qualified owners
	/// (<c>System.Memory.Layout.FromType</c>) resolve to <c>System.Memory.Layout</c>.
	/// </summary>
	private bool TrySplitDottedReceiver(string dottedName, SymbolTable scope, out DottedReceiver receiver)
	{
		receiver = default;

		for (var dot = dottedName.LastIndexOf('.'); dot > 0;)
		{
			var prefix = dottedName[..dot];
			if ((scope.Lookup(prefix) ?? context.ResolveGlobalReference(prefix, out _)) is VariableSymbol value)
			{
				receiver = new DottedReceiver(true, value.Type, dottedName[(dot + 1)..], value.IsMutable);
				return true;
			}

			var nextDot = prefix.LastIndexOf('.');
			dot = nextDot;
		}

		if (!context.TrySplitTypeQualifiedName(dottedName, context.CurrentUnit, out _, out var memberName))
			return false;

		receiver = new DottedReceiver(false, null, memberName, false);
		return true;
	}

	/// <summary>
	/// Splits a dotted call path into the receiver it dispatches on and the member name, using the same
	/// value-wins rule as ordinary call resolution. Exposed so a call site can recognize a generic
	/// extension member with exactly the dispatch form it wrote: a value receiver selects the
	/// receiver-backed template, a type receiver the receiverless one.
	/// </summary>
	/// <param name="receiverType">The receiver's type for a value receiver; null for a type receiver.</param>
	public bool TrySplitDottedReceiver(
		string dottedName,
		SymbolTable scope,
		out bool receiverIsValue,
		out TypeSymbol? receiverType,
		out string memberName)
	{
		receiverIsValue = false;
		receiverType = null;
		memberName = "";

		if (!TrySplitDottedReceiver(dottedName, scope, out var receiver))
			return false;

		receiverIsValue = receiver.IsValue;
		receiverType = receiver.ValueType;
		memberName = receiver.MemberName;
		return true;
	}

	private readonly record struct DottedReceiver(bool IsValue, TypeSymbol? ValueType, string MemberName, bool IsMutable);

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
					 && (parameterTypes[i] is RawPointerTypeSymbol
						 || parameterTypes[i] is UnionTypeSymbol { IsOption: true }
						 || parameterTypes[i] is DelegateTypeSymbol { IsNative: true }))
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
					 && (parameter is RawPointerTypeSymbol
						 || parameter is UnionTypeSymbol { IsOption: true }
						 || parameter is DelegateTypeSymbol { IsNative: true }))
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
	/// Resolves the contract bounds promised by the currently active generic owner without requiring
	/// BindingContext to expose a second generic-scope API. GenericParameterScope records the owner
	/// name; generic struct templates are the authoritative source of `where T : Contract` clauses.
	/// </summary>
	private IReadOnlyList<TypeSymbol> GetActiveGenericParameterBounds(string parameterName)
	{
		var activeScope = context.FindGenericParameterScope(parameterName);
		if (activeScope is null)
			return [];

		var normalizedOwner = context.NormalizeGenericName(activeScope.OwnerName);
		var ownerLeaf = normalizedOwner;
		var ownerDot = ownerLeaf.LastIndexOf('.');
		if (ownerDot >= 0)
			ownerLeaf = ownerLeaf[(ownerDot + 1)..];

		var result = new List<TypeSymbol>();
		var seen = new HashSet<string>(StringComparer.Ordinal);

		foreach (var entry in context.GenericStructTemplates)
		{
			var template = entry.Value;
			var templateName = context.NormalizeGenericName(template.Name);
			var keyName = context.NormalizeGenericName(entry.Key);
			var keyLeaf = keyName;
			var keyDot = keyLeaf.LastIndexOf('.');
			if (keyDot >= 0)
				keyLeaf = keyLeaf[(keyDot + 1)..];

			if (!string.Equals(templateName, ownerLeaf, StringComparison.Ordinal)
				&& !string.Equals(keyName, normalizedOwner, StringComparison.Ordinal)
				&& !string.Equals(keyLeaf, ownerLeaf, StringComparison.Ordinal))
			{
				continue;
			}

			if (!template.GenericParameters.Contains(parameterName)
				|| !template.GenericParameterConstraints.TryGetValue(parameterName, out var constraints))
			{
				continue;
			}

			var previousUnit = context.CurrentUnit;
			var previousNamespace = context.CurrentNamespace;
			try
			{
				if (context.SymbolUnits.TryGetValue(entry.Key, out var ownerUnit))
				{
					context.CurrentUnit = ownerUnit;
					context.CurrentNamespace = ownerUnit.NamespaceDeclaration?.Name;
				}

				foreach (var constraint in constraints)
				{
					if (!context.TryResolveTypeByLookupName(
						context.NormalizeGenericName(constraint),
						context.CurrentUnit,
						out var bound))
					{
						continue;
					}

					if (seen.Add(bound.Name))
						result.Add(bound);
				}
			}
			finally
			{
				context.CurrentUnit = previousUnit;
				context.CurrentNamespace = previousNamespace;
			}
		}

		return result;
	}

	/// <summary>
	/// Returns non-generic instance methods declared in the current open generic extension. Generic
	/// owner extensions are stored in GenericExtensionTemplates and are not registered in the normal
	/// overload table until a concrete owner exists. Their bodies are nevertheless validated while
	/// T/E/etc. are active type parameters, so sibling calls need this declaration-backed candidate
	/// surface during that validation pass.
	/// </summary>
	private IReadOnlyList<FunctionSymbol> GetOpenGenericSiblingCandidates(TypeSymbol ownerType, string memberName)
	{
		if (context.CurrentUnit is null)
			return [];

		var unit = context.CurrentUnit;
		var members = unit.NamespaceDeclaration is { } ns ? ns.Members : unit.Members;
		var result = new List<FunctionSymbol>();

		foreach (var node in members)
		{
			if (node is not ExtensionDeclarationSyntax extension || extension.GenericParameters.Count == 0)
				continue;

			var resolvedOwner = context.ResolveType(context.NormalizeGenericName(extension.ExtendedTypeName));
			if (resolvedOwner is null || !string.Equals(resolvedOwner.Name, ownerType.Name, StringComparison.Ordinal))
				continue;

			foreach (var method in extension.Methods)
			{
				if (method.IsAssociated
					|| method.GenericParameters.Count != 0
					|| !string.Equals(method.Name, memberName, StringComparison.Ordinal))
				{
					continue;
				}

				var returnType = context.ResolveType(method.ReturnType);
				if (returnType is null)
					continue;

				var parameters = new List<ParameterSymbol>
				{
					new("this", new PointerTypeSymbol(ownerType, isMutable: false))
				};

				var valid = true;
				foreach (var parameter in method.Parameters)
				{
					var parameterType = context.ResolveType(parameter.Type);
					if (parameterType is null)
					{
						valid = false;
						break;
					}

					parameters.Add(new ParameterSymbol(parameter.Name, parameterType));
				}

				if (!valid)
					continue;

				var baseName = context.GetMangledName(
					$"{extension.ExtendedTypeName}.{method.Name}",
					unit.NamespaceDeclaration?.Name);

				result.Add(new FunctionSymbol(baseName, returnType, parameters)
				{
					Visibility = method.Visibility,
					DeclaringUnit = unit,
					CallableKind = CallableKind.InstanceExtension
				});
			}
		}

		return result;
	}

	/// <summary>
	/// Builds the callable surface promised by the active bounds of an open generic receiver.
	/// This is semantic template binding only: no generic is instantiated and no synthetic symbol
	/// is registered globally. The temporary FunctionSymbol exists solely so the normal overload
	/// scorer and argument validator can validate a call such as <c>allocator.Alloc(layout)</c>
	/// when <c>allocator : A</c> and <c>A : Allocator</c>.
	/// </summary>
	private IReadOnlyList<FunctionSymbol> GetConstrainedGenericReceiverCandidates(
		TypeParameterSymbol parameter,
		string memberName,
		bool receiverIsMutable)
	{
		var result = new List<FunctionSymbol>();

		foreach (var bound in GetActiveGenericParameterBounds(parameter.Name))
		{
			if (bound is ProtocolTypeSymbol protocol)
			{
				var members = context.ProtocolEffectiveMembers.TryGetValue(protocol.Name, out var effective)
					? effective.Select(entry => entry.Member)
					: protocol.Members;

				foreach (var member in members.Where(member => string.Equals(member.Name, memberName, StringComparison.Ordinal)))
				{
					if (TryBuildConstrainedMember(parameter, protocol.Name, member.Name, member.ReturnType, member.Parameters, receiverIsMutable, out var function))
						result.Add(function);
				}

				continue;
			}

			if (bound is InterfaceTypeSymbol iface)
			{
				var declaration = context.InterfaceTemplates.TryGetValue(iface.Name, out var exact)
					? exact
					: context.InterfaceTemplates.FirstOrDefault(entry =>
						string.Equals(entry.Key[(entry.Key.LastIndexOf('.') + 1)..], iface.Name[(iface.Name.LastIndexOf('.') + 1)..], StringComparison.Ordinal)).Value;

				if (declaration is null)
					continue;

				foreach (var member in declaration.Members.Where(member => string.Equals(member.Name, memberName, StringComparison.Ordinal)))
				{
					if (TryBuildConstrainedMember(parameter, iface.Name, member.Name, member.ReturnType, member.Parameters, receiverIsMutable, out var function))
						result.Add(function);
				}
			}
		}

		return result;
	}

	private bool TryBuildConstrainedMember(
		TypeParameterSymbol parameter,
		string boundName,
		string memberName,
		string returnTypeName,
		IReadOnlyList<Cvolo.Core.AST.Declarations.ParameterSyntax> writtenParameters,
		bool receiverIsMutable,
		out FunctionSymbol function)
	{
		function = null!;
		var previousUnit = context.CurrentUnit;
		var previousNamespace = context.CurrentNamespace;

		try
		{
			// Resolve the contract signature in the contract declaration's namespace, not in the
			// caller's namespace. This matters for stdlib contracts whose parameter types are
			// intentionally written unqualified (Allocator.Alloc(Layout)).
			if (context.SymbolUnits.TryGetValue(boundName, out var boundUnit))
			{
				context.CurrentUnit = boundUnit;
				context.CurrentNamespace = boundUnit.NamespaceDeclaration?.Name;
			}

			var returnType = context.ResolveType(returnTypeName);
			if (returnType is null)
				return false;

			var parameters = new List<ParameterSymbol>
			{
				new("this", new PointerTypeSymbol(parameter, isMutable: receiverIsMutable))
			};

			foreach (var written in writtenParameters)
			{
				var type = context.ResolveType(written.Type);
				if (type is null)
					return false;
				parameters.Add(new ParameterSymbol(written.Name, type));
			}

			function = new FunctionSymbol($"{boundName}.{memberName}", returnType, parameters)
			{
				CallableKind = CallableKind.InstanceExtension
			};
			return true;
		}
		finally
		{
			context.CurrentUnit = previousUnit;
			context.CurrentNamespace = previousNamespace;
		}
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
