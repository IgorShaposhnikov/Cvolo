using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Analysis.Passes.Declaration;

/// <summary>
/// Links interface and protocol inheritance after all raw contract symbols have been registered.
/// </summary>
/// <remarks>
/// This service owns only declaration-time contract hierarchy semantics. It validates contract base
/// clauses, detects protocol inheritance cycles, computes transitive protocol member sets, and rebuilds
/// protocol symbols with canonical effective-member metadata. Interface/protocol conformance of concrete
/// types is handled later by validation services and is intentionally outside this declaration layer.
/// </remarks>
internal sealed class ContractHierarchyLinker(BindingContext context)
{
	/// <summary>
	/// Links all protocol and interface declarations while preserving compilation-unit namespace context.
	/// </summary>
	/// <param name="units">Compilation units whose raw contract symbols were already registered.</param>
	public void Link(IEnumerable<CompilationUnitSyntax> units)
	{
		foreach (var unit in units)
		{
			context.CurrentUnit = unit;
			context.CurrentNamespace = unit.NamespaceDeclaration?.Name;

			var members = context.CurrentNamespace != null ? unit.NamespaceDeclaration!.Members : unit.Members;
			foreach (var member in members)
			{
				if (member is ProtocolDeclarationSyntax protocolDecl)
					LinkProtocol(protocolDecl);
				else if (member is InterfaceDeclarationSyntax interfaceDecl)
					LinkInterface(interfaceDecl);
			}
		}
	}

	/// <summary>
	/// Validates one protocol base clause graph and computes the protocol's effective transitive
	/// member list with child declarations taking precedence over inherited members of the same name.
	/// </summary>
	private void LinkProtocol(ProtocolDeclarationSyntax protocolDecl)
	{
		var mangledName = context.GetMangledName(protocolDecl.Name, context.CurrentNamespace);
		var currentFileContext = context.FileContexts[context.CurrentUnit!];

		foreach (var baseName in protocolDecl.Bases)
		{
			if (context.ResolveType(baseName) is not ProtocolTypeSymbol)
			{
				context.Diagnostics.Report(currentFileContext, protocolDecl.Span,
					$"Unknown protocol '{baseName}' in base clause of protocol '{protocolDecl.Name}'.");
			}
		}

		var effective = new List<(string Owner, ProtocolMethodDeclarationSyntax Member)>();
		effective.AddRange(protocolDecl.Members.Select(member => (mangledName, member)));

		if (protocolDecl.Bases.Count > 0)
		{
			var visited = new HashSet<string>();
			var stack = new HashSet<string>();
			foreach (var baseName in protocolDecl.Bases)
				CollectProtocolBaseMembers(baseName, visited, stack, effective, protocolDecl.Span);
		}

		context.ProtocolEffectiveMembers[mangledName] = effective;

		var canonical = new HashSet<string>();
		foreach (var (owner, member) in effective)
		{
			var ownerGenerics = owner == mangledName
				? protocolDecl.GenericParameters
				: context.ProtocolTemplates.TryGetValue(owner, out var ownerDecl)
					? ownerDecl.GenericParameters
					: protocolDecl.GenericParameters;
			canonical.Add(ProtocolCanonicalizer.BuildMemberToken(member, ownerGenerics, context, selfReplacement: null));
		}

		context.ProtocolTypes[mangledName] = new ProtocolTypeSymbol(
			mangledName,
			effective.Select(entry => entry.Member).ToList(),
			protocolDecl.GenericParameters,
			protocolDecl.Constraint,
			canonical)
		{
			Visibility = protocolDecl.Visibility
		};
	}

	/// <summary>
	/// Recursively appends members inherited from a protocol base while detecting cycles and avoiding
	/// duplicate traversal. Existing child/member names remain authoritative and suppress inherited copies.
	/// </summary>
	private void CollectProtocolBaseMembers(
		string baseName,
		HashSet<string> visited,
		HashSet<string> stack,
		List<(string Owner, ProtocolMethodDeclarationSyntax Member)> effective,
		TextSpan span)
	{
		if (context.ResolveType(baseName) is not ProtocolTypeSymbol protoBase)
			return;

		if (!stack.Add(protoBase.Name))
		{
			context.Diagnostics.Report(context.FileContexts[context.CurrentUnit!], span,
				$"Circular protocol inheritance involving '{baseName}'.");
			return;
		}

		if (visited.Add(protoBase.Name))
		{
			if (context.ProtocolTemplates.TryGetValue(protoBase.Name, out var baseDecl))
			{
				foreach (var baseOfBase in baseDecl.Bases)
					CollectProtocolBaseMembers(baseOfBase, visited, stack, effective, span);

				foreach (var member in baseDecl.Members)
				{
					if (effective.Any(entry => entry.Member.Name == member.Name))
						continue;
					effective.Add((protoBase.Name, member));
				}
			}
		}

		stack.Remove(protoBase.Name);
	}

	/// <summary>
	/// Validates that every interface base names another interface or a protocol. Effective interface
	/// members remain lazily composed later during interface-conformance registration, matching the
	/// existing declaration pipeline.
	/// </summary>
	private void LinkInterface(InterfaceDeclarationSyntax interfaceDecl)
	{
		var currentFileContext = context.FileContexts[context.CurrentUnit!];
		foreach (var baseName in interfaceDecl.Bases)
		{
			var baseType = context.ResolveType(baseName);
			if (baseType is not (InterfaceTypeSymbol or ProtocolTypeSymbol))
			{
				context.Diagnostics.Report(currentFileContext, interfaceDecl.Span,
					$"Unknown contract '{baseName}' in base clause of interface '{interfaceDecl.Name}'.");
			}
		}
	}
}
