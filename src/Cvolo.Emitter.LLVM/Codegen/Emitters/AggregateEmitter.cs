using Cvolo.Analysis;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Collections;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Expressions;
using LLVMSharp.Interop;

namespace Cvolo.Emitter.LLVM.Codegen.Emitters;

/// <summary>
/// Emits materialization and in-place initialization for Cvolo aggregate values.
/// </summary>
/// <remarks>
/// This emitter owns the IR mechanics for struct, union, and fixed-array construction, including
/// nested aggregate initialization, array replication, and the existing zero-initialization rules.
/// Expression evaluation remains delegated to <see cref="CodeGenerator"/> so this extraction does
/// not change expression semantics or introduce a second expression dispatcher.
///
/// Address resolution for arbitrary member/index expressions intentionally remains outside this
/// class for now. That path is coupled to general expression and call emission and can be moved in
/// a later mechanical refactor without mixing responsibilities into this extraction.
/// </remarks>
/// <remarks>
/// Creates an aggregate emitter backed by the module-lifetime code generation context.
/// </remarks>
/// <param name="codegen">Shared LLVM and semantic state for the current module emission.</param>
/// <param name="memory">Low-level memory emitter used for zeroing and target store-size queries.</param>
/// <param name="emitExpression">Existing expression emitter used for scalar and nested values.</param>
/// <param name="getExpressionType">Existing semantic expression-type resolver used by array inference.</param>
internal sealed class AggregateEmitter(
	CodegenContext codegen,
	MemoryEmitter memory,
	Func<ExpressionSyntax, LLVMValueRef> emitExpression,
	Func<ExpressionSyntax, TypeSymbol> getExpressionType)
{
	private LLVMBuilderRef Builder => codegen.Builder;
	private BindingContext BindingContext => codegen.BindingContext ?? throw new InvalidOperationException("Aggregate emission requires an active binding context.");

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
		var elementType = getExpressionType(expr.Elements[0]);
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
		var valueType = getExpressionType(expr.Value);
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

}
