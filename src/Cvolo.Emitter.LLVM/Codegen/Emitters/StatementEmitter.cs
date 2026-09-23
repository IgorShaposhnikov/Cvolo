using Cvolo.Analysis;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Collections;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using Cvolo.Core.AST.Expressions;
using Cvolo.Core.AST.Statements;
using Cvolo.Emitter.LLVM.Codegen.ControlFlow;
using Cvolo.Emitter.LLVM.Codegen.TypeLowering;
using Cvolo.Emitter.LLVM.Codegen.Values;
using LLVMSharp.Interop;

namespace Cvolo.Emitter.LLVM.Codegen.Emitters;

/// <summary>
/// Owns statement-level LLVM lowering, including lexical blocks, local declarations, returns,
/// conditionals, loops, foreach iteration, switch dispatch, unsafe blocks, and break/continue.
/// </summary>
/// <remarks>
/// Semantic expression typing is shared through <see cref="ExpressionTypeResolver"/>.
/// Cleanup, aggregate/address emission, call emission, memory allocation, and value coercion are
/// delegated to their dedicated services so this class owns statement orchestration rather than
/// duplicating those subsystems.
/// </remarks>
/// <remarks>
/// Creates a statement emitter over the shared codegen services and the current function state.
/// </remarks>
/// <remarks>
/// Expression emission remains a migration callback while semantic typing and constructor
/// classification are owned by <see cref="ExpressionTypeResolver"/>. Aggregate address resolution
/// is owned directly by <see cref="AggregateEmitter"/>.
/// </remarks>
internal sealed class StatementEmitter(
	CodegenContext codegen,
	CleanupEmitter cleanup,
	MemoryEmitter memory,
	AggregateEmitter aggregates,
	CallEmitter calls,
	ValueCoercion coercion,
	Func<FunctionCodegenContext> getFunction,
	Func<ExpressionSyntax, LLVMValueRef> emitExpression,
	ExpressionTypeResolver expressionTypes,
	Action emitTrapDefault)
{
	private LLVMBuilderRef Builder => codegen.Builder;
	private FunctionCodegenContext Function => getFunction();
	private BindingContext BindingContext => codegen.BindingContext ?? throw new InvalidOperationException("Statement emission requires an active binding context.");
	private readonly NativeAbiAggregateLowering _nativeAggregates = new(codegen, type => type.Equals(TypeSymbol.Bool) ? LLVMTypeRef.Int1 : codegen.Types.Lower(type));

	/// <summary>
	/// Emits a lexical block, tracks locals declared in that block, and runs scope cleanup when
	/// control reaches the block normally without an LLVM terminator.
	/// </summary>
	public void EmitBlock(BlockStatementSyntax block)
	{
		var blockVars = new List<string>();

		foreach (var stmt in block.Statements)
		{
			if (stmt is VariableDeclarationSyntax v)
			{
				blockVars.Add(v.Name);
			}

			// Skip statements after a terminator (e.g. a `break L;` that already branched).
			if (Builder.InsertBlock.Terminator.Handle == IntPtr.Zero)
			{
				EmitStatement(stmt);
			}
		}

		if (!EndsWithReturn(block))
		{
			cleanup.EmitScopeCleanup(Function, blockVars, skipHeapFree: Function.OwnershipTransferFunction);
		}
	}

	/// <summary>
	/// Emits a labeled block and installs a matching break target for the duration of its body.
	/// </summary>
	private void EmitLabeledBlockStatement(LabeledBlockStatementSyntax labeledBlock)
	{
		var currentFunc = Builder.InsertBlock.Parent;
		var bodyBlock = currentFunc.AppendBasicBlock(labeledBlock.Label + ".blkbody");
		var endBlock = currentFunc.AppendBasicBlock(labeledBlock.Label + ".blkend");

		Builder.BuildBr(bodyBlock);

		Function.LabeledBreaks.Push(new LabeledBreakCodegenFrame(labeledBlock.Label, endBlock));
		Builder.PositionAtEnd(bodyBlock);
		EmitBlock(labeledBlock.Body);
		if (Builder.InsertBlock.Terminator.Handle == IntPtr.Zero)
		{
			Builder.BuildBr(endBlock);
		}

		Function.LabeledBreaks.Pop();

		Builder.PositionAtEnd(endBlock);
	}

	/// <summary>
	/// Dispatches a single statement to the concrete lowering path owned by this emitter.
	/// </summary>
	private void EmitStatement(SyntaxNode stmt)
	{
		switch (stmt)
		{
			case ReturnStatementSyntax ret:
				EmitReturnStatement(ret);
				break;
			case ExpressionStatementSyntax exprStmt:
				emitExpression(exprStmt.Expression);
				break;
			case VariableDeclarationSyntax varDecl:
				EmitVariableDeclaration(varDecl);
				break;
			case BlockStatementSyntax block:
				EmitBlock(block);
				break;
			case LabeledBlockStatementSyntax labeledBlock:
				EmitLabeledBlockStatement(labeledBlock);
				break;
			case IfStatementSyntax ifStmt:
				EmitIfStatement(ifStmt);
				break;
			case SwitchStatementSyntax sw:
				EmitSwitchStatement(sw);
				break;
			case WhileStatementSyntax whileStmt:
				EmitWhileStatement(whileStmt);
				break;
			case ForStatementSyntax forStmt:
				EmitForStatement(forStmt);
				break;
			case ForEachStatementSyntax fe:
				EmitForEachStatement(fe);
				break;
			case UnsafeBlockStatementSyntax unsafeBlock:
				Function.UnsafeDepth++;
				EmitBlock(unsafeBlock.Body);
				Function.UnsafeDepth--;
				break;
			case BreakStatementSyntax brk:
				EmitBreakStatement(brk);
				break;
			case ContinueStatementSyntax cont:
				EmitContinueStatement(cont);
				break;
		}
	}

	/// <summary>
	/// Emits a function return, including option-null lowering, aggregate materialization,
	/// scope cleanup, implicit reference dereference, and exported-bool ABI truncation.
	/// </summary>
	private void EmitReturnStatement(ReturnStatementSyntax ret)
	{
		if (ret.Expression is not null)
		{
			// 1. Handle Null Returns on Options (Lowers null to Option.None)
			var expectedType = codegen.FunctionReturnTypes.TryGetValue(Builder.InsertBlock.Parent.Name, out var et) ? et : TypeSymbol.Int;

			if (ret.Expression is NullLiteralExpressionSyntax && expectedType is UnionTypeSymbol optionUnion && optionUnion.IsOption)
			{
				var unionLayout = codegen.Types.Lower(optionUnion);
				var tempAlloc = Builder.BuildAlloca(unionLayout, "ret_null_tmp");

				// Null-Pointer Optimization: store flat nullptr (None == zero) instead of a tag.
				if (optionUnion.IsNpoEligible)
				{
					Builder.BuildStore(LLVMValueRef.CreateConstPointerNull(unionLayout), tempAlloc);
				}
				else
				{
					var fieldIndex = codegen.AggregateLayout.GetFieldIndex(optionUnion, "None");

					var tagPtr = Builder.BuildGEP2(unionLayout, tempAlloc, new LLVMValueRef[]
					{
						LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0),
						LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0)
					}, "union_tag_ptr");
					Builder.BuildStore(LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, (ulong)fieldIndex), tagPtr);
				}

				var loadedNone = Builder.BuildLoad2(unionLayout, tempAlloc, "loaded_none");

				cleanup.EmitScopeCleanup(Function, [.. Function.Locals.Keys], skipHeapFree: Function.OwnershipTransferFunction);
				Builder.BuildRet(loadedNone);
				return;
			}

			// 2. Handle Standard Returns
			var value = emitExpression(ret.Expression);
			var type = expressionTypes.Resolve(ret.Expression);

			// Implicit Dereference: if expected return type is value but actual is a reference
			if (type is PointerTypeSymbol retPtr && value.TypeOf.Kind == LLVMTypeKind.LLVMPointerTypeKind && expectedType is not PointerTypeSymbol)
			{
				value = Builder.BuildLoad2(codegen.Types.Lower(retPtr.ReferencedType), value, "deref_ret");
				type = retPtr.ReferencedType;
			}

			if (Function.NativeAbiPlan is { } nativePlan
				&& nativePlan.Return.Kind is NativeAbiValuePassKind.DirectIntegerAggregate or NativeAbiValuePassKind.IndirectAggregate)
			{
				if (nativePlan.Return.Kind == NativeAbiValuePassKind.DirectIntegerAggregate)
				{
					var packed = _nativeAggregates.PackDirectAggregate(Builder, nativePlan.Return, value, "native_ret");
					cleanup.EmitScopeCleanup(Function, [.. Function.Locals.Keys], skipHeapFree: Function.OwnershipTransferFunction);
					Builder.BuildRet(packed);
					return;
				}

				var returnStorage = _nativeAggregates.GetAggregateAddress(Builder, nativePlan.Return, value, "native_sret");
				var loadedAggregate = Builder.BuildLoad2(codegen.Types.Lower(expectedType), returnStorage, "native_sret_value");
				cleanup.EmitScopeCleanup(Function, [.. Function.Locals.Keys], skipHeapFree: Function.OwnershipTransferFunction);
				if (Function.NativeSRetPointer is null)
					throw new InvalidOperationException("Native ABI sret return is missing its hidden result pointer.");
				Builder.BuildStore(loadedAggregate, Function.NativeSRetPointer.Value);
				Builder.BuildRetVoid();
				return;
			}

			// Materialize memory-resident return values (structs/unions living in allocas/heap
			// slots) BEFORE scope cleanup frees them - the loaded register is what survives.
			// NPO-eligible unions lower to a single scalar (the flat ptr), so their emitted value
			// is already the scalar - loading it again would double-dereference.
			LLVMValueRef? materialized = null;
			var isMemResident = type is StructTypeSymbol || (type is UnionTypeSymbol u && !u.IsNpoEligible);
			if (isMemResident && value.TypeOf.Kind == LLVMTypeKind.LLVMPointerTypeKind)
			{
				var layout = codegen.Types.Lower(type);
				materialized = Builder.BuildLoad2(layout, value, "struct_ret_val");
			}

			cleanup.EmitScopeCleanup(Function, [.. Function.Locals.Keys], skipHeapFree: Function.OwnershipTransferFunction);

			if (materialized is not null)
			{
				Builder.BuildRet(materialized.Value);
			}
			else
			{
				// Native scalar bool remains i1 in the LLVM function signature; target ABI
				// extension requirements are expressed through zeroext attributes. Object
				// storage such as foreign globals is handled separately as one byte.
				Builder.BuildRet(value);
			}
		}
		else
		{
			cleanup.EmitScopeCleanup(Function, [.. Function.Locals.Keys], skipHeapFree: Function.OwnershipTransferFunction);
			Builder.BuildRetVoid();
		}
	}

	/// <summary>
	/// Emits local variable storage and initialization while preserving reference, heap-allocation,
	/// aggregate, constructor, option, coercion, and type-inference behavior.
	/// </summary>
	private void EmitVariableDeclaration(VariableDeclarationSyntax varDecl)
	{
		TypeSymbol? typeSymbol = null;
		if (varDecl.Type is not null)
		{
			typeSymbol = codegen.BindingContext!.ResolveType(varDecl.Type);
		}

		// Handle References / Borrows
		if (varDecl.Type == "refvar" || varDecl.Type == "ref")
		{
			var val = emitExpression(varDecl.Initializer!);
			var valTy = expressionTypes.Resolve(varDecl.Initializer!);

			var innerType = valTy is PointerTypeSymbol ptrType ? ptrType.ReferencedType : valTy;
			var isMutable = varDecl.Type == "refvar";
			var pointerType = new PointerTypeSymbol(innerType, isMutable);

			var alloca = Builder.BuildAlloca(LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), varDecl.Name);
			Function.Locals[varDecl.Name] = alloca;
			Function.VariableTypes[varDecl.Name] = pointerType;

			Builder.BuildStore(val, alloca);
			return;
		}

		// Handle Heap Allocations
		if (varDecl.Initializer is HeapAllocationExpressionSyntax heapInit)
		{
			var val = emitExpression(heapInit);
			var valTy = expressionTypes.Resolve(heapInit);

			var alloca = Builder.BuildAlloca(LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0), varDecl.Name);
			Function.Locals[varDecl.Name] = alloca;
			Function.VariableTypes[varDecl.Name] = valTy;
			Function.HeapAllocatedVars.Add(varDecl.Name);

			Builder.BuildStore(val, alloca);
			return;
		}
		else if (varDecl.Initializer is HeapArrayAllocationExpressionSyntax heapArrInit)
		{
			var val = emitExpression(heapArrInit);
			var valTy = expressionTypes.Resolve(heapArrInit);

			var alloca = Builder.BuildAlloca(codegen.Types.Lower(valTy), varDecl.Name);
			Function.Locals[varDecl.Name] = alloca;
			Function.VariableTypes[varDecl.Name] = valTy;
			Function.HeapAllocatedVars.Add(varDecl.Name); // Register for RAII cleanup!

			Builder.BuildStore(val, alloca);
			return;
		}

		if (typeSymbol is not null)
		{
			var llvmType = codegen.Types.Lower(typeSymbol);
			var alloca = Builder.BuildAlloca(llvmType, varDecl.Name);
			Function.Locals[varDecl.Name] = alloca;
			Function.VariableTypes[varDecl.Name] = typeSymbol;

			// Panic-safe zero-ing: a ResourceMove-style union local is pre-set to None so that if
			// a panic occurs while its constructor/initializer is still running, the unwinder (and
			// any tag-checked destructor) observes a None slot instead of uninitialized garbage.
			if (typeSymbol is UnionTypeSymbol zeroUnion && cleanup.UnionNeedsTagCheckedCleanup(zeroUnion))
			{
				var zeroNone = zeroUnion.NoneVariant is not null ? codegen.AggregateLayout.GetFieldIndex(zeroUnion, zeroUnion.NoneVariant.Name) : 0;
				var zeroTagPtr = Builder.BuildGEP2(llvmType, alloca, new LLVMValueRef[]
				{
					LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0),
					LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0)
				}, "union_tag_ptr");
				Builder.BuildStore(LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, (ulong)zeroNone), zeroTagPtr);
			}


			if (varDecl.Initializer is not null)
			{
				// Handle Null Initializers on Option Types (Lowers null to Option.None)
				if (varDecl.Initializer is NullLiteralExpressionSyntax && typeSymbol is UnionTypeSymbol optionUnion && optionUnion.IsOption)
				{
					// Null-Pointer Optimization: store flat nullptr (None == zero) instead of a tag.
					if (optionUnion.IsNpoEligible)
					{
						Builder.BuildStore(LLVMValueRef.CreateConstPointerNull(llvmType), alloca);
						return;
					}

					var fieldIndex = codegen.AggregateLayout.GetFieldIndex(optionUnion, "None");

					var tagPtr = Builder.BuildGEP2(llvmType, alloca, new LLVMValueRef[]
					{
						LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0),
						LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0)
					}, "union_tag_ptr");
					Builder.BuildStore(LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, (ulong)fieldIndex), tagPtr);
					return;
				}

				var valTy = expressionTypes.Resolve(varDecl.Initializer);

				if (typeSymbol is SliceTypeSymbol && valTy is ArrayTypeSymbol)
				{
					var arrayPtr = emitExpression(varDecl.Initializer);
					var sliceVal = coercion.CoerceArrayToSlice(arrayPtr, valTy, (typeSymbol as SliceTypeSymbol)!);
					Builder.BuildStore(sliceVal, alloca);
					return;
				}

				if (varDecl.Initializer is StructInitializationExpressionSyntax structInit)
				{
					aggregates.EmitStructInitializationInPlace(structInit, alloca);
				}
				else if (varDecl.Initializer is ArrayInitializationExpressionSyntax arrInit)
				{
					aggregates.EmitArrayInitializationInPlace(arrInit, alloca, (typeSymbol as ArrayTypeSymbol)!);
				}
				else if (varDecl.Initializer is ArrayReplicationExpressionSyntax arrRepl)
				{
					aggregates.EmitArrayReplicationInPlace(arrRepl, alloca, (typeSymbol as ArrayTypeSymbol)!);
				}
				else if (varDecl.Initializer is CallExpressionSyntax ctorCall && expressionTypes.IsConstructorCall(ctorCall, typeSymbol))
				{
					// 'var T v = T(args)': the constructor populates the variable's storage
					// in place via its implicit 'this' parameter; no value store follows.
					calls.Emit(ctorCall, alloca);
				}
				else
				{
					var value = emitExpression(varDecl.Initializer);
					var coerced = typeSymbol is not PointerTypeSymbol
						&& (varDecl.Initializer is MemberAccessExpressionSyntax or IndexExpressionSyntax)
						? coercion.CoerceReferenceToValue(value, expressionTypes.Resolve(varDecl.Initializer!))
						: value;
					coerced = coercion.CoerceIntegerWidth(coerced, expressionTypes.Resolve(varDecl.Initializer!) ?? typeSymbol, typeSymbol);
					coerced = coercion.CoerceFloatWidth(coerced, expressionTypes.Resolve(varDecl.Initializer!) ?? typeSymbol, typeSymbol);
					Builder.BuildStore(coerced, alloca);
				}
			}
		}
		else
		{
			// Type Inference
			var valTy = expressionTypes.Resolve(varDecl.Initializer!);
			Function.VariableTypes[varDecl.Name] = valTy;

			if (varDecl.Initializer is CallExpressionSyntax ctorCall && expressionTypes.IsConstructorCall(ctorCall, valTy))
			{
				var llvmType = codegen.Types.Lower(valTy);
				var alloca = memory.BuildEntryAlloca(llvmType, varDecl.Name);
				Function.Locals[varDecl.Name] = alloca;

				// Panic-safe zero-ing for Unions initialized via Type Inference
				if (valTy is UnionTypeSymbol zeroUnion && cleanup.UnionNeedsTagCheckedCleanup(zeroUnion))
				{
					var zeroNone = zeroUnion.NoneVariant is not null ? codegen.AggregateLayout.GetFieldIndex(zeroUnion, zeroUnion.NoneVariant.Name) : 0;
					var zeroTagPtr = Builder.BuildGEP2(llvmType, alloca, new LLVMValueRef[]
					{
						LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0),
						LLVMValueRef.CreateConstInt(LLVMTypeRef.Int32, 0)
					}, "union_tag_ptr");
					Builder.BuildStore(LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, (ulong)zeroNone), zeroTagPtr);
				}

				calls.Emit(ctorCall, alloca);
			}
			// Register Forwarding: If the aggregate is already allocated on the stack, forward its address
			else if (valTy is StructTypeSymbol || valTy is ArrayTypeSymbol || valTy is UnionTypeSymbol { IsUnsafe: true })
			{
				var val = emitExpression(varDecl.Initializer!);
				if (val.TypeOf.Kind == LLVMTypeKind.LLVMPointerTypeKind)
				{
					// Struct-init/ctor/sret-style expressions already return an alloca pointer:
					// forward it straight into Function.Locals so later GEPs/loads hit storage.
					Function.Locals[varDecl.Name] = val;
				}
				else
				{
					// Value-returning aggregates (e.g. typeof -> const System.Type value):
					// materialize into private storage; field access must GEP off a pointer.
					var llvmType = codegen.Types.Lower(valTy);
					var alloca = memory.BuildEntryAlloca(llvmType, varDecl.Name);
					Function.Locals[varDecl.Name] = alloca;
					Builder.BuildStore(val, alloca);
				}
			}
			else
			{
				var val = emitExpression(varDecl.Initializer!);
				var llvmType = codegen.Types.Lower(valTy);
				var alloca = memory.BuildEntryAlloca(llvmType, varDecl.Name);
				Function.Locals[varDecl.Name] = alloca;
				Builder.BuildStore(val, alloca);
			}
		}
	}

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
		EmitStatement(ifStmt.ThenStatement);
		if (Builder.InsertBlock.Terminator.Handle == IntPtr.Zero)
			Builder.BuildBr(mergeBlock);

		Builder.PositionAtEnd(elseBlock);
		if (ifStmt.ElseClause is not null)
			EmitStatement(ifStmt.ElseClause.Body);
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
		EmitStatement(whileStmt.Body);
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
		EmitVariableDeclaration(forStmt.Initializer);

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
		EmitStatement(forStmt.Body);
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
		var rawCollectionType = expressionTypes.Resolve(fe.Collection);
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
		var (collectionAlloca, _, _, _) = aggregates.GetFieldPointer(fe.Collection);

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

		EmitStatement(fe.Body);
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
		var (collectionAlloca, _, _, _) = aggregates.GetFieldPointer(fe.Collection);
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

		EmitStatement(fe.Body);
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
		var (collectionAlloca, _, _, _) = aggregates.GetFieldPointer(fe.Collection);

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

		EmitStatement(fe.Body);
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
		var switchTargetType = expressionTypes.Resolve(sw.Expression);
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

		var (targetVal, unionType, _, _) = aggregates.GetFieldPointer(sw.Expression);
		var isRefTarget = expressionTypes.Resolve(sw.Expression) is PointerTypeSymbol;
		var isMutableRef = expressionTypes.Resolve(sw.Expression) is PointerTypeSymbol ptrSymbol && ptrSymbol.IsMutable; // Capture original reference mutability

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
					var fieldIndex = codegen.AggregateLayout.GetFieldIndex(unionTypeSym!, c.VariantName);

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
					EmitBlock(new BlockStatementSyntax(c.Span, c.Body));
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
					EmitBlock(new BlockStatementSyntax(c.Span, c.Body));
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
			var fieldIndex = codegen.AggregateLayout.GetFieldIndex(unionTypeSym!, variantName);
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
					var moveNoneIdx = unionTypeSym.NoneVariant is not null ? codegen.AggregateLayout.GetFieldIndex(unionTypeSym, unionTypeSym.NoneVariant.Name) : 0;
					Builder.BuildStore(LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, (ulong)moveNoneIdx), srcTagPtr);
				}
			}
		}

		EmitBlock(new BlockStatementSyntax(c.Span, c.Body));
	}

	/// <summary>
	/// Returns whether a syntax node ends in an explicit return according to the existing
	/// block-termination rule used by function and lexical-block emission.
	/// </summary>
	public static bool EndsWithReturn(SyntaxNode syntax) => syntax switch
	{
		BlockStatementSyntax block => block.Statements.Count > 0 && block.Statements[^1] is ReturnStatementSyntax,
		LabeledBlockStatementSyntax labeled => labeled.Body.Statements.Count > 0 && labeled.Body.Statements[^1] is ReturnStatementSyntax,
		ReturnStatementSyntax => true,
		_ => false,
	};

}
