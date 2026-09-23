using System.Text;
using Cvolo.Analysis;
using Cvolo.Analysis.Symbols;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Collections;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Expressions;
using Cvolo.Emitter.LLVM.Codegen.Values;
using LLVMSharp.Interop;

namespace Cvolo.Emitter.LLVM.Codegen.Emitters;

/// <summary>
/// Coordinates lowering of Cvolo expressions into LLVM values.
/// </summary>
/// <remarks>
/// This class owns expression dispatch, operators, casts, borrows, inline assembly, and heap-expression
/// orchestration. Calls, aggregate construction, delegate construction, cleanup, memory primitives, and
/// value coercion remain delegated to their dedicated components. Semantic type resolution is
/// shared through <see cref="ExpressionTypeResolver"/>, named-value access through
/// <see cref="ValueLoader"/>, and aggregate/member/index addressing through
/// <see cref="AggregateEmitter"/>.
/// </remarks>
/// <remarks>
/// Creates an expression emitter over the shared codegen run and the currently active function frame.
/// </remarks>
internal sealed class ExpressionEmitter(
	CodegenContext codegen,
	CleanupEmitter cleanup,
	MemoryEmitter memory,
	AggregateEmitter aggregates,
	CallEmitter calls,
	DelegateEmitter delegates,
	ValueCoercion coercion,
	Func<FunctionCodegenContext> getFunction,
	ExpressionTypeResolver expressionTypes,
	ValueLoader values)
{
	private readonly Dictionary<string, LLVMValueRef> _typeofGlobals = [];
	private int _typeofCounter;

	private LLVMBuilderRef Builder => codegen.Builder;
	private LLVMModuleRef Module => codegen.Module;
	private FunctionCodegenContext Function => getFunction();
	private BindingContext BindingContext => codegen.BindingContext ?? throw new InvalidOperationException("Expression emission requires an active binding context.");

	/// <summary>
	/// Lowers a semantic Cvolo type to the internal LLVM representation used by expression emission.
	/// </summary>
	private LLVMTypeRef LowerType(TypeSymbol type) => codegen.Types.Lower(type);

	/// <summary>Returns whether a visible storage slot aliases an imported foreign global.</summary>
	private bool IsForeignGlobalStorage(string sourceName, LLVMValueRef storage)
	{
		var key = codegen.GlobalVariables.ContainsKey(sourceName) ? sourceName : values.ResolveGlobalKey(sourceName);
		return key is not null
			&& codegen.ForeignGlobalNames.Contains(key)
			&& codegen.GlobalVariables.TryGetValue(key, out var global)
			&& global.Handle == storage.Handle;
	}

	/// <summary>
	/// Emits an LLVM value for a bound Cvolo expression while delegating specialized call, aggregate, and delegate forms to their dedicated emitters.
	/// </summary>
	public LLVMValueRef Emit(ExpressionSyntax expr)
	{
		if (expr is UnaryExpressionSyntax nativeAddress
			&& BindingContext.ResolvedNativeFunctionAddresses.TryGetValue(nativeAddress, out var nativeAddressBinding))
			return EmitNativeFunctionAddress(nativeAddressBinding.Function, nativeAddressBinding.Delegate);

		if (BindingContext.ResolvedFunctionConversions.TryGetValue(expr, out var groupFn))
			return delegates.EmitFunctionGroupConversion(expr, groupFn);
		if (expr is LambdaExpressionSyntax lamExpr)
			return delegates.EmitLambdaExpression(lamExpr);

		switch (expr)
		{
			case IntegerLiteralExpressionSyntax intLit:
				{
					// Suffixed literals fix their width; others are int unless out of int range.
					var litTy = intLit.LiteralType switch
					{
						"uint" => TypeSymbol.UInt,
						"long" => TypeSymbol.Long,
						"ulong" => TypeSymbol.ULong,
						_ => intLit.Value <= (ulong)int.MaxValue ? TypeSymbol.Int : TypeSymbol.Long,
					};
					return LLVMValueRef.CreateConstInt(LowerType(litTy), intLit.Value);
				}
			case DoubleLiteralExpressionSyntax dblLit:
				return dblLit.IsFloat
					? LLVMValueRef.CreateConstReal(LLVMTypeRef.Float, dblLit.Value)
					: LLVMValueRef.CreateConstReal(LLVMTypeRef.Double, dblLit.Value);
			case BooleanLiteralExpressionSyntax boolLit:
				return LLVMValueRef.CreateConstInt(LLVMTypeRef.Int1, boolLit.Value ? 1UL : 0UL);
			case StringLiteralExpressionSyntax strLit:
				return EmitStringLiteral(strLit.Value);
			case CharacterLiteralExpressionSyntax charLit:
				return LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, charLit.Value);
			case AsmExpressionSyntax asmExpr:
				return EmitInlineAsm(asmExpr);
			case NameofExpressionSyntax nameofExpr:
				return EmitStringLiteral(GetNameofFoldedName(nameofExpr.Argument));
			case TypeofExpressionSyntax typeofExpr:
				return EmitTypeof(typeofExpr);
			case NullLiteralExpressionSyntax:
				// Defensive: the binder rejects 'null' in safe code before emission.
				return LLVMValueRef.CreateConstPointerNull(LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0));
			case DefaultExpressionSyntax d:
				{
					// Bare 'default' is lowered by OptionalSyntaxRewriter inside declarations; if it ever
					// reaches codegen unresolved, validation already reported an error (codegen is skipped).
					var targetType = d.TypeName is null ? TypeSymbol.Int : BindingContext.ResolveType(d.TypeName)!;
					return LLVMValueRef.CreateConstNull(LowerType(targetType));
				}
			case IsPatternExpressionSyntax isPat:
				{
					// Operand may be any tagged or NPO union. NPO options store the payload pointer
					// flat (Some = non-zero, None = zero); tagged unions (Option<T> and general unions
					// such as Result<T, E>) store an i8 discriminator plus the payload, so the match
					// test compares the tag against the variant index (0-based union field order).
					var isNoneVariant = isPat.VariantName is "None";

					// Locate the option's storage: a borrow operand ('ref x') or a refvar-typed
					// operand reads through the reference (mirrors the switch emitter's ref-target
					// handling); otherwise the operand is the union value itself.
					var isBorrowTarget = isPat.Operand is BorrowExpressionSyntax;
					TypeSymbol? operandType;
					LLVMValueRef? storagePtr = null;
					if (isBorrowTarget)
					{
						var (targetPtr, targetUnion, _, _) = aggregates.GetFieldPointer(isPat.Operand);
						operandType = targetUnion is PointerTypeSymbol ptrTy ? ptrTy.ReferencedType : targetUnion;
						storagePtr = targetPtr;
					}
					else
					{
						operandType = expressionTypes.Resolve(isPat.Operand);
						if (operandType is PointerTypeSymbol aliasPtr && aliasPtr.ReferencedType is UnionTypeSymbol)
						{
							// refvar alias: the operand's value is a pointer to the option union.
							isBorrowTarget = true;
							storagePtr = Emit(isPat.Operand);
						}
					}

					var unionType = operandType is PointerTypeSymbol pt ? pt.ReferencedType : operandType;
					var unionTypeSym = unionType as UnionTypeSymbol;
					var isTagged = unionTypeSym is not null && !unionTypeSym.IsNpoEligible;

					LLVMValueRef isSome;
					LLVMValueRef? tagPayloadPtr = null;
					if (isTagged)
					{
						var unionLayout = LowerType(unionType);

						// GEP directly into the union storage for borrows; otherwise materialize a
						// pointer to the fetched union value so tag/payload offsets can be computed.
						LLVMValueRef unionAccessPtr;
						if (storagePtr is not null)
						{
							unionAccessPtr = storagePtr.Value;
						}
						else
						{
							var structVal = Emit(isPat.Operand);
							unionAccessPtr = Builder.BuildAlloca(unionLayout, "is_tag_tmp");
							Builder.BuildStore(structVal, unionAccessPtr);
						}

						var tagPtr = Builder.BuildGEP2(unionLayout, unionAccessPtr, new LLVMValueRef[]
						{
							LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0),
							LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0)
						}, "is_tag_ptr");
						var tagValue = Builder.BuildLoad2(LLVMTypeRef.Int8, tagPtr, "is_tag_val2");

						var matchVariant = isPat.VariantName;
						var matchIndex = codegen.AggregateLayout.GetFieldIndex(unionTypeSym!, matchVariant);
						isSome = Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, tagValue,
							LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, (ulong)matchIndex),
							isNoneVariant ? "is_none" : "is_some");

						tagPayloadPtr = Builder.BuildGEP2(unionLayout, unionAccessPtr, new LLVMValueRef[]
						{
							LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0),
							LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 1)
						}, "is_payload_ptr");
					}
					else
					{
						// NPO: the flat value IS the payload pointer.
						LLVMValueRef operandVal;
						if (storagePtr is not null)
						{
							var unionLayoutType = LowerType(unionType);
							operandVal = Builder.BuildLoad2(unionLayoutType, storagePtr.Value, "is_flat_val");
						}
						else
						{
							operandVal = Emit(isPat.Operand);
						}

						isSome = Builder.BuildICmp(
							isNoneVariant ? LLVMIntPredicate.LLVMIntEQ : LLVMIntPredicate.LLVMIntNE,
							operandVal,
							LLVMValueRef.CreateConstPointerNull(operandVal.TypeOf),
							isNoneVariant ? "is_none" : "is_some");
					}

					if (isPat.BoundName is not null && unionTypeSym is not null)
					{
						TypeSymbol? promotedType = unionTypeSym.IsNpoEligible
							? unionTypeSym.FindField(isPat.VariantName)?.Type ?? unionTypeSym
							: isBorrowTarget
								? new PointerTypeSymbol(
									unionTypeSym.FindField(isPat.VariantName)?.Type ?? unionTypeSym,
									isMutable: expressionTypes.Resolve(isPat.Operand) is PointerTypeSymbol borrowPtr && borrowPtr.IsMutable)
								: unionTypeSym.FindField(isPat.VariantName)?.Type ?? unionTypeSym;

						if (!Function.Locals.TryGetValue(isPat.BoundName, out var bindSlot))
						{
							var bindTy = LowerType(promotedType);
							bindSlot = Builder.BuildAlloca(bindTy, isPat.BoundName);
							Function.Locals[isPat.BoundName] = bindSlot;
							Function.VariableTypes[isPat.BoundName] = promotedType;
						}

						// Every pattern evaluation re-binds the payload: the scrutinee changes
						// between pattern sites (e.g. the same name bound in a later loop).
						if (isTagged)
						{
							if (isBorrowTarget)
							{
								// `ref opt is Some v`: bind v as a reference to the payload slot.
								var castPtr = Builder.BuildBitCast(tagPayloadPtr!.Value, LowerType(promotedType), "is_payload_ref");
								Builder.BuildStore(castPtr, bindSlot);
							}
							else
							{
								var castPtr = Builder.BuildBitCast(tagPayloadPtr!.Value, LLVMTypeRef.CreatePointer(LowerType(promotedType), 0), "is_payload_cast");
								var payloadVal = Builder.BuildLoad2(LowerType(promotedType), castPtr, "is_payload_val");
								Builder.BuildStore(payloadVal, bindSlot);
							}
						}
						else
						{
							var operandVal2 = storagePtr is not null
								? Builder.BuildLoad2(LowerType(unionType), storagePtr.Value, "is_flat_val2")
								: Emit(isPat.Operand);
							var bindVal = operandVal2.TypeOf == LowerType(promotedType)
								? operandVal2
								: Builder.BuildPointerCast(operandVal2, LowerType(promotedType), "is_bind");
							Builder.BuildStore(bindVal, bindSlot);
						}
					}

					return isSome;
				}
			case IdentifierExpressionSyntax id:
				return values.Load(id.Name);
			case MemberAccessExpressionSyntax m:
				{
					// Namespace-qualified global (e.g. 'System.Math.UInt.MaxValue'): the member
					// access is the whole global slot, so load it directly instead of GEPing.
					if (aggregates.TryExtractQualifiedGlobalKey(m) is { } globalMemberKey
						&& codegen.GlobalVariables.TryGetValue(globalMemberKey, out var globalMemberPtr))
					{
						return Builder.BuildLoad2(LowerType(codegen.GlobalVariableTypes[globalMemberKey]), globalMemberPtr, "global_member_val");
					}

					// Enum scoped-variant access: EnumName.Variant is a compile-time constant.
					if (aggregates.TryResolveEnumVariantReceiver(m) is { } enumConstType
						&& enumConstType.FindVariant(m.MemberName) is { } enumConstVariant)
					{
						return LLVMValueRef.CreateConstInt(LowerType(enumConstType), unchecked((ulong)enumConstVariant.Value));
					}

					// Enum metaprogramming surface (spec Â§5): Values is a read-only slice
					// materialized from a .rodata global; Min/Max/Count are compile-time ints.
					if (aggregates.TryResolveEnumVariantReceiver(m) is { } enumMetaType)
					{
						if (m.MemberName == "Values")
						{
							var (valuesPtr, valuesType) = aggregates.EmitEnumValuesSlicePointer(enumMetaType);
							return Builder.BuildLoad2(LowerType(valuesType), valuesPtr, "enum_values");
						}

						if (m.MemberName is "Min" or "Max" or "Count")
						{
							var metaValue = m.MemberName switch
							{
								"Min" => enumMetaType.Variants.Min(v => v.Value),
								"Max" => enumMetaType.Variants.Max(v => v.Value),
								"Count" => enumMetaType.IsFlags
									? enumMetaType.Variants.Count(v => v.Value > 0 && IsPowerOfTwo(v.Value))
									: enumMetaType.Variants.Count,
								_ => 0,
							};
							return LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, unchecked((ulong)metaValue));
						}
					}

					var (ptr, type, _, tbaa) = aggregates.GetFieldPointer(m);
					var instr = Builder.BuildLoad2(LowerType(type), ptr, "member_val");
					aggregates.ApplyTbaa(tbaa, instr);
					return instr;
				}
			case IndexExpressionSyntax idx:
				{
					var (ptr, type, _, tbaa) = aggregates.GetFieldPointer(idx);
					var instr = Builder.BuildLoad2(LowerType(type), ptr, "index_val");
					aggregates.ApplyTbaa(tbaa, instr);
					return instr;
				}
			case BorrowExpressionSyntax b:
				return EmitBorrowExpression(b);
			case HeapAllocationExpressionSyntax h:
				return EmitHeapAllocation(h);
			case HeapArrayAllocationExpressionSyntax hArr:
				return EmitHeapArrayAllocation(hArr);
			case StructInitializationExpressionSyntax s:
				return aggregates.EmitStructInitialization(s);
			case ArrayInitializationExpressionSyntax a:
				return aggregates.EmitArrayInitialization(a);
			case ArrayReplicationExpressionSyntax arrRepl:
				return aggregates.EmitArrayReplication(arrRepl);
			case ParenthesizedStructInitializerExpressionSyntax parenStruct:
				return aggregates.EmitParenthesizedStructInitialization(parenStruct);
			case CallExpressionSyntax call:
				return calls.Emit(call);
			case BinaryExpressionSyntax bin:
				return EmitBinaryExpression(bin);
			case UnaryExpressionSyntax unary:
				return EmitUnaryExpression(unary);
			case TernaryExpressionSyntax t:
				return EmitTernaryExpression(t);
			default:
				throw new InvalidOperationException($"Unknown expression type: {expr.GetType()}");
		}
	}

	/// <summary>
	/// Materializes a UTF-8 string literal as a module-level LLVM string pointer.
	/// </summary>
	public LLVMValueRef EmitStringLiteral(string value)
	{
		// Safely wraps string allocation natively via robust built-in BuildGlobalStringPtr API
		return Builder.BuildGlobalStringPtr(value, "str");
	}

	/// <summary>
	/// Extracts the compile-time identifier text represented by a nameof operand.
	/// </summary>
	private static string GetNameofFoldedName(ExpressionSyntax expr) => expr switch
	{
		IdentifierExpressionSyntax id => id.Name,
		MemberAccessExpressionSyntax m => m.MemberName,
		_ => "<invalid>",
	};

	/// <summary>
	/// Computes the stable 32-bit FNV-1a hash used by synthesized System.Type values.
	/// </summary>
	private static uint Fnv1a32(string value)
	{
		unchecked
		{
			var hash = 2166136261;
			foreach (var c in value)
			{
				hash ^= c;
				hash *= 16777619;
			}

			return hash;
		}
	}

	/// <summary>
	/// Materializes and caches the constant System.Type value produced by a typeof expression.
	/// </summary>
	private LLVMValueRef EmitTypeof(TypeofExpressionSyntax typeofExpr)
	{
		var typeName = typeofExpr.TypeName;
		if (_typeofGlobals.TryGetValue(typeName, out var cached))
			return cached;

		var systemType = BindingContext.ResolveType("System.Type")
			?? throw new InvalidOperationException($"typeof requires System.Type to be available for '{typeName}'.");
		var systemLlvmType = LowerType(systemType);

		// @type_name.<name>.<n> = private unnamed_addr constant [N x i8] c"<name>\00"
		var bytes = typeName.Select(c => (byte)c).Append((byte)0).ToArray();
		var arrayType = LLVMTypeRef.CreateArray(LLVMTypeRef.Int8, (uint)bytes.Length);
		var nameGlobal = Module.AddGlobal(arrayType, $"type_name_{typeName.ToLowerInvariant()}_{++_typeofCounter}");
		nameGlobal.Initializer = LLVMValueRef.CreateConstArray(
			LLVMTypeRef.Int8,
			[.. bytes.Select(b => LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, b))]);
		nameGlobal.IsGlobalConstant = true;
		nameGlobal.Linkage = LLVMLinkage.LLVMPrivateLinkage;

		var zero = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, 0);
		var namePtr = LLVMValueRef.CreateConstInBoundsGEP2(arrayType, nameGlobal, [zero, zero]);

		var value = LLVMValueRef.CreateConstNamedStruct(systemLlvmType,
		[
			LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, Fnv1a32(typeName)),
			namePtr,
		]);
		_typeofGlobals[typeName] = value;
		return value;
	}

	/// <summary>
	/// Lowers an inline-assembly expression, including operand constraints and result typing.
	/// </summary>
	private LLVMValueRef EmitInlineAsm(AsmExpressionSyntax asm)
	{
		var outputs = asm.Operands.Where(o => o.IsOutput).ToList();
		var inputs = asm.Operands.Where(o => !o.IsOutput).ToList();
		TypeSymbol resultType;
		if (asm.ResultType is not null && BindingContext.ResolveType(asm.ResultType) is { } rt)
			resultType = rt;
		else if (outputs.Count > 0)
			resultType = expressionTypes.Resolve(outputs[0].Expression);
		else
			resultType = TypeSymbol.Void;

		// Constraint list: outputs first, then inputs, then clobbers, then intel-dialect keyword.
		var constraints = new List<string>();
		constraints.AddRange(outputs.Select(o => o.Constraint));
		constraints.AddRange(inputs.Select(i => i.Constraint));
		constraints.AddRange(asm.Clobbers.Select(c => $"~{{{c}}}"));
		if ((asm.Options & AsmOptions.Intel) != 0)
			constraints.Add("inteldialect");

		// GCC-style %[name] references in the template -> LLVM positional $N (outputs first).
		var template = InlineAsmRewriteTemplate(asm.Template, outputs, inputs);

		var fnType = LLVMTypeRef.CreateFunction(
			LowerType(resultType),
			[.. inputs.Select(i => LowerType(expressionTypes.Resolve(i.Expression)))]);

		var asmFn = LLVMValueRef.CreateConstInlineAsm(
			fnType,
			template,
			string.Join(",", constraints),
			(asm.Options & AsmOptions.Volatile) != 0,
			(asm.Options & AsmOptions.AlignStack) != 0);

		var args = inputs.Select(i => Emit(i.Expression)).ToArray();
		var callName = resultType.Equals(TypeSymbol.Void) ? "" : "asm";
		return Builder.BuildCall2(fnType, asmFn, args, callName);
	}

	/// <summary>
	/// Rewrites named inline-assembly placeholders to the positional operands expected by LLVM inline asm.
	/// </summary>
	private static string InlineAsmRewriteTemplate(string template, List<AsmOperandSyntax> outputs, List<AsmOperandSyntax> inputs)
	{
		var all = outputs.Concat(inputs).ToList();
		var named = new Dictionary<string, int>(StringComparer.Ordinal);
		for (var i = 0; i < all.Count; i++)
		{
			if (all[i].Name is not null)
				named[all[i].Name!] = i;
		}

		var sb = new StringBuilder();
		for (var i = 0; i < template.Length; i++)
		{
			if (template[i] == '%' && i + 1 < template.Length && template[i + 1] == '[')
			{
				var close = template.IndexOf(']', i + 2);
				if (close > i + 2)
				{
					var name = template[(i + 2)..close];
					if (named.TryGetValue(name, out var operandIndex))
					{
						sb.Append('$').Append(operandIndex);
						i = close;
						continue;
					}
				}
			}

			sb.Append(template[i]);
		}

		return sb.ToString();
	}


	/// <summary>
	/// Returns whether an expression is a compile-time string literal concatenation tree.
	/// </summary>
	internal static bool IsConstantStringTree(ExpressionSyntax expr)
	{
		return expr switch
		{
			StringLiteralExpressionSyntax => true,
			BinaryExpressionSyntax bin when bin.Operator == "+" =>
				IsConstantStringTree(bin.Left) && IsConstantStringTree(bin.Right),
			_ => false,
		};
	}

	/// <summary>
	/// Folds a compile-time string concatenation tree into its final literal value.
	/// </summary>
	private static string ConstantStringValue(ExpressionSyntax expr)
	{
		return expr switch
		{
			StringLiteralExpressionSyntax lit => lit.Value,
			BinaryExpressionSyntax bin when bin.Operator == "+" =>
				ConstantStringValue(bin.Left) + ConstantStringValue(bin.Right),
			_ => throw new InvalidOperationException($"Cannot fold non-constant string expression '{expr.GetType().Name}'."),
		};
	}


	/// <summary>
	/// Lowers binary operators, including arithmetic, comparisons, logical operations, and assignments.
	/// </summary>
	private LLVMValueRef EmitBinaryExpression(BinaryExpressionSyntax bin)
	{
		// Intercept assignment operators first to prevent mathematical promotion conflicts
		if (bin.Operator == "=")
		{
			return EmitAssignStore(bin);
		}

		// Fold '+': constant string concatenation lowers to a single constant.
		if (bin.Operator == "+" && IsConstantStringTree(bin.Left) && IsConstantStringTree(bin.Right))
		{
			return EmitStringLiteral(ConstantStringValue(bin.Left) + ConstantStringValue(bin.Right));
		}

		var left = Emit(bin.Left);
		var right = Emit(bin.Right);
		var lTy = expressionTypes.Resolve(bin.Left);
		var rTy = expressionTypes.Resolve(bin.Right);

		if (lTy is PointerTypeSymbol lPtr && left.TypeOf.Kind == LLVMTypeKind.LLVMPointerTypeKind)
		{
			left = Builder.BuildLoad2(LowerType(lPtr.ReferencedType), left, "deref_left");
			lTy = lPtr.ReferencedType;
		}

		// Implicit Dereference for right operand
		if (rTy is PointerTypeSymbol rPtr && right.TypeOf.Kind == LLVMTypeKind.LLVMPointerTypeKind)
		{
			right = Builder.BuildLoad2(LowerType(rPtr.ReferencedType), right, "deref_right");
			rTy = rPtr.ReferencedType;
		}

		// 1. Unmanaged Pointer Arithmetic
		if (lTy is RawPointerTypeSymbol rawPtrL && TypeSymbol.IsIntegerType(rTy))
		{
			if (bin.Operator == "+")
			{
				return Builder.BuildGEP2(LowerType(rawPtrL.ElementType), left, new[] { right }, "ptr_add");
			}

			if (bin.Operator == "-")
			{
				var negRight = Builder.BuildNeg(right, "ptr_sub_neg");
				return Builder.BuildGEP2(LowerType(rawPtrL.ElementType), left, new[] { negRight }, "ptr_sub");
			}
		}
		else if (rTy is RawPointerTypeSymbol rawPtrR && TypeSymbol.IsIntegerType(lTy) && bin.Operator == "+")
		{
			return Builder.BuildGEP2(LowerType(rawPtrR.ElementType), right, new[] { left }, "ptr_add");
		}

		var isDouble = lTy.Equals(TypeSymbol.Double) || rTy.Equals(TypeSymbol.Double);
		var isFloat = lTy.Equals(TypeSymbol.Float) || rTy.Equals(TypeSymbol.Float);

		if (isDouble || isFloat)
		{
			var targetFpType = isDouble ? LLVMTypeRef.Double : LLVMTypeRef.Float;

			// Promote Left operand
			if (TypeSymbol.IsIntegerType(lTy))
			{
				left = TypeSymbol.IsSignedIntegerType(lTy)
					? Builder.BuildSIToFP(left, targetFpType, "sitofp_left")
					: Builder.BuildUIToFP(left, targetFpType, "uitofp_left");
			}
			else if (isDouble && lTy.Equals(TypeSymbol.Float))
			{
				left = Builder.BuildFPExt(left, LLVMTypeRef.Double, "fpext_left");
			}

			// Promote Right operand
			if (TypeSymbol.IsIntegerType(rTy))
			{
				right = TypeSymbol.IsSignedIntegerType(rTy)
					? Builder.BuildSIToFP(right, targetFpType, "sitofp_right")
					: Builder.BuildUIToFP(right, targetFpType, "uitofp_right");
			}
			else if (isDouble && rTy.Equals(TypeSymbol.Float))
			{
				right = Builder.BuildFPExt(right, LLVMTypeRef.Double, "fpext_right");
			}

			return bin.Operator switch
			{
				"+" => Builder.BuildFAdd(left, right),
				"-" => Builder.BuildFSub(left, right),
				"*" => Builder.BuildFMul(left, right),
				"/" => Builder.BuildFDiv(left, right),
				"%" => Builder.BuildFRem(left, right),
				"==" => Builder.BuildFCmp(LLVMRealPredicate.LLVMRealOEQ, left, right),
				"!=" => Builder.BuildFCmp(LLVMRealPredicate.LLVMRealONE, left, right),
				"<" => Builder.BuildFCmp(LLVMRealPredicate.LLVMRealOLT, left, right),
				">" => Builder.BuildFCmp(LLVMRealPredicate.LLVMRealOGT, left, right),
				"<=" => Builder.BuildFCmp(LLVMRealPredicate.LLVMRealOLE, left, right),
				">=" => Builder.BuildFCmp(LLVMRealPredicate.LLVMRealOGE, left, right),
				_ => throw new InvalidOperationException($"Unknown floating-point operator '{bin.Operator}'"),
			};
		}

		// Integer width promotion: when mixing integer widths, zext/sext the narrower
		// operand up to the wider operand's width before the operation.
		if (TypeSymbol.IsIntegerType(lTy) && TypeSymbol.IsIntegerType(rTy))
		{
			var lWidth = TypeSymbol.IntegerBitWidth(lTy);
			var rWidth = TypeSymbol.IntegerBitWidth(rTy);

			if (lWidth < rWidth)
			{
				left = TypeSymbol.IsSignedIntegerType(lTy)
					? Builder.BuildSExt(left, LowerType(rTy), "promote_left_sext")
					: Builder.BuildZExt(left, LowerType(rTy), "promote_left_zext");
			}
			else if (rWidth < lWidth)
			{
				right = TypeSymbol.IsSignedIntegerType(rTy)
					? Builder.BuildSExt(right, LowerType(lTy), "promote_right_sext")
					: Builder.BuildZExt(right, LowerType(lTy), "promote_right_zext");
			}

			// Signed division/modulo must be relaxed for unsigned operands
			if (bin.Operator is "/" or "%" && (!TypeSymbol.IsSignedIntegerType(lTy) || !TypeSymbol.IsSignedIntegerType(rTy)))
			{
				return bin.Operator switch
				{
					"/" => Builder.BuildUDiv(left, right),
					_ => Builder.BuildURem(left, right),
				};
			}
		}

		return bin.Operator switch
		{
			"+" => Builder.BuildAdd(left, right),
			"-" => Builder.BuildSub(left, right),
			"*" => Builder.BuildMul(left, right),
			"/" => Builder.BuildSDiv(left, right),
			"%" => Builder.BuildSRem(left, right),
			"==" => Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, left, right),
			"!=" => Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, left, right),
			"<" => Builder.BuildICmp(LLVMIntPredicate.LLVMIntSLT, left, right),
			">" => Builder.BuildICmp(LLVMIntPredicate.LLVMIntSGT, left, right),
			"<=" => Builder.BuildICmp(LLVMIntPredicate.LLVMIntSLE, left, right),
			">=" => Builder.BuildICmp(LLVMIntPredicate.LLVMIntSGE, left, right),
			"&" => Builder.BuildAnd(left, right),
			"|" => Builder.BuildOr(left, right),
			"^" => Builder.BuildXor(left, right),
			"&&" => Builder.BuildAnd(left, right),
			"||" => Builder.BuildOr(left, right),
			"<<" => Builder.BuildShl(left, right),
			">>" => Builder.BuildAShr(left, right),
			">>>" => Builder.BuildLShr(left, right),
			_ => throw new InvalidOperationException($"Unknown binary operator '{bin.Operator}'"),
		};
	}

	/// <summary>
	/// Stores an assignment result into its bound destination while preserving cleanup, coercion, and ownership semantics.
	/// </summary>
	private LLVMValueRef EmitAssignStore(BinaryExpressionSyntax bin)
	{
		// 1. Discard assignment: _ = Call(); -> evaluate RHS and discard
		if (bin.Left is IdentifierExpressionSyntax discardId && discardId.Name == "_")
		{
			return Emit(bin.Right);
		}

		var rTy = expressionTypes.Resolve(bin.Right);

		// 2. Handle in-place constructor assignments (e.g. x = Resource(10) or arr[0] = Resource(100))
		// Must be checked BEFORE evaluating bin.Right to prevent void-store LLVM crashes.
		if (bin.Right is CallExpressionSyntax ctorCall && expressionTypes.IsConstructorCall(ctorCall, rTy))
		{
			if (bin.Left is IdentifierExpressionSyntax ctorId)
			{
				if (Function.Locals.TryGetValue(ctorId.Name, out var ptr))
				{
					calls.Emit(ctorCall, ptr);
					return ptr;
				}
				else if (values.ResolveGlobalKey(ctorId.Name) is { } ctorGlobalKey && codegen.GlobalVariables.TryGetValue(ctorGlobalKey, out var ctorGlobalPtr))
				{
					calls.Emit(ctorCall, ctorGlobalPtr);
					return ctorGlobalPtr;
				}
				else if (Function.Locals.TryGetValue("this", out var thisPtr))
				{
					var thisType = Function.VariableTypes["this"] as PointerTypeSymbol;
					var structType = thisType?.ReferencedType as StructTypeSymbol;
					if (structType?.FindField(ctorId.Name) is not null)
					{
						var (fieldPtr, _, _, _) = aggregates.GetFieldPointer(ctorId);
						calls.Emit(ctorCall, fieldPtr);
						return fieldPtr;
					}
				}
			}
			else if (bin.Left is MemberAccessExpressionSyntax m)
			{
				var (fieldPtr, _, _, _) = aggregates.GetFieldPointer(m);
				calls.Emit(ctorCall, fieldPtr);
				return fieldPtr;
			}
			else if (bin.Left is IndexExpressionSyntax idx)
			{
				var (elementPtr, _, _, _) = aggregates.GetFieldPointer(idx);
				calls.Emit(ctorCall, elementPtr);
				return elementPtr;
			}
			else if (bin.Left is UnaryExpressionSyntax { Operator: "*" } deref)
			{
				var targetPtr = Emit(deref.Operand);
				calls.Emit(ctorCall, targetPtr);
				return targetPtr;
			}
			else if (bin.Left is CallExpressionSyntax callLeft)
			{
				var targetPtr = calls.Emit(callLeft);
				calls.Emit(ctorCall, targetPtr);
				return targetPtr;
			}
		}

		// 3. Evaluate right-hand side expression
		var right = Emit(bin.Right);
		var llvmTy = LowerType(rTy);

		// 4. Handle Heap-Allocated owning handles
		if (bin.Right is IdentifierExpressionSyntax heapId
			&& Function.HeapAllocatedVars.Contains(heapId.Name)
			&& Function.Locals.TryGetValue(heapId.Name, out var handleSlot)
			&& expressionTypes.Resolve(bin.Left) is PointerTypeSymbol)
		{
			right = Builder.BuildLoad2(LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), handleSlot, "handle_addr");
			if (rTy is StructTypeSymbol handleStruct)
				rTy = new PointerTypeSymbol(handleStruct, isMutable: false);
			llvmTy = LowerType(rTy);
		}

		// 5. Dereference aggregate pointers before storing them into value targets
		if (right.TypeOf.Kind == LLVMTypeKind.LLVMPointerTypeKind
			&& (rTy is StructTypeSymbol || rTy is ArrayTypeSymbol || rTy is UnionTypeSymbol { IsUnsafe: true }))
		{
			right = Builder.BuildLoad2(llvmTy, right, "loaded_assign_struct");
		}

		// 6. Execute Assignment to Target (Left-Hand Side)
		if (bin.Left is IdentifierExpressionSyntax id)
		{
			if (Function.Locals.TryGetValue(id.Name, out var ptr))
			{
				var type = Function.VariableTypes[id.Name];

				if (bin.Right is NullLiteralExpressionSyntax && type is UnionTypeSymbol optionUnion && optionUnion.IsOption)
				{
					var unionLayout = LowerType(optionUnion);

					// Null-Pointer Optimization: store flat nullptr (None == zero) instead of a tag.
					if (optionUnion.IsNpoEligible)
					{
						Builder.BuildStore(LLVMValueRef.CreateConstPointerNull(unionLayout), ptr);
					}
					else
					{
						var fieldIndex = codegen.AggregateLayout.GetFieldIndex(optionUnion, "None");

						var tagPtr = Builder.BuildGEP2(unionLayout, ptr, new LLVMValueRef[] {
							LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0),
							LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0)
						}, "union_tag_ptr");
						Builder.BuildStore(LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, (ulong)fieldIndex), tagPtr);
					}

					return right;
				}
				else if (type is PointerTypeSymbol)
				{
					if (bin.Right is BorrowExpressionSyntax
						|| (rTy is PointerTypeSymbol rhsRef && cleanup.TypeEscapesHeap(rhsRef.ReferencedType)))
					{
						Builder.BuildStore(right, ptr);
					}
					else
					{
						var actualPtr = Builder.BuildLoad2(LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), ptr, "target_ptr");
						var coerced = coercion.CoerceIntegerWidth(right, rTy, type);
						coerced = coercion.CoerceFloatWidth(coerced, rTy, type);
						Builder.BuildStore(coerced, actualPtr);
					}
				}
				else
				{
					if (type is UnionTypeSymbol reassignedUnion
						&& cleanup.UnionNeedsTagCheckedCleanup(reassignedUnion)
						&& bin.Right is StructInitializationExpressionSyntax
						&& !Function.MovedVars.Contains(id.Name)
						&& !Function.DisposedVars.Contains(id.Name))
					{
						cleanup.EmitUnionTagCheckedCleanup(id.Name, ptr, reassignedUnion);
					}

					if (type is UnionTypeSymbol && bin.Right is StructInitializationExpressionSyntax reinit)
					{
						aggregates.EmitStructInitializationInPlace(reinit, ptr);
					}
					else
					{
						var coerced = (bin.Right is MemberAccessExpressionSyntax or IndexExpressionSyntax)
							? coercion.CoerceReferenceToValue(right, expressionTypes.Resolve(bin.Right))
							: right;
						coerced = coercion.CoerceIntegerWidth(coerced, rTy, type);
						coerced = coercion.CoerceFloatWidth(coerced, rTy, type);
						if (type.Equals(TypeSymbol.Bool) && IsForeignGlobalStorage(id.Name, ptr))
							coerced = Builder.BuildZExt(coerced, LLVMTypeRef.Int8, "foreign_bool_from_internal");
						Builder.BuildStore(coerced, ptr);
					}
				}

				return right;
			}
			else if (values.ResolveGlobalKey(id.Name) is { } globalKey && codegen.GlobalVariables.TryGetValue(globalKey, out var globalPtr))
			{
				var globalType = codegen.GlobalVariableTypes[globalKey];
				if (globalType is UnionTypeSymbol && bin.Right is StructInitializationExpressionSyntax globalReinit)
					aggregates.EmitStructInitializationInPlace(globalReinit, globalPtr);
				else
				{
					var coerced = (bin.Right is MemberAccessExpressionSyntax or IndexExpressionSyntax)
						? coercion.CoerceReferenceToValue(right, expressionTypes.Resolve(bin.Right))
						: right;
					coerced = coercion.CoerceIntegerWidth(coerced, rTy, globalType);
					coerced = coercion.CoerceFloatWidth(coerced, rTy, globalType);
					if (globalType.Equals(TypeSymbol.Bool) && codegen.ForeignGlobalNames.Contains(globalKey))
						coerced = Builder.BuildZExt(coerced, LLVMTypeRef.Int8, "foreign_bool_from_internal");
					Builder.BuildStore(coerced, globalPtr);
				}

				return right;
			}
			else if (Function.Locals.TryGetValue("this", out var thisPtr))
			{
				var thisType = Function.VariableTypes["this"] as PointerTypeSymbol;
				var refType = thisType!.ReferencedType;

				if (refType is StructTypeSymbol structType)
				{
					var field = structType.FindField(id.Name);
					if (field is not null)
					{
						var (fieldPtr, _, _, tbaa) = aggregates.GetFieldPointer(id);
						if (field.Type is UnionTypeSymbol && bin.Right is StructInitializationExpressionSyntax fieldReinit)
							aggregates.EmitStructInitializationInPlace(fieldReinit, fieldPtr);
						else
						{
							var coerced = coercion.CoerceIntegerWidth(right, rTy, field.Type);
							var store = Builder.BuildStore(coercion.CoerceFloatWidth(coerced, rTy, field.Type), fieldPtr);
							aggregates.ApplyTbaa(tbaa, store);
						}

						return right;
					}
				}
				else if (refType is UnionTypeSymbol unionType)
				{
					var field = unionType.FindField(id.Name);
					if (field is not null)
					{
						var (fieldPtr, _, _, _) = aggregates.GetFieldPointer(id);
						if (bin.Right is StructInitializationExpressionSyntax thisFieldReinit)
							aggregates.EmitStructInitializationInPlace(thisFieldReinit, fieldPtr);
						else
						{
							var coerced = coercion.CoerceIntegerWidth(right, rTy, field.Type);
							Builder.BuildStore(coercion.CoerceFloatWidth(coerced, rTy, field.Type), fieldPtr);
						}

						return right;
					}
				}
			}
		}
		else if (bin.Left is MemberAccessExpressionSyntax m)
		{
			var (fieldPtr, fieldType, _, tbaa) = aggregates.GetFieldPointer(m);

			// Set active variant tag when assigning a union variant (e.g. 'this.Some = value')
			if (expressionTypes.Resolve(m.Expression) is UnionTypeSymbol unionType && !unionType.IsNpoEligible && !unionType.IsUnsafe)
			{
				var (unionPtr, _, _, _) = aggregates.GetFieldPointer(m.Expression);
				var variantIndex = codegen.AggregateLayout.GetFieldIndex(unionType, m.MemberName);
				var unionLayout = LowerType(unionType);
				var tagPtr = Builder.BuildGEP2(unionLayout, unionPtr, new LLVMValueRef[] {
					LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0),
					LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0)
				}, "union_tag_ptr");
				Builder.BuildStore(LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, (ulong)variantIndex), tagPtr);
			}

			if (fieldType is UnionTypeSymbol fieldUnion
				&& cleanup.UnionNeedsTagCheckedCleanup(fieldUnion)
				&& bin.Right is StructInitializationExpressionSyntax)
			{
				cleanup.EmitUnionTagCheckedCleanup(m.MemberName, fieldPtr, fieldUnion);
			}

			if (fieldType is UnionTypeSymbol && bin.Right is StructInitializationExpressionSyntax fieldReinit)
				aggregates.EmitStructInitializationInPlace(fieldReinit, fieldPtr);
			else
			{
				var coerced = (bin.Right is MemberAccessExpressionSyntax or IndexExpressionSyntax)
					? coercion.CoerceReferenceToValue(right, expressionTypes.Resolve(bin.Right))
					: right;
				coerced = coercion.CoerceIntegerWidth(coerced, rTy, fieldType);
				coerced = coercion.CoerceFloatWidth(coerced, rTy, fieldType);
				var fieldStore = Builder.BuildStore(coerced, fieldPtr);
				aggregates.ApplyTbaa(tbaa, fieldStore);
			}

			return right;
		}
		else if (bin.Left is IndexExpressionSyntax idx)
		{
			var (elementPtr, elementType, _, tbaa) = aggregates.GetFieldPointer(idx);
			if (elementType is UnionTypeSymbol elemUnion
				&& cleanup.UnionNeedsTagCheckedCleanup(elemUnion)
				&& bin.Right is StructInitializationExpressionSyntax)
			{
				cleanup.EmitUnionTagCheckedCleanup("elem", elementPtr, elemUnion);
			}

			if (elementType is UnionTypeSymbol && bin.Right is StructInitializationExpressionSyntax elemReinit)
				aggregates.EmitStructInitializationInPlace(elemReinit, elementPtr);
			else
			{
				var coerced = (bin.Right is MemberAccessExpressionSyntax or IndexExpressionSyntax)
					? coercion.CoerceReferenceToValue(right, expressionTypes.Resolve(bin.Right))
					: right;
				coerced = coercion.CoerceIntegerWidth(coerced, rTy, elementType);
				coerced = coercion.CoerceFloatWidth(coerced, rTy, elementType);
				var elemStore = Builder.BuildStore(coerced, elementPtr);
				aggregates.ApplyTbaa(tbaa, elemStore);
			}

			return right;
		}
		else if (bin.Left is UnaryExpressionSyntax { Operator: "*" } deref)
		{
			var targetPtr = Emit(deref.Operand);
			var targetType = expressionTypes.Resolve(bin.Left);

			if (targetType is UnionTypeSymbol elemUnion
				&& cleanup.UnionNeedsTagCheckedCleanup(elemUnion)
				&& bin.Right is StructInitializationExpressionSyntax)
			{
				cleanup.EmitUnionTagCheckedCleanup("deref", targetPtr, elemUnion);
			}

			if (targetType is UnionTypeSymbol && bin.Right is StructInitializationExpressionSyntax elemReinit)
			{
				aggregates.EmitStructInitializationInPlace(elemReinit, targetPtr);
			}
			else
			{
				var coerced = (bin.Right is MemberAccessExpressionSyntax or IndexExpressionSyntax)
					? coercion.CoerceReferenceToValue(right, expressionTypes.Resolve(bin.Right))
					: right;
				coerced = coercion.CoerceIntegerWidth(coerced, rTy, targetType);
				coerced = coercion.CoerceFloatWidth(coerced, rTy, targetType);
				Builder.BuildStore(coerced, targetPtr);
			}

			return right;
		}
		else if (bin.Left is CallExpressionSyntax callLeft)
		{
			var callRetType = expressionTypes.Resolve(callLeft);
			var targetType = callRetType is PointerTypeSymbol ptrTy ? ptrTy.ReferencedType : callRetType;

			// Emit the call. Because it returns 'refvar', LLVM returns the pointer directly.
			var targetPtr = calls.Emit(callLeft);

			if (targetType is UnionTypeSymbol elemUnion
				&& cleanup.UnionNeedsTagCheckedCleanup(elemUnion)
				&& bin.Right is StructInitializationExpressionSyntax)
			{
				cleanup.EmitUnionTagCheckedCleanup("call_ret", targetPtr, elemUnion);
			}

			if (targetType is UnionTypeSymbol && bin.Right is StructInitializationExpressionSyntax elemReinit)
			{
				aggregates.EmitStructInitializationInPlace(elemReinit, targetPtr);
			}
			else
			{
				var coerced = (bin.Right is MemberAccessExpressionSyntax or IndexExpressionSyntax)
					? coercion.CoerceReferenceToValue(right, expressionTypes.Resolve(bin.Right))
					: right;
				coerced = coercion.CoerceIntegerWidth(coerced, rTy, targetType);
				coerced = coercion.CoerceFloatWidth(coerced, rTy, targetType);
				Builder.BuildStore(coerced, targetPtr);
			}

			return right;
		}

		throw new InvalidOperationException("Invalid target assignment");
	}

	/// <summary>
	/// Emits a target-typed native function address. Binding already selected the exact function
	/// overload and checked source calling-convention/signature compatibility.
	/// </summary>
	private LLVMValueRef EmitNativeFunctionAddress(FunctionSymbol function, DelegateTypeSymbol delegateType)
	{
		if (!codegen.Globals.TryGetValue(function.Name, out var functionValue))
			throw new InvalidOperationException($"Native function address target '{function.Name}' was not declared in the LLVM module.");

		var delegateLlvmType = codegen.Types.Lower(delegateType);
		return functionValue.TypeOf.Handle == delegateLlvmType.Handle
			? functionValue
			: Builder.BuildPointerCast(functionValue, delegateLlvmType, "native_fn_addr");
	}

	/// <summary>
	/// Lowers unary operators and explicit casts without changing the existing cast semantics.
	/// </summary>
	private LLVMValueRef EmitUnaryExpression(UnaryExpressionSyntax unary)
	{
		if (unary.Operator.EndsWith("_postfix") || unary.Operator.EndsWith("_prefix"))
		{
			return EmitIncrementDecrement(unary, unary.Operator.EndsWith("_prefix"), unary.Operator.StartsWith("++"));
		}

		var operand = Emit(unary.Operand);

		if (unary.Operator.StartsWith('(') && unary.Operator.EndsWith(')'))
		{
			var targetTypeName = unary.Operator[1..^1];
			var targetTypeSymbol = BindingContext.ResolveType(targetTypeName)!;
			var targetType = LowerType(targetTypeSymbol);

			var operandType = expressionTypes.Resolve(unary.Operand);
			var operandLlvmType = LowerType(operandType);

			// Safe/unbound zone: (Enum)integer yields Option<Enum> â€” a checked conversion
			// comparing against every declared variant value (None when no match). The raw
			// enum scalar is only produced by this cast inside unsafe code.
			if (targetTypeSymbol is EnumTypeSymbol safeCastEnum && Function.UnsafeDepth == 0 &&
				operandType is not EnumTypeSymbol && TypeSymbol.IsIntegerType(operandType))
			{
				if (BindingContext.ResolveType($"Option<{safeCastEnum.Name}>") is UnionTypeSymbol optionUnion)
				{
					var (optionPtr, optionTy) = aggregates.MaterializeEnumCastOption(safeCastEnum, operand, operandType, optionUnion);
					return Builder.BuildLoad2(LowerType(optionTy), optionPtr, "enum_cast_option");
				}
			}

			// Destructive cast (T*)<handle>: recover the raw heap pointer instead of
			// bitcasting the whole owning handle. For a heap-allocated handle the block
			// pointer lives in the handle slot (the hidden pointer field of the handle).
			if (targetTypeSymbol is RawPointerTypeSymbol)
			{
				if (unary.Operand is IdentifierExpressionSyntax ownerId
					&& Function.Locals.TryGetValue(ownerId.Name, out var handleSlot)
					&& Function.HeapAllocatedVars.Contains(ownerId.Name))
				{
					var rawHeapPtr = Builder.BuildLoad2(LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), handleSlot, "handle_ptr");
					return rawHeapPtr.TypeOf.Handle == targetType.Handle
						? rawHeapPtr
						: coercion.SafeBitCast(rawHeapPtr, targetType, "handle_cast");
				}

				// By-value handle (e.g. a heap node returned then passed by value): the
				// aggregate's first field carries the owning pointer; extract it rather than
				// casting the struct itself.
				if (operandType is StructTypeSymbol handleStruct
					&& handleStruct.Fields.Count > 0
					&& handleStruct.Fields[0].Type is PointerTypeSymbol or RawPointerTypeSymbol
					&& operand.TypeOf.Kind == LLVMTypeKind.LLVMStructTypeKind)
				{
					var hiddenPtr = Builder.BuildExtractValue(operand, 0, "handle_hidden_ptr");
					return coercion.SafeBitCast(hiddenPtr, targetType, "handle_cast");
				}
			}

			// If casting between the identical LLVM type, return early
			if (targetType.Handle == operandLlvmType.Handle)
				return operand;

			// Enums are flat scalar integers (Â§1): reduce to their underlying storage
			// type so the width-adjustment branches below can operate on them directly.
			var effectiveTarget = targetTypeSymbol is EnumTypeSymbol targetEnum ? targetEnum.StorageType : targetTypeSymbol;
			var effectiveOperand = operandType is EnumTypeSymbol operandEnum ? operandEnum.StorageType : operandType;

			var targetIsInt = TypeSymbol.IsIntegerType(effectiveTarget);
			var operandIsInt = TypeSymbol.IsIntegerType(effectiveOperand);

			// 1. Float <-> Double conversions
			if (effectiveTarget.Equals(TypeSymbol.Double) && effectiveOperand.Equals(TypeSymbol.Float))
			{
				return Builder.BuildFPExt(operand, LLVMTypeRef.Double, "cast_fpext");
			}

			if (effectiveTarget.Equals(TypeSymbol.Float) && effectiveOperand.Equals(TypeSymbol.Double))
			{
				return Builder.BuildFPTrunc(operand, LLVMTypeRef.Float, "cast_fptrunc");
			}

			// 2. Integer -> Float / Double
			if (TypeSymbol.IsFloatingPointType(effectiveTarget) && operandIsInt)
			{
				return TypeSymbol.IsSignedIntegerType(effectiveOperand)
					? Builder.BuildSIToFP(operand, targetType, "cast_sitofp")
					: Builder.BuildUIToFP(operand, targetType, "cast_uitofp");
			}

			// 3. Float / Double -> Integer
			if (TypeSymbol.IsFloatingPointType(effectiveOperand) && targetIsInt)
			{
				return TypeSymbol.IsSignedIntegerType(effectiveTarget)
					? Builder.BuildFPToSI(operand, targetType, "cast_fptosi")
					: Builder.BuildFPToUI(operand, targetType, "cast_fptoui");
			}

			// 4. Integer <-> Integer width conversion (byte/char/short/int/long/nint/nuint)
			if (operandIsInt && targetIsInt)
			{
				var operandWidth = TypeSymbol.IntegerBitWidth(effectiveOperand);
				var targetWidth = TypeSymbol.IntegerBitWidth(effectiveTarget);

				if (operandWidth > targetWidth)
				{
					// Narrowing: truncate the source to the target width
					return Builder.BuildTrunc(operand, targetType, "cast_trunc");
				}

				if (operandWidth < targetWidth)
				{
					// Widening: sign-extend signed sources, zero-extend unsigned sources
					return TypeSymbol.IsSignedIntegerType(effectiveOperand)
						? Builder.BuildSExt(operand, targetType, "cast_sext")
						: Builder.BuildZExt(operand, targetType, "cast_zext");
				}
			}

			// 5. Pointer <-> Integer: a raw pointer re-interpreted as an integer
			// (e.g. (ulong)QueryPerformanceCounter()) is a ptrtoint, and the inverse
			// is an inttoptr. A plain bitcast between ptr and iN is invalid LLVM IR.
			if (operandLlvmType.Kind == LLVMTypeKind.LLVMPointerTypeKind &&
				targetType.Kind == LLVMTypeKind.LLVMIntegerTypeKind)
			{
				return Builder.BuildPtrToInt(operand, targetType, "cast_ptrtoint");
			}

			if (operandLlvmType.Kind == LLVMTypeKind.LLVMIntegerTypeKind &&
				targetType.Kind == LLVMTypeKind.LLVMPointerTypeKind)
			{
				return Builder.BuildIntToPtr(operand, targetType, "cast_inttoptr");
			}

			return coercion.SafeBitCast(operand, targetType, "cast_bitcast");
		}

		switch (unary.Operator)
		{
			case "-":
				{
					var handled = TryFoldConstantNegation(unary, operand, out var folded);
					return handled ? folded : Builder.BuildNeg(operand);
				}
			case "!":
				return Builder.BuildNot(operand);
			case "~":
				{
					// (Â§3.B) On a [Flags] enum, '~' is the masked bitwise complement:
					// (~value) & CombinedAtomicMask, truncated/width-locked to storage width.
					if (expressionTypes.Resolve(unary.Operand) is EnumTypeSymbol { IsFlags: true } flagsEnum)
					{
						var storeTy = LowerType(flagsEnum);
						var notVal = Builder.BuildNot(operand, "flags_not");
						var combinedMask = 0L;
						foreach (var flagVar in flagsEnum.Variants)
						{
							combinedMask |= flagVar.Value;
						}

						return Builder.BuildAnd(notVal,
							LLVMValueRef.CreateConstInt(storeTy, unchecked((ulong)combinedMask)), "flags_masked");
					}

					return Builder.BuildXor(operand, LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, unchecked((ulong)-1)));
				}
			case "*":
				{
					var operandType = expressionTypes.Resolve(unary.Operand);
					if (operandType is RawPointerTypeSymbol rawPtr)
					{
						var elemLlvmType = LowerType(rawPtr.ElementType);
						return Builder.BuildLoad2(elemLlvmType, operand, "deref_val");
					}

					return Builder.BuildLoad2(LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), operand, "deref_val");
				}
			case "&":
				{
					if (unary.Operand is IdentifierExpressionSyntax id && Function.Locals.TryGetValue(id.Name, out var ptr))
						return ptr;
					if (unary.Operand is MemberAccessExpressionSyntax memberAccess)
					{
						var (fieldPtr, _, _, _) = aggregates.GetFieldPointer(memberAccess);
						return fieldPtr;
					}

					if (unary.Operand is IndexExpressionSyntax indexExpr)
					{
						var (elementPtr, _, _, _) = aggregates.GetFieldPointer(indexExpr);
						return elementPtr;
					}

					return operand;
				}
			default:
				throw new InvalidOperationException($"Unknown unary operator '{unary.Operator}'");
		}
	}

	/// <summary>
	/// Folds negation of numeric constants when the result can be emitted directly as an LLVM constant.
	/// </summary>
	private bool TryFoldConstantNegation(UnaryExpressionSyntax unary, LLVMValueRef operand, out LLVMValueRef folded)
	{
		var operandType = expressionTypes.Resolve(unary.Operand);

		switch (unary.Operand)
		{
			case DoubleLiteralExpressionSyntax dblLit when operandType is not null
				&& TypeSymbol.IsFloatingPointType(operandType):
				// Native, pre-folded negative real constant: an inline LLVM constant
				// expression cannot express floating-point math (constexpr ops are
				// integer-only), so invert the sign in the compiler and emit a plain
				// constant value (e.g. `float -5.000000e-01`).
				folded = LLVMValueRef.CreateConstReal(LowerType(operandType), -dblLit.Value);
				return true;
			case IntegerLiteralExpressionSyntax intLit when operandType is not null:
				folded = LLVMValueRef.CreateConstInt(LowerType(operandType), 0UL - intLit.Value);
				return true;
		}

		if (operandType is not null && TypeSymbol.IsFloatingPointType(operandType))
		{
			// Runtime fallback for dynamic real operands: emit an explicit `fsub`
			// against zero bound to a virtual register, never an inline constexpr.
			folded = Builder.BuildFSub(
				LLVMValueRef.CreateConstReal(LowerType(operandType), 0.0), operand, "fneg_tmp");
			return true;
		}

		folded = default;
		return false;
	}

	/// <summary>
	/// Lowers a borrow expression to the address or reference value required by its borrow mode.
	/// </summary>
	private LLVMValueRef EmitBorrowExpression(BorrowExpressionSyntax expr)
	{
		if (expr.Expression is IdentifierExpressionSyntax id && Function.Locals.TryGetValue(id.Name, out var ptr))
		{
			var type = Function.VariableTypes[id.Name];
			var isReference = type is PointerTypeSymbol;
			var isHeap = Function.HeapAllocatedVars.Contains(id.Name) && type is not SliceTypeSymbol;

			if (isReference || isHeap)
			{
				return Builder.BuildLoad2(LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), ptr, "borrow_ref");
			}

			return ptr;
		}
		else if (expr.Expression is MemberAccessExpressionSyntax m)
		{
			var (fieldPtr, _, _, _) = aggregates.GetFieldPointer(m);
			return fieldPtr;
		}
		else if (expr.Expression is IndexExpressionSyntax idx)
		{
			var (elementPtr, _, _, _) = aggregates.GetFieldPointer(idx);
			return elementPtr;
		}
		// Borrowing a dereferenced pointer ('ref *ptr' or 'refvar *ptr') returns the underlying pointer value
		else if (expr.Expression is UnaryExpressionSyntax { Operator: "*" } deref)
		{
			return Emit(deref.Operand);
		}

		throw new InvalidOperationException("Can only borrow variables or member fields");
	}

	/// <summary>
	/// Lowers prefix and postfix increment/decrement operations while preserving the original result value rules.
	/// </summary>
	private LLVMValueRef EmitIncrementDecrement(UnaryExpressionSyntax u, bool isPrefix, bool isIncrement)
	{
		var (ptr, type, _, tbaa) = aggregates.GetFieldPointer(u.Operand);
		var ty = LowerType(type);

		var currentVal = Builder.BuildLoad2(ty, ptr, "incdec_current");
		aggregates.ApplyTbaa(tbaa, currentVal);
		var step = LLVMValueRef.CreateConstInt(ty, 1);

		var newVal = isIncrement
			? Builder.BuildAdd(currentVal, step, "incdec_new")
			: Builder.BuildSub(currentVal, step, "incdec_new");

		var store = Builder.BuildStore(newVal, ptr);
		aggregates.ApplyTbaa(tbaa, store);

		return isPrefix ? newVal : currentVal;
	}

	/// <summary>
	/// Lowers a ternary expression using the existing stack-slot merge strategy.
	/// </summary>
	private LLVMValueRef EmitTernaryExpression(TernaryExpressionSyntax expr)
	{
		var condition = Emit(expr.Condition);
		var thenVal = Emit(expr.ThenExpression);
		var elseVal = Emit(expr.ElseExpression);

		var type = expressionTypes.Resolve(expr.ThenExpression);
		var llvmTy = LowerType(type);

		var currentFunc = Builder.InsertBlock.Parent;
		var thenBlock = currentFunc.AppendBasicBlock("ternary_then");
		var elseBlock = currentFunc.AppendBasicBlock("ternary_else");
		var mergeBlock = currentFunc.AppendBasicBlock("ternary_end");

		var resultAlloc = Builder.BuildAlloca(llvmTy, "ternary_result");
		Builder.BuildCondBr(condition, thenBlock, elseBlock);

		Builder.PositionAtEnd(thenBlock);
		Builder.BuildStore(thenVal, resultAlloc);
		Builder.BuildBr(mergeBlock);

		Builder.PositionAtEnd(elseBlock);
		Builder.BuildStore(elseVal, resultAlloc);
		Builder.BuildBr(mergeBlock);

		Builder.PositionAtEnd(mergeBlock);
		var loadedReg = Builder.BuildLoad2(llvmTy, resultAlloc, "ternary_val");

		return loadedReg;
	}

	/// <summary>
	/// Lowers semantic heap-object allocation while delegating raw allocation to MemoryEmitter and initialization to specialized emitters.
	/// </summary>
	private LLVMValueRef EmitHeapAllocation(HeapAllocationExpressionSyntax expr)
	{
		// The heap target is either a struct literal (`heap Node { ... }`) or a constructor
		// call (`heap Node(args)`). Both allocate unmanaged memory and populate it in place.
		StructTypeSymbol typeSymbol;

		if (expr.Expression is StructInitializationExpressionSyntax structInit)
		{
			typeSymbol = BindingContext.ResolveType(structInit.StructTypeName) as StructTypeSymbol
				?? throw new InvalidOperationException($"heap allocation requires a struct type, but '{structInit.StructTypeName}' did not resolve to one.");
		}
		else if (expr.Expression is CallExpressionSyntax ctorCall)
		{
			// `heap T(args)`: the receiver struct is the constructor's RESOLVED return type
			// (e.g. the instantiated `GBox<int>`), which carries the real fields for sizing.
			// Re-resolving the name during codegen returns the empty generic template
			// placeholder (store size 1), which yields a 1-byte malloc.
			typeSymbol = expressionTypes.Resolve(ctorCall) as StructTypeSymbol
				?? BindingContext.ResolveType(ctorCall.FunctionName) as StructTypeSymbol
				?? throw new InvalidOperationException($"heap allocation requires a struct type, but '{ctorCall.FunctionName}' did not resolve to one.");
		}
		else
		{
			throw new InvalidOperationException($"Unsupported heap allocation target '{expr.Expression.Kind}'.");
		}

		// Allocate using the real LLVM store size, including target padding.
		var rawPtr = memory.AllocateHeap(LowerType(typeSymbol));

		if (expr.Expression is StructInitializationExpressionSyntax litInit)
		{
			aggregates.EmitStructInitializationInPlace(litInit, rawPtr);
		}
		else if (expr.Expression is CallExpressionSyntax callInit)
		{
			// The constructor populates the freshly allocated memory via its implicit `this`
			// pointer (a raw pointer to the allocation), so reference fields can be written.
			calls.Emit(callInit, rawPtr);
		}

		return rawPtr;
	}

	/// <summary>
	/// Lowers heap-array allocation and materializes the slice value returned by the expression.
	/// </summary>
	private LLVMValueRef EmitHeapArrayAllocation(HeapArrayAllocationExpressionSyntax expr)
	{
		var elementType = BindingContext.ResolveType(expr.ElementTypeName);
		var elementLlvmType = LowerType(elementType!);

		// Evaluate the dynamic count, then delegate raw storage sizing/allocation to MemoryEmitter.
		var countVal = Emit(expr.CountExpression);
		var rawPtr = memory.AllocateHeapArray(elementLlvmType, countVal);

		// Assemble the Slice Fat Pointer { ptr, i32 }.
		var sliceType = new SliceTypeSymbol(elementType!);
		var sliceLayout = LowerType(sliceType);
		var sliceAlloc = Builder.BuildAlloca(sliceLayout, "slice_tmp");

		// Store ptr
		var ptrField = Builder.BuildGEP2(sliceLayout, sliceAlloc, new LLVMValueRef[] { LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0), LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0) }, "ptr_field");
		Builder.BuildStore(rawPtr, ptrField);

		// Store length
		var sizeField = Builder.BuildGEP2(sliceLayout, sliceAlloc, new LLVMValueRef[] { LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0), LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 1) }, "size_field");
		Builder.BuildStore(countVal, sizeField);

		return Builder.BuildLoad2(sliceLayout, sliceAlloc, "slice_val");
	}

	/// <summary>
	/// Returns whether a positive enum value represents exactly one atomic flag bit.
	/// </summary>
	private static bool IsPowerOfTwo(long value)
	{
		return value > 0 && (value & (value - 1)) == 0;
	}
}
