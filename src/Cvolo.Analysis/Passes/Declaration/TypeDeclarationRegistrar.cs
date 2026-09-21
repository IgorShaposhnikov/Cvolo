using Cvolo.Analysis.Symbols;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Collections;
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
/// </remarks>
internal sealed class TypeDeclarationRegistrar(BindingContext context)
{
	private readonly AttributeValidator _attributes = new(context);
	private readonly GenericDefaultCopyValidator _genericDefaultCopies = new(context);

	/// <summary>
	/// Enum storage types accepted by the existing declaration rules.
	/// </summary>
	private static readonly HashSet<string> AllowedEnumStorageTypes =
	[
		"int", "uint", "short", "ushort", "long", "ulong", "char", "byte", "sbyte", "nint", "nuint"
	];

	/// <summary>
	/// Registers a delegate declaration, including generic templates, native calling-convention metadata,
	/// provenance-independent return validation, and ordinary delegate parameters.
	/// </summary>
	public void DeclareDelegate(DelegateDeclarationSyntax delegateDecl)
	{
		var mangledName = context.GetMangledName(delegateDecl.Name, context.CurrentNamespace);

		if (context.DelegateTypes.ContainsKey(mangledName)
			|| context.StructTypes.ContainsKey(mangledName)
			|| context.UnionTypes.ContainsKey(mangledName)
			|| context.InterfaceTypes.ContainsKey(mangledName)
			|| context.ProtocolTypes.ContainsKey(mangledName)
			|| context.EnumTypes.ContainsKey(mangledName)
			|| TypeSymbol.FromName(delegateDecl.Name) is not null)
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, delegateDecl.Span, $"Duplicate type definition '{delegateDecl.Name}'");
			return;
		}

		context.SymbolUnits[mangledName] = context.CurrentUnit!;

		var isNative = delegateDecl.IsNative;
		var callingConvention = delegateDecl.CallingConvention;

		// Generic delegate template: register the template; parameters/return are resolved
		// per-instantiation (mirroring generic structs).
		if (delegateDecl.GenericParameters.Count > 0)
		{
			context.GenericDelegateTemplates[mangledName] = delegateDecl;

			var activeParams = new HashSet<string>(delegateDecl.GenericParameters);
			context.ActiveGenericParametersStack.Push(activeParams);
			try
			{
				var templateReturn = context.ResolveType(delegateDecl.ReturnType);
				var templateParams = delegateDecl.Parameters
					.Select(p => new ParameterSymbol(p.Name, context.ResolveType(p.Type) ?? TypeSymbol.Void))
					.ToList();
				var templateSymbol = new DelegateTypeSymbol(
					mangledName,
					templateReturn ?? TypeSymbol.Void,
					templateParams,
					delegateDecl.GenericParameters,
					Array.Empty<TypeSymbol>(),
					isNative,
					callingConvention)
				{
					Visibility = delegateDecl.Visibility,
				};
				context.DelegateTypes[mangledName] = templateSymbol;
			}
			finally
			{
				context.ActiveGenericParametersStack.Pop();
			}
			return;
		}

		var returnType = context.ResolveType(delegateDecl.ReturnType);
		if (returnType is null)
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, delegateDecl.ReturnTypeSpan,
				$"Unknown return type '{delegateDecl.ReturnType}' in delegate declaration '{delegateDecl.Name}'");
			return;
		}

		if (!DelegateTypeHelpers.IsProvenanceIndependentReturn(returnType))
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
					$"Unknown parameter type '{p.Type}' in delegate declaration '{delegateDecl.Name}'");
				continue;
			}
			parameters.Add(new ParameterSymbol(p.Name, paramType));
		}

		context.DelegateTypes[mangledName] = new DelegateTypeSymbol(
			mangledName,
			returnType,
			parameters,
			Array.Empty<string>(),
			Array.Empty<TypeSymbol>(),
			isNative,
			callingConvention)
		{
			Visibility = delegateDecl.Visibility,
		};
	}


	/// <summary>
	/// Registers a struct declaration or generic struct template and materializes its field symbols while
	/// preserving the existing placeholder-based self-reference behavior.
	/// </summary>
	public void DeclareStruct(StructDeclarationSyntax structDecl)
	{
		var mangledName = context.GetMangledName(structDecl.Name, context.CurrentNamespace);

		if (context.StructTypes.ContainsKey(mangledName) || TypeSymbol.FromName(structDecl.Name) is not null)
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, structDecl.Span, $"Duplicate type definition '{structDecl.Name}'");
			return;
		}

		var appliedAttrs = _attributes.Verify(structDecl.Attributes, "Struct", []);
		var (isMustUse, mustUseMsg) = _attributes.ExtractMustUse(structDecl.Attributes);

		// If this is a generic struct template (e.g. struct Point<T>)
		if (structDecl.GenericParameters.Count > 0)
		{
			context.SymbolUnits[mangledName] = context.CurrentUnit!;
			context.GenericStructTemplates[mangledName] = structDecl;

			_genericDefaultCopies.Validate(structDecl);

			var placeholderFields = new List<StructFieldSymbol>();

			var templateSymbol = new StructTypeSymbol(mangledName, placeholderFields)
			{
				IsStrictMutability = appliedAttrs.Contains("StrictMutability"),
				Visibility = structDecl.Visibility,
				IsMustUse = isMustUse,
				MustUseMessage = mustUseMsg
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
			Visibility = structDecl.Visibility,
			IsMustUse = isMustUse,
			MustUseMessage = mustUseMsg
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


	/// <summary>
	/// Registers an interface declaration and its raw template symbol before contract hierarchy linking.
	/// </summary>
	public void DeclareInterface(InterfaceDeclarationSyntax interfaceDecl)
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


	/// <summary>
	/// Registers a protocol declaration and precomputes its canonical structural member tokens before hierarchy linking.
	/// </summary>
	public void DeclareProtocol(ProtocolDeclarationSyntax protocolDecl)
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
	/// Registers a union declaration or generic union template and resolves its variant payload types.
	/// </summary>
	public void DeclareUnion(UnionDeclarationSyntax unionDecl)
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

		var (isMustUse, mustUseMsg) = _attributes.ExtractMustUse(unionDecl.Attributes);

		if (unionDecl.GenericParameters.Count > 0)
		{
			context.SymbolUnits[mangledName] = context.CurrentUnit!;
			context.GenericUnionTemplates[mangledName] = unionDecl;

			_genericDefaultCopies.Validate(unionDecl);

			var placeholderFields = new List<UnionFieldSymbol>();
			var templateSymbol = new UnionTypeSymbol(mangledName, placeholderFields)
			{
				Visibility = unionDecl.Visibility,
				IsMustUse = isMustUse,
				MustUseMessage = mustUseMsg
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
			Visibility = unionDecl.Visibility,
			IsMustUse = isMustUse,
			MustUseMessage = mustUseMsg
		};
		context.UnionTypes[mangledName] = unionSymbol;
	}


	/// <summary>
	/// Registers an enum declaration, validates its storage and flag rules, and evaluates compile-time variant values.
	/// </summary>
	public void DeclareEnum(EnumDeclarationSyntax enumDecl)
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

		var appliedAttributes = _attributes.Verify(enumDecl.Attributes, "Struct", new List<string>());
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

		var (isMustUse, mustUseMsg) = _attributes.ExtractMustUse(enumDecl.Attributes);

		var enumSymbol = new EnumTypeSymbol(mangledName, variants, storageType)
		{
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
