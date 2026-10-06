using Cvolo.Analysis;
using Cvolo.Analysis.Formatting;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Expressions;
using Cvolo.Emitter.LLVM.Codegen.Values;
using LLVMSharp.Interop;

namespace Cvolo.Emitter.LLVM.Codegen.Emitters;

/// <summary>
/// Emits already-bound formatting operations and formatter interpolations into LLVM IR.
/// </summary>
/// <remarks>
/// The binder is the single authority on formatting semantics (<see cref="FormattingResolver"/>);
/// this emitter only consumes the bound operations recorded in
/// <see cref="BindingContext.ResolvedFormattingOperations"/> and
/// <see cref="BindingContext.ResolvedFormatterInterpolations"/>. It performs no conformance lookup, no
/// member resolution, and no formatting-rule selection of its own. The canonical sink model is used for
/// every context: a <c>Formatter</c> is an opaque <c>{ sinkContext, sinkWrite }</c> pair and all output
/// flows through <c>sinkWrite(sinkContext, bytes, length)</c>. Console contexts construct a formatter
/// whose sink forwards to <c>printf("%.*s")</c>; Base-only and freestanding builds never emit that
/// sink and therefore never reference printf.
/// </remarks>
internal sealed class FormattingEmitter(
	CodegenContext codegen,
	AggregateEmitter aggregates,
	Func<FunctionCodegenContext> getFunction,
	Func<ExpressionSyntax, LLVMValueRef> emitExpression,
	ExpressionTypeResolver expressionTypes,
	ValueLoader values)
{
	private static readonly LLVMTypeRef VoidTy = LLVMTypeRef.Void;
	private static readonly LLVMTypeRef Int8Ty = LLVMTypeRef.Int8;
	private static readonly LLVMTypeRef Int32Ty = LLVMTypeRef.Int32;
	private static readonly LLVMTypeRef Int64Ty = LLVMTypeRef.Int64;
	private static readonly LLVMTypeRef I8PtrTy = LLVMTypeRef.CreatePointer(LLVMTypeRef.Int8, 0);

	private const string ConsoleSinkName = "$__cvolo_console_sink";
	private LLVMValueRef? _consoleSink;

	private LLVMBuilderRef Builder => codegen.Builder;
	private FunctionCodegenContext Function => getFunction();
	private BindingContext BindingContext => codegen.BindingContext
		?? throw new InvalidOperationException("Formatting emission requires an active binding context.");

	private TypeSymbol FormatterType => BindingContext.ResolveType("Formatter")
		?? throw new InvalidOperationException("The canonical Formatter type is not available during emission.");

	/// <summary>
	/// Attempts to emit a call previously bound as a formatting operation or formatter interpolation.
	/// Returns <see langword="false"/> when the call is not formatting-bound (the ordinary call path
	/// then handles it).
	/// </summary>
	public bool TryEmit(CallExpressionSyntax call)
	{
		if (BindingContext.ResolvedFormatterInterpolations.TryGetValue(call, out var interpolation))
		{
			var formatterPtr = GetFormatterPointer(call, FormattingContextKind.FormatterWrite);
			foreach (var segment in interpolation.Segments)
			{
				if (!segment.IsExpression)
				{
					EmitSinkWrite(formatterPtr, segment.Text);
					continue;
				}

				var expression = segment.Expression
					?? throw new InvalidOperationException("A formatter interpolation expression segment has no syntax.");
				var segmentOperation = segment.Operation
					?? throw new InvalidOperationException("A formatter interpolation expression segment has no bound operation.");
				var address = GetValueAddress(expression, segmentOperation.ValueType);
				EmitOperation(segmentOperation, formatterPtr, address);
			}

			return true;
		}

		if (BindingContext.ResolvedFormattingOperations.TryGetValue(call, out var operation))
		{
			var formatterPtr = GetFormatterPointer(call, operation.Context);
			var valueAddress = GetValueAddress(call.Arguments[0], operation.ValueType);
			EmitOperation(operation, formatterPtr, valueAddress);

			if (operation.Context == FormattingContextKind.ConsoleWriteLine)
				EmitSinkWrite(formatterPtr, "\n");

			return true;
		}

		return false;
	}

	/// <summary>
	/// Resolves the <c>Formatter</c> instance pointer for a call context. Formatter receivers are
	/// located by the same rules the ordinary call emitter uses; console contexts construct a fresh
	/// formatter bound to the console sink.
	/// </summary>
	private LLVMValueRef GetFormatterPointer(CallExpressionSyntax call, FormattingContextKind kind)
	{
		if (kind != FormattingContextKind.FormatterWrite)
			return CreateConsoleFormatter();

		var lastDot = call.FunctionName.LastIndexOf('.');
		if (lastDot <= 0)
			throw new InvalidOperationException("A Formatter.Write call must have a dotted receiver.");

		var receiverName = call.FunctionName[..lastDot];
		TypeSymbol receiverType = TypeSymbol.Null;

		if (Function.Locals.TryGetValue(receiverName, out var localPtr))
		{
			receiverType = Function.VariableTypes[receiverName];
			if (receiverType is PointerTypeSymbol)
				return Builder.BuildLoad2(I8PtrTy, localPtr, "formatter_ref_ptr");
			return localPtr;
		}

		if ((codegen.GlobalVariables.ContainsKey(receiverName) ? receiverName : values.ResolveGlobalKey(receiverName)) is { } recvKey
			&& codegen.GlobalVariables.TryGetValue(recvKey, out var globalPtr))
		{
			receiverType = codegen.GlobalVariableTypes[recvKey];
			if (receiverType is PointerTypeSymbol)
				return Builder.BuildLoad2(I8PtrTy, globalPtr, "formatter_global_ptr");
			return globalPtr;
		}

		if (Function.Locals.TryGetValue("this", out var thisPtr)
			&& Function.VariableTypes.TryGetValue("this", out var thisType)
			&& thisType is PointerTypeSymbol thisPointer
			&& thisPointer.ReferencedType is StructTypeSymbol thisStruct
			&& thisStruct.FindField(receiverName) is { } field)
		{
			var actualThisPtr = Builder.BuildLoad2(I8PtrTy, thisPtr, "loaded_this_ptr");
			var fieldIndex = codegen.AggregateLayout.GetFieldIndex(thisStruct, receiverName);
			var structLayout = codegen.Types.Lower(thisStruct);
			var receiverPtr = Builder.BuildGEP2(
				structLayout,
				actualThisPtr,
				new LLVMValueRef[] { LLVMValueRef.CreateConstInt(Int32Ty, 0), LLVMValueRef.CreateConstInt(Int32Ty, (ulong)fieldIndex) },
				"formatter_field_ptr");
			if (field.Type is PointerTypeSymbol)
				return Builder.BuildLoad2(I8PtrTy, receiverPtr, "formatter_field_ref_ptr");
			return receiverPtr;
		}

		throw new InvalidOperationException($"Unable to resolve the formatter receiver '{receiverName}'.");
	}

	/// <summary>
	/// Allocates a formatter bound to the module's console sink and returns its address.
	/// </summary>
	private LLVMValueRef CreateConsoleFormatter()
	{
		var formatterStruct = codegen.Types.Lower(FormatterType);
		var formatter = Builder.BuildAlloca(formatterStruct, "console_formatter");
		Builder.BuildStore(
			LLVMValueRef.CreateConstPointerNull(I8PtrTy),
			Builder.BuildGEP2(formatterStruct, formatter, new LLVMValueRef[] { Zero32(), Zero32() }, "fmt_sink_ctx"));
		Builder.BuildStore(
			GetConsoleSinkFunction(),
			Builder.BuildGEP2(formatterStruct, formatter, new LLVMValueRef[] { Zero32(), One32() }, "fmt_sink_write"));
		return formatter;
	}

	/// <summary>
	/// Lazily defines the module-level console sink <c>void(void* ctx, i8* bytes, i64 len)</c> that
	/// forwards its payload to <c>printf("%.*s", (int)len, bytes)</c>. When printf is not part of the
	/// module (Base-only and freestanding compilations), a no-op sink is generated instead so the
	/// formatter carrier remains resolvable without any CRT/stdout dependency.
	/// </summary>
	private LLVMValueRef GetConsoleSinkFunction()
	{
		if (_consoleSink is { } cached)
			return cached;

		var sinkType = LLVMTypeRef.CreateFunction(VoidTy, [I8PtrTy, I8PtrTy, Int64Ty]);
		var function = codegen.Module.AddFunction(ConsoleSinkName, sinkType);
		codegen.Globals[ConsoleSinkName] = function;
		codegen.FunctionTypes[ConsoleSinkName] = sinkType;

		var savedBlock = Builder.InsertBlock;
		var entry = function.AppendBasicBlock("entry");
		Builder.PositionAtEnd(entry);

		if (codegen.Globals.TryGetValue("printf", out var printf) && codegen.FunctionTypes.TryGetValue("printf", out var printfType))
		{
			var format = Builder.BuildGlobalStringPtr("%.*s", "fmt_sink_fmt");
			var bytes = function.GetParam(1);
			var length = function.GetParam(2);
			var lengthI32 = Builder.BuildTrunc(length, Int32Ty, "fmt_sink_len");
			Builder.BuildCall2(printfType, printf, [format, lengthI32, bytes]);
		}

		Builder.BuildRetVoid();

		if (savedBlock.Handle != IntPtr.Zero)
			Builder.PositionAtEnd(savedBlock);

		_consoleSink = function;
		return function;
	}

	/// <summary>
	/// Resolves the address of a formatted value without forcing a formatting-only by-value copy:
	/// lexical storage is preferred, and temporaries are only materialized for non-addressable
	/// expressions (matching the ordinary emitter's aggregate-return behavior).
	/// </summary>
	private LLVMValueRef GetValueAddress(ExpressionSyntax expression, TypeSymbol expectedType)
	{
		try
		{
			var (ptr, _, _, _) = aggregates.GetFieldPointer(expression);
			return ptr;
		}
		catch (InvalidOperationException)
		{
		}

		var value = emitExpression(expression);
		if (value.TypeOf.Kind == LLVMTypeKind.LLVMPointerTypeKind
			&& expressionTypes.Resolve(expression) is PointerTypeSymbol)
		{
			return value;
		}

		var slot = Builder.BuildAlloca(codegen.Types.Lower(expectedType), "fmt_value_tmp");
		Builder.BuildStore(value, slot);
		return slot;
	}

	/// <summary>
	/// Emits one bound formatting operation against the given formatter.
	/// </summary>
	private void EmitOperation(ResolvedFormattingOperation operation, LLVMValueRef formatterPtr, LLVMValueRef valueAddress)
	{
		switch (operation.Kind)
		{
			case FormattingOperationKind.String:
				{
					var text = Builder.BuildLoad2(I8PtrTy, valueAddress, "fmt_str");
					EmitStringWrite(formatterPtr, text);
					break;
				}
			case FormattingOperationKind.IFormattable:
				{
					var target = operation.TargetMethod
						?? throw new InvalidOperationException("An IFormattable operation has no resolved Format method.");
					EmitDirectCall(target.Name, [valueAddress, formatterPtr]);
					break;
				}
			case FormattingOperationKind.IStringable:
				{
					var target = operation.TargetMethod
						?? throw new InvalidOperationException("An IStringable operation has no resolved ToString method.");
					var text = EmitDirectCall(target.Name, [valueAddress], "fmt_tostring");
					EmitStringWrite(formatterPtr, text);
					break;
				}
			case FormattingOperationKind.Option:
			case FormattingOperationKind.Result:
				EmitComposite(operation, formatterPtr, valueAddress);
				break;
			default:
				EmitPrimitive(operation, formatterPtr, valueAddress);
				break;
		}
	}

	private LLVMValueRef EmitDirectCall(string emitName, LLVMValueRef[] arguments, string resultName = "")
	{
		if (!codegen.Globals.TryGetValue(emitName, out var callee) || !codegen.FunctionTypes.TryGetValue(emitName, out var functionType))
			throw new InvalidOperationException($"The formatted-operation target '{emitName}' is not declared in the module.");
		return Builder.BuildCall2(functionType, callee, arguments, resultName);
	}

	private void EmitPrimitive(ResolvedFormattingOperation operation, LLVMValueRef formatterPtr, LLVMValueRef valueAddress)
	{
		var valueType = operation.ValueType;
		if (valueType.Name == "float" || valueType.Name == "double")
		{
			EmitFloat(operation, formatterPtr, valueAddress);
			return;
		}

		var loweredType = codegen.Types.Lower(valueType);
		var value = Builder.BuildLoad2(loweredType, valueAddress, "fmt_primitive");

		if (valueType.Name == "bool")
		{
			var one = Builder.BuildGlobalStringPtr("1", "fmt_bool_1");
			var zero = Builder.BuildGlobalStringPtr("0", "fmt_bool_0");
			var bytes = Builder.BuildSelect(value, one, zero, "fmt_bool_ptr");
			EmitSinkWrite(formatterPtr, bytes, OneI64());
			return;
		}

		if (valueType.Name == "char")
		{
			var cell = Builder.BuildAlloca(Int8Ty, "fmt_char");
			Builder.BuildStore(value, cell);
			EmitSinkWrite(formatterPtr, cell, OneI64());
			return;
		}

		EmitInteger(operation, formatterPtr, valueAddress, value);
	}

	/// <summary>
	/// Emits canonical positional integer formatting (decimal, sign-aware) into a stack buffer and
	/// writes it to the sink. The digit width is derived from the lowered integer type, so
	/// <c>nint</c>/<c>nuint</c> are formatted at the target pointer width rather than a hardcoded size.
	/// </summary>
	private void EmitInteger(ResolvedFormattingOperation operation, LLVMValueRef formatterPtr, LLVMValueRef valueAddress, LLVMValueRef value)
	{
		var loweredType = value.TypeOf;
		var signed = TypeSymbol.IsSignedIntegerType(operation.ValueType);
		var zero = LLVMValueRef.CreateConstInt(loweredType, 0);
		var ten = LLVMValueRef.CreateConstInt(loweredType, 10);
		var digitZeroI8 = LLVMValueRef.CreateConstInt(Int8Ty, '0');

		var isNegative = signed
			? Builder.BuildICmp(LLVMIntPredicate.LLVMIntSLT, value, zero, "fmt_int_neg")
			: LLVMValueRef.CreateConstInt(LLVMTypeRef.Int1, 0);
		var magnitude = signed
			? Builder.BuildSelect(isNegative, Builder.BuildSub(zero, value, "fmt_int_mag"), value, "fmt_int_abs")
			: value;

		const uint bufferCapacity = 96;
		var bufferType = LLVMTypeRef.CreateArray(Int8Ty, bufferCapacity);
		var buffer = Builder.BuildAlloca(bufferType, "fmt_int_buffer");
		var indexSlot = Builder.BuildAlloca(Int32Ty, "fmt_int_index");
		var workSlot = Builder.BuildAlloca(loweredType, "fmt_int_work");
		var negativeSlot = Builder.BuildAlloca(LLVMTypeRef.Int1, "fmt_int_neg_slot");
		Builder.BuildStore(LLVMValueRef.CreateConstInt(Int32Ty, bufferCapacity), indexSlot);
		Builder.BuildStore(magnitude, workSlot);
		Builder.BuildStore(isNegative, negativeSlot);

		var currentFunction = Builder.InsertBlock.Parent;
		var loopBlock = currentFunction.AppendBasicBlock("fmt_int_loop");
		var exitBlock = currentFunction.AppendBasicBlock("fmt_int_loop_exit");

		Builder.BuildBr(loopBlock);
		Builder.PositionAtEnd(loopBlock);

		var work = Builder.BuildLoad2(loweredType, workSlot, "fmt_int_w");
		var digit = Builder.BuildURem(work, ten, "fmt_int_d");
		var nextIndex = Builder.BuildSub(Builder.BuildLoad2(Int32Ty, indexSlot, "fmt_int_i"), One32(), "fmt_int_iNext");
		Builder.BuildStore(nextIndex, indexSlot);
		var digitByte = digit.TypeOf != Int8Ty
			? Builder.BuildAdd(Builder.BuildTrunc(digit, Int8Ty, "fmt_int_digit"), digitZeroI8, "fmt_int_dbyte")
			: Builder.BuildAdd(digit, digitZeroI8, "fmt_int_dbyte");
		Builder.BuildStore(
			digitByte,
			Builder.BuildGEP2(bufferType, buffer, new LLVMValueRef[] { Zero32(), nextIndex }, "fmt_int_digit_slot"));
		var rest = Builder.BuildUDiv(work, ten, "fmt_int_rest");
		Builder.BuildStore(rest, workSlot);
		var continueLoop = Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, rest, zero, "fmt_int_has_more");
		Builder.BuildCondBr(continueLoop, loopBlock, exitBlock);

		Builder.PositionAtEnd(exitBlock);
		var startBlock = currentFunction.AppendBasicBlock("fmt_int_start");
		var negativeBlock = currentFunction.AppendBasicBlock("fmt_int_negative");
		var start = Builder.BuildLoad2(Int32Ty, indexSlot, "fmt_int_start");
		var sign0 = Builder.BuildLoad2(LLVMTypeRef.Int1, negativeSlot, "fmt_int_signed");
		Builder.BuildCondBr(sign0, negativeBlock, startBlock);

		Builder.PositionAtEnd(negativeBlock);
		var negativeIndex = Builder.BuildSub(start, One32(), "fmt_int_negIdx");
		Builder.BuildStore(negativeIndex, indexSlot);
		Builder.BuildStore(
			LLVMValueRef.CreateConstInt(Int8Ty, '-'),
			Builder.BuildGEP2(bufferType, buffer, new LLVMValueRef[] { Zero32(), negativeIndex }, "fmt_int_sign_slot"));
		Builder.BuildBr(startBlock);

		Builder.PositionAtEnd(startBlock);
		var finalIndex = Builder.BuildLoad2(Int32Ty, indexSlot, "fmt_int_final");
		var length = Builder.BuildSub(LLVMValueRef.CreateConstInt(Int32Ty, bufferCapacity), finalIndex, "fmt_int_len");
		var bytes = Builder.BuildGEP2(bufferType, buffer, new LLVMValueRef[] { Zero32(), finalIndex }, "fmt_int_bytes");
		EmitSinkWrite(formatterPtr, bytes, Builder.BuildZExt(length, Int64Ty, "fmt_int_len64"));
	}

	/// <summary>
	/// Emits canonical <c>%f</c>-style formatting (6 fraction digits, trailing zeros kept, sign and
	/// translated NaN/Inf spelled "nan"/"inf"/"-inf") using pure LLVM IR — no C vararg promotion is
	/// ever part of the language contract.
	/// </summary>
	private void EmitFloat(ResolvedFormattingOperation operation, LLVMValueRef formatterPtr, LLVMValueRef valueAddress)
	{
		var isSingle = operation.ValueType.Name == "float";
		var floatTy = isSingle ? LLVMTypeRef.Float : LLVMTypeRef.Double;
		var intTy = isSingle ? Int32Ty : Int64Ty;
		var value = Builder.BuildLoad2(floatTy, valueAddress, "fmt_float");

		var bits = Builder.BuildBitCast(value, intTy, "fmt_float_bits");
		var isNegative = Builder.BuildICmp(LLVMIntPredicate.LLVMIntSLT, bits, LLVMValueRef.CreateConstInt(intTy, 0), "fmt_float_neg");
		var zeroReal = LLVMValueRef.CreateConstReal(floatTy, 0.0);
		var absValue = Builder.BuildSelect(isNegative, Builder.BuildFSub(zeroReal, value, "fmt_float_negated"), value, "fmt_float_abs");

		const uint bufferCapacity = 64;
		var bufferType = LLVMTypeRef.CreateArray(Int8Ty, bufferCapacity);
		var buffer = Builder.BuildAlloca(bufferType, "fmt_float_buffer");
		var cursor = Builder.BuildAlloca(Int32Ty, "fmt_float_cursor");
		Builder.BuildStore(Zero32(), cursor);

		var currentFunction = Builder.InsertBlock.Parent;
		var nanBlock = currentFunction.AppendBasicBlock("fmt_float_nan");
		var nonNanBlock = currentFunction.AppendBasicBlock("fmt_float_non_nan");
		var infinityBlock = currentFunction.AppendBasicBlock("fmt_float_inf");
		var finiteBlock = currentFunction.AppendBasicBlock("fmt_float_finite");
		var emitBlock = currentFunction.AppendBasicBlock("fmt_float_emit");

		var isNaN = Builder.BuildFCmp(LLVMRealPredicate.LLVMRealUNO, value, value, "fmt_float_nan");
		var positiveInfinity = LLVMValueRef.CreateConstReal(floatTy, double.PositiveInfinity);
		var isInfinity = Builder.BuildFCmp(LLVMRealPredicate.LLVMRealOEQ, absValue, positiveInfinity, "fmt_float_inf");
		Builder.BuildCondBr(isNaN, nanBlock, nonNanBlock);

		Builder.PositionAtEnd(nanBlock);
		AppendText(bufferType, buffer, cursor, "nan");
		Builder.BuildBr(emitBlock);

		Builder.PositionAtEnd(nonNanBlock);
		Builder.BuildCondBr(isInfinity, infinityBlock, finiteBlock);

		Builder.PositionAtEnd(infinityBlock);
		var negativeInfBlock = currentFunction.AppendBasicBlock("fmt_float_neginf");
		var positiveInfBlock = currentFunction.AppendBasicBlock("fmt_float_posinf");
		Builder.BuildCondBr(isNegative, negativeInfBlock, positiveInfBlock);

		Builder.PositionAtEnd(negativeInfBlock);
		AppendByte(bufferType, buffer, cursor, LLVMValueRef.CreateConstInt(Int8Ty, '-'));
		AppendText(bufferType, buffer, cursor, "inf");
		Builder.BuildBr(emitBlock);

		Builder.PositionAtEnd(positiveInfBlock);
		AppendText(bufferType, buffer, cursor, "inf");
		Builder.BuildBr(emitBlock);

		Builder.PositionAtEnd(finiteBlock);
		var signNegativeBlock = currentFunction.AppendBasicBlock("fmt_float_sign_neg");
		var signEndBlock = currentFunction.AppendBasicBlock("fmt_float_sign_end");
		Builder.BuildCondBr(isNegative, signNegativeBlock, signEndBlock);

		Builder.PositionAtEnd(signNegativeBlock);
		AppendByte(bufferType, buffer, cursor, LLVMValueRef.CreateConstInt(Int8Ty, '-'));
		Builder.BuildBr(signEndBlock);

		Builder.PositionAtEnd(signEndBlock);
		var limit = LLVMValueRef.CreateConstReal(floatTy, 1000000000000000.0);
		var absoluteSafe = Builder.BuildSelect(
			Builder.BuildFCmp(LLVMRealPredicate.LLVMRealOLT, absValue, limit, "fmt_float_lt_limit"),
			absValue,
			limit,
			"fmt_float_safe");
		var integerPart = Builder.BuildFPToSI(absoluteSafe, Int64Ty, "fmt_float_int");
		var integerPartValue = Builder.BuildSIToFP(integerPart, floatTy, "fmt_float_int_f");
		var fractionPart = Builder.BuildFSub(absoluteSafe, integerPartValue, "fmt_float_frac");
		var scaledFraction = Builder.BuildFMul(fractionPart, LLVMValueRef.CreateConstReal(floatTy, 1000000.0), "fmt_float_scaled");
		var fractionInteger = Builder.BuildFPToSI(scaledFraction, Int64Ty, "fmt_float_frac_int");
		var scaledValue = Builder.BuildSIToFP(fractionInteger, floatTy, "fmt_float_scaled_f");
		var remainder = Builder.BuildFSub(scaledFraction, scaledValue, "fmt_float_rem");

		var isHalf = Builder.BuildFCmp(LLVMRealPredicate.LLVMRealOEQ, remainder, LLVMValueRef.CreateConstReal(floatTy, 0.5), "fmt_float_half");
		var isAboveHalf = Builder.BuildFCmp(LLVMRealPredicate.LLVMRealOGT, remainder, LLVMValueRef.CreateConstReal(floatTy, 0.5), "fmt_float_above");
		var oneI64 = OneI64();
		var isOdd = Builder.BuildICmp(
			LLVMIntPredicate.LLVMIntEQ,
			Builder.BuildAnd(fractionInteger, oneI64, "fmt_float_odd"),
			oneI64,
			"fmt_float_odd_cond");
		var roundUp = Builder.BuildOr(isAboveHalf, Builder.BuildAnd(isHalf, isOdd, "fmt_float_half_odd"), "fmt_float_round_up");
		var addend = Builder.BuildSelect(roundUp, oneI64, LLVMValueRef.CreateConstInt(Int64Ty, 0), "fmt_float_addend");
		var fractionFinal = Builder.BuildAdd(fractionInteger, addend, "fmt_float_frac_final");
		var carry = Builder.BuildICmp(
			LLVMIntPredicate.LLVMIntEQ,
			fractionFinal,
			LLVMValueRef.CreateConstInt(Int64Ty, 1000000),
			"fmt_float_carry");
		var integerFinal = Builder.BuildSelect(
			carry,
			Builder.BuildAdd(integerPart, oneI64, "fmt_float_int_inc"),
			integerPart,
			"fmt_float_int_final");
		var fractionFinalClean = Builder.BuildSelect(
			carry,
			LLVMValueRef.CreateConstInt(Int64Ty, 0),
			fractionFinal,
			"fmt_float_frac_clean");

		AppendInteger(bufferType, buffer, cursor, integerFinal);
		AppendByte(bufferType, buffer, cursor, LLVMValueRef.CreateConstInt(Int8Ty, '.'));
		AppendFixedFraction(bufferType, buffer, cursor, fractionFinalClean);
		Builder.BuildBr(emitBlock);

		Builder.PositionAtEnd(emitBlock);
		var length = Builder.BuildLoad2(Int32Ty, cursor, "fmt_float_len");
		var bytes = Builder.BuildGEP2(bufferType, buffer, new LLVMValueRef[] { Zero32(), Zero32() }, "fmt_float_bytes");
		EmitSinkWrite(formatterPtr, bytes, Builder.BuildZExt(length, Int64Ty, "fmt_float_len64"));
	}

	/// <summary>
	/// Emits Option / Result composite formatting: a tag dispatch followed by the frozen composite
	/// spellings <c>Some(x)</c>/<c>None</c> and <c>Ok(x)</c>/<c>Err(e)</c> with the payload formatted
	/// recursively through the same canonical operation.
	/// </summary>
	private void EmitComposite(ResolvedFormattingOperation operation, LLVMValueRef formatterPtr, LLVMValueRef valueAddress)
	{
		if (operation.ValueType is not UnionTypeSymbol union)
			throw new InvalidOperationException("A composite formatting operation requires a union value.");

		var layout = codegen.Types.Lower(union);
		var tag = Builder.BuildLoad2(
			Int8Ty,
			Builder.BuildGEP2(layout, valueAddress, new LLVMValueRef[] { Zero32(), Zero32() }, "fmt_union_tag"),
			"fmt_union_tag_val");

		string primaryVariantName;
		ResolvedFormattingOperation? primaryPayload;
		if (operation.Kind == FormattingOperationKind.Option)
		{
			primaryVariantName = union.PayloadVariant?.Name
				?? throw new InvalidOperationException("An Option formatting operation has no payload variant.");
			primaryPayload = operation.Payload;
		}
		else
		{
			var okVariant = union.FindField("Ok")
				?? throw new InvalidOperationException("A Result formatting operation has no Ok variant.");
			primaryVariantName = okVariant.Name;
			primaryPayload = operation.SuccessPayload;
		}

		var primaryIndex = codegen.AggregateLayout.GetFieldIndex(union, primaryVariantName);
		var isPrimary = Builder.BuildICmp(
			LLVMIntPredicate.LLVMIntEQ,
			tag,
			LLVMValueRef.CreateConstInt(Int8Ty, (ulong)primaryIndex),
			"fmt_union_primary");

		var currentFunction = Builder.InsertBlock.Parent;
		var primaryBlock = currentFunction.AppendBasicBlock("fmt_union_primary");
		var secondaryBlock = currentFunction.AppendBasicBlock("fmt_union_secondary");
		var mergeBlock = currentFunction.AppendBasicBlock("fmt_union_merge");
		Builder.BuildCondBr(isPrimary, primaryBlock, secondaryBlock);

		Builder.PositionAtEnd(primaryBlock);
		EmitSinkWrite(formatterPtr, operation.OpenText ?? string.Empty);
		if (primaryPayload is not null)
		{
			var payloadType = union.FindField(primaryVariantName)?.Type
				?? throw new InvalidOperationException("A composite payload variant has no type.");
			var payloadPointer = Builder.BuildBitCast(
				Builder.BuildGEP2(layout, valueAddress, new LLVMValueRef[] { Zero32(), One32() }, "fmt_union_payload"),
				LLVMTypeRef.CreatePointer(codegen.Types.Lower(payloadType), 0),
				"fmt_union_payload_ptr");
			EmitOperation(primaryPayload, formatterPtr, payloadPointer);
		}
		EmitSinkWrite(formatterPtr, operation.CloseText ?? string.Empty);
		Builder.BuildBr(mergeBlock);

		Builder.PositionAtEnd(secondaryBlock);
		if (operation.Kind == FormattingOperationKind.Result)
		{
			EmitSinkWrite(formatterPtr, operation.AlternateOpenText ?? string.Empty);
			if (operation.ErrorPayload is not null)
			{
				var errorType = union.FindField("Err")?.Type
					?? throw new InvalidOperationException("A Result error variant has no type.");
				var errorPointer = Builder.BuildBitCast(
					Builder.BuildGEP2(layout, valueAddress, new LLVMValueRef[] { Zero32(), One32() }, "fmt_union_err_payload"),
					LLVMTypeRef.CreatePointer(codegen.Types.Lower(errorType), 0),
					"fmt_union_err_ptr");
				EmitOperation(operation.ErrorPayload, formatterPtr, errorPointer);
			}
			EmitSinkWrite(formatterPtr, operation.CloseText ?? string.Empty);
		}
		else
		{
			EmitSinkWrite(formatterPtr, operation.AlternateText ?? string.Empty);
		}
		Builder.BuildBr(mergeBlock);

		Builder.PositionAtEnd(mergeBlock);
	}

	/// <summary>
	/// Computes a native string's length with a NUL scan and forwards it to the sink. This keeps
	/// string formatting free of libc dependencies (Base-safe).
	/// </summary>
	private void EmitStringWrite(LLVMValueRef formatterPtr, LLVMValueRef text)
	{
		var positionSlot = Builder.BuildAlloca(I8PtrTy, "fmt_str_pos");
		var lengthSlot = Builder.BuildAlloca(Int64Ty, "fmt_str_len");
		Builder.BuildStore(text, positionSlot);
		Builder.BuildStore(ZeroI64(), lengthSlot);

		var currentFunction = Builder.InsertBlock.Parent;
		var loopBlock = currentFunction.AppendBasicBlock("fmt_str_loop");
		var advanceBlock = currentFunction.AppendBasicBlock("fmt_str_advance");
		var doneBlock = currentFunction.AppendBasicBlock("fmt_str_done");

		Builder.BuildBr(loopBlock);
		Builder.PositionAtEnd(loopBlock);
		var position = Builder.BuildLoad2(I8PtrTy, positionSlot, "fmt_str_pos_val");
		var character = Builder.BuildLoad2(Int8Ty, position, "fmt_str_ch");
		var isEnd = Builder.BuildICmp(LLVMIntPredicate.LLVMIntEQ, character, ZeroI8(), "fmt_str_end");
		Builder.BuildCondBr(isEnd, doneBlock, advanceBlock);

		Builder.PositionAtEnd(advanceBlock);
		Builder.BuildStore(
			Builder.BuildAdd(Builder.BuildLoad2(Int64Ty, lengthSlot, "fmt_str_len_val"), OneI64(), "fmt_str_len_next"),
			lengthSlot);
		Builder.BuildStore(
			Builder.BuildGEP2(Int8Ty, position, new LLVMValueRef[] { One32() }, "fmt_str_pos_next"),
			positionSlot);
		Builder.BuildBr(loopBlock);

		Builder.PositionAtEnd(doneBlock);
		var length = Builder.BuildLoad2(Int64Ty, lengthSlot, "fmt_str_len_final");
		EmitSinkWrite(formatterPtr, text, length);
	}

	/// <summary>
	/// Appends literal text at the current cursor and advances it.
	/// </summary>
	private void AppendText(LLVMTypeRef bufferType, LLVMValueRef buffer, LLVMValueRef cursor, string text)
	{
		foreach (var character in text)
		{
			AppendByte(bufferType, buffer, cursor, LLVMValueRef.CreateConstInt(Int8Ty, character));
		}
	}

	/// <summary>
	/// Writes one byte into the buffer at the cursor position and advances the cursor. This must be
	/// emitted from a block that dominates the buffer and cursor allocas.
	/// </summary>
	private void AppendByte(LLVMTypeRef bufferType, LLVMValueRef buffer, LLVMValueRef cursor, LLVMValueRef byteValue)
	{
		var index = Builder.BuildLoad2(Int32Ty, cursor, "fmt_cursor");
		Builder.BuildStore(
			byteValue,
			Builder.BuildGEP2(bufferType, buffer, new LLVMValueRef[] { Zero32(), index }, "fmt_cursor_slot"));
		Builder.BuildStore(
			Builder.BuildAdd(index, One32(), "fmt_cursor_next"),
			cursor);
	}

	/// <summary>
	/// Appends the decimal digits of a non-negative i64 value, skipping leading zeroes (but always
	/// emitting at least one digit).
	/// </summary>
	private void AppendInteger(LLVMTypeRef bufferType, LLVMValueRef buffer, LLVMValueRef cursor, LLVMValueRef value)
	{
		var startedSlot = Builder.BuildAlloca(LLVMTypeRef.Int1, "fmt_num_started");
		Builder.BuildStore(LLVMValueRef.CreateConstInt(LLVMTypeRef.Int1, 0), startedSlot);

		for (ulong power = 1000000000000000; power > 0; power /= 10)
		{
			var digit = Builder.BuildURem(
				Builder.BuildUDiv(value, LLVMValueRef.CreateConstInt(Int64Ty, power), "fmt_num_div"),
				LLVMValueRef.CreateConstInt(Int64Ty, 10),
				"fmt_num_digit");
			var isNonZero = Builder.BuildICmp(LLVMIntPredicate.LLVMIntNE, digit, ZeroI64(), "fmt_num_nonzero");
			var started = Builder.BuildLoad2(LLVMTypeRef.Int1, startedSlot, "fmt_num_started_val");
			var willStart = Builder.BuildOr(started, isNonZero, "fmt_num_will_start");
			Builder.BuildStore(willStart, startedSlot);
			var digitByte = Builder.BuildAdd(Builder.BuildTrunc(digit, Int8Ty, "fmt_num_digit8"), LLVMValueRef.CreateConstInt(Int8Ty, '0'), "fmt_num_digit_byte");

			var appendBlock = Builder.InsertBlock.Parent.AppendBasicBlock("fmt_num_digit_emit");
			var skipBlock = Builder.InsertBlock.Parent.AppendBasicBlock("fmt_num_digit_skip");
			var mergeBlock = Builder.InsertBlock.Parent.AppendBasicBlock("fmt_num_digit_merge");
			Builder.BuildCondBr(willStart, appendBlock, skipBlock);

			Builder.PositionAtEnd(appendBlock);
			AppendByte(bufferType, buffer, cursor, digitByte);
			Builder.BuildBr(mergeBlock);

			Builder.PositionAtEnd(skipBlock);
			Builder.BuildBr(mergeBlock);

			Builder.PositionAtEnd(mergeBlock);
		}

		var neverStarted = Builder.BuildNot(Builder.BuildLoad2(LLVMTypeRef.Int1, startedSlot, "fmt_num_started_final"), "fmt_num_zero");
		var zeroBlock = Builder.InsertBlock.Parent.AppendBasicBlock("fmt_num_zero_emit");
		var zeroDoneBlock = Builder.InsertBlock.Parent.AppendBasicBlock("fmt_num_zero_done");
		Builder.BuildCondBr(neverStarted, zeroBlock, zeroDoneBlock);

		Builder.PositionAtEnd(zeroBlock);
		AppendByte(bufferType, buffer, cursor, LLVMValueRef.CreateConstInt(Int8Ty, '0'));
		Builder.BuildBr(zeroDoneBlock);

		Builder.PositionAtEnd(zeroDoneBlock);
	}

	/// <summary>
	/// Appends exactly six zero-padded decimal digits (the <c>%f</c> fraction width).
	/// </summary>
	private void AppendFixedFraction(LLVMTypeRef bufferType, LLVMValueRef buffer, LLVMValueRef cursor, LLVMValueRef value)
	{
		for (var power = 100000UL; power > 0; power /= 10)
		{
			var digit = Builder.BuildURem(
				Builder.BuildUDiv(value, LLVMValueRef.CreateConstInt(Int64Ty, power), "fmt_frac_div"),
				LLVMValueRef.CreateConstInt(Int64Ty, 10),
				"fmt_frac_digit");
			var digitByte = Builder.BuildAdd(Builder.BuildTrunc(digit, Int8Ty, "fmt_frac_digit8"), LLVMValueRef.CreateConstInt(Int8Ty, '0'), "fmt_frac_digit_byte");
			AppendByte(bufferType, buffer, cursor, digitByte);
		}
	}

	/// <summary>
	/// Emits a single guarded sink write: when the formatter's sink pointer is null (an unbound
	/// formatter), the write is skipped. Console and user-bound formatters always write.
	/// </summary>
	private void EmitSinkWrite(LLVMValueRef formatterPtr, LLVMValueRef bytesPtr, LLVMValueRef length)
	{
		var formatterStruct = codegen.Types.Lower(FormatterType);
		var contextPtr = Builder.BuildGEP2(formatterStruct, formatterPtr, new LLVMValueRef[] { Zero32(), Zero32() }, "fmt_sink_ctx_slot");
		var sinkPtr = Builder.BuildGEP2(formatterStruct, formatterPtr, new LLVMValueRef[] { Zero32(), One32() }, "fmt_sink_write_slot");
		var context = Builder.BuildLoad2(I8PtrTy, contextPtr, "fmt_sink_ctx_val");
		var sink = Builder.BuildLoad2(I8PtrTy, sinkPtr, "fmt_sink_write_val");

		var currentFunction = Builder.InsertBlock.Parent;
		var doBlock = currentFunction.AppendBasicBlock("fmt_sink_do");
		var skipBlock = currentFunction.AppendBasicBlock("fmt_sink_skip");
		var mergeBlock = currentFunction.AppendBasicBlock("fmt_sink_merge");
		var isNull = Builder.BuildICmp(
			LLVMIntPredicate.LLVMIntEQ,
			sink,
			LLVMValueRef.CreateConstPointerNull(I8PtrTy),
			"fmt_sink_isnull");
		Builder.BuildCondBr(isNull, skipBlock, doBlock);

		Builder.PositionAtEnd(doBlock);
		var sinkFunctionType = LLVMTypeRef.CreateFunction(VoidTy, [I8PtrTy, I8PtrTy, Int64Ty]);
		var sinkFunction = Builder.BuildBitCast(
			sink,
			LLVMTypeRef.CreatePointer(sinkFunctionType, 0),
			"fmt_sink_fn");
		Builder.BuildCall2(sinkFunctionType, sinkFunction, [context, bytesPtr, length]);
		Builder.BuildBr(mergeBlock);

		Builder.PositionAtEnd(skipBlock);
		Builder.BuildBr(mergeBlock);

		Builder.PositionAtEnd(mergeBlock);
	}

	private void EmitSinkWrite(LLVMValueRef formatterPtr, string text)
	{
		var bytes = Builder.BuildGlobalStringPtr(text, "fmt_lit");
		EmitSinkWrite(formatterPtr, bytes, LLVMValueRef.CreateConstInt(Int64Ty, (ulong)text.Length));
	}

	private static LLVMValueRef Zero32() => LLVMValueRef.CreateConstInt(Int32Ty, 0);
	private static LLVMValueRef One32() => LLVMValueRef.CreateConstInt(Int32Ty, 1);
	private static LLVMValueRef ZeroI64() => LLVMValueRef.CreateConstInt(Int64Ty, 0);
	private static LLVMValueRef OneI64() => LLVMValueRef.CreateConstInt(Int64Ty, 1);
	private static LLVMValueRef ZeroI8() => LLVMValueRef.CreateConstInt(Int8Ty, 0);
}
