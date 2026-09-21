using Cvolo.Analysis;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Collections;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Expressions;
using Cvolo.Core.Diagnostics;
using Cvolo.Emitter.LLVM.Codegen.Values;
using LLVMSharp.Interop;

namespace Cvolo.Emitter.LLVM.Codegen.Emitters;

/// <summary>
/// Emits materialization and in-place initialization for Cvolo aggregate values.
/// </summary>
/// <remarks>
/// This emitter owns aggregate materialization and aggregate-address mechanics: struct, union, and
/// fixed-array construction; member and index pointer resolution; enum <c>Values</c> backing storage;
/// bounds checks; and field TBAA metadata. General expression evaluation and call emission remain
/// delegated through narrow callbacks so moving this logic does not change language semantics.
/// </remarks>
/// <remarks>
/// Creates an aggregate emitter backed by module state and the currently active function frame.
/// </remarks>
/// <param name="codegen">Shared LLVM and semantic state for the current module emission.</param>
/// <param name="memory">Low-level memory emitter used for zeroing and target store-size queries.</param>
/// <param name="coercion">Shared value coercion helpers used for representation-preserving casts.</param>
/// <param name="getFunction">Returns the function-local code generation state active at the call site.</param>
/// <param name="emitExpression">Emits scalar and nested expressions without introducing a second dispatcher.</param>
/// <param name="emitStringLiteral">Emits diagnostic strings used by runtime bounds failures.</param>
/// <param name="expressionTypes">Shared semantic expression-type resolver.</param>
/// <param name="emitCall">Emits a call when an addressable aggregate is returned by a call expression.</param>
/// <param name="enableTbaa">Whether field accesses may attach type-based alias analysis metadata.</param>
internal sealed class AggregateEmitter(
	CodegenContext codegen,
	MemoryEmitter memory,
	ValueCoercion coercion,
	Func<FunctionCodegenContext> getFunction,
	Func<ExpressionSyntax, LLVMValueRef> emitExpression,
	Func<string, LLVMValueRef> emitStringLiteral,
	ExpressionTypeResolver expressionTypes,
	Func<CallExpressionSyntax, LLVMValueRef> emitCall,
	bool enableTbaa)
{
	private TbaaMetadata? _tbaa;
	private readonly Dictionary<string, LLVMValueRef> _enumValuesGlobals = [];

	private LLVMBuilderRef Builder => codegen.Builder;
	private FunctionCodegenContext Function => getFunction();
	private BindingContext BindingContext => codegen.BindingContext ?? throw new InvalidOperationException("Aggregate emission requires an active binding context.");
	private CompilationContext CompilationContext => codegen.CompilationContext ?? throw new InvalidOperationException("Aggregate emission requires an active compilation context.");
	private TbaaMetadata Tbaa => _tbaa ??= new TbaaMetadata(codegen.LLVMContext, codegen.Module, type => codegen.Types.Lower(type));

	/// <summary>
	/// Materializes a struct or union initializer into temporary storage and returns the expression
	/// representation expected by the existing code generator.
	/// </summary>
	public LLVMValueRef EmitStructInitialization(StructInitializationExpressionSyntax expr)
	{
		var typeSymbol = BindingContext.ResolveType(expr.StructTypeName);
		var structLayout = codegen.Types.Lower(typeSymbol!);
		var tempAlloc = Builder.BuildAlloca(structLayout, "struct_tmp");
		EmitStructInitializationInPlace(expr, tempAlloc);

		// NPO-eligible unions lower to a single scalar (the flat pointer), so the expression's
		// value is the loaded scalar rather than the address of its materialization slot.
		if (typeSymbol is UnionTypeSymbol { IsNpoEligible: true })
		{
			return Builder.BuildLoad2(structLayout, tempAlloc, $"struct_val_{expr.StructTypeName}");
		}

		return tempAlloc;
	}

	/// <summary>
	/// Initializes an existing destination with a struct or union literal without creating another
	/// aggregate temporary.
	/// </summary>
	public void EmitStructInitializationInPlace(StructInitializationExpressionSyntax expr, LLVMValueRef destPtr)
	{
		var typeSymbol = BindingContext.ResolveType(expr.StructTypeName);

		if (typeSymbol is UnionTypeSymbol unionType)
		{
			var unionLayout = codegen.Types.Lower(unionType);
			var init = expr.Initializers[0];
			var fieldIndex = codegen.AggregateLayout.GetFieldIndex(unionType, init.MemberName);
			var field = unionType.Fields[fieldIndex];

			// Null-pointer optimization: the flat slot is the pointer. Some(x) stores the reference
			// directly and None stores nullptr; no tag or payload aggregate exists.
			if (unionType.IsNpoEligible)
			{
				if (field.IsVoidVariant)
				{
					Builder.BuildStore(LLVMValueRef.CreateConstPointerNull(unionLayout), destPtr);
				}
				else
				{
					var value = emitExpression(init.Expression);
					Builder.BuildStore(value, destPtr);
				}

				return;
			}

			var tagPtr = Builder.BuildGEP2(unionLayout, destPtr, new LLVMValueRef[]
			{
				LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0),
				LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0)
			}, "union_tag_ptr");
			Builder.BuildStore(LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, (ulong)fieldIndex), tagPtr);

			if (!field.IsVoidVariant)
			{
				var payloadPtr = Builder.BuildGEP2(unionLayout, destPtr, new LLVMValueRef[]
				{
					LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0),
					LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 1)
				}, "union_payload_ptr");

				var castPtr = Builder.BuildBitCast(
					payloadPtr,
					LLVMTypeRef.CreatePointer(codegen.Types.Lower(field.Type), 0),
					"payload_cast_ptr");

				// Aggregate variants are materialized in place; scalar variants are stored by value.
				if (init.Expression is StructInitializationExpressionSyntax structVariant)
				{
					EmitStructInitializationInPlace(structVariant, castPtr);
				}
				else if (init.Expression is ParenthesizedStructInitializerExpressionSyntax parenVariant)
				{
					EmitParenthesizedStructInitializationInPlace(parenVariant, castPtr);
				}
				else
				{
					var value = emitExpression(init.Expression);
					Builder.BuildStore(value, castPtr);
				}
			}

			return;
		}

		var structType = (StructTypeSymbol)typeSymbol!;
		var structLayout = codegen.Types.Lower(structType);

		var providedFields = new HashSet<string>();
		foreach (var init in expr.Initializers)
		{
			var fieldIndex = codegen.AggregateLayout.GetFieldIndex(structType, init.MemberName);
			providedFields.Add(init.MemberName);
			var targetFieldPtr = Builder.BuildGEP2(structLayout, destPtr, new LLVMValueRef[]
			{
				LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0),
				LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (uint)fieldIndex)
			}, "init_field_ptr");

			if (init.Expression is ParenthesizedStructInitializerExpressionSyntax nestedParen)
			{
				EmitParenthesizedStructInitializationInPlace(nestedParen, targetFieldPtr);
			}
			else if (init.Expression is StructInitializationExpressionSyntax structInit)
			{
				EmitStructInitializationInPlace(structInit, targetFieldPtr);
			}
			else
			{
				var value = emitExpression(init.Expression);
				Builder.BuildStore(value, targetFieldPtr);
			}
		}

		// Deferred reference initialization: omitted ref/refvar fields are seeded with a null marker.
		// Semantic dataflow validation guarantees they are assigned before the unbound boundary exits.
		for (var f = 0; f < structType.Fields.Count; f++)
		{
			var field = structType.Fields[f];
			if (field.Type is PointerTypeSymbol && !providedFields.Contains(field.Name))
			{
				var fieldPtr = Builder.BuildGEP2(structLayout, destPtr, new LLVMValueRef[]
				{
					LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0),
					LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (uint)f)
				}, "deferred_field_ptr");
				Builder.BuildStore(LLVMValueRef.CreateConstPointerNull(codegen.Types.Lower(field.Type)), fieldPtr);
			}
		}
	}

	/// <summary>
	/// Materializes an array literal in stack storage using the existing element-type inference.
	/// </summary>
	public LLVMValueRef EmitArrayInitialization(ArrayInitializationExpressionSyntax expr)
	{
		var elementType = expressionTypes.Resolve(expr.Elements[0]);
		var arrayTypeSymbol = new ArrayTypeSymbol(elementType, expr.Elements.Count);
		var arrayLayout = codegen.Types.Lower(arrayTypeSymbol);

		var tempAlloc = Builder.BuildAlloca(arrayLayout, "arr_tmp");
		EmitArrayInitializationInPlace(expr, tempAlloc, arrayTypeSymbol);
		return tempAlloc;
	}

	/// <summary>
	/// Writes an array literal directly into preallocated array storage.
	/// </summary>
	public void EmitArrayInitializationInPlace(ArrayInitializationExpressionSyntax expr, LLVMValueRef destPtr, ArrayTypeSymbol arrayType)
	{
		if (expr.Elements.Count == 0 && arrayType.Size > 0)
		{
			EmitZeroInitArray(destPtr, arrayType);
			return;
		}

		var arrayLayout = codegen.Types.Lower(arrayType);

		for (var i = 0; i < expr.Elements.Count; i++)
		{
			var elementPtr = Builder.BuildGEP2(arrayLayout, destPtr, new LLVMValueRef[]
			{
				LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0),
				LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (uint)i)
			}, "arr_el");
			var elementExpr = expr.Elements[i];

			if (elementExpr is StructInitializationExpressionSyntax structInit)
			{
				EmitStructInitializationInPlace(structInit, elementPtr);
			}
			else if (elementExpr is ArrayInitializationExpressionSyntax nestedArr)
			{
				EmitArrayInitializationInPlace(nestedArr, elementPtr, (arrayType.ElementType as ArrayTypeSymbol)!);
			}
			else
			{
				var value = emitExpression(elementExpr);
				Builder.BuildStore(value, elementPtr);
			}
		}
	}

	/// <summary>
	/// Materializes an array replication expression and preserves the existing runtime loop shape.
	/// </summary>
	public LLVMValueRef EmitArrayReplication(ArrayReplicationExpressionSyntax expr)
	{
		var valueType = expressionTypes.Resolve(expr.Value);
		var countVal = expr.Count is IntegerLiteralExpressionSyntax countLit ? (int)countLit.Value : 0;
		var arrayTypeSymbol = new ArrayTypeSymbol(valueType, countVal);
		var arrayLayout = codegen.Types.Lower(arrayTypeSymbol);

		var tempAlloc = Builder.BuildAlloca(arrayLayout, "arr_repl_tmp");
		EmitArrayReplicationInPlace(expr, tempAlloc, arrayTypeSymbol);
		return tempAlloc;
	}

	/// <summary>
	/// Emits the existing replication loop into preallocated fixed-array storage.
	/// </summary>
	public void EmitArrayReplicationInPlace(ArrayReplicationExpressionSyntax expr, LLVMValueRef destPtr, ArrayTypeSymbol arrayType)
	{
		var arrayLayout = codegen.Types.Lower(arrayType);

		var currentFunc = Builder.InsertBlock.Parent;
		var condBlock = currentFunc.AppendBasicBlock("repl_cond");
		var bodyBlock = currentFunc.AppendBasicBlock("repl_body");
		var endBlock = currentFunc.AppendBasicBlock("repl_end");

		var counterAlloc = Builder.BuildAlloca(LLVMTypeRef.Int32, "repl_i");
		Builder.BuildStore(LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0), counterAlloc);
		Builder.BuildBr(condBlock);

		Builder.PositionAtEnd(condBlock);
		var iVal = Builder.BuildLoad2(LLVMTypeRef.Int32, counterAlloc, "i_val");
		var limitVal = emitExpression(expr.Count);
		var cmp = Builder.BuildICmp(LLVMIntPredicate.LLVMIntSLT, iVal, limitVal, "repl_cmp");
		Builder.BuildCondBr(cmp, bodyBlock, endBlock);

		Builder.PositionAtEnd(bodyBlock);
		var elementPtr = Builder.BuildGEP2(arrayLayout, destPtr, new LLVMValueRef[]
		{
			LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0), iVal
		}, "repl_el");

		if (expr.Value is ParenthesizedStructInitializerExpressionSyntax nestedParen)
		{
			EmitParenthesizedStructInitializationInPlace(nestedParen, elementPtr);
		}
		else if (expr.Value is StructInitializationExpressionSyntax structInit)
		{
			EmitStructInitializationInPlace(structInit, elementPtr);
		}
		else
		{
			var val = emitExpression(expr.Value);
			Builder.BuildStore(val, elementPtr);
		}

		var nextI = Builder.BuildAdd(iVal, LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 1), "next_i");
		Builder.BuildStore(nextI, counterAlloc);
		Builder.BuildBr(condBlock);

		Builder.PositionAtEnd(endBlock);
	}

	/// <summary>
	/// Materializes a parenthesized struct initializer in temporary stack storage.
	/// </summary>
	public LLVMValueRef EmitParenthesizedStructInitialization(ParenthesizedStructInitializerExpressionSyntax expr)
	{
		var typeSymbol = BindingContext.ResolveType(expr.ResolvedStructTypeName!);
		var structLayout = codegen.Types.Lower(typeSymbol!);
		var tempAlloc = Builder.BuildAlloca(structLayout, "struct_tmp");
		EmitParenthesizedStructInitializationInPlace(expr, tempAlloc);
		return tempAlloc;
	}

	/// <summary>
	/// Writes a parenthesized struct initializer directly into existing struct storage.
	/// </summary>
	public void EmitParenthesizedStructInitializationInPlace(ParenthesizedStructInitializerExpressionSyntax expr, LLVMValueRef destPtr)
	{
		var typeSymbol = BindingContext.ResolveType(expr.ResolvedStructTypeName!);
		if (typeSymbol is not StructTypeSymbol structType)
			return;
		var structLayout = codegen.Types.Lower(structType);

		foreach (var init in expr.Initializers)
		{
			var fieldIndex = codegen.AggregateLayout.GetFieldIndex(structType, init.MemberName);
			var targetFieldPtr = Builder.BuildGEP2(structLayout, destPtr, new LLVMValueRef[]
			{
				LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0),
				LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (uint)fieldIndex)
			}, "init_field_ptr");

			if (init.Expression is ParenthesizedStructInitializerExpressionSyntax nestedInit)
			{
				EmitParenthesizedStructInitializationInPlace(nestedInit, targetFieldPtr);
			}
			else if (init.Expression is StructInitializationExpressionSyntax structInit)
			{
				EmitStructInitializationInPlace(structInit, targetFieldPtr);
			}
			else
			{
				var value = emitExpression(init.Expression);
				Builder.BuildStore(value, targetFieldPtr);
			}
		}
	}

	/// <summary>
	/// Initializes an explicitly sized array to its language-defined empty value without applying a
	/// raw memset to element types whose empty representation requires semantic initialization.
	/// </summary>
	private void EmitZeroInitArray(LLVMValueRef destPtr, ArrayTypeSymbol arrayType)
	{
		var elementType = arrayType.ElementType;

		if (TypeIsZeroInitSafe(elementType))
		{
			memory.ZeroMemory(destPtr, memory.GetStoreSize(codegen.Types.Lower(arrayType)));
			return;
		}

		var arrayLayout = codegen.Types.Lower(arrayType);
		for (var i = 0; i < arrayType.Size; i++)
		{
			var elementPtr = Builder.BuildGEP2(arrayLayout, destPtr, new LLVMValueRef[]
			{
				LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0),
				LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (uint)i)
			}, "zero_el");

			if (elementType is UnionTypeSymbol unionEl)
			{
				var unionLayout = codegen.Types.Lower(unionEl);
				var tagPtr = Builder.BuildGEP2(unionLayout, elementPtr, new LLVMValueRef[]
				{
					LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0),
					LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0)
				}, "zero_tag");
				var noneTag = LLVMValueRef.CreateConstInt(
					LLVMTypeRef.Int8,
					(ulong)codegen.AggregateLayout.GetFieldIndex(unionEl, unionEl.NoneVariant.Name));
				Builder.BuildStore(noneTag, tagPtr);
				continue;
			}

			memory.ZeroMemory(elementPtr, memory.GetStoreSize(codegen.Types.Lower(elementType)));
		}
	}

	/// <summary>
	/// Returns whether a raw all-zero byte representation is a valid empty value for the type under
	/// the current language rules.
	/// </summary>
	private bool TypeIsZeroInitSafe(TypeSymbol type)
	{
		if (type is ArrayTypeSymbol arrayType)
			return TypeIsZeroInitSafe(arrayType.ElementType);

		if (type is UnionTypeSymbol unionType)
		{
			if (unionType.IsNpoEligible)
				return true;
			return false;
		}

		if (type is StructTypeSymbol structType)
		{
			if (BindingContext.OverloadedFunctions.ContainsKey($"{structType.Name}.~{structType.Name}"))
				return false;
			return structType.Fields.All(f => TypeIsZeroInitSafe(f.Type));
		}

		return true;
	}


	/// <summary>
	/// Resolves writable/readable storage for identifiers, members, indices, borrows, safe enum casts,
	/// and aggregate-returning calls while preserving the existing value-provenance and TBAA behavior.
	/// </summary>
	public (LLVMValueRef ptr, TypeSymbol type, bool valueProvenance, LLVMValueRef? tbaa) GetFieldPointer(ExpressionSyntax expr)
	{
		if (expr is IdentifierExpressionSyntax id)
		{
			if (!Function.Locals.TryGetValue(id.Name, out var structPtr))
			{
				// Resolve unqualified receiver fields inside extension/receiver bodies as implicit this.field accesses.
				if (Function.Locals.TryGetValue("this", out var thisPtr))
				{
					var thisType = Function.VariableTypes["this"] as PointerTypeSymbol;
					var refType = thisType!.ReferencedType;

					if (refType is StructTypeSymbol structType)
					{
						var field = structType.FindField(id.Name);
						if (field is not null)
						{
							var actualThisPtr = Builder.BuildLoad2(LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), thisPtr, "loaded_this_ptr");
							var fieldIndex = codegen.AggregateLayout.GetFieldIndex(structType, id.Name);
							var structLayoutTy = codegen.Types.Lower(structType);
							var zero = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0);
							var index = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (uint)fieldIndex);
							var fieldPtr = Builder.BuildGEP2(structLayoutTy, actualThisPtr, new LLVMValueRef[] { zero, index }, "this_field_ptr");
							return (fieldPtr, field.Type, true, GetTbaaTag(structType, fieldIndex));
						}
					}
					else if (refType is UnionTypeSymbol unionType)
					{
						var field = unionType.FindField(id.Name);
						if (field is not null)
						{
							var actualThisPtr = Builder.BuildLoad2(LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), thisPtr, "loaded_this_ptr");
							if (unionType.IsNpoEligible && !field.IsVoidVariant)
								return (actualThisPtr, field.Type, false, null);

							var structLayoutTy = codegen.Types.Lower(unionType);
							var payloadPtr = Builder.BuildGEP2(structLayoutTy, actualThisPtr, new LLVMValueRef[]
							{
								LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0),
								LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 1)
							}, "union_payload_ptr");
							var castPtr = Builder.BuildBitCast(payloadPtr, LLVMTypeRef.CreatePointer(codegen.Types.Lower(field.Type), 0), "payload_cast_ptr");
							return (castPtr, field.Type, false, null);
						}
					}
				}

				throw new InvalidOperationException($"Undefined variable '{id.Name}'");
			}

			var type = Function.VariableTypes[id.Name];
			var isReference = type is PointerTypeSymbol;
			var isHeap = Function.HeapAllocatedVars.Contains(id.Name) && type is not SliceTypeSymbol;
			if (isReference || isHeap)
			{
				var actualPtr = Builder.BuildLoad2(LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), structPtr, "loaded_ptr");
				var innerType = type is PointerTypeSymbol ptrType ? ptrType.ReferencedType : type;
				return (actualPtr, innerType, true, null);
			}

			return (structPtr, type, true, null);
		}

		if (expr is MemberAccessExpressionSyntax member)
		{
			if (TryExtractQualifiedGlobalKey(member) is { } globalBaseKey
				&& codegen.GlobalVariables.TryGetValue(globalBaseKey, out var globalBasePtr))
			{
				return (globalBasePtr, codegen.GlobalVariableTypes[globalBaseKey], true, null);
			}

			if (TryResolveEnumVariantReceiver(member) is { } enumValuesType && member.MemberName == "Values")
			{
				var (enumPtr, enumType) = EmitEnumValuesSlicePointer(enumValuesType);
				return (enumPtr, enumType, false, null);
			}

			var (parentPtr, parentType, valueProvenance, _) = GetFieldPointer(member.Expression);
			if (parentType is SliceTypeSymbol sliceType && member.MemberName == "Length")
			{
				var structLayout = codegen.Types.Lower(sliceType);
				var lengthPtr = Builder.BuildGEP2(structLayout, parentPtr, new LLVMValueRef[]
				{
					LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0),
					LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 1)
				}, "len_ptr");
				return (lengthPtr, TypeSymbol.Int, false, null);
			}

			if (parentType is PointerTypeSymbol refPtrType)
			{
				var referred = refPtrType.ReferencedType;
				var refStruct = referred as StructTypeSymbol ?? BindingContext.ResolveType(referred.Name) as StructTypeSymbol;
				if (refStruct is not null)
				{
					var rawPtr = Builder.BuildLoad2(LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), parentPtr, "reffield_load");
					var refFieldIndex = codegen.AggregateLayout.GetFieldIndex(refStruct, member.MemberName);
					var refFieldType = refStruct.Fields[refFieldIndex].Type;
					var refStructLayoutTy = codegen.Types.Lower(refStruct);
					var refZero = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0);
					var refIndex = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (uint)refFieldIndex);
					var refFieldPtr = Builder.BuildGEP2(refStructLayoutTy, rawPtr, new LLVMValueRef[] { refZero, refIndex }, "reffield_member_ptr");
					return (refFieldPtr, refFieldType, false, GetTbaaTag(refStruct, refFieldIndex));
				}

				parentType = referred;
			}

			if (member.Operator == "->")
			{
				var rawPtr = Builder.BuildLoad2(LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), parentPtr, "arrow_load");
				var structType = (parentType as StructTypeSymbol)
					?? (parentType is RawPointerTypeSymbol rawPointer ? rawPointer.ElementType as StructTypeSymbol : null)
					?? (parentType is PointerTypeSymbol pointer ? pointer.ReferencedType as StructTypeSymbol : null)
					?? BindingContext.ResolveType(parentType.Name) as StructTypeSymbol;
				if (structType is null)
					throw new InvalidOperationException($"Cannot resolve struct type for arrow operator on '{parentType.Name}'");

				var fieldIndex = codegen.AggregateLayout.GetFieldIndex(structType, member.MemberName);
				var fieldType = structType.Fields[fieldIndex].Type;
				var structLayoutTy = codegen.Types.Lower(structType);
				var zero = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0);
				var index = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (uint)fieldIndex);
				var fieldPtr = Builder.BuildGEP2(structLayoutTy, rawPtr, new LLVMValueRef[] { zero, index }, "arrow_field_ptr");
				return (fieldPtr, fieldType, false, GetTbaaTag(structType, fieldIndex));
			}

			if (parentType is not (StructTypeSymbol or UnionTypeSymbol)
				&& BindingContext.ResolveType(parentType.Name) is TypeSymbol resolvedParent)
			{
				parentType = resolvedParent;
			}

			if (parentType is UnionTypeSymbol unionType)
			{
				var fieldIndex = codegen.AggregateLayout.GetFieldIndex(unionType, member.MemberName);
				var fieldType = unionType.Fields[fieldIndex].Type;
				if (unionType.IsNpoEligible && !unionType.Fields[fieldIndex].IsVoidVariant)
					return (parentPtr, fieldType, false, null);

				var structLayoutTy = codegen.Types.Lower(parentType);
				var payloadPtr = Builder.BuildGEP2(structLayoutTy, parentPtr, new LLVMValueRef[]
				{
					LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0),
					LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 1)
				}, "union_payload_ptr");
				var castPtr = coercion.SafeBitCast(payloadPtr, LLVMTypeRef.CreatePointer(codegen.Types.Lower(fieldType), 0), "payload_cast_ptr");
				return (castPtr, fieldType, false, null);
			}

			var dotStructType = (parentType as StructTypeSymbol) ?? BindingContext.ResolveType(parentType.Name) as StructTypeSymbol;
			if (dotStructType is null)
				throw new InvalidOperationException($"Type '{parentType.Name}' is not a struct; cannot access member '{member.MemberName}'");

			var dotFieldIndex = codegen.AggregateLayout.GetFieldIndex(dotStructType, member.MemberName);
			var dotFieldType = dotStructType.Fields[dotFieldIndex].Type;
			var dotStructLayoutTy = codegen.Types.Lower(dotStructType);
			var dotZero = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0);
			var dotIndex = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (uint)dotFieldIndex);
			var dotFieldPtr = Builder.BuildGEP2(dotStructLayoutTy, parentPtr, new LLVMValueRef[] { dotZero, dotIndex }, "member_ptr");
			return (dotFieldPtr, dotFieldType, valueProvenance, valueProvenance ? GetTbaaTag(dotStructType, dotFieldIndex) : null);
		}

		if (expr is IndexExpressionSyntax indexExpression)
		{
			var (parentPtr, parentType, _, _) = GetFieldPointer(indexExpression.Left);
			var indexVal = emitExpression(indexExpression.Index);
			if (parentType is SliceTypeSymbol sliceType)
			{
				var sliceLayout = codegen.Types.Lower(sliceType);
				var arrPtrField = Builder.BuildGEP2(sliceLayout, parentPtr, new LLVMValueRef[]
				{
					LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0),
					LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0)
				}, "arr_field");
				var arrayPtr = Builder.BuildLoad2(LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), arrPtrField, "arr_ptr");
				var lenPtrField = Builder.BuildGEP2(sliceLayout, parentPtr, new LLVMValueRef[]
				{
					LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0),
					LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 1)
				}, "len_field");
				var lengthReg = Builder.BuildLoad2(LLVMTypeRef.Int32, lenPtrField, "len_val");
				InjectBoundsCheck(indexExpression, indexVal, lengthReg);
				var elementLlvmTy = codegen.Types.Lower(sliceType.ElementType);
				var elementPtr = Builder.BuildGEP2(elementLlvmTy, arrayPtr, new LLVMValueRef[] { indexVal }, "element_ptr");
				return (elementPtr, sliceType.ElementType, false, null);
			}

			if (parentType is ArrayTypeSymbol arrayType)
			{
				var arrayLayout = codegen.Types.Lower(arrayType);
				var limit = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (ulong)arrayType.Size);
				InjectBoundsCheck(indexExpression, indexVal, limit);
				var elementPtr = Builder.BuildGEP2(arrayLayout, parentPtr, new LLVMValueRef[]
				{
					LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0), indexVal
				}, "element_ptr");
				return (elementPtr, arrayType.ElementType, false, null);
			}
		}

		if (expr is BorrowExpressionSyntax borrow)
			return GetFieldPointer(borrow.Expression);

		if (expr is UnaryExpressionSyntax castExpr
			&& castExpr.Operator.StartsWith('(')
			&& castExpr.Operator.EndsWith(')')
			&& Function.UnsafeDepth == 0)
		{
			var castTypeName = castExpr.Operator.Substring(1, castExpr.Operator.Length - 2);
			if (BindingContext.ResolveType(castTypeName) is EnumTypeSymbol castEnum)
			{
				var castOperandType = expressionTypes.Resolve(castExpr.Operand);
				if (castOperandType is not EnumTypeSymbol
					&& TypeSymbol.IsIntegerType(castOperandType)
					&& BindingContext.ResolveType($"Option<{castEnum.Name}>") is UnionTypeSymbol castOption)
				{
					var castOperand = emitExpression(castExpr.Operand);
					var (castPtr, castType) = MaterializeEnumCastOption(castEnum, castOperand, castOperandType, castOption);
					return (castPtr, castType, false, null);
				}
			}
		}

		if (expr is CallExpressionSyntax call)
		{
			var retType = expressionTypes.Resolve(call);
			var callVal = emitCall(call);
			if (retType is PointerTypeSymbol ptrType)
			{
				var inner = ptrType.ReferencedType;
				if (inner is not (StructTypeSymbol or UnionTypeSymbol) && BindingContext.ResolveType(inner.Name) is TypeSymbol resolvedInner)
					inner = resolvedInner;
				return (callVal, inner, false, null);
			}

			if (retType is RawPointerTypeSymbol rawPtrType)
			{
				var inner = rawPtrType.ElementType;
				if (inner is not (StructTypeSymbol or UnionTypeSymbol) && BindingContext.ResolveType(inner.Name) is TypeSymbol resolvedInner)
					inner = resolvedInner;
				return (callVal, inner, false, null);
			}

			var structType = retType as StructTypeSymbol ?? BindingContext.ResolveType(retType.Name) as StructTypeSymbol;
			if (structType is not null)
			{
				var structLayout = codegen.Types.Lower(structType);
				var tempAlloc = Builder.BuildAlloca(structLayout, "call_struct_tmp");
				Builder.BuildStore(callVal, tempAlloc);
				return (tempAlloc, structType, false, null);
			}

			var unionType = retType as UnionTypeSymbol ?? BindingContext.ResolveType(retType.Name) as UnionTypeSymbol;
			if (unionType is not null)
			{
				var unionLayout = codegen.Types.Lower(unionType);
				var tempAlloc = Builder.BuildAlloca(unionLayout, "call_union_tmp");
				Builder.BuildStore(callVal, tempAlloc);
				return (tempAlloc, unionType, false, null);
			}

			return (callVal, retType, false, null);
		}

		throw new InvalidOperationException($"Unsupported {expr.GetType()} field pointer expression");
	}

	/// <summary>
	/// Extracts the fully qualified name represented by a pure identifier/member-access chain.
	/// Returns <see langword="null"/> when the expression contains a value-producing receiver.
	/// </summary>
	public string? TryExtractQualifiedGlobalKey(ExpressionSyntax expr)
	{
		if (expr is not MemberAccessExpressionSyntax outer)
			return null;

		var segments = new List<string> { outer.MemberName };
		var current = outer.Expression;
		while (current is MemberAccessExpressionSyntax nested)
		{
			if (nested.Expression is not IdentifierExpressionSyntax && nested.Expression is not MemberAccessExpressionSyntax)
				return null;
			segments.Add(nested.MemberName);
			current = nested.Expression;
		}

		if (current is not IdentifierExpressionSyntax leaf)
			return null;
		segments.Add(leaf.Name);
		segments.Reverse();
		return string.Join(".", segments);
	}

	/// <summary>
	/// Resolves an enum type used as the receiver of a scoped variant or enum metaprogramming access.
	/// Returns <see langword="null"/> when the receiver denotes a runtime value instead of a type name.
	/// </summary>
	public EnumTypeSymbol? TryResolveEnumVariantReceiver(MemberAccessExpressionSyntax member)
	{
		var dotted = GetDottedName(member.Expression);
		return dotted is null ? null : BindingContext.ResolveType(dotted) as EnumTypeSymbol;
	}

	/// <summary>
	/// Materializes <c>Enum.Values</c> as a slice whose data points at a single read-only module global.
	/// </summary>
	public (LLVMValueRef ptr, TypeSymbol type) EmitEnumValuesSlicePointer(EnumTypeSymbol enumType)
	{
		var sliceType = new SliceTypeSymbol(enumType);
		var sliceLayout = codegen.Types.Lower(sliceType);
		var elementTy = codegen.Types.Lower(enumType.StorageType);
		var count = enumType.IsFlags
			? enumType.Variants.Count(variant => variant.Value > 0 && IsPowerOfTwo(variant.Value))
			: enumType.Variants.Count;
		var global = GetOrCreateEnumValuesGlobal(enumType, elementTy, count);

		var tmp = Builder.BuildAlloca(sliceLayout, "values_slice");
		var zero = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0);
		var one = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 1);
		var arrPtr = Builder.BuildGEP2(sliceLayout, tmp, new LLVMValueRef[] { zero, zero }, "values_arr_ptr");
		Builder.BuildStore(Builder.BuildBitCast(global, LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), "values_arr_cast"), arrPtr);
		var lenPtr = Builder.BuildGEP2(sliceLayout, tmp, new LLVMValueRef[] { zero, one }, "values_len_ptr");
		Builder.BuildStore(LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (uint)count), lenPtr);
		return (tmp, sliceType);
	}

	/// <summary>
	/// Builds the tagged <c>Option&lt;Enum&gt;</c> temporary used by safe integer-to-enum casts.
	/// </summary>
	public (LLVMValueRef ptr, UnionTypeSymbol unionType) MaterializeEnumCastOption(
		EnumTypeSymbol enumType,
		LLVMValueRef operand,
		TypeSymbol operandType,
		UnionTypeSymbol optionUnion)
	{
		var storageTy = codegen.Types.Lower(enumType.StorageType);
		var normalized = operand;
		var operandWidth = TypeSymbol.IntegerBitWidth(operandType);
		var storageWidth = TypeSymbol.IntegerBitWidth(enumType.StorageType);
		if (operandWidth > storageWidth)
			normalized = Builder.BuildTrunc(normalized, storageTy, "ecast_trunc");
		else if (operandWidth < storageWidth)
			normalized = TypeSymbol.IsSignedIntegerType(operandType)
				? Builder.BuildSExt(normalized, storageTy, "ecast_sext")
				: Builder.BuildZExt(normalized, storageTy, "ecast_zext");

		var unionLayout = codegen.Types.Lower(optionUnion);
		var tmp = Builder.BuildAlloca(unionLayout, "ecast_tmp");
		var someTag = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, (ulong)codegen.AggregateLayout.GetFieldIndex(optionUnion, "Some"));
		var noneTag = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, (ulong)codegen.AggregateLayout.GetFieldIndex(optionUnion, "None"));
		var zero = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0);
		var one = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 1);
		var tagPtr = Builder.BuildGEP2(unionLayout, tmp, new LLVMValueRef[] { zero, zero }, "ecast_tag");
		var payloadPtr = Builder.BuildGEP2(unionLayout, tmp, new LLVMValueRef[] { zero, one }, "ecast_payload");
		var currentFunc = Builder.InsertBlock.Parent;
		var join = currentFunc.AppendBasicBlock("ecast_join");
		var nextCheck = Builder.InsertBlock;

		foreach (var variant in enumType.Variants)
		{
			var matchBlock = currentFunc.AppendBasicBlock($"ecast_{variant.Name}");
			var afterBlock = currentFunc.AppendBasicBlock($"ecast_{variant.Name}_next");
			Builder.PositionAtEnd(nextCheck);
			var variantConst = LLVMValueRef.CreateConstInt(storageTy, unchecked((ulong)variant.Value));
			var matches = Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, normalized, variantConst, "ecast_cmp");
			Builder.BuildCondBr(matches, matchBlock, afterBlock);
			Builder.PositionAtEnd(matchBlock);
			Builder.BuildStore(someTag, tagPtr);
			Builder.BuildStore(normalized, payloadPtr);
			Builder.BuildBr(join);
			nextCheck = afterBlock;
		}

		Builder.PositionAtEnd(nextCheck);
		Builder.BuildStore(noneTag, tagPtr);
		Builder.BuildBr(join);
		Builder.PositionAtEnd(join);
		return (tmp, optionUnion);
	}

	/// <summary>
	/// Returns field-specific TBAA metadata when alias tagging is enabled and safe for the current access.
	/// </summary>
	public LLVMValueRef? GetTbaaTag(StructTypeSymbol structType, int fieldIndex)
	{
		if (!enableTbaa || Function.UnsafeDepth != 0 || !TbaaMetadata.IsScalar(structType.Fields[fieldIndex].Type))
			return null;
		return Tbaa.GetFieldTag(structType, fieldIndex);
	}

	/// <summary>
	/// Attaches previously computed TBAA metadata to a load or store instruction when a tag is available.
	/// </summary>
	public void ApplyTbaa(LLVMValueRef? tag, LLVMValueRef instruction)
	{
		if (tag is not null)
			instruction.SetMetadata(Tbaa.TbaaKindId, tag.Value);
	}

	/// <summary>
	/// Emits the existing unsigned index bounds check and branches to the runtime panic path on failure.
	/// </summary>
	private void InjectBoundsCheck(IndexExpressionSyntax indexExpression, LLVMValueRef indexValue, LLVMValueRef limitValue)
	{
		var currentFunc = Builder.InsertBlock.Parent;
		var safeBlock = currentFunc.AppendBasicBlock("bounds_safe");
		var panicBlock = currentFunc.AppendBasicBlock("bounds_panic");
		var comparison = Builder.BuildICmp(LLVMIntPredicate.LLVMIntULT, indexValue, limitValue, "is_in_bounds");
		Builder.BuildCondBr(comparison, safeBlock, panicBlock);
		Builder.PositionAtEnd(panicBlock);
		EmitPanicRoutine(indexExpression);
		Builder.PositionAtEnd(safeBlock);
	}

	/// <summary>
	/// Emits the existing formatted runtime bounds diagnostic, terminates the process, and marks the path unreachable.
	/// </summary>
	private void EmitPanicRoutine(IndexExpressionSyntax indexExpression)
	{
		var errorLines = CompilationContext.FormatDiagnostic(
			"Runtime Error",
			"Index was outside the bounds of the array.",
			indexExpression.Span,
			true);
		var putsFunc = codegen.Globals["puts"];
		var putsType = codegen.FunctionTypes["puts"];
		var exitFunc = codegen.Globals["exit"];
		var exitType = codegen.FunctionTypes["exit"];
		foreach (var line in errorLines)
		{
			var strConstant = emitStringLiteral(line);
			Builder.BuildCall2(putsType, putsFunc, new LLVMValueRef[] { strConstant }, "puts_call");
		}
		Builder.BuildCall2(exitType, exitFunc, new LLVMValueRef[]
		{
			LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 1)
		}, "");
		Builder.BuildUnreachable();
	}

	/// <summary>
	/// Returns or creates the read-only module global that stores the scalar values exposed by <c>Enum.Values</c>.
	/// </summary>
	private LLVMValueRef GetOrCreateEnumValuesGlobal(EnumTypeSymbol enumType, LLVMTypeRef elementType, int count)
	{
		if (_enumValuesGlobals.TryGetValue(enumType.Name, out var existing))
			return existing;

		var constElements = enumType.IsFlags
			? enumType.Variants.Where(variant => variant.Value > 0 && IsPowerOfTwo(variant.Value))
				.Select(variant => LLVMValueRef.CreateConstInt(elementType, unchecked((ulong)variant.Value))).ToArray()
			: enumType.Variants
				.Select(variant => LLVMValueRef.CreateConstInt(elementType, unchecked((ulong)variant.Value))).ToArray();
		var global = codegen.Module.AddGlobal(LLVMTypeRef.CreateArray(elementType, (uint)count), $"enum_values_{enumType.Name}");
		global.Initializer = LLVMValueRef.CreateConstArray(elementType, constElements);
		global.IsGlobalConstant = true;
		_enumValuesGlobals[enumType.Name] = global;
		return global;
	}

	/// <summary>
	/// Reconstructs an identifier/member-access chain as a dotted type name for scoped enum lookup.
	/// </summary>
	private static string? GetDottedName(ExpressionSyntax expr)
	{
		if (expr is IdentifierExpressionSyntax id)
			return id.Name;
		if (expr is MemberAccessExpressionSyntax member && GetDottedName(member.Expression) is { } baseName)
			return $"{baseName}.{member.MemberName}";
		return null;
	}

	/// <summary>
	/// Returns whether a positive integral enum flag value contains exactly one set bit.
	/// </summary>
	private static bool IsPowerOfTwo(long value) => value > 0 && (value & (value - 1)) == 0;


}
