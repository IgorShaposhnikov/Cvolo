using Cvolo.Analysis;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Collections;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using Cvolo.Core.AST.Statements;
using Cvolo.Emitter.LLVM.Codegen.ControlFlow;
using LLVMSharp.Interop;

namespace Cvolo.Emitter.LLVM.Codegen.Emitters;

/// <summary>
/// Emits statement-level LLVM control flow for conditionals, loops, foreach iteration, switch
/// dispatch, and break/continue transfers.
/// </summary>
/// <remarks>
/// This extraction intentionally keeps the top-level statement dispatcher, expression emission,
/// variable declaration emission, and block cleanup orchestration in <see cref="CodeGenerator"/>.
/// The callbacks are a migration seam so control-flow lowering can move without changing language
/// semantics. Return/defer/unsafe paths remain outside this class until their cleanup interactions
/// can be moved as a separate mechanical step.
/// </remarks>
/// <remarks>
/// Creates a statement emitter that reuses the current expression, block, variable, address,
/// cleanup, and memory paths while statement lowering is incrementally extracted.
/// </remarks>
internal sealed class StatementEmitter(
	CodegenContext codegen,
	CleanupEmitter cleanup,
	MemoryEmitter memory,
	Func<FunctionCodegenContext> getFunction,
	Func<ExpressionSyntax, LLVMValueRef> emitExpression,
	Func<ExpressionSyntax, TypeSymbol> getExpressionType,
	Func<ExpressionSyntax, (LLVMValueRef ptr, TypeSymbol type, bool valueProvenance, LLVMValueRef? tbaa)> getFieldPointer,
	Action<SyntaxNode> emitStatement,
	Action<BlockStatementSyntax> emitBlock,
	Action<VariableDeclarationSyntax> emitVariableDeclaration,
	Action emitTrapDefault)
{
	private LLVMBuilderRef Builder => codegen.Builder;
	private FunctionCodegenContext Function => getFunction();
	private BindingContext BindingContext => codegen.BindingContext ?? throw new InvalidOperationException("Statement emission requires an active binding context.");

	/// <summary>
	/// Emits conditional control flow for an if/else statement and rejoins unterminated branches at a merge block.
	/// </summary>
	public void EmitIfStatement(IfStatementSyntax ifStmt)
	{
		var condition = emitExpression(ifStmt.Condition);

		var currentFunc = Builder.InsertBlock.Parent;
		var thenBlock = currentFunc.AppendBasicBlock("then");
		var elseBlock = currentFunc.AppendBasicBlock("else");
		var mergeBlock = currentFunc.AppendBasicBlock("ifend");

		Builder.BuildCondBr(condition, thenBlock, elseBlock);

		Builder.PositionAtEnd(thenBlock);
		emitStatement(ifStmt.ThenStatement);
		if (Builder.InsertBlock.Terminator.Handle == IntPtr.Zero)
			Builder.BuildBr(mergeBlock);

		Builder.PositionAtEnd(elseBlock);
		if (ifStmt.ElseClause is not null)
			emitStatement(ifStmt.ElseClause.Body);
		if (Builder.InsertBlock.Terminator.Handle == IntPtr.Zero)
			Builder.BuildBr(mergeBlock);

		Builder.PositionAtEnd(mergeBlock);
	}

	/// <summary>
	/// Emits a while loop, including labeled break/continue targets tracked by the current function context.
	/// </summary>
	public void EmitWhileStatement(WhileStatementSyntax whileStmt)
	{
		var currentFunc = Builder.InsertBlock.Parent;
		var prefix = !string.IsNullOrEmpty(whileStmt.Label) ? whileStmt.Label + "." : "";
		var condBlock = currentFunc.AppendBasicBlock(prefix + "whilecond");
		var bodyBlock = currentFunc.AppendBasicBlock(prefix + "whilebody");
		var endBlock = currentFunc.AppendBasicBlock(prefix + "whileend");

		var ctx = new LoopCodegenFrame(whileStmt.Label, endBlock, condBlock);
		Function.LoopContexts.Push(ctx);
		if (!string.IsNullOrEmpty(whileStmt.Label))
		{
			Function.LabeledBreaks.Push(new LabeledBreakCodegenFrame(whileStmt.Label!, endBlock));
		}

		Builder.BuildBr(condBlock);

		Builder.PositionAtEnd(condBlock);
		var condition = emitExpression(whileStmt.Condition);
		Builder.BuildCondBr(condition, bodyBlock, endBlock);

		Builder.PositionAtEnd(bodyBlock);
		emitStatement(whileStmt.Body);
		if (Builder.InsertBlock.Terminator.Handle == IntPtr.Zero)
		{
			Builder.BuildBr(condBlock);
		}

		Function.LoopContexts.Pop();
		if (!string.IsNullOrEmpty(whileStmt.Label))
		{
			Function.LabeledBreaks.Pop();
		}

		Builder.PositionAtEnd(endBlock);
	}

	/// <summary>
	/// Emits a classic for loop while preserving the existing initializer, condition, increment, and label semantics.
	/// </summary>
	public void EmitForStatement(ForStatementSyntax forStmt)
	{
		emitVariableDeclaration(forStmt.Initializer);

		var currentFunc = Builder.InsertBlock.Parent;
		var prefix = !string.IsNullOrEmpty(forStmt.Label) ? forStmt.Label + "." : "";
		var condBlock = currentFunc.AppendBasicBlock(prefix + "forcond");
		var bodyBlock = currentFunc.AppendBasicBlock(prefix + "forbody");
		var incBlock = currentFunc.AppendBasicBlock(prefix + "forinc");
		var endBlock = currentFunc.AppendBasicBlock(prefix + "forend");

		var ctx = new LoopCodegenFrame(forStmt.Label, endBlock, incBlock);
		Function.LoopContexts.Push(ctx);
		if (!string.IsNullOrEmpty(forStmt.Label))
		{
			Function.LabeledBreaks.Push(new LabeledBreakCodegenFrame(forStmt.Label!, endBlock));
		}

		Builder.BuildBr(condBlock);

		Builder.PositionAtEnd(condBlock);
		var condition = emitExpression(forStmt.Condition);
		Builder.BuildCondBr(condition, bodyBlock, endBlock);

		Builder.PositionAtEnd(bodyBlock);
		emitStatement(forStmt.Body);
		if (Builder.InsertBlock.Terminator.Handle == IntPtr.Zero)
		{
			Builder.BuildBr(incBlock);
		}

		Function.LoopContexts.Pop();
		if (!string.IsNullOrEmpty(forStmt.Label))
			Function.LabeledBreaks.Pop();

		Builder.PositionAtEnd(incBlock);
		emitExpression(forStmt.Increment);
		Builder.BuildBr(condBlock);

		Builder.PositionAtEnd(endBlock);
	}

	/// <summary>
	/// Selects the existing foreach lowering strategy for arrays, slices, or protocol-style enumerators.
	/// </summary>
	public void EmitForEachStatement(ForEachStatementSyntax fe)
	{
		var rawCollectionType = getExpressionType(fe.Collection);
		var underlyingCollectionType = rawCollectionType;
		if (underlyingCollectionType is PointerTypeSymbol colPtr)
		{
			underlyingCollectionType = colPtr.ReferencedType;
		}

		var itemTypeName = fe.ItemTypeName;
		var itemType = itemTypeName is not null ? BindingContext.ResolveType(itemTypeName) ?? TypeSymbol.Int : TypeSymbol.Int;

		var currentFunc = Builder.InsertBlock.Parent;
		var prefix = !string.IsNullOrEmpty(fe.Label) ? fe.Label + "." : "";

		if (underlyingCollectionType is ArrayTypeSymbol arrayType)
		{
			EmitForEachArray(fe, arrayType, itemType, currentFunc, prefix);
		}
		else if (underlyingCollectionType is SliceTypeSymbol sliceType)
		{
			EmitForEachSlice(fe, sliceType, itemType, currentFunc, prefix);
		}
		else
		{
			EmitForEachEnumerator(fe, underlyingCollectionType, itemType, currentFunc, prefix);
		}
	}

	/// <summary>
	/// Emits indexed foreach iteration over a fixed-size array and materializes either value or reference bindings.
	/// </summary>
	private void EmitForEachArray(ForEachStatementSyntax fe, ArrayTypeSymbol arrayType, TypeSymbol itemType, LLVMValueRef currentFunc, string prefix)
	{
		var (collectionAlloca, _, _, _) = getFieldPointer(fe.Collection);

		var counterAlloca = memory.BuildEntryAlloca(LLVMTypeRef.Int32, "__fe_i");
		Builder.BuildStore(LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0), counterAlloca);

		var itemSlotTy = fe.IsReferenceBinding ? LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0) : codegen.Types.Lower(itemType);
		var itemAlloca = memory.BuildEntryAlloca(itemSlotTy, fe.ItemName);
		Function.Locals[fe.ItemName] = itemAlloca;
		Function.VariableTypes[fe.ItemName] = fe.IsReferenceBinding ? new PointerTypeSymbol(itemType, isMutable: fe.BindingKind == ForEachVariableKind.RefVar) : itemType;

		var condBlock = currentFunc.AppendBasicBlock(prefix + "fecond");
		var bodyBlock = currentFunc.AppendBasicBlock(prefix + "febody");
		var incBlock = currentFunc.AppendBasicBlock(prefix + "feinc");
		var endBlock = currentFunc.AppendBasicBlock(prefix + "feend");

		var ctx = new LoopCodegenFrame(fe.Label, endBlock, incBlock);
		Function.LoopContexts.Push(ctx);
		if (!string.IsNullOrEmpty(fe.Label))
			Function.LabeledBreaks.Push(new LabeledBreakCodegenFrame(fe.Label!, endBlock));

		Builder.BuildBr(condBlock);

		Builder.PositionAtEnd(condBlock);
		var iVal = Builder.BuildLoad2(LLVMTypeRef.Int32, counterAlloca, "__fe_i_val");
		var length = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, (ulong)arrayType.Size);
		var cmp = Builder.BuildICmp(LLVMIntPredicate.LLVMIntSLT, iVal, length, "__fe_cmp");
		Builder.BuildCondBr(cmp, bodyBlock, endBlock);

		Builder.PositionAtEnd(bodyBlock);
		var arrayLayout = codegen.Types.Lower(arrayType);
		var elemPtr = Builder.BuildGEP2(arrayLayout, collectionAlloca, new LLVMValueRef[]
		{
			LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0), iVal
		}, "__fe_elem_ptr");
		if (fe.IsReferenceBinding)
		{
			Builder.BuildStore(elemPtr, itemAlloca);
		}
		else
		{
			var elemVal = Builder.BuildLoad2(codegen.Types.Lower(itemType), elemPtr, "__fe_elem");
			Builder.BuildStore(elemVal, itemAlloca);
		}

		emitStatement(fe.Body);
		if (Builder.InsertBlock.Terminator.Handle == IntPtr.Zero)
		{
			Builder.BuildBr(incBlock);
		}

		Function.LoopContexts.Pop();
		if (!string.IsNullOrEmpty(fe.Label))
			Function.LabeledBreaks.Pop();

		Builder.PositionAtEnd(incBlock);
		var newI = Builder.BuildAdd(iVal, LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 1), "__fe_new_i");
		Builder.BuildStore(newI, counterAlloca);
		Builder.BuildBr(condBlock);

		Builder.PositionAtEnd(endBlock);
	}

	/// <summary>
	/// Emits indexed foreach iteration over a slice using its existing pointer-and-length representation.
	/// </summary>
	private void EmitForEachSlice(ForEachStatementSyntax fe, SliceTypeSymbol sliceType, TypeSymbol itemType, LLVMValueRef currentFunc, string prefix)
	{
		var (collectionAlloca, _, _, _) = getFieldPointer(fe.Collection);
		var sliceLayout = codegen.Types.Lower(sliceType);

		var lenPtrField = Builder.BuildGEP2(sliceLayout, collectionAlloca, new LLVMValueRef[]
		{
			LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0),
			LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 1)
		}, "__fe_len_field");
		var length = Builder.BuildLoad2(LLVMTypeRef.Int32, lenPtrField, "__fe_len");

		var counterAlloca = memory.BuildEntryAlloca(LLVMTypeRef.Int32, "__fe_i");
		Builder.BuildStore(LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0), counterAlloca);

		var itemSlotTy = fe.IsReferenceBinding ? LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0) : codegen.Types.Lower(itemType);
		var itemAlloca = memory.BuildEntryAlloca(itemSlotTy, fe.ItemName);
		Function.Locals[fe.ItemName] = itemAlloca;
		Function.VariableTypes[fe.ItemName] = fe.IsReferenceBinding ? new PointerTypeSymbol(itemType, isMutable: fe.BindingKind == ForEachVariableKind.RefVar) : itemType;

		var condBlock = currentFunc.AppendBasicBlock(prefix + "fecond");
		var bodyBlock = currentFunc.AppendBasicBlock(prefix + "febody");
		var incBlock = currentFunc.AppendBasicBlock(prefix + "feinc");
		var endBlock = currentFunc.AppendBasicBlock(prefix + "feend");

		var ctx = new LoopCodegenFrame(fe.Label, endBlock, incBlock);
		Function.LoopContexts.Push(ctx);
		if (!string.IsNullOrEmpty(fe.Label))
			Function.LabeledBreaks.Push(new LabeledBreakCodegenFrame(fe.Label!, endBlock));

		Builder.BuildBr(condBlock);

		Builder.PositionAtEnd(condBlock);
		var iVal = Builder.BuildLoad2(LLVMTypeRef.Int32, counterAlloca, "__fe_i_val");
		var cmp = Builder.BuildICmp(LLVMIntPredicate.LLVMIntSLT, iVal, length, "__fe_cmp");
		Builder.BuildCondBr(cmp, bodyBlock, endBlock);

		Builder.PositionAtEnd(bodyBlock);
		var arrPtrField = Builder.BuildGEP2(sliceLayout, collectionAlloca, new LLVMValueRef[] {
			LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0),
			LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0)
		}, "__fe_arr_field");
		var dataPtr = Builder.BuildLoad2(LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), arrPtrField, "__fe_data_ptr");
		var elemLlvmTy = codegen.Types.Lower(itemType);
		var elemPtr = Builder.BuildGEP2(elemLlvmTy, dataPtr, new LLVMValueRef[] { iVal }, "__fe_elem_ptr");
		if (fe.IsReferenceBinding)
		{
			Builder.BuildStore(elemPtr, itemAlloca);
		}
		else
		{
			var elemVal = Builder.BuildLoad2(elemLlvmTy, elemPtr, "__fe_elem");
			Builder.BuildStore(elemVal, itemAlloca);
		}

		emitStatement(fe.Body);
		if (Builder.InsertBlock.Terminator.Handle == IntPtr.Zero)
		{
			Builder.BuildBr(incBlock);
		}

		Function.LoopContexts.Pop();
		if (!string.IsNullOrEmpty(fe.Label))
			Function.LabeledBreaks.Pop();

		Builder.PositionAtEnd(incBlock);
		var newI = Builder.BuildAdd(iVal, LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 1), "__fe_new_i");
		Builder.BuildStore(newI, counterAlloca);
		Builder.BuildBr(condBlock);

		Builder.PositionAtEnd(endBlock);
	}

	/// <summary>
	/// Emits foreach iteration through compiler-resolved GetEnumerator, MoveNext, and Current functions and performs enumerator cleanup afterward.
	/// </summary>
	private void EmitForEachEnumerator(ForEachStatementSyntax fe, TypeSymbol collectionType, TypeSymbol itemType, LLVMValueRef currentFunc, string prefix)
	{
		var (collectionAlloca, _, _, _) = getFieldPointer(fe.Collection);

		var enumeratorTypeName = fe.EnumeratorTypeName;
		var enumeratorType = enumeratorTypeName is not null ? BindingContext.ResolveType(enumeratorTypeName) : null;
		if (enumeratorType is null)
		{
			return;
		}

		var enumeratorLlvmType = codegen.Types.Lower(enumeratorType);

		var getEnumeratorName = fe.GetEnumeratorFunctionName;
		var moveNextName = fe.MoveNextFunctionName;
		var currentName = fe.CurrentFunctionName;
		if (getEnumeratorName is null || moveNextName is null || currentName is null)
		{
			return;
		}

		var getEnumeratorCallee = codegen.Globals[getEnumeratorName];
		var getEnumeratorFuncType = codegen.FunctionTypes[getEnumeratorName];
		var receiverType = LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0);
		var getEnumeratorResult = Builder.BuildCall2(getEnumeratorFuncType, getEnumeratorCallee, new LLVMValueRef[] { collectionAlloca }, "__fe_enum");

		var enumeratorAlloca = memory.BuildEntryAlloca(enumeratorLlvmType, "__fe_enumerator");
		Builder.BuildStore(getEnumeratorResult, enumeratorAlloca);

		var itemSlotTy = fe.IsReferenceBinding ? LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0) : codegen.Types.Lower(itemType);
		var itemAlloca = memory.BuildEntryAlloca(itemSlotTy, fe.ItemName);
		Function.Locals[fe.ItemName] = itemAlloca;
		Function.VariableTypes[fe.ItemName] = fe.IsReferenceBinding ? new PointerTypeSymbol(itemType, isMutable: fe.BindingKind == ForEachVariableKind.RefVar) : itemType;

		var condBlock = currentFunc.AppendBasicBlock(prefix + "fecond");
		var bodyBlock = currentFunc.AppendBasicBlock(prefix + "febody");
		var endBlock = currentFunc.AppendBasicBlock(prefix + "feend");

		var ctx = new LoopCodegenFrame(fe.Label, endBlock, condBlock);
		Function.LoopContexts.Push(ctx);
		if (!string.IsNullOrEmpty(fe.Label))
		{
			Function.LabeledBreaks.Push(new LabeledBreakCodegenFrame(fe.Label!, endBlock));
		}

		Builder.BuildBr(condBlock);

		Builder.PositionAtEnd(condBlock);
		var moveNextCallee = codegen.Globals[moveNextName];
		var moveNextFuncType = codegen.FunctionTypes[moveNextName];
		var hasMore = Builder.BuildCall2(moveNextFuncType, moveNextCallee, new LLVMValueRef[] { enumeratorAlloca }, "__fe_has_more");
		Builder.BuildCondBr(hasMore, bodyBlock, endBlock);

		Builder.PositionAtEnd(bodyBlock);
		var currentCallee = codegen.Globals[currentName];
		var currentFuncType = codegen.FunctionTypes[currentName];
		var currentVal = Builder.BuildCall2(currentFuncType, currentCallee, new LLVMValueRef[] { enumeratorAlloca }, "__fe_current");
		if (fe.IsReferenceBinding)
		{
			Builder.BuildStore(currentVal, itemAlloca);
		}
		else if (fe.CurrentReturnsReference)
		{
			var derefVal = Builder.BuildLoad2(codegen.Types.Lower(itemType), currentVal, "__fe_current_val");
			Builder.BuildStore(derefVal, itemAlloca);
		}
		else
		{
			Builder.BuildStore(currentVal, itemAlloca);
		}

		emitStatement(fe.Body);
		if (Builder.InsertBlock.Terminator.Handle == IntPtr.Zero)
		{
			Builder.BuildBr(condBlock);
		}

		Function.LoopContexts.Pop();
		if (!string.IsNullOrEmpty(fe.Label))
		{
			Function.LabeledBreaks.Pop();
		}

		Builder.PositionAtEnd(endBlock);

		cleanup.EmitForEachEnumeratorCleanup(enumeratorAlloca, enumeratorType);
	}

	/// <summary>
	/// Emits a branch for labeled or unlabeled break statements using the active switch/loop/labeled-block frames.
	/// </summary>
	public void EmitBreakStatement(BreakStatementSyntax brk)
	{
		// A labeled break targets the innermost matching labeled construct (labeled block or
		// labeled loop) in lexical descent order; both kinds are tracked on Function.LabeledBreaks.
		if (brk.TargetLabel is not null)
		{
			foreach (var target in Function.LabeledBreaks)
			{
				if (target.Label == brk.TargetLabel)
				{
					Builder.BuildBr(target.BreakBlock);
					return;
				}
			}
		}

		// An unlabeled break exits the innermost switch frame first (C-style); otherwise the
		// nearest loop. With no frame at all the statement was rejected by validation
		// (CVL1070/CVL1066), so there is nothing meaningful to lower.
		if (Function.SwitchBreaks.Count > 0)
		{
			Builder.BuildBr(Function.SwitchBreaks.Peek());
			return;
		}

		if (Function.LoopContexts.Count > 0)
		{
			Builder.BuildBr(Function.LoopContexts.Peek().BreakBlock);
		}
	}

	/// <summary>
	/// Emits a branch to the appropriate loop continue target, honoring an optional loop label.
	/// </summary>
	public void EmitContinueStatement(ContinueStatementSyntax cont)
	{
		if (Function.LoopContexts.Count == 0)
			return;

		if (cont.Label is not null)
		{
			foreach (var ctx in Function.LoopContexts)
			{
				if (ctx.Label == cont.Label)
				{
					Builder.BuildBr(ctx.ContinueBlock);
					return;
				}
			}
		}

		Builder.BuildBr(Function.LoopContexts.Peek().ContinueBlock);
	}

	/// <summary>
	/// Emits enum or union switch control flow while preserving promoted case bindings and resource-move cleanup behavior.
	/// </summary>
	public void EmitSwitchStatement(SwitchStatementSyntax sw)
	{
		var switchTargetType = getExpressionType(sw.Expression);
		EnumTypeSymbol? enumTarget = null;
		if (switchTargetType is EnumTypeSymbol et)
		{
			enumTarget = et;
		}
		else if (switchTargetType is PointerTypeSymbol pt && pt.ReferencedType is EnumTypeSymbol pet)
		{
			enumTarget = pet;
		}

		if (enumTarget is not null)
		{
			var value = emitExpression(sw.Expression);
			EmitEnumSwitch(sw, enumTarget, value);
			return;
		}

		var (targetVal, unionType, _, _) = getFieldPointer(sw.Expression);
		var isRefTarget = getExpressionType(sw.Expression) is PointerTypeSymbol;
		var isMutableRef = getExpressionType(sw.Expression) is PointerTypeSymbol ptrSymbol && ptrSymbol.IsMutable; // Capture original reference mutability

		if (unionType is PointerTypeSymbol ptr)
		{
			unionType = ptr.ReferencedType;
		}

		var unionLayout = codegen.Types.Lower(unionType);
		var isNpo = unionType is UnionTypeSymbol npoUt && npoUt.IsNpoEligible;

		// 1. Load the discriminator: for NPO unions this is the flat pointer value
		//    itself (None = null); for tagged unions it is the i8 tag at struct index 0.
		LLVMValueRef discriminator;
		if (isNpo)
		{
			discriminator = Builder.BuildLoad2(unionLayout, targetVal, "flat_ptr_val");
		}
		else
		{
			var tagPtr = Builder.BuildGEP2(unionLayout, targetVal, new LLVMValueRef[] {
				LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0),
				LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0)
			}, "tag_ptr");
			discriminator = Builder.BuildLoad2(LLVMTypeRef.Int8, tagPtr, "tag_val");
		}

		var currentFunc = Builder.InsertBlock.Parent;
		var endBlock = currentFunc.AppendBasicBlock("sw_end");
		var nextCheckBlock = Builder.InsertBlock;
		Function.SwitchBreaks.Push(endBlock);

		try
		{
			foreach (var c in sw.Cases)
			{
				Builder.PositionAtEnd(nextCheckBlock);

				if (c.IsDefault || c.VariantName == "_")
				{
					var bodyBlock = currentFunc.AppendBasicBlock("default_body");
					Builder.BuildBr(bodyBlock);

					Builder.PositionAtEnd(bodyBlock);
					EmitSwitchCaseBody(c, targetVal, unionType, "", isDefault: true, isRefTarget, isMutableRef);
					if (Builder.InsertBlock.Terminator.Handle == IntPtr.Zero)
						Builder.BuildBr(endBlock);

					break;
				}
				else
				{
					var unionTypeSym = unionType as UnionTypeSymbol;
					var fieldIndex = GetFieldIndex(unionTypeSym!, c.VariantName);

					var caseBodyBlock = currentFunc.AppendBasicBlock($"case_{c.VariantName}_body");
					nextCheckBlock = currentFunc.AppendBasicBlock($"case_{c.VariantName}_next");

					LLVMValueRef cond;
					if (isNpo)
					{
						// NPO: Some (payload) matches non-null; None (void) matches null.
						var isNone = unionTypeSym!.Fields[fieldIndex].IsVoidVariant;
						var nullConst = LLVMValueRef.CreateConstPointerNull(unionLayout);
						cond = isNone
							? Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, discriminator, nullConst, "npo_is_none")
							: Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, discriminator, nullConst, "npo_is_some");
					}
					else
					{
						cond = Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, discriminator, LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, (ulong)fieldIndex), "tag_match");
					}

					Builder.BuildCondBr(cond, caseBodyBlock, nextCheckBlock);

					Builder.PositionAtEnd(caseBodyBlock);
					EmitSwitchCaseBody(c, targetVal, unionType, c.VariantName, isDefault: false, isRefTarget, isMutableRef);
					if (Builder.InsertBlock.Terminator.Handle == IntPtr.Zero)
						Builder.BuildBr(endBlock);
				}
			}
		}
		finally
		{
			Function.SwitchBreaks.Pop();
		}

		Builder.PositionAtEnd(nextCheckBlock);
		if (Builder.InsertBlock.Terminator.Handle == IntPtr.Zero)
		{
			Builder.BuildBr(endBlock);
		}

		Builder.PositionAtEnd(endBlock);
	}

	/// <summary>
	/// Emits the compare chain for an enum switch and traps when execution reaches an exhaustive switch without a default arm.
	/// </summary>
	private void EmitEnumSwitch(SwitchStatementSyntax sw, EnumTypeSymbol enumTarget, LLVMValueRef value)
	{
		var storageTy = codegen.Types.Lower(enumTarget);
		var currentFunc = Builder.InsertBlock.Parent;
		var endBlock = currentFunc.AppendBasicBlock("sw_end");
		var nextCheckBlock = Builder.InsertBlock;
		var hasDefault = false;
		Function.SwitchBreaks.Push(endBlock);

		try
		{
			foreach (var c in sw.Cases)
			{
				Builder.PositionAtEnd(nextCheckBlock);

				if (c.IsDefault || c.VariantName == "_")
				{
					hasDefault = true;
					var bodyBlock = currentFunc.AppendBasicBlock("default_body");
					Builder.BuildBr(bodyBlock);

					Builder.PositionAtEnd(bodyBlock);
					emitBlock(new BlockStatementSyntax(c.Span, c.Body));
					if (Builder.InsertBlock.Terminator.Handle == IntPtr.Zero)
						Builder.BuildBr(endBlock);

					break;
				}
				else
				{
					var variant = enumTarget.FindVariant(c.VariantName) ?? enumTarget.Variants[0];

					var caseBodyBlock = currentFunc.AppendBasicBlock($"enum_case_{c.VariantName}_body");
					nextCheckBlock = currentFunc.AppendBasicBlock($"enum_case_{c.VariantName}_next");

					var cond = Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, value,
						LLVMValueRef.CreateConstInt(storageTy, unchecked((ulong)variant.Value)), "enum_switch_match");
					Builder.BuildCondBr(cond, caseBodyBlock, nextCheckBlock);

					Builder.PositionAtEnd(caseBodyBlock);
					emitBlock(new BlockStatementSyntax(c.Span, c.Body));
					if (Builder.InsertBlock.Terminator.Handle == IntPtr.Zero)
						Builder.BuildBr(endBlock);
				}
			}
		}
		finally
		{
			Function.SwitchBreaks.Pop();
		}

		Builder.PositionAtEnd(nextCheckBlock);
		if (!hasDefault)
		{
			emitTrapDefault();
		}
		else if (Builder.InsertBlock.Terminator.Handle == IntPtr.Zero)
		{
			Builder.BuildBr(endBlock);
		}

		Builder.PositionAtEnd(endBlock);
	}

	/// <summary>
	/// Materializes an optional union payload binding for a switch case and emits the case body.
	/// </summary>
	private void EmitSwitchCaseBody(SwitchCaseSyntax c, LLVMValueRef targetVal, TypeSymbol unionType, string variantName, bool isDefault, bool isRefTarget, bool isMutableRef)
	{
		var unionTypeSym = unionType as UnionTypeSymbol;

		if (c.VariableName is not null && !isDefault)
		{
			var fieldIndex = GetFieldIndex(unionTypeSym!, variantName);
			var field = unionTypeSym.Fields[fieldIndex];
			var isNpo = unionTypeSym.IsNpoEligible;

			LLVMValueRef payloadPtr;
			LLVMValueRef castPtr;
			if (isNpo)
			{
				// NPO: the payload IS the flat pointer slot itself (no {0,1} GEP, no bitcast).
				payloadPtr = targetVal;
				castPtr = targetVal;
			}
			else
			{
				var unionLayout = codegen.Types.Lower(unionType);
				payloadPtr = Builder.BuildGEP2(unionLayout, targetVal, new LLVMValueRef[]
				{
					LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0),
					LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 1)
				}, "union_payload_ptr");

				castPtr = Builder.BuildBitCast(payloadPtr, LLVMTypeRef.CreatePointer(codegen.Types.Lower(field.Type), 0), "payload_cast_ptr");
			}

			// Structurally mirror the target reference mutability. For NPO the payload is
			// already the inner reference, so the promoted variable is that same reference.
			var varType = isNpo ? field.Type : (isRefTarget ? new PointerTypeSymbol(field.Type, isMutable: isMutableRef) : field.Type);

			var alloca = Builder.BuildAlloca(codegen.Types.Lower(varType), c.VariableName);
			Function.Locals[c.VariableName] = alloca;
			Function.VariableTypes[c.VariableName] = varType;

			if (isNpo)
			{
				// NPO: the promoted variable holds the inner reference itself (the flat pointer
				// value), regardless of whether the target was taken by ref or by value.
				var val = Builder.BuildLoad2(codegen.Types.Lower(field.Type), targetVal, "flat_payload_val");
				Builder.BuildStore(val, alloca);
			}
			else if (isRefTarget)
			{
				Builder.BuildStore(castPtr, alloca);
			}
			else
			{
				var val = Builder.BuildLoad2(codegen.Types.Lower(field.Type), castPtr, "payload_val");
				Builder.BuildStore(val, alloca);

				// By-value switch over a ResourceMove-style union moves the payload into the case
				// binding (the sole owner now); reset the source slot to None so its own tag-checked
				// cleanup cannot drop the same resource a second time.
				if (cleanup.UnionNeedsTagCheckedCleanup(unionTypeSym!))
				{
					var srcLayout = codegen.Types.Lower(unionType);
					var srcTagPtr = Builder.BuildGEP2(srcLayout, targetVal, new LLVMValueRef[] {
						LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0),
						LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0)
					}, "move_src_tag_ptr");
					var moveNoneIdx = unionTypeSym.NoneVariant is not null ? GetFieldIndex(unionTypeSym, unionTypeSym.NoneVariant.Name) : 0;
					Builder.BuildStore(LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, (ulong)moveNoneIdx), srcTagPtr);
				}
			}
		}

		emitBlock(new BlockStatementSyntax(c.Span, c.Body));
	}

	/// <summary>
	/// Returns the storage index of a named struct field using the semantic field order.
	/// </summary>
	private static int GetFieldIndex(StructTypeSymbol type, string name)
	{
		for (var i = 0; i < type.Fields.Count; i++)
		{
			if (type.Fields[i].Name == name)
				return i;
		}

		throw new KeyNotFoundException($"Field {name} not found in struct {type.Name}");
	}

	/// <summary>
	/// Returns the storage index of a named union variant using the semantic variant order.
	/// </summary>
	private static int GetFieldIndex(UnionTypeSymbol type, string name)
	{
		for (var i = 0; i < type.Fields.Count; i++)
		{
			if (type.Fields[i].Name == name)
				return i;
		}

		throw new KeyNotFoundException($"Variant {name} not found in union {type.Name}");
	}
}
