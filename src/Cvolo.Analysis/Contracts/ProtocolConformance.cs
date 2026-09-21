using Cvolo.Analysis.Symbols;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.AST.Declarations;

namespace Cvolo.Analysis.Contracts;

/// <summary>
/// Evaluates structural protocol conformance and protocol requires-clause constraints.
/// </summary>
/// <remarks>
/// The implementation preserves the validation pass's existing canonical-token matching,
/// deferred semantic type comparison, width-lock rules, inherited defaults, and ambiguity rules.
/// It does not perform AST traversal or instantiate protocol-parameterized functions.
/// </remarks>
internal sealed class ProtocolConformance(BindingContext context, InterfaceConformance interfaces)
{
	/// <summary>
	/// Returns whether a concrete type structurally satisfies every effective member of a protocol.
	/// </summary>
	public bool Conforms(TypeSymbol type, ProtocolTypeSymbol protocol)
	{
		var baseType = type is PointerTypeSymbol pointer ? pointer.ReferencedType : type;
		if (baseType is ProtocolTypeSymbol or InterfaceTypeSymbol)
			return false;

		// Generic Contracts spec §6.A: a parameterized protocol only matches a concrete type with
		// the same generic-width topology.
		if (protocol.GenericParameters.Count > 0
			&& GetConcreteGenericArity(baseType) != protocol.GenericParameters.Count)
		{
			return false;
		}

		foreach (var member in protocol.Members)
		{
			var matched = false;
			foreach (var (_, candidate) in context.GetExtensionMethodCandidates(baseType, context.CurrentUnit, member.Name))
			{
				if (MemberMatches(member, candidate, protocol, baseType))
				{
					matched = true;
					break;
				}
			}

			if (!matched && HasSatisfyingDefault(member, protocol))
				matched = true;

			if (!matched)
				return false;
		}

		return true;
	}

	/// <summary>
	/// Returns whether a concrete type satisfies a protocol <c>for ...</c> requires-clause.
	/// </summary>
	/// <remarks>
	/// Nominal interface constraints use the registered interface-conformance relation, while
	/// protocol constraints recursively use structural conformance. Unknown contracts fail closed.
	/// </remarks>
	public bool SatisfiesConstraint(TypeSymbol concrete, string constraintText)
	{
		var contract = ResolveContractBase(constraintText, concrete.Name);
		return contract switch
		{
			ProtocolTypeSymbol protocol => Conforms(concrete, protocol),
			InterfaceTypeSymbol interfaceType => interfaces.Conforms(concrete, interfaceType),
			_ => false,
		};
	}

	/// <summary>
	/// Finds a protocol member for which more than one distinct extension implementation matches the
	/// same concrete type. Returns <see langword="true"/> when dispatch would be ambiguous.
	/// </summary>
	public bool TryFindAmbiguousMember(TypeSymbol concrete, ProtocolTypeSymbol protocol, out string? memberName)
	{
		foreach (var member in protocol.Members)
		{
			var matches = context.GetExtensionMethodCandidates(concrete, context.CurrentUnit, member.Name)
				.Select(candidate => candidate.Function)
				.Where(candidate => MemberMatches(member, candidate, protocol, concrete))
				.Distinct()
				.Count();

			if (matches > 1)
			{
				memberName = member.Name;
				return true;
			}
		}

		memberName = null;
		return false;
	}

	/// <summary>
	/// Returns whether the protocol or the owning base protocol supplies a canonical default
	/// implementation for the requested member.
	/// </summary>
	private bool HasSatisfyingDefault(ProtocolMethodDeclarationSyntax member, ProtocolTypeSymbol protocol)
	{
		var protocolName = protocol.Name;
		if (protocol.GenericTypeArguments is not null)
		{
			var openBracket = protocolName.IndexOf('<');
			if (openBracket > 0)
				protocolName = protocolName[..openBracket];
		}

		var ownerName = protocolName;
		var ownerGenerics = protocol.GenericParameters;
		if (context.ProtocolEffectiveMembers.TryGetValue(protocolName, out var effective))
		{
			var owner = effective.FirstOrDefault(entry => ReferenceEquals(entry.Member, member));
			if (owner == default)
				owner = effective.FirstOrDefault(entry => entry.Member.Name == member.Name);
			if (owner != default)
			{
				ownerName = owner.OwnerProtocol;
				if (context.ProtocolTemplates.TryGetValue(owner.OwnerProtocol, out var ownerDeclaration))
					ownerGenerics = ownerDeclaration.GenericParameters;
			}
		}

		if (!context.ProtocolDefaults.TryGetValue(ownerName, out var defaults))
			return false;

		var memberToken = ProtocolCanonicalizer.BuildMemberToken(
			member,
			ownerGenerics,
			context,
			selfReplacement: null,
			protocol.GenericTypeArguments);

		foreach (var (defaultName, declaration) in defaults)
		{
			if (defaultName != member.Name)
				continue;

			var defaultToken = ProtocolCanonicalizer.BuildFunctionToken(
				declaration,
				ownerGenerics,
				context,
				protocol.GenericTypeArguments);
			if (defaultToken == memberToken)
				return true;
		}

		return false;
	}

	/// <summary>
	/// Returns the declared generic arity of a concrete generic struct or union instance.
	/// Flat types return zero.
	/// </summary>
	private int GetConcreteGenericArity(TypeSymbol baseType)
	{
		var name = baseType.Name;
		var openBracket = name.LastIndexOf('<');
		if (openBracket <= 0)
			return 0;

		var baseName = name[..openBracket];
		if (context.GenericStructTemplates.TryGetValue(baseName, out var structTemplate))
			return structTemplate.GenericParameters.Count;
		if (context.GenericUnionTemplates.TryGetValue(baseName, out var unionTemplate))
			return unionTemplate.GenericParameters.Count;
		return 0;
	}

	/// <summary>
	/// Returns whether one resolved concrete extension method satisfies a protocol member exactly
	/// under canonical-token and deferred semantic comparison rules.
	/// </summary>
	private bool MemberMatches(
		ProtocolMethodDeclarationSyntax member,
		FunctionSymbol candidate,
		ProtocolTypeSymbol protocol,
		TypeSymbol baseType)
	{
		if (candidate.Parameters.Count == 0 || candidate.Parameters[0].Name != "this")
			return false;
		if (candidate.Parameters.Count - 1 != member.Parameters.Count)
			return false;

		var concreteToken = BuildConcreteCanonicalToken(candidate, member.Name);
		if (protocol.CanonicalMembers.Contains(concreteToken))
			return true;

		if (MemberReferencesSelf(member)
			&& ProtocolCanonicalizer.BuildMemberToken(
				member,
				protocol.GenericParameters,
				context,
				baseType.Name,
				protocol.GenericTypeArguments) == concreteToken)
		{
			return true;
		}

		for (var i = 0; i < member.Parameters.Count; i++)
		{
			var protocolParameterType = ResolveMemberType(member.Parameters[i].Type, baseType, protocol);
			if (protocolParameterType is null || !protocolParameterType.Equals(candidate.Parameters[i + 1].Type))
				return false;
		}

		var protocolReturnType = ResolveMemberType(member.ReturnType, baseType, protocol);
		if (protocolReturnType is not null && !protocolReturnType.Equals(candidate.ReturnType))
			return false;

		return true;
	}

	/// <summary>
	/// Resolves a protocol member type for deferred semantic matching, substituting <c>Self</c> and
	/// instantiated protocol type parameters while preserving ref/refvar wrappers.
	/// </summary>
	private TypeSymbol? ResolveMemberType(string typeText, TypeSymbol baseType, ProtocolTypeSymbol protocol)
	{
		var typeToken = typeText.Trim();
		var prefix = "";
		if (typeToken.StartsWith("refvar ", StringComparison.Ordinal))
		{
			prefix = "refvar ";
			typeToken = typeToken[7..];
		}
		else if (typeToken.StartsWith("ref ", StringComparison.Ordinal))
		{
			prefix = "ref ";
			typeToken = typeToken[4..];
		}

		if (ProtocolCanonicalizer.StripWrappers(typeToken) == "Self")
			return WrapReference(baseType, prefix);

		if (protocol.GenericTypeArguments is not null)
		{
			for (var i = 0; i < protocol.GenericParameters.Count; i++)
			{
				if (typeToken != protocol.GenericParameters[i])
					continue;

				var argumentType = context.ResolveType(protocol.GenericTypeArguments[i]);
				return argumentType is null ? null : WrapReference(argumentType, prefix);
			}
		}

		return context.ResolveType(typeText);
	}

	/// <summary>
	/// Applies the parsed ref/refvar wrapper represented by <paramref name="prefix"/> to a semantic
	/// type, or returns the original type when no wrapper is present.
	/// </summary>
	private static TypeSymbol WrapReference(TypeSymbol type, string prefix)
	{
		return prefix switch
		{
			"refvar " => new PointerTypeSymbol(type, isMutable: true),
			"ref " => new PointerTypeSymbol(type, isMutable: false),
			_ => type,
		};
	}

	/// <summary>
	/// Returns whether a protocol member signature contains a <c>Self</c> anchor.
	/// </summary>
	private static bool MemberReferencesSelf(ProtocolMethodDeclarationSyntax member)
	{
		return ReferencesSelf(member.ReturnType) || member.Parameters.Any(parameter => ReferencesSelf(parameter.Type));
	}

	/// <summary>
	/// Returns whether a type token denotes <c>Self</c> after protocol wrapper syntax is removed.
	/// </summary>
	private static bool ReferencesSelf(string typeText)
	{
		return ProtocolCanonicalizer.StripWrappers(typeText) == "Self";
	}

	/// <summary>
	/// Builds the canonical resolved signature token for a concrete extension method, excluding its
	/// implicit <c>this</c> receiver.
	/// </summary>
	private static string BuildConcreteCanonicalToken(FunctionSymbol candidate, string memberName)
	{
		var parameterTokens = new List<string>();
		for (var i = 1; i < candidate.Parameters.Count; i++)
			parameterTokens.Add(candidate.Parameters[i].Type.Name);

		return $"{candidate.ReturnType.Name}:{memberName}({string.Join(",", parameterTokens)})";
	}

	/// <summary>
	/// Resolves a requires-clause contract after substituting <c>Self</c> and stripping generic
	/// arguments from the contract name, matching the previous validation behavior.
	/// </summary>
	private TypeSymbol? ResolveContractBase(string constraintText, string concreteName)
	{
		var substituted = constraintText.Replace("Self", concreteName);
		var openBracket = substituted.IndexOf('<');
		var baseName = (openBracket > 0 ? substituted[..openBracket] : substituted).Trim();
		return context.ResolveType(baseName);
	}
}
