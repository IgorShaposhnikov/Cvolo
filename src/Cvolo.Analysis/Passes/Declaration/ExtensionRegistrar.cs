using Cvolo.Analysis.Symbols;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Analysis.Passes.Declaration;

/// <summary>
/// Registers extension declarations and their declaration-time contract semantics during Pass 1.
/// </summary>
/// <remarks>
/// This service owns extension methods, constructors, destructors, protocol defaults, generic-extension
/// templates, and explicit nominal interface conformance. Raw type registration and free-function/FFI
/// registration remain separate declaration responsibilities.
/// </remarks>
internal sealed class ExtensionRegistrar(
	BindingContext context,
	FunctionDeclarationRegistrar functions,
	DestructorValidator destructors,
	GenericDefaultCopyValidator genericDefaultCopies)
{
	private readonly AttributeValidator _attributes = new(context);

	/// <summary>
	/// Maps declaration visibility to its ordering rank for extension-member narrowing checks.
	/// </summary>
	private static int VisibilityRank(Visibility visibility) => visibility switch
	{
		Visibility.Public => 2,
		Visibility.Internal => 1,
		_ => 0
	};

	/// <summary>
	/// Reports an extension-declaration diagnostic with a stable diagnostic identifier in the current unit.
	/// </summary>
	private void ReportDeclarationDiagnostic(SyntaxNode node, string message, string diagnosticId)
	{
		var currentFileContext = context.FileContexts[context.CurrentUnit!];
		context.Diagnostics.Report(currentFileContext, node.Span, message, diagnosticId);
	}


	/// <summary>
	/// Reports an extension-declaration diagnostic without a stable diagnostic identifier in the current unit.
	/// </summary>
	private void ReportDeclarationDiagnostic(SyntaxNode node, string message)
	{
		var currentFileContext = context.FileContexts[context.CurrentUnit!];
		context.Diagnostics.Report(currentFileContext, node.Span, message);
	}

	/// <summary>
	/// Registers one extension declaration, including methods, constructors, destructors,
	/// protocol defaults, generic-extension templates, and explicit nominal conformance.
	/// </summary>
	public void Declare(ExtensionDeclarationSyntax extDecl)
	{
		// Visibility: the extension block itself carries a modifier affecting all members
		// (default 'internal'). A member may only NARROW the block's visibility; any wider
		// member modifier is a CVL1031 error and the member takes the block level instead.
		var blockVisibility = extDecl.Visibility;
		var extendedType = context.ResolveType(extDecl.ExtendedTypeName);
		if (extendedType is null)
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, extDecl.NameSpan, $"Unknown type '{extDecl.ExtendedTypeName}' inside extension block.");
			return;
		}

		// DEFER: Check if this is a generic struct OR generic union template
		if (context.GenericStructTemplates.ContainsKey(extendedType.Name) || context.GenericUnionTemplates.ContainsKey(extendedType.Name))
		{
			if (!context.GenericExtensionTemplates.TryGetValue(extendedType.Name, out var templates))
			{
				templates = [];
				context.GenericExtensionTemplates[extendedType.Name] = templates;
			}

			genericDefaultCopies.Validate(extDecl);

			templates.Add(extDecl);
			return;
		}

		// PROTOCOL DEFAULTS: an extension block written directly ON a protocol
		// definition provides default implementations. Conforming concrete types
		// inherit them unless they define a matching method of their own. The
		// naked "{Protocol}.{Method}" symbols registered below are never emitted
		// (CodeGenerator skips protocol-typed extension blocks); ValidationPass
		// materializes a substituted copy onto each conforming concrete type.
		if (extendedType is ProtocolTypeSymbol protoExtType)
		{
			if (!context.ProtocolDefaults.TryGetValue(protoExtType.Name, out var defaults))
			{
				defaults = [];
				context.ProtocolDefaults[protoExtType.Name] = defaults;
			}

			foreach (var method in extDecl.Methods)
				defaults.Add((method.Name, method));
		}

		// RETROACTIVE CONFORMANCE: "extension T : IName" records that the
		// extended concrete type conforms to the named interface and validates
		// that this extension provides every required method with a matching
		// signature. (Generic conformance `extension Pair<T> : IFoo` is deferred.)
		if (extDecl.ConformsTo is not null)
			RegisterConformance(extDecl, extendedType);

		// Destructors register as ordinary extension methods named "~T" (void, this-only)
		foreach (var method in extDecl.Methods.Concat(extDecl.Destructors.Select(static d => d.ToFunctionDeclaration())))
		{
			var isDestructor = method.Name.StartsWith('~');
			if (isDestructor && !destructors.ValidateDeclaration(extDecl.ExtendedTypeName, method))
				continue;

			// Mangled name represents the scoped path, e.g., "MyNamespace.Point.Move"
			var baseMangledName = context.GetMangledName($"{extDecl.ExtendedTypeName}.{method.Name}", context.CurrentNamespace);

			// 1. Inject the implicit first parameter: "this"
			// It starts as a read-only pointer. The ValidationPass will upgrade it to mutable if needed!
			var thisParamType = new PointerTypeSymbol(extendedType, isMutable: false);
			var thisParam = new ParameterSymbol("this", thisParamType);

			var parameters = new List<ParameterSymbol> { thisParam };
			foreach (var param in method.Parameters)
			{
				var paramSymbol = functions.CreateParameter(param);
				if (paramSymbol is null)
				{
					ReportDeclarationDiagnostic(param, $"Unknown parameter type '{param.Type}'");
					continue;
				}

				parameters.Add(paramSymbol);
			}

			var returnType = context.ResolveType(method.ReturnType);
			if (returnType is null)
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, method.ReturnTypeSpan, $"Unknown return type '{method.ReturnType}'");
				return;
			}

			// 2. Register the overloaded, parameter-mangled global signature
			var overloadedName = context.GetOverloadedMangledName(baseMangledName, parameters.Select(p => p.Type).ToList());

			var memberVisibility = method.SyntacticVisibility ?? blockVisibility;
			if (!context.LegacyVisibility && method.SyntacticVisibility is { } memberVis && VisibilityRank(memberVis) > VisibilityRank(blockVisibility))
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, method.NameSpan,
					$"Element '{method.Name}' cannot declare a wider visibility modifier than its enclosing extension block visibility level ({blockVisibility}).",
					DiagnosticIds.VisibilityExpansionInExtension);
			}

			var newSymbol = new FunctionSymbol(overloadedName, returnType, parameters)
			{
				Visibility = memberVisibility,
				DeclaringUnit = context.CurrentUnit
			};
			var methodSuppressedWarnings = new List<string>();
			_attributes.ApplyFunctionAttributes(
				_attributes.Verify(method.Attributes, isDestructor ? "Destructor" : "Method", methodSuppressedWarnings),
				newSymbol,
				methodSuppressedWarnings,
				method.Attributes);
			_attributes.WarnIfUnsafeBodyUnused(method.NameSpan, method.Body, newSymbol, methodSuppressedWarnings);

			// COLLISION RULE: an extension may not re-declare a method the type already
			// has with a matching signature (another extension block, the proto-default
			// registry's naked symbols, or a conformer-declared override). First wins.
			if (context.Globals.Lookup(overloadedName) is not null)
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, method.NameSpan,
					$"Duplicate symbol '{method.Name}' on type '{extDecl.ExtendedTypeName}' in extension blocks.");
				continue;
			}

			context.Globals.Declare(newSymbol);

			if (!context.OverloadedFunctions.TryGetValue(baseMangledName, out var candidates))
			{
				candidates = [];
				context.OverloadedFunctions[baseMangledName] = candidates;
			}

			candidates.Add(newSymbol);

			context.SymbolUnits[overloadedName] = context.CurrentUnit!;

			if (isDestructor)
				destructors.Register(extDecl.ExtendedTypeName, newSymbol);
		}

		foreach (var ctorDecl in extDecl.Constructors)
		{
			if (ctorDecl.StructName != extDecl.ExtendedTypeName)
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, ctorDecl.NameSpan, $"Constructor name '{ctorDecl.StructName}' must match the extended type '{extDecl.ExtendedTypeName}'.");
				continue;
			}

			if (extendedType is not StructTypeSymbol ctorStructType)
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, ctorDecl.NameSpan, $"Cannot define a constructor for non-struct type '{extDecl.ExtendedTypeName}'.");
				continue;
			}

			// Mangled base name is just the struct name: bare calls 'T(args)' resolve like free functions
			var ctorBaseMangledName = context.GetMangledName(extDecl.ExtendedTypeName, context.CurrentNamespace);

			// 1. Inject the implicit first parameter: "this" (the destination storage)
			var ctorThisParamType = new PointerTypeSymbol(ctorStructType, isMutable: true);
			var ctorParameters = new List<ParameterSymbol> { new ParameterSymbol("this", ctorThisParamType) };

			var hasBadParam = false;
			foreach (var param in ctorDecl.Parameters)
			{
				var paramSymbol = functions.CreateParameter(param);
				if (paramSymbol is null)
				{
					ReportDeclarationDiagnostic(param, $"Unknown parameter type '{param.Type}'");
					hasBadParam = true;
					continue;
				}

				ctorParameters.Add(paramSymbol);
			}

			if (hasBadParam)
				continue;

			// 2. Register under the struct's name so 'T(...)' call sites resolve via existing overload machinery
			var ctorOverloadedName = context.GetOverloadedMangledName(ctorBaseMangledName, ctorParameters.Select(p => p.Type).ToList());
			var ctorVisibility = ctorDecl.SyntacticVisibility ?? blockVisibility;
			if (!context.LegacyVisibility && ctorDecl.SyntacticVisibility is { } ctorVis && VisibilityRank(ctorVis) > VisibilityRank(blockVisibility))
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, ctorDecl.NameSpan,
					$"Element '{ctorDecl.StructName}' cannot declare a wider visibility modifier than its enclosing extension block visibility level ({blockVisibility}).",
					DiagnosticIds.VisibilityExpansionInExtension);
			}

			var ctorSymbol = new FunctionSymbol(ctorOverloadedName, ctorStructType, ctorParameters)
			{
				Visibility = ctorVisibility,
				DeclaringUnit = context.CurrentUnit
			};
			var ctorSuppressedWarnings = new List<string>();
			_attributes.ApplyFunctionAttributes(_attributes.Verify(ctorDecl.Attributes, "Constructor", ctorSuppressedWarnings), ctorSymbol, ctorSuppressedWarnings);
			_attributes.WarnIfUnsafeBodyUnused(ctorDecl.NameSpan, ctorDecl.Body, ctorSymbol, ctorSuppressedWarnings);

			// COLLISION RULE: duplicate constructor signatures on the same type.
			if (context.Globals.Lookup(ctorOverloadedName) is not null)
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, ctorDecl.NameSpan,
					$"Duplicate constructor signature for type '{extDecl.ExtendedTypeName}'.");
				continue;
			}

			context.Globals.Declare(ctorSymbol);

			if (!context.OverloadedFunctions.TryGetValue(ctorBaseMangledName, out var ctorCandidates))
			{
				ctorCandidates = [];
				context.OverloadedFunctions[ctorBaseMangledName] = ctorCandidates;
			}

			ctorCandidates.Add(ctorSymbol);

			if (context.CurrentUnit is not null)
			{
				context.SymbolUnits[ctorOverloadedName] = context.CurrentUnit;
			}

			if (!context.Constructors.TryGetValue(extDecl.ExtendedTypeName, out var registeredCtors))
			{
				registeredCtors = [];
				context.Constructors[extDecl.ExtendedTypeName] = registeredCtors;
			}

			registeredCtors.Add(ctorSymbol);
		}
	}

	/// <summary>
	/// Records and validates nominal interface conformance declared by an extension block.
	/// </summary>
	private void RegisterConformance(ExtensionDeclarationSyntax extDecl, TypeSymbol extendedType)
	{
		// Resolve the interface within the extension's declaration context.
		var prevUnit = context.CurrentUnit;
		var prevNs = context.CurrentNamespace;
		context.CurrentUnit = context.SymbolUnits.TryGetValue(extendedType.Name, out var extUnit) ? extUnit : context.CurrentUnit;
		context.CurrentNamespace = context.CurrentUnit?.NamespaceDeclaration?.Name;

		var interfaceType = context.ResolveType(extDecl.ConformsTo!);
		context.CurrentUnit = prevUnit;
		context.CurrentNamespace = prevNs;

		if (interfaceType is not InterfaceTypeSymbol interfaceSymbol)
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, extDecl.Span, $"Unknown interface '{extDecl.ConformsTo}' in conformance declaration.");
			return;
		}

		// Record conformance: concrete type -> interface.
		if (!context.Conformance.TryGetValue(extendedType.Name, out var interfaces))
		{
			interfaces = [];
			context.Conformance[extendedType.Name] = interfaces;
		}

		interfaces.Add(interfaceSymbol.Name);

		var interfaceDecl = context.InterfaceTemplates[interfaceSymbol.Name];

		// Record transitive nominal ancestors (interface base clauses), so a
		// conforming type implicitly satisfies the parent capability graph.
		// Protocol bases are satisfied structurally elsewhere and stay nominal-free.
		CollectInterfaceAncestors(interfaceDecl, interfaces);

		// A conforming type must provide every required member of the effective
		// interface (own members + base-clause closure).
		var providedMethods = new HashSet<(string Name, string ReturnType, string Params)>();
		foreach (var method in extDecl.Methods)
			providedMethods.Add((method.Name, method.ReturnType, ParamsSignature(method.Parameters)));

		foreach (var member in GetEffectiveInterfaceMembers(interfaceSymbol, new Dictionary<string, List<InterfaceMethodDeclarationSyntax>>(), new HashSet<string>()))
		{
			var requiredSig = (member.Name, member.ReturnType, ParamsSignature(member.Parameters));
			if (!providedMethods.Contains(requiredSig))
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, extDecl.Span,
					$"Type '{extDecl.ExtendedTypeName}' does not implement member '{RequiredSigText(member)}' required by interface '{extDecl.ConformsTo}'.");
			}
		}

		// Interface `for ...` requires-clause (spec §7.B), enforced eagerly at the
		// conformance site: the extended type must satisfy the named contract.
		if (interfaceDecl.Constraint is not null)
			EnforceInterfaceConstraint(interfaceDecl, extDecl, providedMethods);
	}

	/// <summary>
	/// Records every transitive interface ancestor of <paramref name="iface"/> into
	/// the concrete type's conformance set (interface base clauses only; protocol
	/// bases are structural and are never recorded nominally).
	/// </summary>
	private void CollectInterfaceAncestors(InterfaceDeclarationSyntax iface, HashSet<string> interfaces)
	{
		foreach (var baseName in iface.Bases)
		{
			if (context.ResolveType(baseName) is not InterfaceTypeSymbol baseIface)
				continue;

			if (!interfaces.Add(baseIface.Name))
				continue;

			if (context.InterfaceTemplates.TryGetValue(baseIface.Name, out var baseDecl))
				CollectInterfaceAncestors(baseDecl, interfaces);
		}
	}

	/// <summary>
	/// The effective member set of an interface: its own required members plus the
	/// transitive closure of interface parents, plus protocol-parent members.
	/// Child declarations override inherited members with identical signatures.
	/// </summary>
	private List<InterfaceMethodDeclarationSyntax> GetEffectiveInterfaceMembers(
		InterfaceTypeSymbol iface, Dictionary<string, List<InterfaceMethodDeclarationSyntax>> cache, HashSet<string> visiting)
	{
		if (cache.TryGetValue(iface.Name, out var cached) || !visiting.Add(iface.Name))
			return cached;

		var decl = context.InterfaceTemplates[iface.Name];
		var result = new List<InterfaceMethodDeclarationSyntax>();
		foreach (var baseName in decl.Bases)
		{
			if (context.ResolveType(baseName) is InterfaceTypeSymbol baseIface)
				result.AddRange(GetEffectiveInterfaceMembers(baseIface, cache, visiting));
			else if (context.ResolveType(baseName) is ProtocolTypeSymbol baseProto
				&& context.ProtocolTemplates.TryGetValue(baseProto.Name, out var protoDecl))
			{
				// A protocol parent contributes every required member of its own
				// effective set (a protocol may itself aggregate other protocols).
				IEnumerable<(string Owner, ProtocolMethodDeclarationSyntax Member)> protocolMembers =
					context.ProtocolEffectiveMembers.TryGetValue(baseProto.Name, out var effective)
						? effective
						: protoDecl.Members.Select(m => (baseProto.Name, m));
				foreach (var (_, member) in protocolMembers)
					result.Add(new InterfaceMethodDeclarationSyntax(member.Span, member.ReturnType, member.Name, member.Parameters));
			}
		}

		// Child overrides win: inherited members identical to an own member are dropped.
		result.RemoveAll(m => decl.Members.Any(own =>
			own.Name == m.Name && own.ReturnType == m.ReturnType && ParamsSignature(own.Parameters) == ParamsSignature(m.Parameters)));
		result.AddRange(decl.Members);

		visiting.Remove(iface.Name);
		cache[iface.Name] = result;
		return result;
	}

	/// <summary>
	/// Aggressive enforcement of an interface's `for ...` requires-clause at the
	/// conformance site. The extended type is checked against the named contract:
	/// a nominal interface must be in the conformance set; a structural protocol
	/// must be satisfied by the provided methods (name-level).
	/// </summary>
	private void EnforceInterfaceConstraint(InterfaceDeclarationSyntax interfaceDecl, ExtensionDeclarationSyntax extDecl, HashSet<(string Name, string ReturnType, string Params)> providedMethods)
	{
		var currentFileContext = context.FileContexts[context.CurrentUnit!];
		var contract = ResolveContractType(interfaceDecl.Constraint, extDecl.ExtendedTypeName);

		switch (contract)
		{
			case InterfaceTypeSymbol consIface:
				{
					if (!context.Conformance.TryGetValue(extDecl.ExtendedTypeName, out var ifaces) || !ifaces.Contains(consIface.Name))
						context.Diagnostics.Report(currentFileContext, extDecl.Span,
							$"Type '{extDecl.ExtendedTypeName}' does not satisfy the requires-clause '{interfaceDecl.Constraint}' of interface '{extDecl.ConformsTo}': it does not conform to interface '{consIface.Name}'.");
					break;
				}
			case ProtocolTypeSymbol consProto:
				{
					var requiredNames = new HashSet<string>();
					foreach (var member in GetProtocolRequirements(consProto.Name))
						requiredNames.Add(member.Name);
					var providedNames = new HashSet<string>(extDecl.Methods.Select(m => m.Name));
					foreach (var required in requiredNames)
					{
						if (!providedNames.Contains(required))
							context.Diagnostics.Report(currentFileContext, extDecl.Span,
								$"Type '{extDecl.ExtendedTypeName}' does not satisfy the requires-clause '{interfaceDecl.Constraint}' of interface '{extDecl.ConformsTo}': missing protocol member '{required}'.");
					}

					break;
				}
			default:
				context.Diagnostics.Report(currentFileContext, extDecl.Span,
					$"Unknown contract '{interfaceDecl.Constraint}' in requires-clause of interface '{extDecl.ConformsTo}'.");
				break;
		}
	}

	/// <summary>
	/// Returns the effective structural requirements of a protocol, falling back to its directly declared members.
	/// </summary>
	private List<ProtocolMethodDeclarationSyntax> GetProtocolRequirements(string protoMangledName)
	{
		if (context.ProtocolTemplates.TryGetValue(protoMangledName, out var decl)
			&& context.ProtocolEffectiveMembers.TryGetValue(protoMangledName, out var effective))
			return effective.Select(e => e.Member).ToList();

		// Fall back to the declared members if effective membership is unavailable.
		return context.ProtocolTemplates.TryGetValue(protoMangledName, out var protoDecl)
			? protoDecl.Members.ToList()
			: [];
	}

	/// <summary>
	/// Resolves a requires-clause type in the context of the extended type.
	/// Literal `Self` tokens are replaced with the concrete type name, and a
	/// generic instantiation (e.g. `IComparable&lt;Self&gt;`) is stripped to its
	/// base contract name (generic interfaces are not instantiable in this model).
	/// </summary>
	private TypeSymbol? ResolveContractType(string constraintText, string concreteName)
	{
		var substituted = constraintText.Replace("Self", concreteName);
		var openBracket = substituted.IndexOf('<');
		var baseName = (openBracket > 0 ? substituted[..openBracket] : substituted).Trim();
		return context.ResolveType(baseName);
	}

	/// <summary>
	/// Builds the parameter-type signature used to compare extension members with contract requirements.
	/// </summary>
	private static string ParamsSignature(IReadOnlyList<ParameterSyntax> parameters)
		=> string.Join(",", parameters.Select(p => p.Type));

	/// <summary>
	/// Formats an interface requirement for declaration diagnostics.
	/// </summary>
	private static string RequiredSigText(InterfaceMethodDeclarationSyntax member)
		=> $"{member.ReturnType} {member.Name}({ParamsSignature(member.Parameters)})";
}
