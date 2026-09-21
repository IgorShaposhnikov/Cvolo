using Cvolo.Analysis.Symbols;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Collections;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Analysis.Passes.Declaration;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using Cvolo.Core.AST.Directives;
using Cvolo.Core.AST.Expressions;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Analysis.Passes;

public sealed class DeclarationPass(BindingContext context)
{
	private readonly AttributeValidator _attributes = new(context);
	private readonly TypeAliasValidator _aliases = new(context);
	private readonly TypeDeclarationRegistrar _types = new(context);
	private readonly ContractHierarchyLinker _contracts = new(context);
	private readonly EmbedLinker _embeds = new(context);
	private readonly EmbeddedMethodPromoter _embeddedMethods = new(context);
	private readonly DestructorValidator _destructors = new(context);
	private readonly GenericDefaultConstraintValidator _genericDefaults = new(context);
	private readonly GenericDefaultCopyValidator _genericDefaultCopies = new(context);
	private readonly FunctionDeclarationRegistrar _functions = new(context);
	/// <summary>
	/// The compiler's built-in attribute names, without the optional <c>Attribute</c> suffix, in
	/// declaration order. Tooling surfaces use this forwarding property to preserve the existing
	/// <see cref="DeclarationPass"/> API while attribute semantics live in <see cref="AttributeValidator"/>.
	/// </summary>
	public static IReadOnlyCollection<string> IntrinsicAttributeNames => AttributeValidator.IntrinsicAttributeNames;


	// Visibility tier ordering: Private < Internal < Public.
	private static int VisibilityRank(Visibility visibility) => visibility switch
	{
		Visibility.Public => 2,
		Visibility.Internal => 1,
		_ => 0
	};

	public void Process(IEnumerable<CompilationUnitSyntax> units)
	{
		ProcessExposeUsings(units);

		// Pass 0a-pre: Register all type aliases before any type/field/parameter resolution
		// so struct fields and function signatures may reference them. Names are reserved
		// now; underlying types are validated eagerly in Pass 0a-post (below).
		foreach (var unit in units)
		{
			context.CurrentUnit = unit;
			context.CurrentNamespace = unit.NamespaceDeclaration?.Name;

			var aliasMembers = context.CurrentNamespace != null ? unit.NamespaceDeclaration!.Members : unit.Members;
			foreach (var member in aliasMembers)
			{
				if (member is TypeAliasDeclarationSyntax aliasDecl)
					_aliases.Declare(aliasDecl);
			}
		}

		// Pass 0a: Register all Struct/Union/Interface/Protocol raw symbols across all files
		foreach (var unit in units)
		{
			context.CurrentUnit = unit;
			context.CurrentNamespace = unit.NamespaceDeclaration?.Name;

			var members = context.CurrentNamespace != null ? unit.NamespaceDeclaration!.Members : unit.Members;
			foreach (var member in members)
			{
				if (member is StructDeclarationSyntax structDecl)
					_types.DeclareStruct(structDecl);
				else if (member is UnionDeclarationSyntax unionDecl)
					_types.DeclareUnion(unionDecl);
				else if (member is InterfaceDeclarationSyntax interfaceDecl)
					_types.DeclareInterface(interfaceDecl);
				else if (member is ProtocolDeclarationSyntax protocolDecl)
					_types.DeclareProtocol(protocolDecl);
				else if (member is EnumDeclarationSyntax enumDecl)
					_types.DeclareEnum(enumDecl);
				else if (member is DelegateDeclarationSyntax delegateDecl)
					_types.DeclareDelegate(delegateDecl);
			}
		}

		// Pass 0a-post: Eagerly validate every type alias now that all real type names are
		// registered — alias-vs-type conflicts, unresolvable targets (CVL1200), alias cycles
		// (CVL1201), and aliases in `where` constraints (CVL1202).
		_aliases.Validate(units);

		// Pass 0b: Link contract hierarchy (`:` base clauses) after every raw contract symbol exists.
		_contracts.Link(units);

		// Pass 0c: Link `struct T embed Base` clauses — validate the embedded type,
		// detect cycles/generics, and rebuild struct symbols with the embedded
		// fields flattened at the FRONT of their layout.
		_embeds.Link(units);

		// Pass 1: Register all Function/Extern signatures across all files
		foreach (var unit in units)
		{
			context.CurrentUnit = unit;
			context.CurrentNamespace = unit.NamespaceDeclaration?.Name;

			var members = unit.NamespaceDeclaration != null ? unit.NamespaceDeclaration.Members : unit.Members;
			foreach (var member in members)
			{
				if (member is FunctionDeclarationSyntax func)
					_functions.DeclareFunction(func);
				else if (member is ExternDeclarationSyntax ext)
					_functions.DeclareExternFunction(ext);
				else if (member is ExternBlockSyntax extBlock)
					_functions.DeclareExternBlock(extBlock);
				else if (member is ExposeExternBlockSyntax exportBlock)
					_functions.DeclareExposeExternBlock(exportBlock);
				else if (member is ExtensionDeclarationSyntax extDecl)
					DeclareExtension(extDecl);
				else if (member is GlobalVariableDeclarationSyntax globalDecl)
					DeclareGlobalVariable(globalDecl);
			}
		}

		// Pass 1.5: Promote embedded-type extension methods onto every struct that
		// embeds them — `w.TakeDamage(20)` on a struct that `embed`s BaseEntity
		// resolves BaseEntity's extension with the outer struct as `this`.
		_embeddedMethods.Promote(units);

		// Pass 2: Enforce the destructor nesting-depth limit (Memory & Safety §2). Run after every
		// struct symbol (embeds included) is fully materialized so the transitive ownership graph
		// is complete.
		_destructors.ValidateDepth(units);

		// Pass 2.5: Validate that default generic parameter types satisfy their `where X : T`
		// constraints (CVL1042). This must run AFTER Pass 1 registers extension methods —
		// structural protocol conformance is proven by the presence of the extended methods
		// on the concrete type — so it cannot live inside Pass 0a's DeclareStruct.
		_genericDefaults.Validate(units);
	}

	private void ReportDeclarationDiagnostic(SyntaxNode node, string message, string diagnosticId)
	{
		var currentFileContext = context.FileContexts[context.CurrentUnit!];
		context.Diagnostics.Report(currentFileContext, node.Span, message, diagnosticId);
	}


	private void ReportDeclarationDiagnostic(SyntaxNode node, string message)
	{
		var currentFileContext = context.FileContexts[context.CurrentUnit!];
		context.Diagnostics.Report(currentFileContext, node.Span, message);
	}

	private void DeclareExtension(ExtensionDeclarationSyntax extDecl)
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

			_genericDefaultCopies.Validate(extDecl);

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
			if (isDestructor && !_destructors.ValidateDeclaration(extDecl.ExtendedTypeName, method))
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
				var paramSymbol = _functions.CreateParameter(param);
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
				_destructors.Register(extDecl.ExtendedTypeName, newSymbol);
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
				var paramSymbol = _functions.CreateParameter(param);
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

	private static string ParamsSignature(IReadOnlyList<ParameterSyntax> parameters)
		=> string.Join(",", parameters.Select(p => p.Type));

	private static string RequiredSigText(InterfaceMethodDeclarationSyntax member)
		=> $"{member.ReturnType} {member.Name}({ParamsSignature(member.Parameters)})";

	private void DeclareGlobalVariable(GlobalVariableDeclarationSyntax globalDecl)
	{
		// Reject 'global var ref/refvar ...' — reference types use ref/refvar directly, not var
		if (globalDecl.IsMutable && globalDecl.Type is not null && globalDecl.Type.StartsWith("ref"))
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, globalDecl.Span,
				$"Cannot use 'var' with reference type in global declaration. Use 'global ref' or 'global refvar' instead.");
			return;
		}

		var type = context.ResolveType(globalDecl.Type);
		if (type is null)
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, globalDecl.Span, $"Unknown type '{globalDecl.Type}' in global variable '{globalDecl.Name}'.");
			return;
		}

		var qualifiedName = context.GetMangledName(globalDecl.Name, context.CurrentNamespace);
		if (context.GlobalsByQualifiedName.ContainsKey(qualifiedName))
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, globalDecl.Span, $"Duplicate definition of global variable '{globalDecl.Name}'.");
			return;
		}

		if (ContainsConstantIntegerDivisionByZero(globalDecl.Initializer))
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, globalDecl.Span, $"Division by zero in constant initializer for global variable '{globalDecl.Name}'.", DiagnosticIds.ConstantIntegerDivisionByZero);
			return;
		}

		// §16: safe delegates are non-null / non-default-initializable; a delegate-typed
		// global must carry an explicit initializer, and 'null' is never a legal value.
		if (type is DelegateTypeSymbol)
		{
			if (globalDecl.Initializer is null)
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, globalDecl.Span,
					$"Global delegate '{globalDecl.Name}' requires an initializer; delegates are non-null and cannot be default-initialized.");
				return;
			}

			if (globalDecl.Initializer is NullLiteralExpressionSyntax)
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, globalDecl.Initializer.Span,
					$"Cannot initialize delegate '{globalDecl.Name}' with 'null'; delegates are non-null values.",
					DiagnosticIds.NullLiteralForDelegate);
				return;
			}
		}

		// Globals may reference a function group when the slot is delegate-typed; the actual
		// conversion is resolved and recorded during validation for the emitter.
		if (type is DelegateTypeSymbol && globalDecl.Initializer is IdentifierExpressionSyntax)
		{
			// Fall through: function references are treated as usable global initializers.
		}
		else if (!IsCompileTimeConstant(globalDecl.Initializer))
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, globalDecl.Span, $"Global variable '{globalDecl.Name}' must be initialized with a compile-time constant.", DiagnosticIds.GlobalInitializerNotConstant);
			return;
		}

		// CVL1036 (§TBAA): a multi-word container exposed with module-wide (ABI) visibility
		// invites unsynchronized 16-byte register tearing. Single-word public scalars and small
		// (<9 byte) aggregates are fine; anything wider must live behind a Lock/Mutex wrapper.
		if (!context.LegacyVisibility && globalDecl.Visibility == Visibility.Public && IsMultiWordContainer(type))
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, globalDecl.Span,
				$"Shared multi-word container '{globalDecl.Name}' cannot be exposed publicly without synchronization. Potential 16-byte register tearing and Type Confusion detected. Wrap the global in a 'Lock' or 'Mutex'.",
				DiagnosticIds.MultiWordPublicGlobal);
		}

		// ref/refvar globals are inherently re-assignable (mutable references)
		var isRefType = globalDecl.Type is not null && globalDecl.Type.StartsWith("ref");
		var symbol = new VariableSymbol(globalDecl.Name, type, isMutable: globalDecl.IsMutable || isRefType)
		{
			IsInitialized = true,
			IsGlobal = true,
			Origin = OriginKind.Global,
			Visibility = globalDecl.Visibility,
			DeclaringUnit = context.CurrentUnit,
			DeclaringNamespace = context.CurrentNamespace
		};
		context.GlobalsByQualifiedName[qualifiedName] = symbol;
		if (!context.GlobalsByShortName.TryGetValue(globalDecl.Name, out var shortNameList))
		{
			shortNameList = [];
			context.GlobalsByShortName[globalDecl.Name] = shortNameList;
		}
		shortNameList.Add(symbol);
		context.GlobalVariables.Add((globalDecl, symbol));
	}

	// Multi-word containers are exactly those whose value spills past 8 bytes: slices/fat
	// pointers, interface references, and aggregates wider than a single machine word.
	private bool IsMultiWordContainer(TypeSymbol type)
	{
		switch (type)
		{
			case SliceTypeSymbol or InterfaceTypeSymbol:
				return true;
			case StructTypeSymbol or UnionTypeSymbol or EnumTypeSymbol:
				return ComputeByteSize(type, new HashSet<string>()) > 8;
			default:
				return false;
		}
	}

	// Best-effort recursive byte size for aggregates. Unknown/placeholder types are conservatively
	// treated as 8 bytes (single word) so the CVL1036 gate never fires spuriously.
	private int ComputeByteSize(TypeSymbol type, HashSet<string> seen)
	{
		if (type is PointerTypeSymbol or SliceTypeSymbol)
			return type is SliceTypeSymbol ? 16 : 8;

		if (type is ArrayTypeSymbol array)
			return array.Size == int.MaxValue ? 8 : array.Size * ComputeByteSize(array.ElementType, seen);

		if (type is EnumTypeSymbol enumType)
			return TypeSymbol.PrimitiveByteSize(enumType.StorageType);
		if (type is UnionTypeSymbol unionType)
		{
			if (!seen.Add(unionType.Name))
				return 8;
			var max = 0;
			foreach (var field in unionType.Fields)
				max = Math.Max(max, field.IsVoidVariant ? 0 : ComputeByteSize(field.Type, seen));
			return max;
		}
		if (type is StructTypeSymbol structType)
		{
			if (!seen.Add(structType.Name))
				return 8;
			var total = 0;
			foreach (var field in structType.Fields)
				total += ComputeByteSize(field.Type, seen);
			return total;
		}

		return TypeSymbol.PrimitiveByteSize(type);
	}

	private static bool IsCompileTimeConstant(ExpressionSyntax? expr)
	{
		if (expr is null)
			return true; // zero-initialized

		return expr switch
		{
			IntegerLiteralExpressionSyntax or DoubleLiteralExpressionSyntax or BooleanLiteralExpressionSyntax or CharacterLiteralExpressionSyntax or NullLiteralExpressionSyntax => true,
			UnaryExpressionSyntax { Operator: "-" } unary => IsCompileTimeConstant(unary.Operand),
			BinaryExpressionSyntax { Operator: "+" or "-" or "*" or "/" or "%" } bin
				=> IsCompileTimeConstant(bin.Left) && IsCompileTimeConstant(bin.Right),
			StructInitializationExpressionSyntax structInit => structInit.Initializers.All(static m => IsCompileTimeConstant(m.Expression)),
			_ => false
		};
	}

	/// <summary>
	/// True when the initializer divides or takes the remainder of a PURE-INTEGER constant by zero.
	/// Float literals switch the expression into IEEE context (1.0 / 0.0 yields +Inf and is legal),
	/// so the fold bails the moment it meets a non-integer node. Mirrors the wrapping arithmetic that
	/// CodeGenerator.BuildGlobalInitializer uses when it folds the same tree.
	/// </summary>
	private static bool ContainsConstantIntegerDivisionByZero(ExpressionSyntax? expr)
	{
		return expr switch
		{
			BinaryExpressionSyntax { Operator: "/" or "%" } bin
				=> (TryFoldIntegerConstant(bin.Right, out var divisor) && divisor == 0)
					|| ContainsConstantIntegerDivisionByZero(bin.Left)
					|| ContainsConstantIntegerDivisionByZero(bin.Right),
			BinaryExpressionSyntax { Operator: "+" or "-" or "*" } bin
				=> ContainsConstantIntegerDivisionByZero(bin.Left) || ContainsConstantIntegerDivisionByZero(bin.Right),
			UnaryExpressionSyntax { Operator: "-" } unary => ContainsConstantIntegerDivisionByZero(unary.Operand),
			StructInitializationExpressionSyntax structInit => structInit.Initializers.Any(static m => ContainsConstantIntegerDivisionByZero(m.Expression)),
			_ => false
		};
	}

	/// <summary>Folds a subtree of integer literals (wrapping) to a single value; false when the subtree
	/// contains any non-integer node (float literal, call, identifier, ...).</summary>
	private static bool TryFoldIntegerConstant(ExpressionSyntax expr, out long value)
	{
		switch (expr)
		{
			case IntegerLiteralExpressionSyntax lit:
				value = unchecked((long)lit.Value);
				return true;
			case UnaryExpressionSyntax { Operator: "-" } unary when TryFoldIntegerConstant(unary.Operand, out var inner):
				value = unchecked(-inner);
				return true;
			case BinaryExpressionSyntax bin when bin.Operator is "+" or "-" or "*" or "/" or "%"
				&& TryFoldIntegerConstant(bin.Left, out var l) && TryFoldIntegerConstant(bin.Right, out var r):
				value = bin.Operator switch
				{
					"+" => unchecked(l + r),
					"-" => unchecked(l - r),
					"*" => unchecked(l * r),
					"/" => unchecked(l / r),
					"%" => r == 0 ? 0 : unchecked(l % r),
					_ => 0
				};
				return true;
			default:
				value = 0;
				return false;
		}
	}


	/// <summary>
	/// Pass 0: Registers all declared namespaces, collects 'expose using' re-exports,
	/// and validates that:
	/// 1. 'expose using' is only declared inside a namespace (CVL1060).
	/// 2. Target namespaces of 'expose using' actually exist across the compilation units (CVL1061).
	/// </summary>
	private void ProcessExposeUsings(IEnumerable<CompilationUnitSyntax> units)
	{
		context.DeclaredNamespaces.Clear();
		context.NamespaceReExports.Clear();

		// 1. Gather all declared namespaces across all units
		foreach (var unit in units)
		{
			if (unit.NamespaceDeclaration is not null)
			{
				context.DeclaredNamespaces.Add(unit.NamespaceDeclaration.Name);
			}
		}

		// 2. Validate and register 'expose using' directives
		var exposeDirectives = new List<(CompilationContext FileCtx, UsingDirectiveSyntax Directive, string? EnclosingNs)>();

		foreach (var unit in units)
		{
			context.CurrentUnit = unit;
			var fileContext = context.FileContexts[unit];

			// File-level usings (outside any namespace)
			foreach (var u in unit.Usings)
			{
				if (u.IsExposed)
				{
					context.Diagnostics.Report(
						fileContext,
						u.Span,
						"'expose using' can only be used inside a namespace.",
						DiagnosticIds.ExposeUsingOutsideNamespace);
				}
			}

			// Namespace-level usings
			if (unit.NamespaceDeclaration is not null)
			{
				var currentNs = unit.NamespaceDeclaration.Name;
				foreach (var u in unit.NamespaceDeclaration.Usings)
				{
					if (u.IsExposed)
					{
						if (!context.NamespaceReExports.TryGetValue(currentNs, out var set))
						{
							set = new HashSet<string>(StringComparer.Ordinal);
							context.NamespaceReExports[currentNs] = set;
						}

						set.Add(u.NamespaceName);
						exposeDirectives.Add((fileContext, u, currentNs));
					}
				}
			}
		}

		// 3. Verify that re-export target namespaces exist (CVL1061)
		foreach (var (fileCtx, directive, _) in exposeDirectives)
		{
			if (!context.DeclaredNamespaces.Contains(directive.NamespaceName))
			{
				context.Diagnostics.Report(
					fileCtx,
					directive.Span,
					$"Target namespace '{directive.NamespaceName}' of 'expose using' does not exist.",
					DiagnosticIds.ExposeUsingNamespaceNotFound);
			}
		}
	}

}
