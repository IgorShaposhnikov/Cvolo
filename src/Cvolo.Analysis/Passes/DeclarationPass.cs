using Cvolo.Analysis.Symbols;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Collections;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using Cvolo.Core.AST.Directives;
using Cvolo.Core.AST.Expressions;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Analysis.Passes;

public sealed class DeclarationPass(BindingContext context)
{
	// M1 attribute model: only System.* intrinsics exist. Their [AttributeUsage]-style rules
	// (syntactic target x safety context, per spec section 4) are modeled compiler-side until
	// the language has enums/inheritance to declare them in source. Attributes are erased
	// before emission - they never reach LLVM IR.
	private static readonly Dictionary<string, (string[] Targets, SafetyTier[] Contexts)> IntrinsicAttributes = new()
	{
		["UnsafeBody"] = (["Function", "Method", "Constructor", "Destructor"], [SafetyTier.Safe, SafetyTier.Unbound, SafetyTier.Unsafe]),
		["NoAlias"] = (["Function", "Method", "Parameter"], [SafetyTier.Unbound, SafetyTier.Unsafe]),
		["SuppressWarning"] = (["Struct", "Function", "Method", "Constructor", "Destructor", "Parameter"], [SafetyTier.Safe, SafetyTier.Unbound, SafetyTier.Unsafe]),
		["Flags"] = (["Struct"], [SafetyTier.Safe, SafetyTier.Unbound, SafetyTier.Unsafe]),
		["NonExhaustive"] = (["Struct"], [SafetyTier.Safe, SafetyTier.Unbound, SafetyTier.Unsafe]),
		["StrictMutability"] = (["Struct"], [SafetyTier.Safe, SafetyTier.Unbound, SafetyTier.Unsafe]),
		["Intrinsic"] = (["Function", "Method"], [SafetyTier.Safe, SafetyTier.Unbound, SafetyTier.Unsafe]),
	};

	private static readonly HashSet<string> KnownWarningIds =
	[
		DiagnosticIds.UnsafeBodyNoEffect, DiagnosticIds.UnknownAttribute, DiagnosticIds.UnboundNoRefParams, DiagnosticIds.AutoInferMutationWarning
	];

	// E1 enum underlying storage types (§1.A). Enums are strictly flat, unmanaged
	// scalar integers. 'char' is allowed as a 1-byte storage type.
	private static readonly HashSet<string> AllowedEnumStorageTypes =
	[
		"int", "uint", "short", "ushort", "long", "ulong", "char", "byte", "sbyte", "nint", "nuint"
	];

	// Memory & Safety spec §2: the destructor nesting depth is capped. Dropping a value of a
	// deeply nested (by-value) move type would recurse once per nested owner; past this bound we
	// refuse to compile rather than risk unbounded cleanup recursion.
	private const int MaxDestructorNestingDepth = 1024;

	private const string CyclicDestructorDepthError =
		"Cyclic destructor nesting depth exceeded. Please use an arena allocator or manual cleanup.";

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

		// Pass 0a: Register all Struct/Union/Interface/Protocol raw symbols across all files
		foreach (var unit in units)
		{
			context.CurrentUnit = unit;
			context.CurrentNamespace = unit.NamespaceDeclaration?.Name;

			var members = context.CurrentNamespace != null ? unit.NamespaceDeclaration!.Members : unit.Members;
			foreach (var member in members)
			{
				if (member is StructDeclarationSyntax structDecl)
					DeclareStruct(structDecl);
				else if (member is UnionDeclarationSyntax unionDecl)
					DeclareUnion(unionDecl);
				else if (member is InterfaceDeclarationSyntax interfaceDecl)
					DeclareInterface(interfaceDecl);
				else if (member is ProtocolDeclarationSyntax protocolDecl)
					DeclareProtocol(protocolDecl);
				else if (member is EnumDeclarationSyntax enumDecl)
					DeclareEnum(enumDecl);
			}
		}

		// Pass 0b: Link contract hierarchy (`:` base clauses) — validate bases,
		// compute protocol effective members (transitive closure), and rebuild the
		// protocol symbols with the expanded member set.
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

		// Pass 0c: Link `struct T embed Base` clauses — validate the embedded type,
		// detect cycles/generics, and rebuild struct symbols with the embedded
		// fields flattened at the FRONT of their layout.
		LinkEmbeds(units);

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
				else if (member is ExtensionDeclarationSyntax extDecl)
					DeclareExtension(extDecl);
				else if (member is GlobalVariableDeclarationSyntax globalDecl)
					DeclareGlobalVariable(globalDecl);
			}
		}

		// Pass 1.5: Promote embedded-type extension methods onto every struct that
		// embeds them — `w.TakeDamage(20)` on a struct that `embed`s BaseEntity
		// resolves BaseEntity's extension with the outer struct as `this`.
		PromoteEmbeddedMethods(units);

		// Pass 2: Enforce the destructor nesting-depth limit (Memory & Safety §2). Run after every
		// struct symbol (embeds included) is fully materialized so the transitive ownership graph
		// is complete.
		CheckDestructorDepth(units);
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

	/// <summary>
	/// Pass 0c. Flatten `struct T embed Base` compositions: every field of Base
	/// (recursively, chains included) is prepended to T's own fields so the LLVM
	/// layout, field lookup, struct literals and byte size all treat them as T's
	/// own. Generic struct templates cannot be embedded (their fields are not
	/// materialized as symbols) — rejected with a diagnostic.
	/// </summary>
	private void LinkEmbeds(IEnumerable<CompilationUnitSyntax> units)
	{
		var structDecls = new Dictionary<string, StructDeclarationSyntax>();
		foreach (var unit in units)
		{
			var members = unit.NamespaceDeclaration is not null ? unit.NamespaceDeclaration.Members : unit.Members;
			foreach (var member in members)
			{
				if (member is StructDeclarationSyntax structDecl)
					structDecls[context.GetMangledName(structDecl.Name, unit.NamespaceDeclaration?.Name)] = structDecl;
			}
		}

		var flattened = new Dictionary<string, List<StructFieldSymbol>>();
		foreach (var (mangledName, decl) in structDecls)
		{
			if (decl.EmbeddedType is null)
				continue;

			FlattenStructFields(mangledName, decl, structDecls, flattened, new HashSet<string>());
		}
	}

	private List<StructFieldSymbol> FlattenStructFields(
		string mangledName,
		StructDeclarationSyntax decl,
		Dictionary<string, StructDeclarationSyntax> structDecls,
		Dictionary<string, List<StructFieldSymbol>> flattened,
		HashSet<string> stack)
	{
		if (flattened.TryGetValue(mangledName, out var cached))
			return cached;

		var currentFileContext = context.FileContexts[context.CurrentUnit!];

		var ownFields = context.StructTypes[mangledName].Fields.ToList();
		if (decl.EmbeddedType is null)
		{
			flattened[mangledName] = ownFields;
			return ownFields;
		}

		if (context.GenericStructTemplates.ContainsKey(mangledName))
		{
			context.Diagnostics.Report(currentFileContext, decl.Span, $"Cannot use embed in generic struct template '{decl.Name}'.");
			flattened[mangledName] = ownFields;
			return ownFields;
		}

		var embeddedName = decl.EmbeddedType;
		var baseType = context.ResolveType(embeddedName) as StructTypeSymbol;
		if (baseType is null || !structDecls.ContainsKey(baseType.Name))
		{
			context.Diagnostics.Report(currentFileContext, decl.Span, $"Unknown struct '{embeddedName}' in embed clause of struct '{decl.Name}'.");
			flattened[mangledName] = ownFields;
			return ownFields;
		}

		if (context.GenericStructTemplates.ContainsKey(baseType.Name))
		{
			context.Diagnostics.Report(currentFileContext, decl.Span, $"Cannot embed generic struct template '{embeddedName}' in struct '{decl.Name}'.");
			flattened[mangledName] = ownFields;
			return ownFields;
		}

		if (!stack.Add(baseType.Name))
		{
			context.Diagnostics.Report(currentFileContext, decl.Span, $"Circular embed clause involving struct '{decl.Name}'.");
			flattened[mangledName] = ownFields;
			return ownFields;
		}

		var baseDecl = structDecls[baseType.Name];
		var baseFields = FlattenStructFields(baseType.Name, baseDecl, structDecls, flattened, stack);
		stack.Remove(baseType.Name);

		var conflict = ownFields.FirstOrDefault(f => baseFields.Any(b => b.Name == f.Name));
		if (conflict is not null)
		{
			context.Diagnostics.Report(currentFileContext, decl.Span,
				$"Field '{conflict.Name}' of struct '{decl.Name}' conflicts with embedded field from '{embeddedName}'.");
			flattened[mangledName] = ownFields;
			return ownFields;
		}

		var combined = new List<StructFieldSymbol>(baseFields.Count + ownFields.Count);
		combined.AddRange(baseFields);
		combined.AddRange(ownFields);

		var rebuiltEmbed = baseType; // the (already flattened) embedded composition
		var rebuilt = new StructTypeSymbol(mangledName, combined, rebuiltEmbed)
		{
			Visibility = decl.Visibility
		};
		context.StructTypes[mangledName] = rebuilt;
		context.ReplaceTypeInCache(mangledName, rebuilt);
		flattened[mangledName] = combined;
		return combined;
	}

	/// <summary>
	/// Pass 1.5. For every struct that embeds another type, register carbon copies
	/// of the embedded type's extension methods as the outer struct's own — the
	/// body is validated against the outer's flat fields and emitted with the outer
	/// as `this` (layout prefix offset is zero, so field GEPs stay identical).
	/// A method the outer already declares wins (collision-rule parity). This makes
	/// promoted methods satisfy non-nominal protocols implicitly; nominal interface
	/// markers stay non-transitive because Conformance is only ever filled by
	/// explicit `extension T : I` blocks.
	/// </summary>
	private void PromoteEmbeddedMethods(IEnumerable<CompilationUnitSyntax> units)
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

	/// <summary>
	/// Registers a structural type (struct) into the binding context.
	/// Creates the symbol representation, handles generic templates,
	/// and applies intrinsic attributes like [StrictMutability].
	/// </summary>
	/// <param name="structDecl">The syntax node for the struct declaration.</param>
	private void DeclareStruct(StructDeclarationSyntax structDecl)
	{
		var mangledName = context.GetMangledName(structDecl.Name, context.CurrentNamespace);

		if (context.StructTypes.ContainsKey(mangledName) || TypeSymbol.FromName(structDecl.Name) is not null)
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, structDecl.Span, $"Duplicate type definition '{structDecl.Name}'");
			return;
		}

		var appliedAttrs = VerifyAttributes(structDecl.Attributes, "Struct", new List<string>());

		// If this is a generic struct template (e.g. struct Point<T>)
		if (structDecl.GenericParameters.Count > 0)
		{
			context.SymbolUnits[mangledName] = context.CurrentUnit!;
			context.GenericStructTemplates[mangledName] = structDecl;

			var placeholderFields = new List<StructFieldSymbol>();

			var templateSymbol = new StructTypeSymbol(mangledName, placeholderFields)
			{
				IsStrictMutability = appliedAttrs.Contains("StrictMutability"),
				Visibility = structDecl.Visibility
			};

			context.StructTypes[mangledName] = templateSymbol;
			return;
		}

		context.SymbolUnits[mangledName] = context.CurrentUnit!;

		// 1. Register a placeholder symbol BEFORE resolving fields so that
		// self-referential field types (e.g. `Option<ref Node>`) can resolve
		// the enclosing struct's own name during generic instantiation.
		var placeholder = new StructTypeSymbol(mangledName, [])
		{
			Visibility = structDecl.Visibility
		};
		context.StructTypes[mangledName] = placeholder;

		var fields = new List<StructFieldSymbol>();
		var fieldNames = new HashSet<string>();

		foreach (var field in structDecl.Fields)
		{
			if (!fieldNames.Add(field.Name))
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, field.Span, $"Duplicate field '{field.Name}' in struct '{structDecl.Name}'");
				continue;
			}

			var fieldType = context.ResolveType(field.Type);
			if (fieldType is null)
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, field.Span, $"Unknown type '{field.Type}' of field '{field.Name}'");
				continue;
			}

			fields.Add(new StructFieldSymbol(field.Name, fieldType)
			{
				Visibility = field.Visibility
			});
		}

		// 2. Populate the placeholder IN PLACE rather than creating a new object.
		// Reference field types resolved during field population captured a PointerTypeSymbol
		// whose ReferencedType is this placeholder; mutating its Fields keeps those references valid.
		placeholder.PopulateFields(fields);
		placeholder.IsStrictMutability = appliedAttrs.Contains("StrictMutability");

		context.ReplaceTypeInCache(mangledName, placeholder);
	}

	private void DeclareInterface(InterfaceDeclarationSyntax interfaceDecl)
	{
		var mangledName = context.GetMangledName(interfaceDecl.Name, context.CurrentNamespace);

		if (context.InterfaceTypes.ContainsKey(mangledName))
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, interfaceDecl.Span, $"Duplicate interface definition '{interfaceDecl.Name}'");
			return;
		}

		context.SymbolUnits[mangledName] = context.CurrentUnit!;
		context.InterfaceTemplates[mangledName] = interfaceDecl;
		context.InterfaceTypes[mangledName] = new InterfaceTypeSymbol(mangledName)
		{
			Visibility = interfaceDecl.Visibility
		};
	}

	private void DeclareProtocol(ProtocolDeclarationSyntax protocolDecl)
	{
		var mangledName = context.GetMangledName(protocolDecl.Name, context.CurrentNamespace);

		if (context.ProtocolTypes.ContainsKey(mangledName))
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, protocolDecl.Span, $"Duplicate protocol definition '{protocolDecl.Name}'");
			return;
		}

		context.SymbolUnits[mangledName] = context.CurrentUnit!;
		context.ProtocolTemplates[mangledName] = protocolDecl;

		// Phase-1 structural pre-match: build the canonical member tokens once,
		// resolved in this protocol's namespace, so conformance checks are O(1)
		// set membership (topological, naming-independent) rather than symbolic.
		var canonicalMembers = ProtocolCanonicalizer.BuildCanonicalMembers(protocolDecl, context);
		context.ProtocolTypes[mangledName] = new ProtocolTypeSymbol(mangledName, protocolDecl.Members, protocolDecl.GenericParameters, protocolDecl.Constraint, canonicalMembers)
		{
			Visibility = protocolDecl.Visibility
		};
	}

	/// <summary>
	/// Pass 0b protocol linking: validates `:` base clauses and computes the
	/// protocol's effective member list (its own members plus the transitive
	/// closure of its protocol parents, with child overrides winning by name).
	/// The registered protocol symbol is rebuilt with the expanded member set so
	/// conformance checks, dispatch, and default lookup all see the full graph.
	/// </summary>
	private void LinkProtocol(ProtocolDeclarationSyntax protocolDecl)
	{
		var mangledName = context.GetMangledName(protocolDecl.Name, context.CurrentNamespace);
		var currentFileContext = context.FileContexts[context.CurrentUnit!];

		// Only protocols may be protocol bases; anything else is a declaration error.
		foreach (var baseName in protocolDecl.Bases)
		{
			if (context.ResolveType(baseName) is not ProtocolTypeSymbol)
			{
				context.Diagnostics.Report(currentFileContext, protocolDecl.Span,
					$"Unknown protocol '{baseName}' in base clause of protocol '{protocolDecl.Name}'.");
			}
		}

		var effective = new List<(string Owner, ProtocolMethodDeclarationSyntax Member)>();
		effective.AddRange(protocolDecl.Members.Select(m => (mangledName, m)));

		if (protocolDecl.Bases.Count > 0)
		{
			var visited = new HashSet<string>();
			var stack = new HashSet<string>();
			foreach (var baseName in protocolDecl.Bases)
				CollectProtocolBaseMembers(baseName, visited, stack, effective, protocolDecl.Span);
		}

		context.ProtocolEffectiveMembers[mangledName] = effective;

		// Rebuild the symbol: expanded members + canonical tokens (each member
		// canonicalized with its OWNER's generic parameters to preserve widths).
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
			mangledName, effective.Select(e => e.Member).ToList(), protocolDecl.GenericParameters, protocolDecl.Constraint, canonical)
		{
			Visibility = protocolDecl.Visibility
		};
	}

	private void CollectProtocolBaseMembers(
		string baseName, HashSet<string> visited, HashSet<string> stack,
		List<(string Owner, ProtocolMethodDeclarationSyntax Member)> effective, TextSpan span)
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
					if (effective.Any(e => e.Member.Name == member.Name))
						continue;
					effective.Add((protoBase.Name, member));
				}
			}
		}

		stack.Remove(protoBase.Name);
	}

	/// <summary>
	/// Pass 0b interface linking: validates `:` base clauses. Interface bases may
	/// be other interfaces or protocols; the effective member set is computed
	/// lazily during conformance registration (effective interface members).
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

	private void DeclareFunction(FunctionDeclarationSyntax func)
	{
		// Receiver markers ('ref this' / 'refvar this') are only valid on extension methods.
		if (func.Receiver != ReceiverContract.None)
		{
			context.Diagnostics.Report(context.FileContexts[context.CurrentUnit!], func.Span,
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
					context.Diagnostics.Report(currentFileContext, func.Span, $"Unknown return type '{func.ReturnType}'");
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
			context.Diagnostics.Report(currentFileContext, func.Span, $"Unknown return type '{func.ReturnType}'");
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
			context.Diagnostics.Report(currentFileContext, func.Span, $"Duplicate definition of function '{func.Name}' with a matching parameter signature.");
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

		ApplyFunctionAttributes(
			VerifyAttributes(func.Attributes, "Function", suppressedWarnings, safetyTier),
			newSymbol,
			suppressedWarnings,
			func.Attributes);

		// [UnsafeBody] promotes to Unsafe tier even without the unsafe modifier
		if (newSymbol.IsUnsafeBody)
			newSymbol.SafetyTier = SafetyTier.Unsafe;

		WarnIfUnsafeBodyUnused(func.Span, func.Body, newSymbol, suppressedWarnings);

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

	private static string? NormalizeAttributeName(string attributeName)
	{
		var simple = attributeName.Contains('.') ? attributeName[(attributeName.LastIndexOf('.') + 1)..] : attributeName;
		if (simple.EndsWith("Attribute", StringComparison.Ordinal))
			simple = simple[..^"Attribute".Length];

		return IntrinsicAttributes.ContainsKey(simple) ? simple : null;
	}

	/// <summary>Verifies each attribute against its syntactic attach point and returns the canonical keys that passed.</summary>
	private List<string> VerifyAttributes(IReadOnlyList<AttributeSyntax> attributes, string syntacticTarget, List<string>? suppressedWarnings = null, SafetyTier safetyTier = SafetyTier.Safe)
	{
		var applied = new List<string>();
		var seen = new HashSet<string>();
		var unknownAttributes = new List<AttributeSyntax>();
		foreach (var attr in attributes)
		{
			var key = NormalizeAttributeName(attr.Name);
			if (key is null)
			{
				// Hybrid stance: unknown names are accepted and erased at codegen, but
				// flagged so typos stay visible. Reported after the loop so a
				// [SuppressWarning] anywhere in the same list silences it regardless of order.
				unknownAttributes.Add(attr);
				continue;
			}

			if (!seen.Add(key))
			{
				ReportDeclarationDiagnostic(attr, $"Duplicate attribute '[{key}]'.");
				continue;
			}

			var (targets, contexts) = IntrinsicAttributes[key];
			if (!targets.Contains(syntacticTarget))
			{
				ReportDeclarationDiagnostic(attr, $"Attribute '[{key}]' cannot be applied to {syntacticTarget.ToLowerInvariant()} declarations.");
				continue;
			}

			if (!contexts.Contains(safetyTier))
			{
				ReportDeclarationDiagnostic(attr, $"Attribute '[{key}]' cannot be applied in {safetyTier} context.");
				continue;
			}

			if (key == "SuppressWarning")
			{
				ApplySuppressWarning(attr, suppressedWarnings);
				continue;
			}

			applied.Add(key);
		}

		foreach (var unknown in unknownAttributes)
		{
			if (suppressedWarnings?.Contains(DiagnosticIds.UnknownAttribute) == true)
				continue;

			ReportDeclarationWarning(unknown, $"Unknown attribute '{unknown.Name}'; it will be ignored.", DiagnosticIds.UnknownAttribute);
		}

		return applied;
	}

	private void ApplySuppressWarning(AttributeSyntax attr, List<string>? suppressedWarnings)
	{
		if (attr.Arguments.Count != 1 || attr.Arguments[0] is not StringLiteralExpressionSyntax literal)
		{
			ReportDeclarationDiagnostic(attr, "Attribute '[SuppressWarning]' requires exactly one string literal argument.");
			return;
		}

		var warningId = literal.Value;
		if (!KnownWarningIds.Contains(warningId))
		{
			ReportDeclarationDiagnostic(attr, $"Unknown warning id '{warningId}'.");
			return;
		}

		suppressedWarnings?.Add(warningId);
	}

	private static void ApplyFunctionAttributes(List<string> appliedKeys, FunctionSymbol symbol, List<string> suppressedWarnings, IReadOnlyList<AttributeSyntax>? attributes = null)
	{
		if (appliedKeys.Contains("UnsafeBody"))
			symbol.IsUnsafeBody = true;

		if (appliedKeys.Contains("NoAlias"))
			symbol.IsNoAlias = true;

		if (appliedKeys.Contains("Intrinsic") && attributes is not null)
		{
			var intrinsicAttr = attributes.FirstOrDefault(a => a.Name is "Intrinsic" or "System.Intrinsic" or "IntrinsicAttribute");
			if (intrinsicAttr?.Arguments.Count > 0 && intrinsicAttr.Arguments[0] is StringLiteralExpressionSyntax str)
			{
				symbol.IntrinsicName = str.Value;
			}
		}

		foreach (var warningId in suppressedWarnings)
			symbol.SuppressedWarnings.Add(warningId);
	}

	/// <summary>
	/// Flags [UnsafeBody] declarations whose bodies contain nothing unsafe; suppressible via [SuppressWarning].
	/// </summary>
	private void WarnIfUnsafeBodyUnused(TextSpan declarationSpan, SyntaxNode body, FunctionSymbol symbol, List<string> suppressedWarnings)
	{
		if (body is null)
			return;

		if (!symbol.IsUnsafeBody || suppressedWarnings.Contains(DiagnosticIds.UnsafeBodyNoEffect))
			return;

		if (UnsafeOperationScanner.ContainsUnsafeOperations(body))
			return;

		context.Diagnostics.ReportWarning(
			context.FileContexts[context.CurrentUnit!],
			declarationSpan,
			"'[UnsafeBody]' attribute has no effect because function contains no unsafe operations.",
			DiagnosticIds.UnsafeBodyNoEffect);
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
		if (VerifyAttributes(param.Attributes, "Parameter", paramSuppressedWarnings).Contains("NoAlias"))
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
			context.Diagnostics.Report(currentFileContext, extDecl.Span, $"Unknown type '{extDecl.ExtendedTypeName}' inside extension block.");
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
				context.Diagnostics.Report(currentFileContext, method.Span, $"Destructor name '{method.Name}' does not match extended type '{extDecl.ExtendedTypeName}'.");
				continue;
			}

			if (method.Name.StartsWith('~') && context.Destructors.ContainsKey(extDecl.ExtendedTypeName))
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, method.Span, $"Duplicate destructor definition for type '{extDecl.ExtendedTypeName}'.");
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
				context.Diagnostics.Report(currentFileContext, method.Span, $"Unknown return type '{method.ReturnType}'");
				return;
			}

			// 2. Register the overloaded, parameter-mangled global signature
			var overloadedName = context.GetOverloadedMangledName(baseMangledName, parameters.Select(p => p.Type).ToList());

			var memberVisibility = method.SyntacticVisibility ?? blockVisibility;
			if (!context.LegacyVisibility && method.SyntacticVisibility is { } memberVis && VisibilityRank(memberVis) > VisibilityRank(blockVisibility))
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, method.Span,
					$"Element '{method.Name}' cannot declare a wider visibility modifier than its enclosing extension block visibility level ({blockVisibility}).",
					DiagnosticIds.VisibilityExpansionInExtension);
			}

			var newSymbol = new FunctionSymbol(overloadedName, returnType, parameters)
			{
				Visibility = memberVisibility,
				DeclaringUnit = context.CurrentUnit
			};
			var methodSuppressedWarnings = new List<string>();
			ApplyFunctionAttributes(
				VerifyAttributes(method.Attributes, method.Name.StartsWith('~') ? "Destructor" : "Method", methodSuppressedWarnings),
				newSymbol,
				methodSuppressedWarnings,
				method.Attributes);
			WarnIfUnsafeBodyUnused(method.Span, method.Body, newSymbol, methodSuppressedWarnings);

			// COLLISION RULE: an extension may not re-declare a method the type already
			// has with a matching signature (another extension block, the proto-default
			// registry's naked symbols, or a conformer-declared override). First wins.
			if (context.Globals.Lookup(overloadedName) is not null)
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, method.Span,
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
				context.Diagnostics.Report(currentFileContext, ctorDecl.Span, $"Constructor name '{ctorDecl.StructName}' must match the extended type '{extDecl.ExtendedTypeName}'.");
				continue;
			}

			if (extendedType is not StructTypeSymbol ctorStructType)
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, ctorDecl.Span, $"Cannot define a constructor for non-struct type '{extDecl.ExtendedTypeName}'.");
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
				context.Diagnostics.Report(currentFileContext, ctorDecl.Span,
					$"Element '{ctorDecl.StructName}' cannot declare a wider visibility modifier than its enclosing extension block visibility level ({blockVisibility}).",
					DiagnosticIds.VisibilityExpansionInExtension);
			}

			var ctorSymbol = new FunctionSymbol(ctorOverloadedName, ctorStructType, ctorParameters)
			{
				Visibility = ctorVisibility,
				DeclaringUnit = context.CurrentUnit
			};
			var ctorSuppressedWarnings = new List<string>();
			ApplyFunctionAttributes(VerifyAttributes(ctorDecl.Attributes, "Constructor", ctorSuppressedWarnings), ctorSymbol, ctorSuppressedWarnings);
			WarnIfUnsafeBodyUnused(ctorDecl.Span, ctorDecl.Body, ctorSymbol, ctorSuppressedWarnings);

			// COLLISION RULE: duplicate constructor signatures on the same type.
			if (context.Globals.Lookup(ctorOverloadedName) is not null)
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, ctorDecl.Span,
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

		if (context.Globals.Lookup(globalDecl.Name) is not null)
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, globalDecl.Span, $"Duplicate definition of global variable '{globalDecl.Name}'.");
			return;
		}

		if (!IsCompileTimeConstant(globalDecl.Initializer))
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, globalDecl.Span, $"Global variable '{globalDecl.Name}' must be initialized with a compile-time constant.");
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
			DeclaringUnit = context.CurrentUnit
		};
		context.Globals.Declare(symbol);
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
			StructInitializationExpressionSyntax structInit => structInit.Initializers.All(static m => IsCompileTimeConstant(m.Expression)),
			_ => false
		};
	}

	private void DeclareUnion(UnionDeclarationSyntax unionDecl)
	{
		var mangledName = context.GetMangledName(unionDecl.Name, context.CurrentNamespace);
		// A local/global declaration shadows any imported resolution of the same bare name
		// that may have been cached via a 'using' lookup (e.g. user `Result<T>` vs stdlib
		// `System.Result<T,E>`); drop the stale entry so the new declaration can win.
		context.InvalidateTypeCache(unionDecl.Name);

		if (context.UnionTypes.ContainsKey(mangledName) || context.StructTypes.ContainsKey(mangledName) || TypeSymbol.FromName(unionDecl.Name) is not null)
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, unionDecl.Span, $"Duplicate type definition '{unionDecl.Name}'");
			return;
		}

		if (unionDecl.GenericParameters.Count > 0)
		{
			context.SymbolUnits[mangledName] = context.CurrentUnit!;
			context.GenericUnionTemplates[mangledName] = unionDecl;

			var placeholderFields = new List<UnionFieldSymbol>();
			var templateSymbol = new UnionTypeSymbol(mangledName, placeholderFields)
			{
				Visibility = unionDecl.Visibility
			};
			context.UnionTypes[mangledName] = templateSymbol;
			return;
		}

		context.SymbolUnits[mangledName] = context.CurrentUnit!;
		var fields = new List<UnionFieldSymbol>();
		var fieldNames = new HashSet<string>();

		foreach (var field in unionDecl.Fields)
		{
			if (!fieldNames.Add(field.Name))
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, field.Span, $"Duplicate field '{field.Name}' in union '{unionDecl.Name}'");
				continue;
			}

			TypeSymbol fieldType;
			var isVoidVariant = field.Type == "void";
			if (isVoidVariant)
			{
				fieldType = TypeSymbol.Void;
			}
			else
			{
				fieldType = context.ResolveType(field.Type ?? "");
				if (fieldType is null)
				{
					var currentFileContext = context.FileContexts[context.CurrentUnit!];
					context.Diagnostics.Report(currentFileContext, field.Span, $"Unknown type '{field.Type}' of field '{field.Name}' in union '{unionDecl.Name}'");
					continue;
				}
			}

			fields.Add(new UnionFieldSymbol(field.Name, fieldType, isVoidVariant)
			{
				Visibility = unionDecl.Visibility
			});
		}

		var unionSymbol = new UnionTypeSymbol(mangledName, fields)
		{
			Visibility = unionDecl.Visibility
		};
		context.UnionTypes[mangledName] = unionSymbol;
	}

	private void DeclareEnum(EnumDeclarationSyntax enumDecl)
	{
		var mangledName = context.GetMangledName(enumDecl.Name, context.CurrentNamespace);

		if (context.EnumTypes.ContainsKey(mangledName) || context.StructTypes.ContainsKey(mangledName)
			|| context.UnionTypes.ContainsKey(mangledName) || context.InterfaceTypes.ContainsKey(mangledName)
			|| context.ProtocolTypes.ContainsKey(mangledName) || TypeSymbol.FromName(enumDecl.Name) is not null)
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, enumDecl.Span, $"Duplicate type definition '{enumDecl.Name}'");
			return;
		}

		var appliedAttributes = VerifyAttributes(enumDecl.Attributes, "Struct", new List<string>());
		var isFlags = appliedAttributes.Contains("Flags");
		var isNonExhaustive = appliedAttributes.Contains("NonExhaustive");

		var storageName = enumDecl.StorageType ?? "int";
		if (!AllowedEnumStorageTypes.Contains(storageName))
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, enumDecl.Span,
				$"Invalid underlying storage type '{storageName}' for enum '{enumDecl.Name}'. Allowed storage types: int, uint, short, ushort, long, ulong, char, byte, sbyte.");
			return;
		}

		// [Flags] (§3.A.1): a bitmask enum is only well-formed over unsigned storage,
		// otherwise the synthesized ~ operator could produce negative intermediate values.
		if (isFlags && storageName is "int" or "short" or "long" or "sbyte")
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, enumDecl.Span,
				$"Invalid underlying storage type '{storageName}' for [Flags] enum '{enumDecl.Name}': [Flags] enums require unsigned storage (uint, ushort, byte, ulong, or char).");
			return;
		}

		// The Empty Restriction (§1.A): an enum must contain at least one variant.
		if (enumDecl.Variants.Count == 0)
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, enumDecl.Span,
				$"Enum '{enumDecl.Name}' must contain at least one variant (empty enums are prohibited).");
			return;
		}

		context.SymbolUnits[mangledName] = context.CurrentUnit!;

		var storageType = TypeSymbol.FromName(storageName)!;
		var variants = new List<EnumVariantSymbol>();
		var variantNames = new HashSet<string>();
		var nextAuto = 0L;

		// [Flags] bookkeeping (§3.A): every produced/consumed value must be unique, and
		// auto-generated unvalued variants advance relative to the highest atomic bit.
		var usedFlagValues = isFlags ? new HashSet<long>() : null;
		long? highestAtomicFlag = null;

		foreach (var variant in enumDecl.Variants)
		{
			if (!variantNames.Add(variant.Name))
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, variant.Span, $"Duplicate variant '{variant.Name}' in enum '{enumDecl.Name}'");
				continue;
			}

			long value;
			if (variant.Value is null)
			{
				if (!isFlags)
				{
					value = nextAuto;
				}
				else if (highestAtomicFlag is null)
				{
					// (§3.A.3) First unvalued flag: a leading None/Zero names the empty
					// mask (0); anything else starts the sequence at the first bit (1).
					value = IsZeroFlagName(variant.Name) ? 0 : 1;
					if (value != 0)
					{
						highestAtomicFlag = value;
					}
				}
				else
				{
					// (§3.A.4) Relative auto-advance: next bit above the highest atomic flag.
					value = highestAtomicFlag.Value << 1;
					highestAtomicFlag = value;
				}
			}
			else
			{
				var resolved = EvaluateEnumConstant(variant.Value,
					name => variants.FirstOrDefault(v => v.Name == name)?.Value);
				if (resolved is null)
				{
					var currentFileContext = context.FileContexts[context.CurrentUnit!];
					context.Diagnostics.Report(currentFileContext, variant.Value.Span,
						$"Variant '{variant.Name}' in enum '{enumDecl.Name}' must be assigned a compile-time constant integer value.");
					continue;
				}

				value = resolved.Value;
				if (isFlags && value > 0 && IsPowerOfTwo(value) && (highestAtomicFlag is null || value > highestAtomicFlag.Value))
				{
					highestAtomicFlag = value;
				}
			}

			if (isFlags)
			{
				// (§3.A.2) The zero mask must be explicitly named None/Empty/Unset/Zero.
				if (value == 0 && !IsZeroFlagName(variant.Name))
				{
					var currentFileContext = context.FileContexts[context.CurrentUnit!];
					context.Diagnostics.Report(currentFileContext, variant.Span,
						$"Variant '{variant.Name}' in [Flags] enum '{enumDecl.Name}' has value 0 and must be named None, Empty, Unset, or Zero.");
					continue;
				}

				// (§3.A.4) Collision detection: composite masks and atomic bits are all
				// reserved; a duplicate (incl. two identical auto/explicit values) is an error.
				if (!usedFlagValues!.Add(value))
				{
					var currentFileContext = context.FileContexts[context.CurrentUnit!];
					context.Diagnostics.Report(currentFileContext, variant.Span,
						$"Variant '{variant.Name}' in [Flags] enum '{enumDecl.Name}' collides with an existing value '{value}'.");
					continue;
				}
			}

			variants.Add(new EnumVariantSymbol(variant.Name, value));
			nextAuto = value + 1;
		}

		var enumSymbol = new EnumTypeSymbol(mangledName, variants, storageType)
		{
			IsFlags = isFlags,
			IsNonExhaustive = isNonExhaustive,
			Visibility = enumDecl.Visibility
		};
		context.EnumTypes[mangledName] = enumSymbol;
		context.ReplaceTypeInCache(mangledName, enumSymbol);
	}

	// [Flags] (§3.A.2): a zero-valued variant must carry the canonical empty-mask name.
	private static bool IsZeroFlagName(string name) => name is "None" or "Empty" or "Unset" or "Zero";

	// [Flags] (§3.A.4): a positive value with exactly one bit set is an atomic flag.
	private static bool IsPowerOfTwo(long value) => value > 0 && (value & (value - 1)) == 0;

	/// <summary>
	/// Compile-time constant evaluator for enum variant values: integer literals, unary
	/// minus, in-[Flags]-enum bitwise combinations (|, &amp;, ^) and references to
	/// already-declared sibling variants (used for composite masks like Read | Write).
	/// </summary>
	private static long? EvaluateEnumConstant(ExpressionSyntax expr, Func<string, long?>? variantLookup = null)
	{
		switch (expr)
		{
			case IntegerLiteralExpressionSyntax intLit:
				return intLit.Value;
			case UnaryExpressionSyntax { Operator: "-" } unary:
				var operand = EvaluateEnumConstant(unary.Operand, variantLookup);
				return operand is null ? null : -operand.Value;
			case IdentifierExpressionSyntax id when variantLookup is not null:
				return variantLookup(id.Name);
			case BinaryExpressionSyntax bin when bin.Operator is "|" or "&" or "^":
				var left = EvaluateEnumConstant(bin.Left, variantLookup);
				var right = EvaluateEnumConstant(bin.Right, variantLookup);
				if (left is null || right is null)
				{
					return null;
				}

				return bin.Operator switch
				{
					"|" => left.Value | right.Value,
					"&" => left.Value & right.Value,
					_ => left.Value ^ right.Value,
				};
			default:
				return null;
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
