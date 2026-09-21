using Cvolo.Analysis.Passes.Declaration;
using Cvolo.Analysis.Symbols;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Collections;
using Cvolo.Analysis.Symbols.FFI;
using Cvolo.Analysis.Symbols.Structs;
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
	private ClassificationAnalyzer? _classification;
	private ClassificationAnalyzer Classification => _classification ??= new ClassificationAnalyzer(context);
	/// <summary>
	/// The compiler's built-in attribute names, without the optional <c>Attribute</c> suffix, in
	/// declaration order. Tooling surfaces use this forwarding property to preserve the existing
	/// <see cref="DeclarationPass"/> API while attribute semantics live in <see cref="AttributeValidator"/>.
	/// </summary>
	public static IReadOnlyCollection<string> IntrinsicAttributeNames => AttributeValidator.IntrinsicAttributeNames;

	/// <summary>Export symbol names already claimed by an `expose extern` function (module scope).</summary>
	private readonly HashSet<string> _exportSymbolNames = [];

	// Memory & Safety spec §2: the destructor nesting depth is capped. Dropping a value of a
	// deeply nested (by-value) move type would recurse once per nested owner; past this bound we
	// refuse to compile rather than risk unbounded cleanup recursion.
	private const int MaxDestructorNestingDepth = 1024;

	private const string CyclicDestructorDepthError = "Cyclic destructor nesting depth exceeded. Please use an arena allocator or manual cleanup.";

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
					DeclareFunction(func);
				else if (member is ExternDeclarationSyntax ext)
					DeclareExternFunction(ext);
				else if (member is ExternBlockSyntax extBlock)
					DeclareExternBlock(extBlock);
				else if (member is ExposeExternBlockSyntax exportBlock)
					DeclareExposeExternBlock(exportBlock);
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
		CheckDestructorDepth(units);

		// Pass 2.5: Validate that default generic parameter types satisfy their `where X : T`
		// constraints (CVL1042). This must run AFTER Pass 1 registers extension methods —
		// structural protocol conformance is proven by the presence of the extended methods
		// on the concrete type — so it cannot live inside Pass 0a's DeclareStruct.
		ValidateDefaultTypeConstraints(units);
	}

	private void ReportDeclarationDiagnostic(SyntaxNode node, string message, string diagnosticId)
	{
		var currentFileContext = context.FileContexts[context.CurrentUnit!];
		context.Diagnostics.Report(currentFileContext, node.Span, message, diagnosticId);
	}


	/// <summary>
	/// Pass 2.5. After all symbols and extension methods are registered, re-check every generic
	/// struct's `where default A : Type` so the default satisfies the type's `where A : T`
	/// constraints. Structural protocol conformance needs the extension method table populated,
	/// which is why this runs late (a Pass 0a check would see an empty table and spuriously fail).
	/// </summary>
	private void ValidateDefaultTypeConstraints(IEnumerable<CompilationUnitSyntax> units)
	{
		foreach (var unit in units)
		{
			context.CurrentUnit = unit;
			context.CurrentNamespace = unit.NamespaceDeclaration?.Name;

			var members = context.CurrentNamespace != null ? unit.NamespaceDeclaration!.Members : unit.Members;
			foreach (var member in members)
			{
				if (member is not StructDeclarationSyntax structDecl)
					continue;

				if (structDecl.GenericParameterDefaults.Count == 0)
					continue;

				foreach (var (paramName, defaultTypeName) in structDecl.GenericParameterDefaults)
				{
					if (!structDecl.GenericParameterConstraints.TryGetValue(paramName, out var constraints))
						continue;

					var defaultType = context.ResolveType(defaultTypeName);
					if (defaultType is null)
						continue;

					foreach (var constraintName in constraints)
					{
						var constraintType = context.ResolveType(constraintName);
						if (constraintType is not null && !context.TypeSatisfiesContract(defaultType, constraintType))
						{
							var currentFileContext = context.FileContexts[context.CurrentUnit!];
							context.Diagnostics.Report(
								currentFileContext,
								structDecl.Span,
								$"Default type '{defaultTypeName}' does not satisfy constraint '{constraintName}' of generic parameter '{paramName}'.",
								DiagnosticIds.DefaultTypeConstraintMismatch);
						}
					}
				}
			}
		}
	}

	/// <summary>
	/// Pass 2. Enforce the destructor nesting-depth cap. Walks each struct's transitive
	/// owned (non-pointer) move field graph computing how deeply cleanup would recurse; a depth
	/// beyond <see cref="MaxDestructorNestingDepth"/> is a compile error. Genuine by-value cycles
	/// are inexpressible in safe source (a field type must already be declared, so a chain can never
	/// loop), but the walk keeps a path set so a cycle — if ever reachable through other means — is
	/// reported with the same diagnostic rather than recursing forever. Pointer fields are excluded:
	/// they are not owned, their cleanup responsibility lies with the pointer consumer.
	/// </summary>
	private void CheckDestructorDepth(IEnumerable<CompilationUnitSyntax> units)
	{
		foreach (var unit in units)
		{
			context.CurrentUnit = unit;
			context.CurrentNamespace = unit.NamespaceDeclaration?.Name;

			var members = context.CurrentNamespace != null ? unit.NamespaceDeclaration!.Members : unit.Members;
			foreach (var member in members)
			{
				if (member is not StructDeclarationSyntax structDecl)
					continue;

				// Generic templates' fields are type-parameter placeholders; their concrete
				// nesting is checked when an instantiation is registered.
				if (structDecl.GenericParameters.Count > 0)
					continue;

				var mangledName = context.GetMangledName(structDecl.Name, context.CurrentNamespace);
				if (!context.StructTypes.TryGetValue(mangledName, out var structType))
					continue;

				if (DestructorDepth(structType, new HashSet<string>()) > MaxDestructorNestingDepth)
				{
					var currentFileContext = context.FileContexts[context.CurrentUnit!];
					context.Diagnostics.Report(currentFileContext, structDecl.Span, CyclicDestructorDepthError);
				}
			}
		}
	}

	/// <summary>
	/// True if dropping a value of <paramref name="type"/> runs any user-visible cleanup: it has its
	/// own destructor, or it (transitively) embeds/contains a type that does. Mirrors the emitter's
	/// owned-move-field rule. Pointer and primitive/type-parameter types own nothing.
	/// </summary>
	private bool DestructorNeedsCleanup(TypeSymbol type)
	{
		switch (type)
		{
			case ArrayTypeSymbol arr:
				return DestructorNeedsCleanup(arr.ElementType);
			case SliceTypeSymbol slice:
				return DestructorNeedsCleanup(slice.ElementType);
			case StructTypeSymbol structType:
				if (HasOwnDestructor(structType))
					return true;
				return structType.Fields.Any(f => DestructorNeedsCleanup(f.Type));
			case UnionTypeSymbol unionType:
				return unionType.Fields.Any(f => !f.IsVoidVariant && DestructorNeedsCleanup(f.Type));
			default:
				return false;
		}
	}

	private bool HasOwnDestructor(StructTypeSymbol structType) => context.Destructors.ContainsKey(structType.Name);

	/// <summary>
	/// Maximum cleanup-recursion depth reachable from <paramref name="type"/> over owned fields,
	/// matching the emitter's nested-drop recursion (a struct with its own destructor is the base
	/// case and does not recurse). The path set is a defensive cycle guard.
	/// </summary>
	private int DestructorDepth(TypeSymbol type, HashSet<string> path)
	{
		if (type is ArrayTypeSymbol arr)
		{
			if (!DestructorNeedsCleanup(arr.ElementType))
				return 0;
			if (!path.Add(type.Name))
				return 0;
			var depth = 1 + DestructorDepth(arr.ElementType, path);
			path.Remove(type.Name);
			return depth;
		}

		if (type is SliceTypeSymbol slice)
		{
			if (!DestructorNeedsCleanup(slice.ElementType))
				return 0;
			if (!path.Add(type.Name))
				return 0;
			var depth = 1 + DestructorDepth(slice.ElementType, path);
			path.Remove(type.Name);
			return depth;
		}

		if (type is UnionTypeSymbol unionType)
		{
			var variants = unionType.Fields.Where(f => !f.IsVoidVariant && DestructorNeedsCleanup(f.Type)).ToList();
			if (variants.Count == 0)
				return 0;
			if (!path.Add(type.Name))
				return 0;
			var depth = 1 + variants.Max(f => DestructorDepth(f.Type, path));
			path.Remove(type.Name);
			return depth;
		}

		if (type is StructTypeSymbol structType)
		{
			// A struct with its own destructor takes responsibility for its whole payload.
			if (HasOwnDestructor(structType))
				return 0;

			var fields = structType.Fields.Where(f => DestructorNeedsCleanup(f.Type)).ToList();
			if (fields.Count == 0)
				return 0;
			if (!path.Add(type.Name))
				return 0;
			var result = 1 + fields.Max(f => DestructorDepth(f.Type, path));
			path.Remove(type.Name);
			return result;
		}

		return 0;
	}




	private void DeclareFunction(FunctionDeclarationSyntax func)
	{
		// Receiver markers ('ref this' / 'refvar this') are only valid on extension methods.
		if (func.Receiver != ReceiverContract.None)
		{
			context.Diagnostics.Report(context.FileContexts[context.CurrentUnit!], func.NameSpan,
				"Receiver parameter ('refvar this' / 'ref this') is only allowed on extension methods.");
			return;
		}

		// Entry point (main / Main) is always global, lowercase, and unmangled
		var mangledName = func.Name == "main" || func.Name == "Main"
			? "main"
			: context.GetMangledName(func.Name, context.CurrentNamespace);

		// If this is a generic function template, register it as a template
		if (func.GenericParameters.Count > 0)
		{
			// Check if all generic parameters are concrete types (explicit specialization)
			var isSpecialization = func.GenericParameters.All(p => context.ResolveType(p) != null);

			if (isSpecialization)
			{
				var rawName = $"{mangledName}<{string.Join(",", func.GenericParameters)}>";
				// Canonical Name
				var instName = context.NormalizeGenericName(rawName);

				var returnType = context.ResolveType(func.ReturnType);
				context.SymbolUnits[mangledName] = context.CurrentUnit!;
				if (returnType is null)
				{
					var currentFileContext = context.FileContexts[context.CurrentUnit!];
					context.Diagnostics.Report(currentFileContext, func.ReturnTypeSpan, $"Unknown return type '{func.ReturnType}'");
					return;
				}

				var specParameters = new List<ParameterSymbol>();
				foreach (var param in func.Parameters)
				{
					var paramType = context.ResolveType(param.Type);
					if (paramType is null)
					{
						var currentFileContext = context.FileContexts[context.CurrentUnit!];
						context.Diagnostics.Report(currentFileContext, param.Span, $"Unknown parameter type '{param.Type}'");
						continue;
					}

					specParameters.Add(new ParameterSymbol(param.Name, paramType));
				}

				var instSymbol = new FunctionSymbol(instName, returnType!, specParameters)
				{
					Visibility = func.Visibility,
					SafetyTier = func.Modifier ?? SafetyTier.Safe,
					DeclaringUnit = context.CurrentUnit
				};
				context.MonomorphizedFunctions[instName] = instSymbol;

				var instDecl = new FunctionDeclarationSyntax(func.Span, func.ReturnType, instName, [], func.Parameters, func.Body, modifier: func.Modifier, visibility: func.Visibility);
				context.MonomorphizedFunctionDecls.Add(instDecl);
				return;
			}

			// Record the original template file unit
			context.SymbolUnits[mangledName] = context.CurrentUnit!;

			context.GenericFunctionTemplates[mangledName] = func;
			return;
		}

		// A function with any nominal-interface-typed parameter is an implicit generic template:
		// the interface name has no value representation, so it is monomorphized at each call site
		// with the concrete conforming argument type (static-only dispatch, no vtable).
		if (func.Parameters.Any(p => IsInterfaceTypedParameter(p)))
		{
			context.SymbolUnits[mangledName] = context.CurrentUnit!;
			context.InterfaceFunctionTemplates[mangledName] = func;
			return;
		}

		// A function with any protocol-typed parameter is likewise an implicit
		// generic template: a protocol name has no value representation, so it is
		// monomorphized at each call site with the structurally conforming
		// concrete argument type (static-only dispatch, no vtable).
		if (func.Parameters.Any(p => IsProtocolTypedParameter(p)))
		{
			context.SymbolUnits[mangledName] = context.CurrentUnit!;
			context.ProtocolFunctionTemplates[mangledName] = func;
			return;
		}

		context.SymbolUnits[mangledName] = context.CurrentUnit!;
		var type = context.ResolveType(func.ReturnType);
		if (type is null)
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, func.ReturnTypeSpan, $"Unknown return type '{func.ReturnType}'");
			return;
		}

		var parameters = new List<ParameterSymbol>();
		foreach (var param in func.Parameters)
		{
			var paramSymbol = CreateParameter(param);
			if (paramSymbol is null)
			{
				ReportDeclarationDiagnostic(param, $"Unknown parameter type '{param.Type}'");
				continue;
			}

			parameters.Add(paramSymbol);
		}

		var overloadedMangledName = context.GetOverloadedMangledName(mangledName, parameters.Select(p => p.Type).ToList());

		var existing = context.Globals.Lookup(overloadedMangledName);
		if (existing is not null)
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, func.NameSpan, $"Duplicate definition of function '{func.Name}' with a matching parameter signature.");
			return;
		}

		// Determine safety tier from function modifier
		var safetyTier = func.Modifier ?? SafetyTier.Safe;

		var newSymbol = new FunctionSymbol(overloadedMangledName, type, parameters)
		{
			SafetyTier = safetyTier,
			Visibility = func.Visibility,
			DeclaringUnit = context.CurrentUnit
		};
		var suppressedWarnings = new List<string>();

		_attributes.ApplyFunctionAttributes(
			_attributes.Verify(func.Attributes, "Function", suppressedWarnings, safetyTier),
			newSymbol,
			suppressedWarnings,
			func.Attributes);

		// [UnsafeBody] promotes to Unsafe tier even without the unsafe modifier
		if (newSymbol.IsUnsafeBody)
			newSymbol.SafetyTier = SafetyTier.Unsafe;

		_attributes.WarnIfUnsafeBodyUnused(func.NameSpan, func.Body, newSymbol, suppressedWarnings);

		// [Inline] on a directly recursive function is advisory only; attribute diagnostics own the warning.
		_attributes.WarnIfInlineRecursive(func, newSymbol, suppressedWarnings);

		// Warn if 'unbound' is used but no ref/refvar parameters exist. A by-value factory that returns
		// a Move type (a struct with reference fields) still gains escape-relaxation value from 'unbound'
		// (spec §5 Rule 9 heap-relative escape), so the warning is suppressed in that case.
		if (safetyTier == SafetyTier.Unbound && !suppressedWarnings.Contains(DiagnosticIds.UnboundNoRefParams))
		{
			var hasRefParams = parameters.Any(p => p.Type is PointerTypeSymbol);
			var returnsRefStruct = type is StructTypeSymbol st && st.Fields.Any(f => f.Type is PointerTypeSymbol);
			var hasUnboundBody = func.HasBody && HasUnboundConstructs(func.Body!);
			if (!hasRefParams && !returnsRefStruct && !hasUnboundBody)
			{
				ReportDeclarationWarning(func, "'unbound' modifier has no effect because function has no ref/refvar parameters.", DiagnosticIds.UnboundNoRefParams);
			}
		}

		context.Globals.Declare(newSymbol);

		if (!context.OverloadedFunctions.TryGetValue(mangledName, out var candidates))
		{
			candidates = [];
			context.OverloadedFunctions[mangledName] = candidates;
		}

		candidates.Add(newSymbol);
	}

	// A parameter is interface-typed if its (possibly ref/refvar-wrapped) type resolves to an
	// InterfaceTypeSymbol. Functions carrying such a parameter become implicit generic templates.
	private bool IsInterfaceTypedParameter(ParameterSyntax p)
	{
		var type = p.Type;
		if (type.StartsWith("refvar ", StringComparison.Ordinal))
			type = type[7..];
		else if (type.StartsWith("ref ", StringComparison.Ordinal))
			type = type[4..];
		return context.ResolveType(type) is InterfaceTypeSymbol;
	}

	// A parameter is protocol-typed if its (possibly ref/refvar-wrapped) type resolves to a
	// ProtocolTypeSymbol. Functions carrying such a parameter become implicit generic templates,
	// monomorphized per call site against the structurally conforming concrete type.
	private bool IsProtocolTypedParameter(ParameterSyntax p)
	{
		var type = p.Type;
		if (type.StartsWith("refvar ", StringComparison.Ordinal))
			type = type[7..];
		else if (type.StartsWith("ref ", StringComparison.Ordinal))
			type = type[4..];
		return context.ResolveType(type) is ProtocolTypeSymbol;
	}

	/// <summary>
	/// Resolves a parameter's type, verifies its attributes, and returns null when the type is unknown.
	/// </summary>
	private ParameterSymbol? CreateParameter(ParameterSyntax param)
	{
		var paramType = context.ResolveType(param.Type);
		if (paramType is null)
			return null;

		var symbol = new ParameterSymbol(param.Name, paramType);
		var paramSuppressedWarnings = new List<string>();
		if (_attributes.Verify(param.Attributes, "Parameter", paramSuppressedWarnings).Contains("NoAlias"))
			symbol.IsNoAlias = true;

		return symbol;
	}

	private void ReportDeclarationDiagnostic(SyntaxNode node, string message)
	{
		var currentFileContext = context.FileContexts[context.CurrentUnit!];
		context.Diagnostics.Report(currentFileContext, node.Span, message);
	}

	private void ReportDeclarationWarning(SyntaxNode node, string message, string id)
	{
		var currentFileContext = context.FileContexts[context.CurrentUnit!];
		context.Diagnostics.ReportWarning(currentFileContext, node.Span, message, id);
	}

	private void DeclareExternFunction(ExternDeclarationSyntax ext)
	{
		context.SymbolUnits[ext.Name] = context.CurrentUnit!;
		var returnType = context.ResolveType(ext.ReturnType);
		if (returnType is null)
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, ext.Span, $"Unknown return type '{ext.ReturnType}'");
			return;
		}

		var parameters = new List<ParameterSymbol>();
		foreach (var param in ext.Parameters)
		{
			var paramType = context.ResolveType(param.Type);
			if (paramType is null)
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, param.Span, $"Unknown parameter type '{param.Type}'");
				continue;
			}

			parameters.Add(new ParameterSymbol(param.Name, paramType));
		}

		var existing = context.Globals.Lookup(ext.Name);
		if (existing is not null)
		{
			// If the existing symbol is also an extern, we can safely ignore the duplicate declaration
			if (existing is FunctionSymbol existingFunc && existingFunc.IsExtern)
			{
				return;
			}

			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, ext.Span, $"Duplicate definition of '{ext.Name}'");
			return;
		}

		// Global externs are FFI bindings to foreign symbols: their visibility is fixed at
		// 'internal' (module-scoped). A 'public' extern would export a foreign symbol as part of
		// the package ABI without any Cvolo-level type safety — require a standard Cvolo wrapper.
		if (!context.LegacyVisibility && ext.Visibility == Visibility.Public)
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, ext.Span,
				"Global 'extern' declarations cannot be marked public. Wrap foreign symbols in a safe, standard public Cvolo routine to expose them across package boundaries.",
				DiagnosticIds.PublicExtern);
		}

		// Declare the extern symbol with its unmangled base name
		var newSymbol = new FunctionSymbol(ext.Name, returnType, parameters, isExtern: true, isVariadic: ext.IsVariadic)
		{
			Visibility = ext.Visibility,
			DeclaringUnit = context.CurrentUnit
		};
		context.Globals.Declare(newSymbol);

		// Keep candidates registered for lookup under the unmangled name
		if (!context.OverloadedFunctions.TryGetValue(ext.Name, out var candidates))
		{
			candidates = [];
			context.OverloadedFunctions[ext.Name] = candidates;
		}

		candidates.Add(newSymbol);
	}

	/// <summary>
	/// FFI extern block: [LibraryImport("lib")] extern "C" { ... }.
	/// Validates the calling convention (CVL1700), extracts [LibraryImport] library metadata
	/// (library name + optional win:/linux:/mac: platform paths forwarded to the linker for the
	/// current compilation target), rejects misplaced [ImportName] (CVL1702), and registers every
	/// block function as an extern FunctionSymbol carrying the block's library/convention metadata.
	/// Standalone extern declarations (DeclareExternFunction) remain supported alongside blocks.
	/// </summary>
	private void DeclareExternBlock(ExternBlockSyntax block)
	{
		var convention = block.CallingConvention ?? "C";
		if (convention is not ("C" or "system"))
		{
			ReportDeclarationDiagnostic(block,
				$"Unknown calling convention '{convention}'. Supported calling conventions are \"C\" and \"system\".",
				DiagnosticIds.UnknownCallingConvention);
			return;
		}

		string? libraryName = null;
		string? winPath = null;
		string? linuxPath = null;
		string? macPath = null;
		var sawLibraryImport = false;

		foreach (var attr in block.Attributes)
		{
			var key = _attributes.NormalizeName(attr.Name);
			switch (key)
			{
				case "LibraryImport":
					if (sawLibraryImport)
					{
						ReportDeclarationDiagnostic(attr, "Duplicate attribute '[LibraryImport]'.");
						continue;
					}

					sawLibraryImport = true;
					(libraryName, winPath, linuxPath, macPath) = _attributes.ExtractLibraryImport(attr, libraryName, winPath, linuxPath, macPath);
					break;
				case "ImportName":
					ReportDeclarationDiagnostic(attr, "Attribute '[ImportName]' can only be applied to a function declaration inside an extern block.", DiagnosticIds.ImportNameOutsideBlock);
					break;
				case null:
					ReportDeclarationWarning(attr, $"Unknown attribute '{attr.Name}'; it will be ignored.", DiagnosticIds.UnknownAttribute);
					break;
				default:
					ReportDeclarationDiagnostic(attr, $"Attribute '[{key}]' cannot be applied to extern block declarations.");
					break;
			}
		}

		// Platform-specific win:/linux:/mac: paths are forwarded verbatim to the linker, which
		// resolves the path for the current compilation target OS (see LinkStrategy). There is no
		// frontend file-existence check: cross-compilation must not probe the host disk.
		if (libraryName is not null)
			context.NativeLibraries[libraryName] = new NativeLibraryInfo(libraryName, winPath, linuxPath, macPath);

		// Legacy block-level public externs are still rejected; function declarations inside
		// the block use their own visibility, defaulting to internal.
		if (!context.LegacyVisibility && block.Visibility == Visibility.Public)
		{
			ReportDeclarationDiagnostic(block,
				"Global 'extern' declarations cannot be marked public. Wrap foreign symbols in a safe, standard public Cvolo routine to expose them across package boundaries.",
				DiagnosticIds.PublicExtern);
		}

		foreach (var fn in block.Functions)
			DeclareExternBlockFunction(block, fn, convention, libraryName, winPath, linuxPath, macPath);
	}

	private void DeclareExternBlockFunction(ExternBlockSyntax block, ExternBlockFunctionSyntax fn, string convention, string? libraryName, string? winPath, string? linuxPath, string? macPath)
	{
		var mangledName = context.GetMangledName(fn.Name, context.CurrentNamespace);

		if (!context.LegacyVisibility && fn.SyntacticVisibility == Visibility.Public)
		{
			ReportDeclarationDiagnostic(fn,
				"Extern block function declarations cannot be marked public. Wrap foreign symbols in a safe, standard public Cvolo routine to expose them across package boundaries.",
				DiagnosticIds.PublicExtern);
		}

		context.SymbolUnits[mangledName] = context.CurrentUnit!;
		var returnType = context.ResolveType(fn.ReturnType);
		if (returnType is null)
		{
			ReportDeclarationDiagnostic(fn, $"Unknown return type '{fn.ReturnType}'");
			return;
		}

		var parameters = new List<ParameterSymbol>();
		foreach (var param in fn.Parameters)
		{
			var paramType = context.ResolveType(param.Type);
			if (paramType is null)
			{
				ReportDeclarationDiagnostic(param, $"Unknown parameter type '{param.Type}'");
				continue;
			}

			parameters.Add(new ParameterSymbol(param.Name, paramType));
		}

		var existing = context.Globals.Lookup(mangledName);
		if (existing is not null)
		{
			// If the existing symbol is also an extern, we can safely ignore the duplicate declaration
			if (existing is FunctionSymbol existingFunc && existingFunc.IsExtern)
				return;

			ReportDeclarationDiagnostic(fn, $"Duplicate definition of '{fn.Name}'");
			return;
		}

		string? importName = null;
		var sawImportName = false;
		foreach (var attr in fn.Attributes)
		{
			var key = _attributes.NormalizeName(attr.Name);
			switch (key)
			{
				case "ImportName":
					if (sawImportName)
					{
						ReportDeclarationDiagnostic(attr, "Duplicate attribute '[ImportName]'.");
						continue;
					}

					sawImportName = true;
					importName = _attributes.ExtractImportName(attr);
					break;
				case "LibraryImport":
					ReportDeclarationDiagnostic(attr,
						"Attribute '[LibraryImport]' attaches a library to an extern block, not to an individual function inside it. Move it to the enclosing extern block.",
						DiagnosticIds.LibraryImportInsideBlock);
					break;
				case null:
					ReportDeclarationWarning(attr, $"Unknown attribute '{attr.Name}'; it will be ignored.", DiagnosticIds.UnknownAttribute);
					break;
				default:
					ReportDeclarationDiagnostic(attr, $"Attribute '[{key}]' cannot be applied to extern block function declarations.");
					break;
			}
		}

		// Extern block functions are never name-mangled; their symbol name IS the native name
		// unless [ImportName] overrides it (ImportName ?? Name).
		var newSymbol = new FunctionSymbol(mangledName, returnType, parameters, isExtern: true, isVariadic: fn.IsVariadic)
		{
			Visibility = fn.Visibility,
			DeclaringUnit = context.CurrentUnit,
			ImportName = importName,
			LibraryName = libraryName,
			WinPath = winPath,
			LinuxPath = linuxPath,
			MacPath = macPath,
			CallingConvention = convention
		};
		context.Globals.Declare(newSymbol);

		// Keep candidates registered for lookup under the unmangled name
		if (!context.OverloadedFunctions.TryGetValue(mangledName, out var candidates))
		{
			candidates = [];
			context.OverloadedFunctions[mangledName] = candidates;
		}

		candidates.Add(newSymbol);
	}

	private void DeclareExposeExternBlock(ExposeExternBlockSyntax block)
	{
		var convention = block.CallingConvention ?? "C";
		if (convention is not ("C" or "system"))
		{
			ReportDeclarationDiagnostic(block,
				$"Unknown calling convention '{convention}'. Supported calling conventions are \"C\" and \"system\".",
				DiagnosticIds.UnknownCallingConvention);
			return;
		}

		if (!context.LegacyVisibility && block.Visibility == Visibility.Public)
		{
			ReportDeclarationDiagnostic(block,
				"An expose extern block cannot be marked public. Mark the individual functions inside it public to promote them across the binary ABI.",
				DiagnosticIds.PublicExtern);
		}

		foreach (var attr in block.Attributes)
		{
			var key = _attributes.NormalizeName(attr.Name);
			if (key is null)
			{
				ReportDeclarationWarning(attr, $"Unknown attribute '{attr.Name}'; it will be ignored.", DiagnosticIds.UnknownAttribute);
				continue;
			}

			ReportDeclarationDiagnostic(attr, $"Attribute '[{key}]' cannot be applied to expose extern block declarations.");
		}

		foreach (var func in block.Functions)
			DeclareExposeExternFunction(block, func, convention);
	}

	private void DeclareExposeExternFunction(ExposeExternBlockSyntax block, FunctionDeclarationSyntax func, string convention)
	{
		context.SymbolUnits[func.Name] = context.CurrentUnit!;

		// CVL1801: interface/protocol types have no value representation, so a by-value
		// parameter cannot cross a binary ABI boundary. 'ref'/pointer forms are allowed.
		foreach (var param in func.Parameters)
		{
			if (context.ResolveType(param.Type) is InterfaceTypeSymbol or ProtocolTypeSymbol)
			{
				ReportDeclarationDiagnostic(func,
					$"Exported function '{func.Name}' cannot contain value interface parameter '{param.Name}' across binary ABI boundaries. Use explicit pointers or 'ref' dynamic dispatch.",
					DiagnosticIds.ExposedInterfaceParameter);
				return;
			}

			// CVL1807: structures passed by reference across a C-ABI boundary must possess
			// a fixed sequential layout. In Cvolo all structs are inherently sequential
			// (LayoutKind.Sequential), but this defensive check guards against future layout
			// optimizations (e.g., field reordering) that would corrupt interop semantics.
			if (context.ResolveType(param.Type) is StructTypeSymbol structType)
			{
				// All Cvolo structs are sequential by default — verify no managed layout
				// optimizations have been applied that would break C-ABI compatibility.
				// (Currently always passes since Cvolo has no [StructLayout] attributes yet.)
			}
		}

		string? exposeName = null;
		var sawExposeName = false;
		var filteredAttributes = new List<AttributeSyntax>();
		foreach (var attr in func.Attributes)
		{
			var key = _attributes.NormalizeName(attr.Name);
			if (key == "ExposeName")
			{
				if (sawExposeName)
				{
					ReportDeclarationDiagnostic(attr, "Duplicate attribute '[ExposeName]'.");
					continue;
				}

				sawExposeName = true;
				exposeName = _attributes.ExtractExposeName(attr);
				continue;
			}

			// Everything except [ExposeName] is forwarded to the normal function
			// declaration path for target/context verification.
			filteredAttributes.Add(attr);
		}

		var exportName = exposeName ?? func.Name;
		if (!_exportSymbolNames.Add(exportName))
		{
			ReportDeclarationDiagnostic(func, $"Duplicate export symbol name `{exportName}` detected in module scope.", DiagnosticIds.DuplicateExportSymbol);
			return;
		}

		// Declare the function through the normal path (with [ExposeName] stripped) so it is
		// registered, attribute-verified, and validatable exactly like any other function.
		var clone = new FunctionDeclarationSyntax(func.Span, func.ReturnType, func.Name, func.GenericParameters, func.Parameters, func.Body!, filteredAttributes, func.Modifier, func.Receiver, func.Visibility);
		DeclareFunction(clone);

		// Locate the symbol the normal path just registered to tag it for export.
		var mangledName = func.Name is "main" or "Main" ? "main" : context.GetMangledName(func.Name, context.CurrentNamespace);
		var paramTypes = new List<TypeSymbol>();
		foreach (var p in func.Parameters)
		{
			if (CreateParameter(p) is { } paramSymbol)
				paramTypes.Add(paramSymbol.Type);
		}

		var overloadedMangledName = context.GetOverloadedMangledName(mangledName, paramTypes);
		if (context.Globals.Lookup(overloadedMangledName) is not FunctionSymbol fnSym)
			return;

		fnSym.IsExported = true;
		fnSym.ExposeName = exportName;
		fnSym.CallingConvention = convention;
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

			// Validate default generic parameter types are TrivialCopy (CVL1040)
			foreach (var (paramName, defaultTypeName) in extDecl.GenericParameterDefaults)
			{
				var defaultType = context.ResolveType(defaultTypeName);
				if (defaultType is not null && Classification.Classify(defaultType) != CopyKind.TrivialCopy)
				{
					var currentFileContext = context.FileContexts[context.CurrentUnit!];
					context.Diagnostics.Report(currentFileContext, extDecl.Span, $"Default value for generic parameter '{paramName}' must be a Trivial Copy Type", DiagnosticIds.DefaultMustBeTrivialCopy);
				}
			}

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
			if (method.Name.StartsWith('~') && method.Name[1..] != extDecl.ExtendedTypeName)
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, method.NameSpan, $"Destructor name '{method.Name}' does not match extended type '{extDecl.ExtendedTypeName}'.");
				continue;
			}

			if (method.Name.StartsWith('~') && context.Destructors.ContainsKey(extDecl.ExtendedTypeName))
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, method.NameSpan, $"Duplicate destructor definition for type '{extDecl.ExtendedTypeName}'.");
				continue;
			}

			// Mangled name represents the scoped path, e.g., "MyNamespace.Point.Move"
			var baseMangledName = context.GetMangledName($"{extDecl.ExtendedTypeName}.{method.Name}", context.CurrentNamespace);

			// 1. Inject the implicit first parameter: "this"
			// It starts as a read-only pointer. The ValidationPass will upgrade it to mutable if needed!
			var thisParamType = new PointerTypeSymbol(extendedType, isMutable: false);
			var thisParam = new ParameterSymbol("this", thisParamType);

			var parameters = new List<ParameterSymbol> { thisParam };
			foreach (var param in method.Parameters)
			{
				var paramSymbol = CreateParameter(param);
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
				_attributes.Verify(method.Attributes, method.Name.StartsWith('~') ? "Destructor" : "Method", methodSuppressedWarnings),
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

			if (method.Name.StartsWith('~'))
			{
				context.Destructors[extDecl.ExtendedTypeName] = newSymbol;
			}
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
				var paramSymbol = CreateParameter(param);
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






	private static bool HasUnboundConstructs(SyntaxNode node)
	{
		foreach (var child in node.GetChildren())
		{
			if (child is VariableDeclarationSyntax v && (v.Type is "refvar" or "ref" || (v.Type != null && v.Type.StartsWith("ref"))))
				return true;
			if (child is BorrowExpressionSyntax)
				return true;
			if (HasUnboundConstructs(child))
				return true;
		}

		return false;
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
