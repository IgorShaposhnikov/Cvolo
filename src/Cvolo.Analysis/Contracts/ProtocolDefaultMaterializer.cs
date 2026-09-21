using Cvolo.Analysis.Symbols;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.AST.Declarations;

namespace Cvolo.Analysis.Contracts;

/// <summary>
/// Materializes inherited protocol default implementations onto structurally conforming concrete
/// structs and unions.
/// </summary>
/// <remarks>
/// Materialized methods are registered through the existing monomorphized-function pipeline. This
/// service deliberately preserves the current registration order and override behavior.
/// </remarks>
internal sealed class ProtocolDefaultMaterializer(BindingContext context)
{
	/// <summary>
	/// Registers inherited defaults for one concrete/protocol pair unless that pair has already been
	/// materialized. Concrete implementations continue to override matching defaults.
	/// </summary>
	public void Materialize(TypeSymbol concrete, ProtocolTypeSymbol protocol)
	{
		var baseType = concrete is PointerTypeSymbol pointer ? pointer.ReferencedType : concrete;
		if (baseType is not (StructTypeSymbol or UnionTypeSymbol))
			return;

		var protocolName = protocol.Name;
		if (protocol.GenericTypeArguments is not null)
		{
			var openBracket = protocolName.IndexOf('<');
			if (openBracket > 0)
				protocolName = protocolName[..openBracket];
		}

		var materializationKey = $"{baseType.Name}|{protocolName}";
		if (!context.MaterializedProtocolDefaults.Add(materializationKey))
			return;

		var inheritedDefaults = GetInheritedDefaults(protocolName, protocol);
		foreach (var (memberName, declaration) in inheritedDefaults)
			MaterializeDefault(baseType, memberName, declaration);
	}

	/// <summary>
	/// Enumerates satisfying defaults from the owning protocol of each effective member, falling back
	/// to the flat default registry when no inherited-member map exists.
	/// </summary>
	private IEnumerable<(string MemberName, FunctionDeclarationSyntax Declaration)> GetInheritedDefaults(
		string protocolName,
		ProtocolTypeSymbol protocol)
	{
		if (!context.ProtocolEffectiveMembers.TryGetValue(protocolName, out var effective))
		{
			if (!context.ProtocolDefaults.TryGetValue(protocolName, out var flatDefaults))
				return [];
			return flatDefaults.Select(entry => (entry.MemberName, entry.Decl));
		}

		var result = new List<(string MemberName, FunctionDeclarationSyntax Declaration)>();
		foreach (var (owner, member) in effective)
		{
			var ownerGenerics = context.ProtocolTemplates.TryGetValue(owner, out var ownerDeclaration)
				? ownerDeclaration.GenericParameters
				: protocol.GenericParameters;
			if (!context.ProtocolDefaults.TryGetValue(owner, out var ownerDefaults))
				continue;

			var memberToken = ProtocolCanonicalizer.BuildMemberToken(
				member,
				ownerGenerics,
				context,
				selfReplacement: null,
				protocol.GenericTypeArguments);

			foreach (var (defaultName, declaration) in ownerDefaults)
			{
				if (defaultName != member.Name)
					continue;

				var defaultToken = ProtocolCanonicalizer.BuildFunctionToken(
					declaration,
					ownerGenerics,
					context,
					protocol.GenericTypeArguments);
				if (defaultToken != memberToken)
					continue;

				result.Add((defaultName, declaration));
				break;
			}
		}

		return result;
	}

	/// <summary>
	/// Registers one inherited default as a concrete extension-style method when the concrete type
	/// does not already provide the same overloaded signature.
	/// </summary>
	private void MaterializeDefault(
		TypeSymbol baseType,
		string memberName,
		FunctionDeclarationSyntax declaration)
	{
		var baseMangledName = $"{baseType.Name}.{memberName}";
		var thisParameterType = new PointerTypeSymbol(baseType, isMutable: false);
		var parameters = new List<ParameterSymbol> { new("this", thisParameterType) };

		foreach (var parameter in declaration.Parameters)
		{
			var parameterType = context.ResolveType(parameter.Type);
			if (parameterType is null)
				return;
			parameters.Add(new ParameterSymbol(parameter.Name, parameterType));
		}

		var returnType = context.ResolveType(declaration.ReturnType);
		if (returnType is null)
			return;

		var overloadedName = context.GetOverloadedMangledName(
			baseMangledName,
			parameters.Select(parameter => parameter.Type).ToList());
		if (context.Globals.Lookup(overloadedName) is not null)
			return;

		var symbol = new FunctionSymbol(overloadedName, returnType, parameters)
		{
			Visibility = declaration.Visibility,
			DeclaringUnit = context.CurrentUnit,
		};
		context.Globals.Declare(symbol);

		if (!context.OverloadedFunctions.TryGetValue(baseMangledName, out var candidates))
		{
			candidates = [];
			context.OverloadedFunctions[baseMangledName] = candidates;
		}
		candidates.Add(symbol);

		context.SymbolUnits[overloadedName] = context.CurrentUnit!;
		context.MonomorphizedFunctionDecls.Add(new FunctionDeclarationSyntax(
			declaration.Span,
			declaration.ReturnType,
			overloadedName,
			[],
			declaration.Parameters,
			declaration.Body,
			declaration.Attributes,
			declaration.Modifier,
			visibility: declaration.Visibility));
	}
}
