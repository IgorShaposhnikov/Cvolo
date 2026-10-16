using Cvolo.Analysis.Symbols;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Collections;
using Cvolo.Analysis.Symbols.FFI;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using Cvolo.Core.AST.Expressions;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Analysis.Passes.Declaration;

/// <summary>
/// Registers raw semantic type symbols during the declaration pass before hierarchy linking and function registration.
/// </summary>
/// <remarks>
/// This service owns declaration-time construction of struct, union, enum, interface, protocol, and delegate symbols.
/// Contract hierarchy linking, embedded-struct flattening, destructor-depth validation, and function/extension registration
/// deliberately remain separate declaration-pass responsibilities.
/// <para>
/// Registration is split so that no semantic result depends on <see cref="Cvolo.Core.AST.Declarations.CompilationUnitSyntax"/>
/// enumeration order: <see cref="PredeclareTypes"/> makes every type identity (and generic template)
/// visible across all units first, then <see cref="CompleteStorage"/> and <see cref="CompleteHeaders"/>
/// fill in each declaration's contents under the lexical context of the unit that declared it.
/// Identities are never replaced once published — they are completed in place.
/// </para>
/// </remarks>
internal sealed class TypeDeclarationRegistrar(BindingContext context)
{
	private readonly AttributeValidator _attributes = new(context);
	private readonly GenericDefaultCopyValidator _genericDefaultCopies = new(context);

	/// <summary>
	/// A struct identity published before its fields are resolved.
	/// </summary>
	private sealed record PendingStruct(
		CompilationUnitSyntax Unit,
		StructDeclarationSyntax Declaration,
		StructTypeSymbol Symbol,
		IReadOnlyList<string> AppliedAttributes,
		bool IsGenericTemplate);

	/// <summary>
	/// A union identity published before its variants are resolved.
	/// </summary>
	private sealed record PendingUnion(
		CompilationUnitSyntax Unit,
		UnionDeclarationSyntax Declaration,
		UnionTypeSymbol Symbol,
		bool IsGenericTemplate);

	/// <summary>
	/// A delegate identity published before its signature is resolved.
	/// </summary>
	private sealed record PendingDelegate(
		CompilationUnitSyntax Unit,
		DelegateDeclarationSyntax Declaration,
		DelegateTypeSymbol Symbol,
		bool IsGenericTemplate);

	/// <summary>
	/// A protocol identity published before its canonical member tokens are built.
	/// </summary>
	private sealed record PendingProtocol(
		CompilationUnitSyntax Unit,
		ProtocolDeclarationSyntax Declaration,
		ProtocolTypeSymbol Symbol);

	private readonly List<PendingStruct> _pendingStructs = [];
	private readonly List<PendingUnion> _pendingUnions = [];
	private readonly List<PendingDelegate> _pendingDelegates = [];
	private readonly List<PendingProtocol> _pendingProtocols = [];

	/// <summary>
	/// Enum storage types accepted by the existing declaration rules.
	/// </summary>
	private static readonly HashSet<string> AllowedEnumStorageTypes =
	[
		"int", "uint", "short", "ushort", "long", "ulong", "char", "byte", "sbyte", "nint", "nuint"
	];

	/// <summary>
	/// Publishes the nominal identity of every type declared in <paramref name="unit"/> so later units
	/// can name it regardless of enumeration order.
	/// </summary>
	/// <remarks>
	/// The caller owns <see cref="BindingContext.CurrentUnit"/>/<see cref="BindingContext.CurrentNamespace"/>,
	/// which name the unit being walked. Enums and interfaces resolve no named types, so they are
	/// completed here rather than deferred; everything that names another type is published as a
	/// placeholder identity and completed by <see cref="CompleteStorage"/>/<see cref="CompleteHeaders"/>.
	/// Duplicate detection runs in this phase so both orders of a conflicting pair are rejected.
	/// </remarks>
	public void PredeclareTypes(CompilationUnitSyntax unit)
	{
		var members = context.CurrentNamespace != null ? unit.NamespaceDeclaration!.Members : unit.Members;

		foreach (var member in members)
		{
			switch (member)
			{
				case StructDeclarationSyntax structDecl:
					PredeclareStruct(unit, structDecl);
					break;
				case UnionDeclarationSyntax unionDecl:
					PredeclareUnion(unit, unionDecl);
					break;
				case InterfaceDeclarationSyntax interfaceDecl:
					DeclareInterface(interfaceDecl);
					break;
				case ProtocolDeclarationSyntax protocolDecl:
					PredeclareProtocol(unit, protocolDecl);
					break;
				case EnumDeclarationSyntax enumDecl:
					DeclareEnum(enumDecl);
					break;
				case DelegateDeclarationSyntax delegateDecl:
					PredeclareDelegate(unit, delegateDecl);
					break;
				case DelegateBlockDeclarationSyntax delegateBlock:
					foreach (var declared in EffectiveDelegatesIn(delegateBlock))
						PredeclareDelegate(unit, declared);
					break;
			}
		}
	}

	/// <summary>
	/// Resolves the storage layout (struct fields, union variants) of every predeclared type.
	/// </summary>
	/// <remarks>
	/// This runs before <see cref="CompleteHeaders"/> because delegate and protocol validation
	/// inspect aggregate contents transitively (native ABI representability and provenance rules
	/// walk struct/union fields), while field resolution itself only needs identities to exist.
	/// </remarks>
	public void CompleteStorage()
	{
		// Bind templates before concrete fields that may instantiate them. This
		// preserves the declaration-site failure as the root even across files
		// whose enumeration order changes between compilation snapshots.
		foreach (var pending in _pendingStructs.Where(p => p.IsGenericTemplate))
		{
			UseUnit(pending.Unit);
			CompleteStruct(pending);
		}

		foreach (var pending in _pendingUnions.Where(p => p.IsGenericTemplate))
		{
			UseUnit(pending.Unit);
			CompleteUnion(pending);
		}

		foreach (var pending in _pendingStructs.Where(p => !p.IsGenericTemplate))
		{
			UseUnit(pending.Unit);
			CompleteStruct(pending);
		}

		foreach (var pending in _pendingUnions.Where(p => !p.IsGenericTemplate))
		{
			UseUnit(pending.Unit);
			CompleteUnion(pending);
		}

		// Aggregate payloads are only fully connected once every field type has been resolved, so
		// the by-value size check runs here rather than inside a single phase body.
		ValidateByValueLayouts();

		_pendingStructs.Clear();
		_pendingUnions.Clear();
	}

	/// <summary>
	/// Rejects aggregate types whose storage has no finite size because the type is reachable from
	/// itself through by-value fields.
	/// </summary>
	/// <remarks>
	/// Once identity is predeclared for every unit, a struct in one unit can name a struct in another
	/// unit that in turn names the first, so a cycle that previously failed to resolve now becomes a
	/// real by-value cycle. Pointer, raw-pointer, reference, delegate, protocol, interface, enum,
	/// and type-parameter fields are indirections or opaque handles and therefore terminate the walk.
	/// </remarks>
	private void ValidateByValueLayouts()
	{
		foreach (var pending in _pendingStructs)
		{
			if (!ContainsByValueCycle(pending.Symbol, [pending.Symbol.Name]))
				continue;

			UseUnit(pending.Unit);
			context.Diagnostics.Report(
				context.FileContexts[context.CurrentUnit!],
				pending.Declaration.Span,
				$"Type '{pending.Declaration.Name}' contains itself by value and therefore has no finite size. Use a pointer or reference field instead.",
				DiagnosticIds.TypeContainsItselfByValue);
		}

		foreach (var pending in _pendingUnions)
		{
			if (!ContainsByValueCycle(pending.Symbol, [pending.Symbol.Name]))
				continue;

			UseUnit(pending.Unit);
			context.Diagnostics.Report(
				context.FileContexts[context.CurrentUnit!],
				pending.Declaration.Span,
				$"Type '{pending.Declaration.Name}' contains itself by value and therefore has no finite size. Use a pointer or reference field instead.",
				DiagnosticIds.TypeContainsItselfByValue);
		}
	}

	/// <summary>
	/// Returns whether <paramref name="symbol"/> is reachable from itself by value. Only aggregates
	/// contribute to size, so every other type kind ends the walk.
	/// </summary>
	private static bool ContainsByValueCycle(TypeSymbol symbol, HashSet<string> active) => symbol switch
	{
		StructTypeSymbol structType => AnyByValueFieldReaches(structType.Fields.Select(f => f.Type), active),
		UnionTypeSymbol unionType => AnyByValueFieldReaches(unionType.Fields.Where(f => !f.IsVoidVariant).Select(f => f.Type), active),
		_ => false,
	};

	private static bool AnyByValueFieldReaches(IEnumerable<TypeSymbol> fieldTypes, HashSet<string> active)
	{
		foreach (var fieldType in fieldTypes)
		{
			switch (fieldType)
			{
				case StructTypeSymbol structType:
					if (!active.Add(structType.Name))
						return true;
					if (AnyByValueFieldReaches(structType.Fields.Select(f => f.Type), active))
						return true;
					active.Remove(structType.Name);
					break;
				case UnionTypeSymbol unionType:
					if (!active.Add(unionType.Name))
						return true;
					if (AnyByValueFieldReaches(unionType.Fields.Where(f => !f.IsVoidVariant).Select(f => f.Type), active))
						return true;
					active.Remove(unionType.Name);
					break;
				case ArrayTypeSymbol arrayType:
					if (AnyByValueFieldReaches([arrayType.ElementType], active))
						return true;
					break;
				case SliceTypeSymbol sliceType:
					if (AnyByValueFieldReaches([sliceType.ElementType], active))
						return true;
					break;
			}
		}

		return false;
	}

	/// <summary>
	/// Resolves the headers (delegate signatures, protocol canonical members) of every predeclared type.
	/// </summary>
	public void CompleteHeaders()
	{
		foreach (var pending in _pendingProtocols)
		{
			UseUnit(pending.Unit);

			// Phase-1 structural pre-match: build the canonical member tokens once, resolved in
			// this protocol's namespace, so conformance checks are O(1) set membership
			// (topological, naming-independent) rather than symbolic.
			pending.Symbol.CompleteCanonicalMembers(
				ProtocolCanonicalizer.BuildCanonicalMembers(pending.Declaration, context));
		}

		_pendingProtocols.Clear();

		foreach (var pending in _pendingDelegates)
		{
			UseUnit(pending.Unit);
			CompleteDelegate(pending);
		}

		_pendingDelegates.Clear();
	}

	/// <summary>
	/// Restores the lexical context of the unit that declared a deferred type so its using
	/// directives, namespace alias expansions, and namespace scoping apply to its own names.
	/// </summary>
	private void UseUnit(CompilationUnitSyntax unit)
	{
		context.CurrentUnit = unit;
		context.CurrentNamespace = unit.NamespaceDeclaration?.Name;
	}

	/// <summary>
	/// Yields the effective delegate declarations of an ABI block, reporting an unsupported block
	/// calling convention and yielding nothing when the block itself is malformed.
	/// </summary>
	private IEnumerable<DelegateDeclarationSyntax> EffectiveDelegatesIn(DelegateBlockDeclarationSyntax delegateBlock)
	{
		// Delegate ABI blocks ('unsafe "C" { delegate ... }') are pure declaration
		// scopes: they give the contained delegates one calling convention but are
		// NOT executable unsafe blocks. Register each delegate with the block's
		// convention spliced in when the declaration omits its own.
		var blockConvention = delegateBlock.CallingConvention;
		if (blockConvention is not ("C" or "system"))
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, delegateBlock.Span,
				$"Unknown calling convention '{delegateBlock.CallingConvention}'. Supported calling conventions are \"C\" and \"system\".",
				DiagnosticIds.UnknownCallingConvention);
			yield break;
		}

		foreach (var d in delegateBlock.Delegates)
		{
			// If the inner delegate already declares a convention it wins; otherwise
			// it inherits the block's. Emit as a fresh node so PredeclareDelegate sees
			// the effective convention without mutating syntax.
			yield return d.IsNative || d.CallingConvention is not null
				? d
				: new DelegateDeclarationSyntax(d.Span, d.ReturnType, d.Name, d.GenericParameters, d.Parameters,
					d.Visibility, isNative: true, callingConvention: blockConvention, d.NameSpan, d.ReturnTypeSpan);
		}
	}

	/// <summary>
	/// Publishes a struct identity (and its generic template, when applicable) without resolving fields.
	/// </summary>
	private void PredeclareStruct(CompilationUnitSyntax unit, StructDeclarationSyntax structDecl)
	{
		var mangledName = context.GetMangledName(structDecl.Name, context.CurrentNamespace);

		if (IsTypeNameTaken(mangledName, structDecl.Name))
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, DeclarationNameSpan(currentFileContext, structDecl.Name, structDecl.Span), $"Duplicate type definition '{structDecl.Name}'", DiagnosticIds.DuplicateTypeDefinition);
			return;
		}

		var appliedAttrs = _attributes.Verify(structDecl.Attributes, "Struct", []);
		var (isMustUse, mustUseMsg) = _attributes.ExtractMustUse(structDecl.Attributes);

		context.SymbolUnits[mangledName] = unit;

		var isGenericTemplate = structDecl.GenericParameters.Count > 0;
		if (isGenericTemplate)
			context.GenericStructTemplates[mangledName] = structDecl;

		// Publish the identity before any field is resolved so self-referential and
		// cross-unit forward field types resolve to this exact instance, which
		// CompleteStruct later populates in place.
		var symbol = new StructTypeSymbol(mangledName, [])
		{
			IsStrictMutability = appliedAttrs.Contains("StrictMutability"),
			Visibility = structDecl.Visibility,
			IsMustUse = isMustUse,
			MustUseMessage = mustUseMsg
		};

		context.StructTypes[mangledName] = symbol;
		_pendingStructs.Add(new PendingStruct(unit, structDecl, symbol, appliedAttrs, isGenericTemplate));
	}

	/// <summary>
	/// Resolves a struct's field symbols into the identity published by <see cref="PredeclareStruct"/>.
	/// </summary>
	private TextSpan DeclaredTypeSpan(TextSpan declaration, string typeName)
	{
		var source = context.FileContexts[context.CurrentUnit!].Source;
		if (declaration.Start < 0 || declaration.Start >= source.Length)
			return declaration;
		var length = Math.Min(declaration.Length, source.Length - declaration.Start);
		var offset = source.Substring(declaration.Start, length).IndexOf(typeName, StringComparison.Ordinal);
		return offset >= 0 ? new TextSpan(declaration.Start + offset, typeName.Length) : declaration;
	}

	private void CompleteStruct(PendingStruct pending)
	{
		var structDecl = pending.Declaration;

		// If this is a generic struct template (e.g. struct Point<T>)
		if (pending.IsGenericTemplate)
		{
			_genericDefaultCopies.Validate(structDecl);

			// A generic template is never instantiated for the editor, but its body must still
			// be bound so that field types resolve to type parameters (e.g. `T* Ptr` where T is
			// this struct's own parameter). Bind the fields inside a generic scope so completion,
			// go-to-definition and references work on the template itself. No code is emitted for
			// this symbol; instantiation still happens per use site as before.
			var templateFields = new List<StructFieldSymbol>();
			var templateFieldNames = new HashSet<string>();
			context.PushGenericScope("struct", structDecl.Name, structDecl.GenericParameters);
			try
			{
				foreach (var field in structDecl.Fields)
				{
					if (!templateFieldNames.Add(field.Name))
					{
						context.Diagnostics.Report(context.FileContexts[context.CurrentUnit!], field.Span,
							$"Duplicate field '{field.Name}' in struct '{structDecl.Name}'", DiagnosticIds.DuplicateFieldInStruct);
						continue;
					}

					if (context.ResolveType(field.Type) is not { } fieldType)
					{
						context.RegisterInvalidAggregateMember(pending.Symbol.Name, field.Name, field, DiagnosticIds.UnknownTypeOfField, isUnion: false);
						context.Diagnostics.Report(context.FileContexts[context.CurrentUnit!], DeclaredTypeSpan(field.Span, field.Type),
							$"Unknown type '{field.Type}' of field '{field.Name}'", DiagnosticIds.UnknownTypeOfField);
						continue;
					}

					templateFields.Add(new StructFieldSymbol(field.Name, fieldType)
					{
						Visibility = field.Visibility
					});
				}
			}
			finally
			{
				context.PopGenericScope();
			}

			pending.Symbol.PopulateFields(templateFields);
			context.ReplaceTypeInCache(pending.Symbol.Name, pending.Symbol);
			return;
		}

		var fields = new List<StructFieldSymbol>();
		var fieldNames = new HashSet<string>();

		foreach (var field in structDecl.Fields)
		{
			if (!fieldNames.Add(field.Name))
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, field.Span, $"Duplicate field '{field.Name}' in struct '{structDecl.Name}'", DiagnosticIds.DuplicateFieldInStruct);
				continue;
			}

			var fieldType = context.ResolveType(field.Type);
			if (fieldType is null)
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.RegisterInvalidAggregateMember(pending.Symbol.Name, field.Name, field, DiagnosticIds.UnknownTypeOfField, isUnion: false);
				context.Diagnostics.Report(currentFileContext, DeclaredTypeSpan(field.Span, field.Type), $"Unknown type '{field.Type}' of field '{field.Name}'", DiagnosticIds.UnknownTypeOfField);
				continue;
			}

			fields.Add(new StructFieldSymbol(field.Name, fieldType)
			{
				Visibility = field.Visibility
			});
		}

		// Populate the published identity IN PLACE rather than creating a new object.
		// Reference field types resolved during field population captured a PointerTypeSymbol
		// whose ReferencedType is this symbol; mutating its Fields keeps those references valid.
		pending.Symbol.PopulateFields(fields);
		context.ReplaceTypeInCache(pending.Symbol.Name, pending.Symbol);
	}

	/// <summary>
	/// Publishes a union identity (and its generic template, when applicable) without resolving variants.
	/// </summary>
	private void PredeclareUnion(CompilationUnitSyntax unit, UnionDeclarationSyntax unionDecl)
	{
		var mangledName = context.GetMangledName(unionDecl.Name, context.CurrentNamespace);
		// A local/global declaration shadows any imported resolution of the same bare name
		// that may have been cached via a 'using' lookup (e.g. user `Result<T>` vs stdlib
		// `System.Result<T,E>`); drop the stale entry so the new declaration can win.
		context.InvalidateTypeCache(unionDecl.Name);

		if (IsTypeNameTaken(mangledName, unionDecl.Name))
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, DeclarationNameSpan(currentFileContext, unionDecl.Name, unionDecl.Span), $"Duplicate type definition '{unionDecl.Name}'", DiagnosticIds.DuplicateTypeDefinition);
			return;
		}

		var (isMustUse, mustUseMsg) = _attributes.ExtractMustUse(unionDecl.Attributes);

		ValidateResultShape(unionDecl);

		if (unionDecl.IsUnsafe && unionDecl.GenericParameters.Count > 0)
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, unionDecl.Span, $"Raw unsafe union '{unionDecl.Name}' cannot be generic.", DiagnosticIds.UnsafeUnionGenericUnsupported);
			return;
		}

		context.SymbolUnits[mangledName] = unit;

		var isGenericTemplate = unionDecl.GenericParameters.Count > 0;
		if (isGenericTemplate)
			context.GenericUnionTemplates[mangledName] = unionDecl;

		var symbol = new UnionTypeSymbol(mangledName, [])
		{
			Visibility = unionDecl.Visibility,
			IsMustUse = isMustUse,
			MustUseMessage = mustUseMsg,
			IsUnsafe = unionDecl.IsUnsafe,
			IsErrorUnion = HasErrorAttribute(unionDecl.Attributes),
			IsCanonicalOption = IsCanonicalOptionDeclaration(unit, unionDecl)
		};

		context.UnionTypes[mangledName] = symbol;
		_pendingUnions.Add(new PendingUnion(unit, unionDecl, symbol, isGenericTemplate));
	}

	/// <summary>
	/// True when the declaration carries <c>[Error]</c>/<c>[ErrorAttribute]</c. Error unions are
	/// try/catch error types; the lowering synthesizes <c>default(ErrorUnion)</c> for catch slots,
	/// which the defaultability rules must allow.
	/// </summary>
	private bool HasErrorAttribute(IReadOnlyList<AttributeSyntax> attributes)
	{
		foreach (var attr in attributes)
		{
			if (_attributes.NormalizeName(attr.Name) == "Error")
				return true;
		}

		return false;
	}

	/// <summary>
	/// Establishes the canonical Option declaration identity: only the Base-SDK
	/// source's own <c>Option</c> union is marked, using the same origin detection as
	/// <c>BuiltinValidator.ResolveOrigin</c>. Consumers (the default recipe) then rely on the
	/// symbol flag, never on a display-name or shape test, so a user declaration with the same
	/// spelling cannot acquire the flag.
	/// </summary>
	private bool IsCanonicalOptionDeclaration(CompilationUnitSyntax unit, UnionDeclarationSyntax unionDecl)
	{
		if (unionDecl.Name != "Option" || unionDecl.GenericParameters.Count != 1)
			return false;

		if (!context.FileContexts.TryGetValue(unit, out var fileContext))
			return false;

		if (fileContext.Origin == SourceOrigin.BaseSdk)
			return true;

		if (context.BaseSourcePaths.Count == 0)
			return false;

		return context.BaseSourcePaths.Contains(Path.GetFullPath(fileContext.FilePath));
	}

	/// <summary>
	/// Resolves a union's variant payload types into the identity published by <see cref="PredeclareUnion"/>.
	/// </summary>
	private void CompleteUnion(PendingUnion pending)
	{
		var unionDecl = pending.Declaration;

		if (pending.IsGenericTemplate)
		{
			_genericDefaultCopies.Validate(unionDecl);

			// Same editor-semantics contract as generic structs: bind the variant types inside a
			// generic scope so the uninstantiated template resolves its own type parameters,
			// without instantiating or emitting anything for it.
			var templateFields = new List<UnionFieldSymbol>();
			var templateFieldNames = new HashSet<string>();
			context.PushGenericScope("union", unionDecl.Name, unionDecl.GenericParameters);
			try
			{
				foreach (var field in unionDecl.Fields)
				{
					if (!templateFieldNames.Add(field.Name))
					{
						context.Diagnostics.Report(context.FileContexts[context.CurrentUnit!], field.Span,
							$"Duplicate field '{field.Name}' in union '{unionDecl.Name}'", DiagnosticIds.DuplicateFieldInUnion);
						continue;
					}

					if (field.Type == "void")
						continue;

					if (context.ResolveType(field.Type) is not { } templateFieldType)
					{
						context.RegisterInvalidAggregateMember(pending.Symbol.Name, field.Name, field, DiagnosticIds.UnknownTypeOfFieldInUnion, isUnion: true);
						context.Diagnostics.Report(context.FileContexts[context.CurrentUnit!], DeclaredTypeSpan(field.Span, field.Type),
							$"Unknown type '{field.Type}' of field '{field.Name}' in union '{unionDecl.Name}'", DiagnosticIds.UnknownTypeOfFieldInUnion);
						continue;
					}

					templateFields.Add(new UnionFieldSymbol(field.Name, templateFieldType, isVoidVariant: false)
					{
						Visibility = field.Visibility
					});
				}
			}
			finally
			{
				context.PopGenericScope();
			}

			pending.Symbol.PopulateFields(templateFields);
			context.ReplaceTypeInCache(pending.Symbol.Name, pending.Symbol);
			return;
		}

		var fields = new List<UnionFieldSymbol>();
		var fieldNames = new HashSet<string>();

		foreach (var field in unionDecl.Fields)
		{
			if (!fieldNames.Add(field.Name))
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, field.Span, $"Duplicate field '{field.Name}' in union '{unionDecl.Name}'", DiagnosticIds.DuplicateFieldInUnion);
				continue;
			}

			TypeSymbol fieldType;
			var isVoidVariant = field.Type == "void";
			if (isVoidVariant)
			{
				if (unionDecl.IsUnsafe)
				{
					var currentFileContext = context.FileContexts[context.CurrentUnit!];
					context.Diagnostics.Report(currentFileContext, field.Span,
						$"Raw 'unsafe union' '{unionDecl.Name}' cannot contain a 'void' variant; raw unions have no tag and every field must be a concrete ABI-safe value type.",
						DiagnosticIds.UnsafeUnionFieldNotAbiSafe);
					continue;
				}

				fieldType = TypeSymbol.Void;
			}
			else
			{
				fieldType = context.ResolveType(field.Type ?? "");
				if (fieldType is null)
				{
					var currentFileContext = context.FileContexts[context.CurrentUnit!];
					context.RegisterInvalidAggregateMember(pending.Symbol.Name, field.Name, field, DiagnosticIds.UnknownTypeOfFieldInUnion, isUnion: true);
					context.Diagnostics.Report(currentFileContext, DeclaredTypeSpan(field.Span, field.Type), $"Unknown type '{field.Type}' of field '{field.Name}' in union '{unionDecl.Name}'", DiagnosticIds.UnknownTypeOfFieldInUnion);
					continue;
				}

				if (unionDecl.IsUnsafe && !NativeAbiRepresentability.IsNativeAbiRepresentable(fieldType, NativeAbiPosition.RawUnionField))
				{
					var currentFileContext = context.FileContexts[context.CurrentUnit!];
					context.Diagnostics.Report(currentFileContext, field.Span,
						$"Field '{field.Name}' of type '{fieldType.Name}' is not representable in an 'unsafe union' across the C ABI boundary.",
						DiagnosticIds.UnsafeUnionFieldNotAbiSafe);
					continue;
				}
			}

			fields.Add(new UnionFieldSymbol(field.Name, fieldType, isVoidVariant)
			{
				// An omitted field modifier inherits the union visibility for compatibility with
				// existing tagged-union semantics. An explicit modifier remains authoritative so
				// package metadata can preserve a narrower field across serialization boundaries.
				Visibility = field.SyntacticVisibility ?? unionDecl.Visibility
			});
		}

		if (unionDecl.IsUnsafe && fields.Count == 0)
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, unionDecl.Span,
				$"Raw 'unsafe union' '{unionDecl.Name}' requires at least one field.",
				DiagnosticIds.UnsafeUnionEmpty);
			return;
		}

		pending.Symbol.PopulateFields(fields);
	}

	/// <summary>
	/// Publishes a protocol identity without building its canonical member tokens.
	/// </summary>
	private void PredeclareProtocol(CompilationUnitSyntax unit, ProtocolDeclarationSyntax protocolDecl)
	{
		var mangledName = context.GetMangledName(protocolDecl.Name, context.CurrentNamespace);

		if (IsTypeNameTaken(mangledName, protocolDecl.Name))
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, protocolDecl.Span, $"Duplicate protocol definition '{protocolDecl.Name}'", DiagnosticIds.DuplicateProtocolDefinition);
			return;
		}

		context.SymbolUnits[mangledName] = unit;
		context.ProtocolTemplates[mangledName] = protocolDecl;

		var symbol = new ProtocolTypeSymbol(mangledName, protocolDecl.Members, protocolDecl.GenericParameters, protocolDecl.Constraint, new HashSet<string>())
		{
			Visibility = protocolDecl.Visibility
		};

		context.ProtocolTypes[mangledName] = symbol;
		_pendingProtocols.Add(new PendingProtocol(unit, protocolDecl, symbol));
	}


	/// <summary>
	/// Registers an interface declaration and its raw template symbol before contract hierarchy linking.
	/// </summary>
	public void DeclareInterface(InterfaceDeclarationSyntax interfaceDecl)
	{
		var mangledName = context.GetMangledName(interfaceDecl.Name, context.CurrentNamespace);

		if (IsTypeNameTaken(mangledName, interfaceDecl.Name))
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, interfaceDecl.Span, $"Duplicate interface definition '{interfaceDecl.Name}'", DiagnosticIds.DuplicateInterfaceDefinition);
			return;
		}

		context.SymbolUnits[mangledName] = context.CurrentUnit!;
		context.InterfaceTemplates[mangledName] = interfaceDecl;
		context.InterfaceTypes[mangledName] = new InterfaceTypeSymbol(mangledName)
		{
			Visibility = interfaceDecl.Visibility
		};
	}

	/// <summary>
	/// Publishes a delegate identity (and its generic template, when applicable) without resolving
	/// the return or parameter types.
	/// </summary>
	private void PredeclareDelegate(CompilationUnitSyntax unit, DelegateDeclarationSyntax delegateDecl)
	{
		var mangledName = context.GetMangledName(delegateDecl.Name, context.CurrentNamespace);

		if (IsTypeNameTaken(mangledName, delegateDecl.Name))
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, delegateDecl.NameSpan, $"Duplicate type definition '{delegateDecl.Name}'", DiagnosticIds.DuplicateTypeDefinition);
			return;
		}

		var isNative = delegateDecl.IsNative;
		var callingConvention = delegateDecl.CallingConvention;

		if (isNative && callingConvention is not ("C" or "system"))
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, delegateDecl.Span,
				$"Unknown calling convention '{callingConvention}'. Supported calling conventions are \"C\" and \"system\".",
				DiagnosticIds.UnknownCallingConvention);
			return;
		}

		if (isNative && delegateDecl.GenericParameters.Count > 0)
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, delegateDecl.Span, $"Native delegate '{delegateDecl.Name}' cannot be generic.", DiagnosticIds.NativeDelegateGenericUnsupported);
			return;
		}

		context.SymbolUnits[mangledName] = unit;

		// Generic delegate template: register the template; parameters/return are resolved
		// per-instantiation (mirroring generic structs).
		var isGenericTemplate = delegateDecl.GenericParameters.Count > 0;
		if (isGenericTemplate)
			context.GenericDelegateTemplates[mangledName] = delegateDecl;

		var symbol = new DelegateTypeSymbol(
			mangledName,
			TypeSymbol.Void,
			Array.Empty<ParameterSymbol>(),
			delegateDecl.GenericParameters,
			Array.Empty<TypeSymbol>(),
			isNative,
			callingConvention)
		{
			Visibility = delegateDecl.Visibility
		};

		context.DelegateTypes[mangledName] = symbol;
		_pendingDelegates.Add(new PendingDelegate(unit, delegateDecl, symbol, isGenericTemplate));
	}

	/// <summary>
	/// Resolves a delegate's return/parameter types into the identity published by
	/// <see cref="PredeclareDelegate"/>, including native calling-convention metadata,
	/// provenance-independent return validation, and ordinary delegate parameters.
	/// </summary>
	private void CompleteDelegate(PendingDelegate pending)
	{
		var delegateDecl = pending.Declaration;
		var isNative = pending.Symbol.IsNative;

		if (pending.IsGenericTemplate)
		{
			var activeParams = new HashSet<string>(delegateDecl.GenericParameters);
			context.PushGenericScope("delegate", delegateDecl.Name, activeParams);
			try
			{
				var templateReturn = context.ResolveType(delegateDecl.ReturnType);
				var templateParams = delegateDecl.Parameters
					.Select(p => new ParameterSymbol(p.Name, context.ResolveType(p.Type) ?? TypeSymbol.Void))
					.ToList();
				pending.Symbol.CompleteSignature(templateReturn ?? TypeSymbol.Void, templateParams);
			}
			finally
			{
				context.PopGenericScope();
			}

			return;
		}

		var returnType = context.ResolveType(delegateDecl.ReturnType);
		if (returnType is null)
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, delegateDecl.ReturnTypeSpan,
				$"Unknown return type '{delegateDecl.ReturnType}' in delegate declaration '{delegateDecl.Name}'", DiagnosticIds.UnknownReturnTypeInDelegateDeclaration);
			return;
		}

		if (isNative)
		{
			if (NativeAbiSemanticSafety.ContainsResourceBearingValue(context, returnType))
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, delegateDecl.ReturnTypeSpan, $"Return type '{returnType.Name}' is resource-bearing.", DiagnosticIds.NativeAbiResourceBearing);
				return;
			}

			if (NativeAbiRepresentability.ContainsEnumWithoutExplicitStorage(returnType))
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, delegateDecl.ReturnTypeSpan, $"Return type '{returnType.Name}' contains an enum without explicit ABI storage.", DiagnosticIds.NativeEnumRequiresExplicitStorage);
				return;
			}

			if (!NativeAbiRepresentability.IsNativeAbiRepresentable(returnType, NativeAbiPosition.Return))
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, delegateDecl.ReturnTypeSpan,
					$"Return type '{returnType.Name}' of native delegate '{delegateDecl.Name}' is not representable across the C ABI boundary.",
					DiagnosticIds.NativeAbiTypeNotRepresentable);
				return;
			}

			if (!NativeAbiAggregateClassification.IsVerifiedForDirectBoundary(returnType, context.NativePointerBytes, context.NativeAggregateAbiSupported))
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, delegateDecl.ReturnTypeSpan,
					$"Return aggregate '{returnType.Name}' of native delegate '{delegateDecl.Name}' has no verified target ABI classification.",
					DiagnosticIds.NativeAbiAggregateUnclassified);
				return;
			}
		}
		else if (!DelegateTypeHelpers.IsProvenanceIndependentReturn(returnType))
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			var provenanceId = returnType switch
			{
				PointerTypeSymbol => DiagnosticIds.DelegateReturnRefType,
				SliceTypeSymbol => DiagnosticIds.DelegateReturnSliceType,
				_ => DiagnosticIds.DelegateReturnTransitiveProvenance,
			};
			context.Diagnostics.Report(currentFileContext, delegateDecl.ReturnTypeSpan,
				$"Invalid delegate return type '{returnType.Name}': delegate '{delegateDecl.Name}' may not return a provenance-bearing type (§3.2).",
				provenanceId);
		}

		var parameters = new List<ParameterSymbol>();
		foreach (var p in delegateDecl.Parameters)
		{
			// Receiver forms ('ref this'/'refvar this') are not permitted on delegate parameters.
			if (p.Name == "this")
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, p.Span,
					$"Delegate parameter may not be a receiver ('{p.Type} this'); delegates declare ordinary value/reference parameters only.",
					DiagnosticIds.ReceiverParamInDelegateDeclaration);
				continue;
			}

			var paramType = context.ResolveType(p.Type);
			if (paramType is null)
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, p.Span,
					$"Unknown parameter type '{p.Type}' in delegate declaration '{delegateDecl.Name}'", DiagnosticIds.UnknownParameterTypeInDelegateDeclaration);
				continue;
			}

			if (isNative && NativeAbiSemanticSafety.ContainsResourceBearingValue(context, paramType))
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, p.Span, $"Parameter '{p.Name}' is resource-bearing.", DiagnosticIds.NativeAbiResourceBearing);
				continue;
			}

			if (isNative && NativeAbiRepresentability.ContainsEnumWithoutExplicitStorage(paramType))
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, p.Span, $"Parameter '{p.Name}' contains an enum without explicit ABI storage.", DiagnosticIds.NativeEnumRequiresExplicitStorage);
				continue;
			}

			if (isNative && !NativeAbiRepresentability.IsNativeAbiRepresentable(paramType, NativeAbiPosition.Parameter))
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, p.Span,
					$"Parameter '{p.Name}' of type '{paramType.Name}' in native delegate '{delegateDecl.Name}' is not representable across the C ABI boundary.",
					DiagnosticIds.NativeAbiTypeNotRepresentable);
				continue;
			}

			if (isNative && !NativeAbiAggregateClassification.IsVerifiedForDirectBoundary(paramType, context.NativePointerBytes, context.NativeAggregateAbiSupported))
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, p.Span,
					$"Parameter aggregate '{paramType.Name}' in native delegate '{delegateDecl.Name}' has no verified target ABI classification.",
					DiagnosticIds.NativeAbiAggregateUnclassified);
				continue;
			}

			parameters.Add(new ParameterSymbol(p.Name, paramType));
		}

		pending.Symbol.CompleteSignature(returnType, parameters);
	}


	private void ValidateResultShape(UnionDeclarationSyntax unionDecl)
	{
		var attribute = FindResultAttribute(unionDecl.Attributes);
		if (attribute is null)
		{
			return;
		}

		var currentFileContext = context.FileContexts[context.CurrentUnit!];

		if (attribute.Arguments.Count > 0)
		{
			context.Diagnostics.Report(currentFileContext, unionDecl.Span,
				"The '[Result]' attribute does not accept arguments.",
				DiagnosticIds.ResultShapeArguments);
		}

		var okCount = unionDecl.Fields.Count(field => field.Name == "Ok");
		var errCount = unionDecl.Fields.Count(field => field.Name == "Err");

		if (okCount == 0)
		{
			context.Diagnostics.Report(currentFileContext, unionDecl.Span,
				$"Result union '{unionDecl.Name}' must declare an 'Ok' variant.",
				DiagnosticIds.ResultShapeMissingOk);
		}

		if (errCount == 0)
		{
			context.Diagnostics.Report(currentFileContext, unionDecl.Span,
				$"Result union '{unionDecl.Name}' must declare an 'Err' variant.",
				DiagnosticIds.ResultShapeMissingErr);
		}

		if (okCount == 1 && errCount == 1 && unionDecl.Fields.Count > 2)
		{
			context.Diagnostics.Report(currentFileContext, unionDecl.Span,
				$"Result union '{unionDecl.Name}' must declare exactly the 'Ok' and 'Err' variants.",
				DiagnosticIds.ResultShapeExtraVariant);
		}

		var errField = unionDecl.Fields.FirstOrDefault(field => field.Name == "Err");
		if (errField is not null && errField.Type == "void")
		{
			context.Diagnostics.Report(currentFileContext, errField.Span,
				$"Result union '{unionDecl.Name}' cannot use a 'void' error variant.",
				DiagnosticIds.ResultShapeVoidErr);
		}
	}

	private static AttributeSyntax? FindResultAttribute(IReadOnlyList<AttributeSyntax>? attributes)
	{
		if (attributes is null)
		{
			return null;
		}

		foreach (var attribute in attributes)
		{
			var name = attribute.Name;
			var lastDot = name.LastIndexOf('.');
			var leaf = lastDot >= 0 ? name[(lastDot + 1)..] : name;
			if (leaf.EndsWith("Attribute", StringComparison.Ordinal))
			{
				leaf = leaf[..^"Attribute".Length];
			}

			if (leaf == "Result")
			{
				return attribute;
			}
		}

		return null;
	}


	/// <summary>
	/// Determines whether a type name is already taken by any declared type, in any kind, or by a
	/// builtin primitive spelling.
	/// </summary>
	/// <remarks>
	/// Every declaration kind must answer this identically. Scanning only its own registry makes
	/// duplicate detection order-dependent: a <c>union Thing</c> seen before <c>struct Thing</c> was
	/// reported, while the same pair in the opposite order was accepted because the struct only
	/// consulted <c>StructTypes</c>. Because predeclaration publishes identities for all units before
	/// any storage is resolved, an undetected clash would otherwise silently leave two distinct
	/// symbols reachable under one name.
	/// </remarks>
	private bool IsTypeNameTaken(string mangledName, string declaredName) =>
		context.StructTypes.ContainsKey(mangledName)
		|| context.UnionTypes.ContainsKey(mangledName)
		|| context.InterfaceTypes.ContainsKey(mangledName)
		|| context.ProtocolTypes.ContainsKey(mangledName)
		|| context.EnumTypes.ContainsKey(mangledName)
		|| context.DelegateTypes.ContainsKey(mangledName)
		|| TypeSymbol.FromName(declaredName) is not null;

	private static TextSpan DeclarationNameSpan(CompilationContext fileContext, string name, TextSpan declarationSpan)
	{
		var source = fileContext.Source;
		if (declarationSpan.Start >= 0 && declarationSpan.Start < source.Length)
		{
			var length = Math.Min(declarationSpan.Length, source.Length - declarationSpan.Start);
			var index = source.IndexOf(name, declarationSpan.Start, length, StringComparison.Ordinal);
			if (index >= 0)
				return new TextSpan(index, name.Length);
		}

		return new TextSpan(declarationSpan.Start, Math.Min(name.Length, Math.Max(1, declarationSpan.Length)));
	}

	/// <summary>
	/// Publishes an enum declaration, validates its storage and flag rules, and evaluates compile-time variant values.
	/// </summary>
	public void DeclareEnum(EnumDeclarationSyntax enumDecl)
	{
		var mangledName = context.GetMangledName(enumDecl.Name, context.CurrentNamespace);

		if (IsTypeNameTaken(mangledName, enumDecl.Name))
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, DeclarationNameSpan(currentFileContext, enumDecl.Name, enumDecl.Span), $"Duplicate type definition '{enumDecl.Name}'", DiagnosticIds.DuplicateTypeDefinition);
			return;
		}

		var appliedAttributes = _attributes.Verify(enumDecl.Attributes, "Struct", new List<string>());
		var isFlags = appliedAttributes.Contains("Flags");
		var isNonExhaustive = appliedAttributes.Contains("NonExhaustive");

		var storageName = enumDecl.StorageType ?? "int";
		if (!AllowedEnumStorageTypes.Contains(storageName))
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, enumDecl.Span,
				$"Invalid underlying storage type '{storageName}' for enum '{enumDecl.Name}'. Allowed storage types: int, uint, short, ushort, long, ulong, char, byte, sbyte.", DiagnosticIds.InvalidUnderlyingStorageTypeForEnumAllowedStorageTypesIntUin);
			return;
		}

		// [Flags] (§3.A.1): a bitmask enum is only well-formed over unsigned storage,
		// otherwise the synthesized ~ operator could produce negative intermediate values.
		if (isFlags && storageName is "int" or "short" or "long" or "sbyte")
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, enumDecl.Span,
				$"Invalid underlying storage type '{storageName}' for [Flags] enum '{enumDecl.Name}': [Flags] enums require unsigned storage (uint, ushort, byte, ulong, or char).", DiagnosticIds.InvalidUnderlyingStorageTypeForFlagsEnumFlagsEnumsRequireUns);
			return;
		}

		// The Empty Restriction (§1.A): an enum must contain at least one variant.
		if (enumDecl.Variants.Count == 0)
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, enumDecl.Span,
				$"Enum '{enumDecl.Name}' must contain at least one variant (empty enums are prohibited).", DiagnosticIds.EnumMustContainAtLeastOneVariantEmptyEnumsAreProhibited);
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
				context.Diagnostics.Report(currentFileContext, variant.Span, $"Duplicate variant '{variant.Name}' in enum '{enumDecl.Name}'", DiagnosticIds.DuplicateVariantInEnum);
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
						$"Variant '{variant.Name}' in enum '{enumDecl.Name}' must be assigned a compile-time constant integer value.", DiagnosticIds.VariantInEnumMustBeAssignedACompileTimeConstantIntegerValue);
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
						$"Variant '{variant.Name}' in [Flags] enum '{enumDecl.Name}' has value 0 and must be named None, Empty, Unset, or Zero.", DiagnosticIds.VariantInFlagsEnumHasValue0AndMustBeNamedNoneEmptyUnsetOrZer);
					continue;
				}

				// (§3.A.4) Collision detection: composite masks and atomic bits are all
				// reserved; a duplicate (incl. two identical auto/explicit values) is an error.
				if (!usedFlagValues!.Add(value))
				{
					var currentFileContext = context.FileContexts[context.CurrentUnit!];
					context.Diagnostics.Report(currentFileContext, variant.Span,
						$"Variant '{variant.Name}' in [Flags] enum '{enumDecl.Name}' collides with an existing value '{value}'.", DiagnosticIds.VariantInFlagsEnumCollidesWithAnExistingValue);
					continue;
				}
			}

			variants.Add(new EnumVariantSymbol(variant.Name, value));
			nextAuto = value + 1;
		}

		var (isMustUse, mustUseMsg) = _attributes.ExtractMustUse(enumDecl.Attributes);

		var enumSymbol = new EnumTypeSymbol(mangledName, variants, storageType)
		{
			HasExplicitStorageType = enumDecl.StorageType is not null,
			IsFlags = isFlags,
			IsNonExhaustive = isNonExhaustive,
			Visibility = enumDecl.Visibility,
			IsMustUse = isMustUse,
			MustUseMessage = mustUseMsg
		};

		context.EnumTypes[mangledName] = enumSymbol;
		context.ReplaceTypeInCache(mangledName, enumSymbol);
	}


	/// <summary>
	/// Returns whether a flag variant name is one of the canonical names permitted for the zero-valued empty mask.
	/// </summary>
	private static bool IsZeroFlagName(string name) => name is "None" or "Empty" or "Unset" or "Zero";


	/// <summary>
	/// Returns whether a positive integral flag value contains exactly one set bit.
	/// </summary>
	private static bool IsPowerOfTwo(long value) => value > 0 && (value & (value - 1)) == 0;


	/// <summary>
	/// Evaluates the compile-time integer expression forms permitted in enum variant initializers.
	/// </summary>
	private static long? EvaluateEnumConstant(ExpressionSyntax expr, Func<string, long?>? variantLookup = null)
	{
		switch (expr)
		{
			case IntegerLiteralExpressionSyntax intLit:
				return (long)intLit.Value;
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
}
