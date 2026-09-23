using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.FFI;
using Cvolo.Analysis.Symbols.Structs;
using LLVMSharp.Interop;

namespace Cvolo.Emitter.LLVM.Codegen.TypeLowering;

internal enum NativeAbiValuePassKind
{
	Scalar,
	DirectIntegerAggregate,
	IndirectAggregate,
}

internal sealed record NativeAbiValuePlan(
	TypeSymbol SemanticType,
	NativeAbiValuePassKind Kind,
	LLVMTypeRef AbiType,
	int Size,
	int Alignment);

internal sealed record NativeAbiFunctionPlan(
	NativeAbiValuePlan Return,
	IReadOnlyList<NativeAbiValuePlan> Parameters,
	LLVMTypeRef FunctionReturnType,
	IReadOnlyList<LLVMTypeRef> FunctionParameterTypes,
	bool HasSRet,
	bool UsesSysVByVal);

/// <summary>
/// Verified x64 aggregate ABI lowering for the conservative aggregate shapes admitted by
/// NativeAbiAggregateClassification. Small integer-class aggregates (one eightbyte) are coerced
/// to an integer register; large aggregates use indirect parameter storage and an sret result.
/// 9..16-byte split-register aggregates remain fail-closed in analysis.
/// </summary>
internal sealed class NativeAbiAggregateLowering(CodegenContext codegen, Func<TypeSymbol, LLVMTypeRef> lowerScalar)
{
	private readonly CodegenContext _codegen = codegen;
	private readonly Func<TypeSymbol, LLVMTypeRef> _lowerScalar = lowerScalar;

	public NativeAbiFunctionPlan Build(TypeSymbol returnType, IReadOnlyList<TypeSymbol> parameterTypes)
	{
		var ret = Classify(returnType, isReturn: true);
		var parameters = parameterTypes.Select(type => Classify(type, isReturn: false)).ToArray();

		var llvmParameters = new List<LLVMTypeRef>();
		if (ret.Kind == NativeAbiValuePassKind.IndirectAggregate)
			llvmParameters.Add(LLVMTypeRef.CreatePointer(_codegen.Types.Lower(returnType), 0));
		llvmParameters.AddRange(parameters.Select(p => p.AbiType));

		return new NativeAbiFunctionPlan(
			ret,
			parameters,
			ret.Kind == NativeAbiValuePassKind.IndirectAggregate ? LLVMTypeRef.Void : ret.AbiType,
			llvmParameters,
			ret.Kind == NativeAbiValuePassKind.IndirectAggregate,
			IsSysVX64());
	}

	public NativeAbiValuePlan Classify(TypeSymbol type, bool isReturn)
	{
		if (type is not (StructTypeSymbol or UnionTypeSymbol { IsUnsafe: true }))
			return new NativeAbiValuePlan(type, NativeAbiValuePassKind.Scalar, _lowerScalar(type), 0, 0);

		if (!NativeAbiAggregateClassification.IsVerifiedForDirectBoundary(type, _codegen.NativePointerBytes, IsSupportedAggregateTarget()))
			throw new InvalidOperationException($"Native aggregate ABI for '{type.Name}' is not in the verified lowering subset.");

		EnsureSupportedTarget();
		var (size, alignment) = _codegen.AggregateLayout.GetNativeLayout(type);

		if (IsWindowsX64() && size <= 8 && size is not (1 or 2 or 4 or 8))
		{
			return new NativeAbiValuePlan(
				type,
				NativeAbiValuePassKind.IndirectAggregate,
				LLVMTypeRef.CreatePointer(_codegen.Types.Lower(type), 0),
				size,
				alignment);
		}

		if (size <= 8)
		{
			var bits = IsAArch64() && !isReturn ? 64u : (uint)Math.Max(8, size * 8);
			return new NativeAbiValuePlan(
				type,
				NativeAbiValuePassKind.DirectIntegerAggregate,
				_codegen.LLVMContext.GetIntType(bits),
				size,
				alignment);
		}

		if (size > 16 || IsWindowsX64())
		{
			return new NativeAbiValuePlan(
				type,
				NativeAbiValuePassKind.IndirectAggregate,
				LLVMTypeRef.CreatePointer(_codegen.Types.Lower(type), 0),
				size,
				alignment);
		}

		throw new InvalidOperationException(
			$"Native aggregate ABI for '{type.Name}' ({size} bytes) is not verified for target '{_codegen.Module.Target}'.");
	}

	public LLVMValueRef PackDirectAggregate(LLVMBuilderRef builder, NativeAbiValuePlan plan, LLVMValueRef value, string name)
	{
		if (plan.Kind != NativeAbiValuePassKind.DirectIntegerAggregate)
			return value;

		LLVMValueRef storage;
		if (value.TypeOf.Kind == LLVMTypeKind.LLVMPointerTypeKind)
		{
			storage = value;
		}
		else
		{
			storage = builder.BuildAlloca(_codegen.Types.Lower(plan.SemanticType), name + ".agg.tmp");
			builder.BuildStore(value, storage);
		}

		var naturalIntType = _codegen.LLVMContext.GetIntType((uint)Math.Max(8, plan.Size * 8));
		var intPtr = builder.BuildBitCast(storage, LLVMTypeRef.CreatePointer(naturalIntType, 0), name + ".abi.ptr");
		var packed = builder.BuildLoad2(naturalIntType, intPtr, name + ".abi");
		if (plan.AbiType.Kind == LLVMTypeKind.LLVMIntegerTypeKind
			&& naturalIntType.IntWidth < plan.AbiType.IntWidth)
			packed = builder.BuildZExt(packed, plan.AbiType, name + ".abi.extend");
		return packed;
	}

	public LLVMValueRef MaterializeDirectAggregate(LLVMBuilderRef builder, NativeAbiValuePlan plan, LLVMValueRef abiValue, string name)
	{
		var aggregateType = _codegen.Types.Lower(plan.SemanticType);
		var storage = builder.BuildAlloca(aggregateType, name + ".agg");
		var naturalIntType = _codegen.LLVMContext.GetIntType((uint)Math.Max(8, plan.Size * 8));
		var materialized = abiValue;
		if (materialized.TypeOf.Kind == LLVMTypeKind.LLVMIntegerTypeKind
			&& materialized.TypeOf.IntWidth > naturalIntType.IntWidth)
			materialized = builder.BuildTrunc(materialized, naturalIntType, name + ".abi.trunc");
		var intPtr = builder.BuildBitCast(storage, LLVMTypeRef.CreatePointer(naturalIntType, 0), name + ".abi.ptr");
		builder.BuildStore(materialized, intPtr);
		return storage;
	}

	public LLVMValueRef GetAggregateAddress(LLVMBuilderRef builder, NativeAbiValuePlan plan, LLVMValueRef value, string name)
	{
		if (value.TypeOf.Kind == LLVMTypeKind.LLVMPointerTypeKind)
			return value;

		var storage = builder.BuildAlloca(_codegen.Types.Lower(plan.SemanticType), name + ".agg.addr");
		builder.BuildStore(value, storage);
		return storage;
	}

	public LLVMValueRef PrepareIndirectArgument(LLVMBuilderRef builder, NativeAbiValuePlan plan, LLVMValueRef value, string name)
	{
		var source = GetAggregateAddress(builder, plan, value, name + ".src");
		var aggregateType = _codegen.Types.Lower(plan.SemanticType);
		var copy = builder.BuildAlloca(aggregateType, name + ".byval");
		var loaded = builder.BuildLoad2(aggregateType, source, name + ".copy.value");
		builder.BuildStore(loaded, copy);
		return copy;
	}

	public void ApplyFunctionAggregateAttributes(LLVMValueRef function, NativeAbiFunctionPlan plan)
	{
		if (plan.HasSRet)
			NativeAbiAttributeEmitter.AddTypeFunctionAttribute(_codegen.LLVMContext, function, (LLVMAttributeIndex)1, "sret", _codegen.Types.Lower(plan.Return.SemanticType));

		if (!plan.UsesSysVByVal)
			return;

		var llvmIndex = plan.HasSRet ? 2 : 1;
		foreach (var parameter in plan.Parameters)
		{
			if (parameter.Kind == NativeAbiValuePassKind.IndirectAggregate)
				NativeAbiAttributeEmitter.AddTypeFunctionAttribute(_codegen.LLVMContext, function, (LLVMAttributeIndex)llvmIndex, "byval", _codegen.Types.Lower(parameter.SemanticType));
			llvmIndex++;
		}
	}

	public void ApplyCallSiteAggregateAttributes(LLVMValueRef call, NativeAbiFunctionPlan plan)
	{
		if (plan.HasSRet)
			NativeAbiAttributeEmitter.AddTypeCallSiteAttribute(_codegen.LLVMContext, call, (LLVMAttributeIndex)1, "sret", _codegen.Types.Lower(plan.Return.SemanticType));

		if (!plan.UsesSysVByVal)
			return;

		var llvmIndex = plan.HasSRet ? 2 : 1;
		foreach (var parameter in plan.Parameters)
		{
			if (parameter.Kind == NativeAbiValuePassKind.IndirectAggregate)
				NativeAbiAttributeEmitter.AddTypeCallSiteAttribute(_codegen.LLVMContext, call, (LLVMAttributeIndex)llvmIndex, "byval", _codegen.Types.Lower(parameter.SemanticType));
			llvmIndex++;
		}
	}

	private void EnsureSupportedTarget()
	{
		var target = _codegen.Module.Target ?? string.Empty;
		if (!IsSupportedAggregateTarget())
		{
			throw new InvalidOperationException(
				$"Verified native aggregate ABI lowering is available for x86-64 and AArch64 targets; active target is '{target}'.");
		}
	}

	private bool IsSupportedAggregateTarget()
	{
		var target = _codegen.Module.Target ?? string.Empty;
		var supportedOs = target.Contains("windows", StringComparison.OrdinalIgnoreCase)
			|| target.Contains("linux", StringComparison.OrdinalIgnoreCase)
			|| target.Contains("darwin", StringComparison.OrdinalIgnoreCase)
			|| target.Contains("apple", StringComparison.OrdinalIgnoreCase);
		return supportedOs && (IsX64() || IsAArch64());
	}

	private bool IsX64()
	{
		var target = _codegen.Module.Target ?? string.Empty;
		return target.Contains("x86_64", StringComparison.OrdinalIgnoreCase)
			|| target.Contains("amd64", StringComparison.OrdinalIgnoreCase);
	}

	private bool IsAArch64()
	{
		var target = _codegen.Module.Target ?? string.Empty;
		return target.Contains("aarch64", StringComparison.OrdinalIgnoreCase)
			|| target.Contains("arm64", StringComparison.OrdinalIgnoreCase);
	}

	private bool IsWindowsX64()
		=> IsX64() && (_codegen.Module.Target ?? string.Empty).Contains("win", StringComparison.OrdinalIgnoreCase);

	private bool IsSysVX64()
	{
		var target = _codegen.Module.Target ?? string.Empty;
		return IsX64() && !IsWindowsX64()
			&& (target.Contains("linux", StringComparison.OrdinalIgnoreCase)
				|| target.Contains("darwin", StringComparison.OrdinalIgnoreCase)
				|| target.Contains("apple", StringComparison.OrdinalIgnoreCase));
	}
}
