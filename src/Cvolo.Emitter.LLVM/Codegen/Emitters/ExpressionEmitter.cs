using System.Text;
using Cvolo.Analysis;
using Cvolo.Analysis.Semantics;
using Cvolo.Analysis.Ownership;
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
	ValueLoader values,
	Action emitRuntimeTrap)
{
	private readonly Dictionary<string, LLVMValueRef> _typeofGlobals = [];
	private int _typeofCounter;

	/// <summary>
	/// Makes the synthetic payload local names of nested optional-chaining expressions unique.
	/// </summary>
	private int _optionalChainCounter;

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
			case SizeofExpressionSyntax sizeofExpr:
				return EmitLayoutConstant(sizeofExpr);
			case AlignofExpressionSyntax alignofExpr:
				return EmitLayoutConstant(alignofExpr);
			case OffsetofExpressionSyntax offsetofExpr:
				return EmitLayoutConstant(offsetofExpr);
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
					// Context-typed `Option.None`: a zero-initialized Option is already the
					// none state, because None is the first variant. The NPO representation
					// gives the same answer for free — its None state is the null payload
					// pointer — so both lower to an all-zero (or null) value of the Option type.
					if (BindingContext.ResolvedTargetTypedOptionNone.TryGetValue(m, out var noneOptionType)
						&& noneOptionType is not null)
					{
						var noneLayout = LowerType(noneOptionType);
						var noneSlot = memory.BuildEntryAlloca(noneLayout, "option_none");
						var noneInit = noneOptionType is UnionTypeSymbol { IsNpoEligible: true }
							? LLVMValueRef.CreateConstPointerNull(noneLayout)
							: LLVMValueRef.CreateConstNull(noneLayout);
						Builder.BuildStore(noneInit, noneSlot);
						return Builder.BuildLoad2(noneLayout, noneSlot, "option_none_val");
					}

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
			case CoalesceExpressionSyntax coalesce:
				return EmitCoalesceExpression(coalesce);
			case OptionalChainExpressionSyntax optionalChain:
				return EmitOptionalChainExpression(optionalChain);
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
	private LLVMValueRef EmitLayoutConstant(ExpressionSyntax expr)
	{
		var value = BindingContext.ResolvedLayoutConstants.TryGetValue(expr, out var folded) ? folded : BindingContext.EvaluateLayoutConstant(expr);
		return LLVMValueRef.CreateConstInt(LowerType(TypeSymbol.NUInt), (ulong)value);
	}

	private LLVMValueRef EmitTypeof(TypeofExpressionSyntax typeofExpr)
	{
		var typeName = typeofExpr.TypeName;
		if (_typeofGlobals.TryGetValue(typeName, out var cached))
			return cached;

		var systemType = BindingContext.ResolveType("Type")
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
		// Assignment owns storage/lifetime semantics rather than operator semantics. Validation still
		// records a canonical Assignment descriptor for the syntax node, but lowering stays on the
		// dedicated store path. Frontend-desugared compound operations (*=, /=, <<=, ...) share the
		// exact left syntax node with the outer assignment and get a dedicated single-evaluation path.
		if (bin.Operator == "=")
			return EmitAssignment(bin);

		if (!BindingContext.ResolvedBinaryExpressions.TryGetValue(bin, out var resolved)
			|| resolved.State != BinaryBindingState.Success)
		{
			throw new InvalidOperationException(
				$"Binary expression '{bin.Operator}' reached code generation without a successful canonical binding.");
		}

		if (resolved.OperatorOverload is not null)
			return EmitBoundOperatorOverload(resolved);

		if (resolved.BuiltinOperatorKind == BuiltinBinaryOperatorKind.ConstantStringConcatenation)
			return EmitStringLiteral(ConstantStringValue(bin.Left) + ConstantStringValue(bin.Right));

		if (resolved.BuiltinOperatorKind is BuiltinBinaryOperatorKind.BooleanLogicalAnd or BuiltinBinaryOperatorKind.BooleanLogicalOr)
			return EmitShortCircuitLogical(bin, resolved);

		var left = ApplyBinaryConversion(Emit(bin.Left), resolved.LeftConversion
			?? throw new InvalidOperationException("Canonical binary binding is missing the left operand conversion."), "left");
		var right = ApplyBinaryConversion(Emit(bin.Right), resolved.RightConversion
			?? throw new InvalidOperationException("Canonical binary binding is missing the right operand conversion."), "right");

		return EmitResolvedPrimitiveBinary(resolved, left, right);
	}

	/// <summary>
	/// Lowers one already-bound builtin primitive operation over its exactly-once evaluated,
	/// canonically converted operands. Integer-edge guards (zero divisor, signed MIN / -1, and the
	/// shift-count domain) are inserted here from the bound operation's composable guard policy and
	/// consume the very same converted operands emitted by the arithmetic instruction.
	/// </summary>
	private LLVMValueRef EmitResolvedPrimitiveBinary(ResolvedBinaryExpression resolved, LLVMValueRef left, LLVMValueRef right)
	{
		EmitIntegerEdgeGuards(resolved, left, right);

		return resolved.BuiltinOperatorKind switch
		{
			BuiltinBinaryOperatorKind.RawPointerAdd =>
				Builder.BuildGEP2(LowerType(((RawPointerTypeSymbol)resolved.ResultType!).ElementType), left, new[] { right }, "ptr_add"),
			BuiltinBinaryOperatorKind.RawPointerAddReversed =>
				Builder.BuildGEP2(LowerType(((RawPointerTypeSymbol)resolved.ResultType!).ElementType), right, new[] { left }, "ptr_add"),
			BuiltinBinaryOperatorKind.RawPointerSubtract =>
				Builder.BuildGEP2(LowerType(((RawPointerTypeSymbol)resolved.ResultType!).ElementType), left, new[] { Builder.BuildNeg(right, "ptr_sub_neg") }, "ptr_sub"),
			BuiltinBinaryOperatorKind.FloatingArithmetic => EmitFloatingArithmetic(resolved.Operator, left, right),
			BuiltinBinaryOperatorKind.FloatingComparison => EmitFloatingComparison(resolved.Operator, left, right),
			BuiltinBinaryOperatorKind.IntegerArithmetic => EmitIntegerArithmetic(resolved.Operator, left, right, resolved.UseUnsignedIntegerSemantics),
			BuiltinBinaryOperatorKind.IntegerComparison => EmitIntegerComparison(resolved.Operator, left, right, resolved.IsUnsignedIntegerComparison),
			BuiltinBinaryOperatorKind.BooleanComparison => EmitIntegerComparison(resolved.Operator, left, right),
			BuiltinBinaryOperatorKind.StringComparison => EmitIntegerComparison(resolved.Operator, left, right),
			BuiltinBinaryOperatorKind.NativeDelegateEquality => EmitIntegerComparison(resolved.Operator, left, right),
			BuiltinBinaryOperatorKind.EnumComparison => EmitIntegerComparison(resolved.Operator, left, right),
			BuiltinBinaryOperatorKind.RawPointerComparison => EmitIntegerComparison(resolved.Operator, left, right),
			BuiltinBinaryOperatorKind.IntegerBitwise => resolved.Operator switch
			{
				"&" => Builder.BuildAnd(left, right),
				"|" => Builder.BuildOr(left, right),
				"^" => Builder.BuildXor(left, right),
				_ => throw new InvalidOperationException($"Unexpected bitwise operator '{resolved.Operator}'."),
			},
			BuiltinBinaryOperatorKind.FlagsBitwise => resolved.Operator switch
			{
				"&" => Builder.BuildAnd(left, right),
				"|" => Builder.BuildOr(left, right),
				"^" => Builder.BuildXor(left, right),
				_ => throw new InvalidOperationException($"Unexpected flags operator '{resolved.Operator}'."),
			},
			BuiltinBinaryOperatorKind.ShiftLeft => Builder.BuildShl(left, right),
			BuiltinBinaryOperatorKind.ShiftRightArithmetic => Builder.BuildAShr(left, right),
			BuiltinBinaryOperatorKind.ShiftRightLogical => Builder.BuildLShr(left, right),
			_ => throw new InvalidOperationException(
				$"Unsupported canonical builtin binary operation '{resolved.BuiltinOperatorKind}' for '{resolved.Operator}'."),
		};
	}

	/// <summary>
	/// Emits the composable integer-edge runtime guards declared by the bound operation. Each guard
	/// traps through the canonical fatal runtime path so the invalid LLVM instruction never executes.
	/// </summary>
	private void EmitIntegerEdgeGuards(ResolvedBinaryExpression resolved, LLVMValueRef left, LLVMValueRef right)
	{
		if (resolved.IntegerEdgeCheckDivisorNonZero)
		{
			GuardTrap(
				Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, right, LLVMValueRef.CreateConstInt(right.TypeOf, 0), "edge_div_zero"),
				"divzero");

			if (resolved.IntegerEdgeCheckSignedMinMinusOne)
			{
				var width = resolved.IntegerEdgeValueBitWidth;
				var isMin = Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, left,
					LLVMValueRef.CreateConstInt(left.TypeOf, 1UL << (width - 1)), "edge_lhs_is_min");
				var isMinusOne = Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, right,
					LLVMValueRef.CreateConstInt(right.TypeOf, ulong.MaxValue), "edge_rhs_is_minus_one");
				GuardTrap(Builder.BuildAnd(isMin, isMinusOne, "edge_min_pair"), "min");
			}
		}
		else if (resolved.IntegerEdgeCheckShiftCountDomain)
		{
			var width = resolved.IntegerEdgeValueBitWidth;
			if (resolved.RightConversion is { } rightConversion
				&& TypeSymbol.IsSignedIntegerType(rightConversion.ValueType))
			{
				GuardTrap(
					Builder.BuildICmp(LLVMIntPredicate.LLVMIntSLT, right, LLVMValueRef.CreateConstInt(right.TypeOf, 0), "edge_shift_neg"),
					"shiftneg");
			}

			GuardTrap(
				Builder.BuildICmp(LLVMIntPredicate.LLVMIntUGE, right, LLVMValueRef.CreateConstInt(right.TypeOf, (ulong)width), "edge_shift_width"),
				"shiftwidth");
		}
	}

	/// <summary>Branches to the canonical trap on <paramref name="condition"/>, otherwise continues.</summary>
	private void GuardTrap(LLVMValueRef condition, string label)
	{
		var currentBlock = Builder.InsertBlock.Parent;
		var trapBlock = currentBlock.AppendBasicBlock($"edge_{label}_trap");
		var okBlock = currentBlock.AppendBasicBlock($"edge_{label}_ok");
		Builder.BuildCondBr(condition, trapBlock, okBlock);
		Builder.PositionAtEnd(trapBlock);
		emitRuntimeTrap();
		Builder.PositionAtEnd(okBlock);
	}

	/// <summary>Applies exactly the operand conversion selected by semantic binary binding.</summary>
	private LLVMValueRef ApplyBinaryConversion(LLVMValueRef value, BinaryOperandConversion conversion, string side)
	{
		// ValueLoader already materializes scalar safe-reference reads. Aggregate references remain
		// pointer-shaped until their consumer requests the value, so only emit an LLVM load when the
		// representation still is a pointer. The semantic conversion itself remains recorded here.
		if (conversion.Dereference && value.TypeOf.Kind == LLVMTypeKind.LLVMPointerTypeKind)
			value = Builder.BuildLoad2(LowerType(conversion.ValueType), value, $"binary_{side}_deref");

		return conversion.NumericKind switch
		{
			BinaryNumericConversionKind.None => value,
			BinaryNumericConversionKind.IntegerWidth => coercion.CoerceIntegerWidth(value, conversion.ValueType, conversion.TargetType),
			BinaryNumericConversionKind.IntegerToFloating => TypeSymbol.IsSignedIntegerType(conversion.ValueType)
				? Builder.BuildSIToFP(value, LowerType(conversion.TargetType), $"binary_{side}_sitofp")
				: Builder.BuildUIToFP(value, LowerType(conversion.TargetType), $"binary_{side}_uitofp"),
			BinaryNumericConversionKind.FloatToDouble => Builder.BuildFPExt(value, LLVMTypeRef.Double, $"binary_{side}_fpext"),
			_ => throw new InvalidOperationException($"Unknown binary operand conversion '{conversion.NumericKind}'."),
		};
	}

	/// <summary>Emits the already-selected user-defined binary operator and its bound conversions.</summary>
	private LLVMValueRef EmitBoundOperatorOverload(ResolvedBinaryExpression resolved)
	{
		var overload = resolved.OperatorOverload
			?? throw new InvalidOperationException("User-defined binary binding has no operator function.");
		if (!codegen.Globals.TryGetValue(overload.Name, out var callee)
			|| !codegen.FunctionTypes.TryGetValue(overload.Name, out var functionType))
		{
			throw new InvalidOperationException($"Operator overload '{overload.Name}' was never declared for emission.");
		}

		var left = ApplyBinaryConversion(Emit(resolved.Syntax.Left), resolved.LeftConversion!, "operator_left");
		var right = ApplyBinaryConversion(Emit(resolved.Syntax.Right), resolved.RightConversion!, "operator_right");
		return Builder.BuildCall2(functionType, callee, new[] { left, right },
			overload.ReturnType.Equals(TypeSymbol.Void) ? "" : "operator_call");
	}

	/// <summary>Preserves language-level conditional RHS evaluation for && and ||.</summary>
	private LLVMValueRef EmitShortCircuitLogical(BinaryExpressionSyntax binary, ResolvedBinaryExpression resolved)
	{
		var kind = resolved.BuiltinOperatorKind;
		var left = ApplyBinaryConversion(Emit(binary.Left), resolved.LeftConversion
			?? throw new InvalidOperationException("Canonical logical binding is missing the left operand conversion."), "logical_left");
		var currentFunction = Builder.InsertBlock.Parent;
		var rhsBlock = currentFunction.AppendBasicBlock(kind == BuiltinBinaryOperatorKind.BooleanLogicalAnd ? "and_rhs" : "or_rhs");
		var mergeBlock = currentFunction.AppendBasicBlock(kind == BuiltinBinaryOperatorKind.BooleanLogicalAnd ? "and_end" : "or_end");
		var result = memory.BuildEntryAlloca(LLVMTypeRef.Int1, "logical_result");

		if (kind == BuiltinBinaryOperatorKind.BooleanLogicalAnd)
		{
			Builder.BuildStore(LLVMValueRef.CreateConstInt(LLVMTypeRef.Int1, 0), result);
			Builder.BuildCondBr(left, rhsBlock, mergeBlock);
		}
		else
		{
			Builder.BuildStore(LLVMValueRef.CreateConstInt(LLVMTypeRef.Int1, 1), result);
			Builder.BuildCondBr(left, mergeBlock, rhsBlock);
		}

		Builder.PositionAtEnd(rhsBlock);
		var right = ApplyBinaryConversion(Emit(binary.Right), resolved.RightConversion
			?? throw new InvalidOperationException("Canonical logical binding is missing the right operand conversion."), "logical_right");
		Builder.BuildStore(right, result);
		if (Builder.InsertBlock.Terminator.Handle == IntPtr.Zero)
			Builder.BuildBr(mergeBlock);

		Builder.PositionAtEnd(mergeBlock);
		return Builder.BuildLoad2(LLVMTypeRef.Int1, result, "logical_value");
	}

	private LLVMValueRef EmitFloatingArithmetic(string op, LLVMValueRef left, LLVMValueRef right)
		=> op switch
		{
			"+" => Builder.BuildFAdd(left, right),
			"-" => Builder.BuildFSub(left, right),
			"*" => Builder.BuildFMul(left, right),
			"/" => Builder.BuildFDiv(left, right),
			"%" => Builder.BuildFRem(left, right),
			_ => throw new InvalidOperationException($"Unknown floating-point arithmetic operator '{op}'."),
		};

	private LLVMValueRef EmitFloatingComparison(string op, LLVMValueRef left, LLVMValueRef right)
		=> op switch
		{
			"==" => Builder.BuildFCmp(LLVMRealPredicate.LLVMRealOEQ, left, right),
			"!=" => Builder.BuildFCmp(LLVMRealPredicate.LLVMRealONE, left, right),
			"<" => Builder.BuildFCmp(LLVMRealPredicate.LLVMRealOLT, left, right),
			">" => Builder.BuildFCmp(LLVMRealPredicate.LLVMRealOGT, left, right),
			"<=" => Builder.BuildFCmp(LLVMRealPredicate.LLVMRealOLE, left, right),
			">=" => Builder.BuildFCmp(LLVMRealPredicate.LLVMRealOGE, left, right),
			_ => throw new InvalidOperationException($"Unknown floating-point comparison operator '{op}'."),
		};

	private LLVMValueRef EmitIntegerArithmetic(string op, LLVMValueRef left, LLVMValueRef right, bool useUnsigned)
		=> op switch
		{
			"+" => Builder.BuildAdd(left, right),
			"-" => Builder.BuildSub(left, right),
			"*" => Builder.BuildMul(left, right),
			"/" when useUnsigned => Builder.BuildUDiv(left, right),
			"/" => Builder.BuildSDiv(left, right),
			"%" when useUnsigned => Builder.BuildURem(left, right),
			"%" => Builder.BuildSRem(left, right),
			_ => throw new InvalidOperationException($"Unknown integer arithmetic operator '{op}'."),
		};

	private LLVMValueRef EmitIntegerComparison(string op, LLVMValueRef left, LLVMValueRef right, bool useUnsigned = false)
		=> op switch
		{
			"==" => Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, left, right),
			"!=" => Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, left, right),
			"<" => Builder.BuildICmp(useUnsigned ? LLVMIntPredicate.LLVMIntULT : LLVMIntPredicate.LLVMIntSLT, left, right),
			">" => Builder.BuildICmp(useUnsigned ? LLVMIntPredicate.LLVMIntUGT : LLVMIntPredicate.LLVMIntSGT, left, right),
			"<=" => Builder.BuildICmp(useUnsigned ? LLVMIntPredicate.LLVMIntULE : LLVMIntPredicate.LLVMIntSLE, left, right),
			">=" => Builder.BuildICmp(useUnsigned ? LLVMIntPredicate.LLVMIntUGE : LLVMIntPredicate.LLVMIntSGE, left, right),
			_ => throw new InvalidOperationException($"Unknown integer comparison operator '{op}'."),
		};

	/// <summary>Returns whether <paramref name="op"/> is a frontend-desugared compound assignment operator.</summary>
	private static bool IsCompoundBinaryOperator(string op)
		=> op is "+" or "-" or "*" or "/" or "%" or "<<" or ">>" or ">>>" or "&" or "|" or "^";

	/// <summary>
	/// Routes a plain assignment to its store path, or to the dedicated single-evaluation path when the
	/// right side is a frontend-desugared compound operation that reuses this exact left syntax node.
	/// </summary>
	private LLVMValueRef EmitAssignment(BinaryExpressionSyntax bin)
	{
		if (bin.Right is BinaryExpressionSyntax compound
			&& ReferenceEquals(compound.Left, bin.Left)
			&& IsCompoundBinaryOperator(compound.Operator))
		{
			return EmitCompoundStore(bin, compound);
		}

		return EmitAssignStore(bin);
	}

	/// <summary>
	/// Lowers desugared compound assignments (x /= y, a[i] <<= n, ...) with exactly-once evaluation of
	/// the left storage, the old value, and every right-side operand. The bound compound operation is
	/// reused so integer-edge guards fire on the same values the arithmetic consumes, and the result is
	/// coerced back to the target storage width before a single store.
	/// </summary>
	private LLVMValueRef EmitCompoundStore(BinaryExpressionSyntax bin, BinaryExpressionSyntax compound)
	{
		if (!BindingContext.ResolvedBinaryExpressions.TryGetValue(compound, out var resolved)
			|| resolved.State != BinaryBindingState.Success
			|| resolved.OperatorOverload is not null)
		{
			return EmitAssignStore(bin);
		}

		var target = ResolveCompoundTarget(bin.Left);
		if (target is null)
			return EmitAssignStore(bin);

		var (address, targetType, tbaa) = target.Value;

		var oldValue = Builder.BuildLoad2(LowerType(targetType), address, "compound_old");
		var rhs = Emit(compound.Right);

		var left = ApplyBinaryConversion(oldValue, resolved.LeftConversion
			?? throw new InvalidOperationException("Compound binary binding is missing the left operand conversion."), "left");
		var right = ApplyBinaryConversion(rhs, resolved.RightConversion
			?? throw new InvalidOperationException("Compound binary binding is missing the right operand conversion."), "right");

		var result = EmitResolvedPrimitiveBinary(resolved, left, right);

		var coerced = coercion.CoerceIntegerWidth(result, resolved.ResultType!, targetType);
		coerced = coercion.CoerceFloatWidth(coerced, resolved.ResultType!, targetType);
		var store = Builder.BuildStore(coerced, address);
		if (tbaa is not null)
			aggregates.ApplyTbaa(tbaa, store);

		return result;
	}

	/// <summary>
	/// Resolves the storage address, semantic type, and TBAA metadata of a compound-assignment target.
	/// Returns null for shapes the single-evaluation path does not yet handle (pointer-typed locals,
	/// union targets, unrecognized identifiers), causing the caller to fall back to the general store path.
	/// </summary>
	private (LLVMValueRef Address, TypeSymbol Type, LLVMValueRef? Tbaa)? ResolveCompoundTarget(ExpressionSyntax left)
	{
		switch (left)
		{
			case IdentifierExpressionSyntax id:
			{
				if (Function.Locals.TryGetValue(id.Name, out var localPtr))
				{
					var type = Function.VariableTypes[id.Name];
					if (type is PointerTypeSymbol or UnionTypeSymbol)
						return null;
					return (localPtr, type, null);
				}

				if (values.ResolveGlobalKey(id.Name) is { } globalKey
					&& codegen.GlobalVariables.TryGetValue(globalKey, out var globalPtr))
				{
					var type = codegen.GlobalVariableTypes[globalKey];
					if (type is UnionTypeSymbol)
						return null;
					return (globalPtr, type, null);
				}

				if (Function.Locals.TryGetValue("this", out var thisPtr)
					&& Function.VariableTypes["this"] is PointerTypeSymbol thisPointer
					&& thisPointer.ReferencedType is StructTypeSymbol thisStruct
					&& thisStruct.FindField(id.Name) is not null)
				{
					var (fieldPtr, fieldType, _, tbaa) = aggregates.GetFieldPointer(id);
					return (fieldPtr, fieldType, tbaa);
				}

				return null;
			}

			case MemberAccessExpressionSyntax member:
			{
				var (fieldPtr, fieldType, _, tbaa) = aggregates.GetFieldPointer(member);
				return (fieldPtr, fieldType, tbaa);
			}

			case IndexExpressionSyntax index:
			{
				var (elementPtr, elementType, _, tbaa) = aggregates.GetFieldPointer(index);
				return (elementPtr, elementType, tbaa);
			}

			case UnaryExpressionSyntax { Operator: "*" } deref:
				return (Emit(deref.Operand), expressionTypes.Resolve(left), null);

			case CallExpressionSyntax callLeft:
				return (calls.Emit(callLeft), expressionTypes.Resolve(left), null);

			default:
				return null;
		}
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
					EmitOwnershipPreStoreCleanup(bin, ptr, expressionTypes.Resolve(bin.Left), ctorId.Name);
					calls.Emit(ctorCall, ptr);
					return ptr;
				}
				else if (values.ResolveGlobalKey(ctorId.Name) is { } ctorGlobalKey && codegen.GlobalVariables.TryGetValue(ctorGlobalKey, out var ctorGlobalPtr))
				{
					EmitOwnershipPreStoreCleanup(bin, ctorGlobalPtr, expressionTypes.Resolve(bin.Left), ctorId.Name);
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
						EmitOwnershipPreStoreCleanup(bin, fieldPtr, expressionTypes.Resolve(bin.Left), ctorId.Name);
						calls.Emit(ctorCall, fieldPtr);
						return fieldPtr;
					}
				}
			}
			else if (bin.Left is MemberAccessExpressionSyntax m)
			{
				var (fieldPtr, _, _, _) = aggregates.GetFieldPointer(m);
				EmitOwnershipPreStoreCleanup(bin, fieldPtr, expressionTypes.Resolve(bin.Left), m.MemberName);
				calls.Emit(ctorCall, fieldPtr);
				return fieldPtr;
			}
			else if (bin.Left is IndexExpressionSyntax idx)
			{
				var (elementPtr, _, _, _) = aggregates.GetFieldPointer(idx);
				EmitOwnershipPreStoreCleanup(bin, elementPtr, expressionTypes.Resolve(bin.Left), "elem");
				calls.Emit(ctorCall, elementPtr);
				return elementPtr;
			}
			else if (bin.Left is UnaryExpressionSyntax { Operator: "*" } deref)
			{
				var targetPtr = Emit(deref.Operand);
				EmitOwnershipPreStoreCleanup(bin, targetPtr, expressionTypes.Resolve(bin.Left), "deref");
				calls.Emit(ctorCall, targetPtr);
				return targetPtr;
			}
			else if (bin.Left is CallExpressionSyntax callLeft)
			{
				var targetPtr = calls.Emit(callLeft);
				EmitOwnershipPreStoreCleanup(bin, targetPtr, expressionTypes.Resolve(bin.Left), "call_ret");
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
					EmitOwnershipPreStoreCleanup(bin, ptr, type, id.Name);
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
					EmitOwnershipPreStoreCleanup(bin, ptr, type, id.Name);

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
						if (!unionType.IsNpoEligible && !unionType.IsUnsafe)
						{
							var unionLayout = LowerType(unionType);
							var unionPtr = Builder.BuildLoad2(
								LLVMTypeRef.CreatePointer(unionLayout, 0),
								thisPtr,
								"union_this_ptr");
							var variantIndex = codegen.AggregateLayout.GetFieldIndex(unionType, id.Name);
							var tagPtr = Builder.BuildGEP2(unionLayout, unionPtr, new LLVMValueRef[]
							{
								LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0),
								LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0)
							}, "union_tag_ptr");
							Builder.BuildStore(LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, (ulong)variantIndex), tagPtr);
						}

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

			EmitOwnershipPreStoreCleanup(bin, fieldPtr, fieldType, m.MemberName);

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
			EmitOwnershipPreStoreCleanup(bin, elementPtr, elementType, "elem");

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

			EmitOwnershipPreStoreCleanup(bin, targetPtr, targetType, "deref");

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

			EmitOwnershipPreStoreCleanup(bin, targetPtr, targetType, "call_ret");

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
	/// Emits the pre-store ownership cleanup for an assignment destination whose previously-held
	/// value is live. The decision of what to destroy was bound during analysis; the emitter only
	/// materializes that destruction at the resolved destination address, before the new value is
	/// installed.
	/// </summary>
	private void EmitOwnershipPreStoreCleanup(BinaryExpressionSyntax bin, LLVMValueRef destinationPtr, TypeSymbol destinationType, string name)
	{
		if (!BindingContext.ResolvedAssignments.TryGetValue(bin, out var operation))
			return;

		// A field projection whose ownership is transferred by this assignment must be skipped by
		// the enclosing scope's projection-aware cleanup. Whole-root moves are handled through the
		// existing name-based move/reset paths instead.
		if (operation.SourceMovePath is { Depth: > 0 } sourcePath && sourcePath.ToFieldPathString() is { } movedFieldPath)
			Function.MovedFieldPaths.Add(movedFieldPath);

		switch (operation.DestinationCleanupPlan)
		{
			case DestinationCleanupPlan.WholeLive:
				cleanup.EmitDestructionAt(destinationPtr, destinationType, name);
				break;
			case DestinationCleanupPlan.TagAwareUnionCleanup:
				if (destinationType is UnionTypeSymbol unionType && cleanup.UnionNeedsTagCheckedCleanup(unionType))
					cleanup.EmitUnionTagCheckedCleanup(name, destinationPtr, unionType);
				break;
		}
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
	/// Emits a call to a declared operator overload (spec §14). An operator overload is a
	/// receiverless associated function, so the call is an ordinary direct call whose only arguments
	/// are the operands. Operands are normalized exactly as at a normal call site: a struct value
	/// held in a storage pointer is loaded, and a builtin numeric literal is widened to the declared
	/// operand type.
	/// </summary>
	private LLVMValueRef EmitOperatorOverload(FunctionSymbol overload, IReadOnlyList<ExpressionSyntax> operands)
	{
		if (!codegen.Globals.TryGetValue(overload.Name, out var callee)
			|| !codegen.FunctionTypes.TryGetValue(overload.Name, out var functionType))
		{
			throw new InvalidOperationException($"Operator overload '{overload.Name}' was never declared for emission.");
		}

		var args = new LLVMValueRef[operands.Count];
		for (var i = 0; i < operands.Count; i++)
		{
			var operandType = expressionTypes.Resolve(operands[i]);
			var parameterType = i < overload.Parameters.Count ? overload.Parameters[i].Type : operandType;
			var value = Emit(operands[i]);

			if (operandType is PointerTypeSymbol storedType && !parameterType.Equals(operandType))
			{
				value = Builder.BuildLoad2(LowerType(storedType.ReferencedType), value, "operator_operand");
			}

			if (TypeSymbol.IsFloatingPointType(parameterType) && TypeSymbol.IsIntegerType(operandType))
			{
				value = TypeSymbol.IsSignedIntegerType(operandType)
					? Builder.BuildSIToFP(value, LowerType(parameterType), "operator_sitofp")
					: Builder.BuildUIToFP(value, LowerType(parameterType), "operator_uitofp");
			}
			else if (TypeSymbol.IsIntegerType(operandType) && TypeSymbol.IsIntegerType(parameterType) && !parameterType.Equals(operandType))
			{
				value = coercion.CoerceIntegerWidth(value, operandType, parameterType);
			}

			args[i] = value;
		}

		return Builder.BuildCall2(functionType, callee, args, overload.ReturnType.Equals(TypeSymbol.Void) ? "" : "operator_call");
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

		// OPERATOR OVERLOAD (spec §14): a declared unary operator is a receiverless associated call.
		if (BindingContext.FindOperatorOverload(unary.Operator, expressionTypes.Resolve(unary.Operand)) is { } overload)
			return EmitOperatorOverload(overload, [unary.Operand]);

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
		if (expr.Expression is IdentifierExpressionSyntax id)
		{
			var (ptr, _, _, _) = aggregates.GetFieldPointer(id);
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

		var resultAlloc = memory.BuildEntryAlloca(llvmTy, "ternary_result");
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
	/// Lowers the Option fallback operator <c>left ?? right</c>.
	/// </summary>
	/// <remarks>
	/// <para>
	/// The left operand is materialized exactly once into an entry slot and the discriminant is tested
	/// there, so the None test cannot re-run left's side effects. The right operand is emitted inside
	/// the None block only: an eager emission would evaluate the fallback even when the left Option is
	/// already Some, which is the entire point of short-circuiting.
	/// </para>
	/// <para>
	/// Tagged Options carry an <c>i8</c> discriminator compared against the None variant's field index;
	/// NPO Options lower to a bare pointer where null means None. Both representations are handled
	/// here so the caller never has to know which one it holds.
	/// </para>
	/// </remarks>
	private LLVMValueRef EmitCoalesceExpression(CoalesceExpressionSyntax expr)
	{
		if (!BindingContext.ResolvedCoalesceExpressions.TryGetValue(expr, out var resolved) || !resolved.IsSuccess)
		{
			throw new InvalidOperationException(
				"Fallback expression reached code generation without a canonical successful semantic binding.");
		}

		var optionType = resolved.OptionType!;
		var payloadType = resolved.PayloadType!;
		var optionLayout = LowerType(optionType);
		var usesNpo = optionType.IsNpoEligible;
		var preservesOption = resolved.Mode == CoalesceResultMode.PreserveOptionalOrFallback;
		var resultLayout = LowerType(resolved.ResultType!);

		// Materialize the left operand once. The discriminant is read from this slot, never by
		// re-emitting the left expression.
		var leftSlot = memory.BuildEntryAlloca(optionLayout, "coalesce_left");
		Builder.BuildStore(Emit(expr.Left), leftSlot);

		LLVMValueRef isNone;
		if (usesNpo)
		{
			var flatValue = Builder.BuildLoad2(optionLayout, leftSlot, "coalesce_flat");
			isNone = Builder.BuildICmp(
				LLVMIntPredicate.LLVMIntEQ,
				flatValue,
				LLVMValueRef.CreateConstPointerNull(flatValue.TypeOf),
				"coalesce_is_none");
		}
		else
		{
			var noneVariant = optionType.NoneVariant
				?? throw new InvalidOperationException("A coalescing Option must expose a None variant.");
			var noneIndex = (ulong)codegen.AggregateLayout.GetFieldIndex(optionType, noneVariant.Name);
			var tagPtr = Builder.BuildGEP2(optionLayout, leftSlot, new LLVMValueRef[]
			{
				LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0),
				LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0)
			}, "coalesce_tag_ptr");
			var tag = Builder.BuildLoad2(LLVMTypeRef.Int8, tagPtr, "coalesce_tag");
			isNone = Builder.BuildICmp(
				LLVMIntPredicate.LLVMIntEQ,
				tag,
				LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, noneIndex),
				"coalesce_is_none");
		}

		var currentFunc = Builder.InsertBlock.Parent;
		var someBlock = currentFunc.AppendBasicBlock("coalesce_some");
		var noneBlock = currentFunc.AppendBasicBlock("coalesce_none");
		var mergeBlock = currentFunc.AppendBasicBlock("coalesce_merge");
		var resultSlot = memory.BuildEntryAlloca(resultLayout, "coalesce_result");

		Builder.BuildCondBr(isNone, noneBlock, someBlock);

		// Some: the existing payload is the answer. Extracting mode hands the bare payload over;
		// preserving mode must re-tag it, because the result still promises an Option.
		Builder.PositionAtEnd(someBlock);
		var leftPayload = LoadOptionPayload(leftSlot, optionType, optionLayout, payloadType, usesNpo);
		Builder.BuildStore(
			BuildCoalesceResult(optionType, optionLayout, resultLayout, payloadType, usesNpo, preservesOption, branchIsOption: false, leftPayload),
			resultSlot);
		if (Builder.InsertBlock.Terminator.Handle == IntPtr.Zero)
			Builder.BuildBr(mergeBlock);

		// None: the fallback is evaluated here, and nowhere else.
		Builder.PositionAtEnd(noneBlock);
		var fallback = Emit(expr.Right);
		Builder.BuildStore(
			BuildCoalesceResult(optionType, optionLayout, resultLayout, payloadType, usesNpo, preservesOption, resolved.RightConversion == CoalesceRightConversion.Optional, fallback),
			resultSlot);
		if (Builder.InsertBlock.Terminator.Handle == IntPtr.Zero)
			Builder.BuildBr(mergeBlock);

		Builder.PositionAtEnd(mergeBlock);
		return Builder.BuildLoad2(resultLayout, resultSlot, "coalesce_val");
	}

	/// <summary>
	/// Lowers an optional-chaining expression.
	/// </summary>
	/// <remarks>
	/// <para>
	/// The receiver is materialized into one entry slot and its discriminant is read from there, so
	/// it is evaluated exactly once. The member access or invocation — including every argument — is
	/// emitted only in the <c>Some</c> block; the <c>None</c> block just materializes absence.
	/// </para>
	/// <para>
	/// The payload is exposed to the existing member-access and call machinery as a synthetic local
	/// whose slot is the <em>address</em> of the payload inside the Option. That keeps receiver
	/// capability and provenance exactly those of the ordinary operation: <c>ref</c> payload
	/// projections stay <c>ref</c>, the operator never upgrades a read-only receiver to
	/// <c>refvar</c>, and no payload copy or move is introduced.
	/// </para>
	/// </remarks>
	private LLVMValueRef EmitOptionalChainExpression(OptionalChainExpressionSyntax expr)
	{
		if (!BindingContext.ResolvedOptionalChains.TryGetValue(expr, out var resolved) || !resolved.IsSuccess)
		{
			throw new InvalidOperationException(
				"Optional chaining reached code generation without a canonical successful semantic binding.");
		}

		var optionType = resolved.OptionType!;
		var optionLayout = LowerType(optionType);
		var usesNpo = optionType.IsNpoEligible;
		var receiverSlot = memory.BuildEntryAlloca(optionLayout, "optional_chain_receiver");
		Builder.BuildStore(Emit(expr.Receiver), receiverSlot);

		LLVMValueRef isNone;
		if (usesNpo)
		{
			var flatValue = Builder.BuildLoad2(optionLayout, receiverSlot, "optional_chain_flat");
			isNone = Builder.BuildICmp(
				LLVMIntPredicate.LLVMIntEQ,
				flatValue,
				LLVMValueRef.CreateConstPointerNull(flatValue.TypeOf),
				"optional_chain_is_none");
		}
		else
		{
			var noneVariant = optionType.NoneVariant
				?? throw new InvalidOperationException("An optional-chained Option must expose a None variant.");
			var noneIndex = (ulong)codegen.AggregateLayout.GetFieldIndex(optionType, noneVariant.Name);
			var tagPtr = Builder.BuildGEP2(optionLayout, receiverSlot, new LLVMValueRef[]
			{
				LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0),
				LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0)
			}, "optional_chain_tag_ptr");
			var tag = Builder.BuildLoad2(LLVMTypeRef.Int8, tagPtr, "optional_chain_tag");
			isNone = Builder.BuildICmp(
				LLVMIntPredicate.LLVMIntEQ,
				tag,
				LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, noneIndex),
				"optional_chain_is_none");
		}

		var currentFunc = Builder.InsertBlock.Parent;
		var someBlock = currentFunc.AppendBasicBlock("optional_chain_some");
		var noneBlock = currentFunc.AppendBasicBlock("optional_chain_none");

		// A `void` member produces no value, so there is nothing to join: the None branch is a plain
		// no-op and no Option<void> is synthesized.
		if (!resolved.Lifts)
		{
			Builder.BuildCondBr(isNone, noneBlock, someBlock);

			Builder.PositionAtEnd(someBlock);
			EmitChainedMemberThroughPayload(expr, resolved, receiverSlot, optionType, optionLayout, usesNpo);
			if (Builder.InsertBlock.Terminator.Handle == IntPtr.Zero)
				Builder.BuildBr(noneBlock);

			Builder.PositionAtEnd(noneBlock);
			return LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0);
		}

		var resultOption = (UnionTypeSymbol)resolved.ResultType!;
		var resultLayout = LowerType(resultOption);
		var mergeBlock = currentFunc.AppendBasicBlock("optional_chain_merge");
		var resultSlot = memory.BuildEntryAlloca(resultLayout, "optional_chain_result");

		Builder.BuildCondBr(isNone, noneBlock, someBlock);

		Builder.PositionAtEnd(someBlock);
		var memberValue = EmitChainedMemberThroughPayload(expr, resolved, receiverSlot, optionType, optionLayout, usesNpo);
		Builder.BuildStore(
			BuildLiftedOptionalMemberResult(resultOption, resultLayout, memberValue),
			resultSlot);
		if (Builder.InsertBlock.Terminator.Handle == IntPtr.Zero)
			Builder.BuildBr(mergeBlock);

		// None: absence propagates, and nothing else is evaluated.
		Builder.PositionAtEnd(noneBlock);
		Builder.BuildStore(BuildAbsentOptionalResult(resultOption, resultLayout), resultSlot);
		if (Builder.InsertBlock.Terminator.Handle == IntPtr.Zero)
			Builder.BuildBr(mergeBlock);

		Builder.PositionAtEnd(mergeBlock);
		return Builder.BuildLoad2(resultLayout, resultSlot, "optional_chain_val");
	}

	/// <summary>
	/// Projects the payload out of the materialized receiver and emits the selected member through
	/// it, returning the member's own (unlifted) value.
	/// </summary>
	private LLVMValueRef EmitChainedMemberThroughPayload(
		OptionalChainExpressionSyntax expr,
		ResolvedOptionalChainExpression resolved,
		LLVMValueRef receiverSlot,
		UnionTypeSymbol optionType,
		LLVMTypeRef optionLayout,
		bool usesNpo)
	{
		var declaredPayload = optionType.PayloadVariant!.Type;
		var payloadPtr = GetOptionalChainPayloadPointer(receiverSlot, optionType, optionLayout, declaredPayload, usesNpo);

		// The synthetic local holds the payload *address*, typed as the payload itself. Every existing
		// consumer (field projection, receiver argument, delegate storage) then behaves exactly as it
		// would for an ordinary receiver of that type.
		var payloadName = $"$optional_chain_payload${_optionalChainCounter++}";
		Function.Locals[payloadName] = payloadPtr;
		Function.VariableTypes[payloadName] = declaredPayload;

		var payloadReference = new IdentifierExpressionSyntax(expr.Receiver.Span, payloadName);
		ExpressionSyntax member;

		switch (resolved.MemberKind)
		{
			case OptionalChainMemberKind.Method:
				{
					var synthesizedCall = new CallExpressionSyntax(
						expr.Span,
						$"{payloadName}.{expr.MemberName}",
						[],
						expr.Arguments!.ToList());
					BindingContext.ResolvedCalls[synthesizedCall] = resolved.Method!;
					member = synthesizedCall;
					break;
				}

			case OptionalChainMemberKind.DelegateInvoke:
				{
					// Reuses the ordinary safe/native delegate-call lowering verbatim: no `Invoke`
					// wrapper is emitted, and the inner native function pointer gets no null check.
					var synthesizedCall = new CallExpressionSyntax(
						expr.Span,
						payloadName,
						[],
						expr.Arguments!.ToList());
					BindingContext.ResolvedDelegateCalls[synthesizedCall] = resolved.DelegateType!;
					member = synthesizedCall;
					break;
				}

			default:
				member = new MemberAccessExpressionSyntax(expr.Span, payloadReference, expr.MemberName);
				break;
		}

		return Emit(member);
	}

	/// <summary>
	/// Returns the address of the payload inside a materialized Option slot.
	/// </summary>
	/// <remarks>
	/// Under NPO the slot already holds the payload pointer; a tagged Option reads its payload slot at
	/// <c>{0,1}</c> and reinterprets it, so the projection never copies or moves the payload.
	/// </remarks>
	private LLVMValueRef GetOptionalChainPayloadPointer(
		LLVMValueRef receiverSlot,
		UnionTypeSymbol optionType,
		LLVMTypeRef optionLayout,
		TypeSymbol declaredPayload,
		bool usesNpo)
	{
		if (usesNpo)
			return Builder.BuildLoad2(optionLayout, receiverSlot, "optional_chain_payload");

		var declaredLayout = LowerType(declaredPayload);
		var payloadPtr = Builder.BuildGEP2(optionLayout, receiverSlot, new LLVMValueRef[]
		{
			LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0),
			LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 1)
		}, "optional_chain_payload_ptr");

		return payloadPtr.TypeOf == LLVMTypeRef.CreatePointer(declaredLayout, 0)
			? payloadPtr
			: Builder.BuildBitCast(payloadPtr, LLVMTypeRef.CreatePointer(declaredLayout, 0), "optional_chain_payload_cast");
	}

	/// <summary>
	/// Wraps a member value in exactly one synthetic Option layer for the <c>Some</c> path.
	/// </summary>
	/// <remarks>
	/// The member value — including one that is itself an <c>Option&lt;U&gt;</c> — becomes the payload
	/// of a freshly built outer <c>Some</c>. That is what makes <c>Option&lt;Option&lt;U&gt;&gt;</c>
	/// distinct from <c>Option&lt;U&gt;</c>: the member's own discriminant lives inside the payload
	/// slot, it is never merged with the outer discriminant. Under NPO the member value is already a
	/// payload pointer, and a non-null pointer is precisely <c>Some</c>.
	/// </remarks>
	private LLVMValueRef BuildLiftedOptionalMemberResult(
		UnionTypeSymbol resultOption,
		LLVMTypeRef resultLayout,
		LLVMValueRef memberValue)
	{
		if (resultOption.IsNpoEligible)
			return memberValue;

		var payloadVariant = resultOption.PayloadVariant
			?? throw new InvalidOperationException("A lifted optional-chain result must expose a payload variant.");
		var payloadIndex = (ulong)codegen.AggregateLayout.GetFieldIndex(resultOption, payloadVariant.Name);

		var someSlot = memory.BuildEntryAlloca(resultLayout, "optional_chain_some");
		Builder.BuildStore(
			LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, payloadIndex),
			Builder.BuildGEP2(resultLayout, someSlot, new LLVMValueRef[]
			{
				LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0),
				LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0)
			}, "optional_chain_some_tag_slot"));
		Builder.BuildStore(
			memberValue,
			Builder.BuildGEP2(resultLayout, someSlot, new LLVMValueRef[]
			{
				LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0),
				LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 1)
			}, "optional_chain_some_payload_slot"));

		return Builder.BuildLoad2(resultLayout, someSlot, "optional_chain_some_val");
	}

	/// <summary>
	/// Builds the <c>None</c> value for an optional-chain result. Under NPO absence is the null
	/// payload pointer; a tagged Option gets a zero-initialized slot, which is <c>None</c> because
	/// the void variant is declared first.
	/// </summary>
	private LLVMValueRef BuildAbsentOptionalResult(UnionTypeSymbol resultOption, LLVMTypeRef resultLayout)
	{
		if (resultOption.IsNpoEligible)
			return LLVMValueRef.CreateConstPointerNull(resultLayout);

		var noneSlot = memory.BuildEntryAlloca(resultLayout, "optional_chain_none");
		Builder.BuildStore(LLVMValueRef.CreateConstNull(resultLayout), noneSlot);
		return Builder.BuildLoad2(resultLayout, noneSlot, "optional_chain_none_val");
	}

	/// <summary>
	/// Reads the payload out of a materialized Option slot.
	/// </summary>
	/// <remarks>
	/// Under NPO the Option value already <em>is</em> the payload pointer, so the payload is the slot
	/// itself; a tagged Option reads its payload slot at <c>{0,1}</c>.
	/// </remarks>
	private LLVMValueRef LoadOptionPayload(
		LLVMValueRef optionSlot,
		UnionTypeSymbol optionType,
		LLVMTypeRef optionLayout,
		TypeSymbol payloadType,
		bool usesNpo)
	{
		if (usesNpo)
			return Builder.BuildLoad2(optionLayout, optionSlot, "coalesce_payload");

		var payloadVariant = optionType.PayloadVariant
			?? throw new InvalidOperationException("A coalescing Option must expose a payload variant.");
		var payloadPtr = Builder.BuildGEP2(optionLayout, optionSlot, new LLVMValueRef[]
		{
			LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0),
			LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 1)
		}, "coalesce_payload_ptr");
		var typedPayloadPtr = Builder.BuildBitCast(
			payloadPtr,
			LLVMTypeRef.CreatePointer(LowerType(payloadType), 0),
			"coalesce_payload_cast");

		return Builder.BuildLoad2(LowerType(payloadType), typedPayloadPtr, "coalesce_payload_val");
	}

	/// <summary>
	/// Adapts a branch's payload-or-fallback value to the expression's result type.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Extracting mode passes the value straight through. Preserving mode must produce a real
	/// <c>Some</c>, but only when the branch supplied a bare payload: the <c>Some</c> path always
	/// yields an extracted payload, while the <c>None</c> path yields whatever the fallback
	/// expression produced. A fallback that already resolved to an Option of the same payload is
	/// already the right shape and must be handed over untouched -- re-tagging it would splice the
	/// Option's own discriminant into the payload slot.
	/// </para>
	/// <para>
	/// Under NPO both cases are the identity, because a non-null payload pointer <em>is</em>
	/// <c>Some</c>.
	/// </para>
	/// </remarks>
	private LLVMValueRef BuildCoalesceResult(
		UnionTypeSymbol optionType,
		LLVMTypeRef optionLayout,
		LLVMTypeRef resultLayout,
		TypeSymbol payloadType,
		bool usesNpo,
		bool preservesOption,
		bool branchIsOption,
		LLVMValueRef branchValue)
	{
		if (!preservesOption || usesNpo || branchIsOption)
			return branchValue;

		var payloadVariant = optionType.PayloadVariant
			?? throw new InvalidOperationException("A coalescing Option must expose a payload variant.");
		var payloadIndex = codegen.AggregateLayout.GetFieldIndex(optionType, payloadVariant.Name);

		var slot = memory.BuildEntryAlloca(resultLayout, "coalesce_some_tmp");
		var tagPtr = Builder.BuildGEP2(optionLayout, slot, new LLVMValueRef[]
		{
			LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0),
			LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0)
		}, "coalesce_some_tag_ptr");
		Builder.BuildStore(LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, (ulong)payloadIndex), tagPtr);

		var payloadPtr = Builder.BuildGEP2(optionLayout, slot, new LLVMValueRef[]
		{
			LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0),
			LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 1)
		}, "coalesce_some_payload_ptr");
		var typedPayloadPtr = Builder.BuildBitCast(
			payloadPtr,
			LLVMTypeRef.CreatePointer(LowerType(payloadType), 0),
			"coalesce_some_payload_cast");
		Builder.BuildStore(branchValue, typedPayloadPtr);

		return Builder.BuildLoad2(resultLayout, slot, "coalesce_some_val");
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
		var sliceAlloc = memory.BuildEntryAlloca(sliceLayout, "slice_tmp");

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
