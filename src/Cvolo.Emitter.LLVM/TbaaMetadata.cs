using Cvolo.Analysis.Symbols;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Collections;
using Cvolo.Analysis.Symbols.Structs;
using LLVMSharp.Interop;

namespace Cvolo.Emitter.LLVM;

/// <summary>
/// Builds and caches LLVM '!tbaa' (Type-Based Alias Analysis) metadata in the
/// struct-path schema accepted by the LLVM verifier: a "scalar" root node, one
/// leaf base node per primitive type, one struct node per struct type whose
/// embedded field descriptors enumerate the scalar members as (type, byte
/// offset) pairs, and one access tag per scalar field of the form
/// {struct node, leaf node, i64 byte offset}. Reference-layer fields
/// (ref/refvar/pointer) are never annotated.
/// </summary>
internal sealed class TbaaMetadata
{
	private readonly LLVMContextRef _context;
	private readonly LLVMModuleRef _module;
	private readonly Func<StructTypeSymbol, LLVMTypeRef> _getLlvmType;
	private readonly Lazy<LLVMTargetDataRef> _targetData;
	private readonly Dictionary<string, LLVMValueRef> _typeNodes = [];
	private readonly Dictionary<string, LLVMValueRef> _structNodes = [];
	private readonly Dictionary<(string StructName, int FieldIndex), LLVMValueRef> _fieldNodes = [];
	private LLVMValueRef? _scalarRoot;
	private uint? _tbaaKindId;

	public TbaaMetadata(LLVMContextRef context, LLVMModuleRef module, Func<StructTypeSymbol, LLVMTypeRef> getLlvmType)
	{
		_context = context;
		_module = module;
		_getLlvmType = getLlvmType;
		_targetData = new Lazy<LLVMTargetDataRef>(() => LLVMTargetDataRef.FromStringRepresentation(_module.DataLayout));
	}

	public uint TbaaKindId => _tbaaKindId ??= GetMdKindId("tbaa");

	/// <summary>
	/// Returns the !tbaa access tag for the given struct field, or null when the
	/// field is not a scalar primitive (reference/struct/union fields are untagged).
	/// </summary>
	public LLVMValueRef? GetFieldTag(StructTypeSymbol structType, int fieldIndex)
	{
		var fieldType = structType.Fields[fieldIndex].Type;
		if (!IsScalar(fieldType))
			return null;

		// Memory spec §4.C.1: a struct carrying a reference-layer field (ref, refvar,
		// slice, or interface) must never annotate its scalar fields, across all tiers.
		if (HasReferenceField(structType))
			return null;

		var key = (structType.Name, fieldIndex);
		if (_fieldNodes.TryGetValue(key, out var cached))
			return cached;

		var structNode = GetStructNode(structType);
		var typeNode = GetTypeNode(fieldType);
		var offsetTag = CreateOffsetTag(FieldOffset(structType, fieldIndex));

		var tag = LLVMValueRef.CreateMDNode(new[] { structNode, typeNode, offsetTag });
		_fieldNodes[key] = tag;
		return tag;
	}

	private ulong FieldOffset(StructTypeSymbol structType, int fieldIndex) =>
		_targetData.Value.OffsetOfElement(_getLlvmType(structType), (uint)fieldIndex);

	private LLVMValueRef CreateOffsetTag(ulong offset)
	{
		var offsetConst = LLVMValueRef.CreateConstInt(LLVMTypeRef.Int64, offset, false);
		LLVMValueRef offsetTag;
		unsafe
		{
			offsetTag = LLVMSharp.Interop.LLVM.MetadataAsValue(_context, LLVMSharp.Interop.LLVM.ValueAsMetadata(offsetConst));
		}

		return offsetTag;
	}

	/// <summary>
	/// The named struct node embedding a (leaf type node, byte offset) field
	/// descriptor pair for every scalar member, in ascending offset order.
	/// </summary>
	private LLVMValueRef GetStructNode(StructTypeSymbol structType)
	{
		if (_structNodes.TryGetValue(structType.Name, out var node))
			return node;

		var operands = new List<LLVMValueRef> { MdString(structType.Name) };
		for (int i = 0; i < structType.Fields.Count; i++)
		{
			var fieldType = structType.Fields[i].Type;
			if (!IsScalar(fieldType))
				continue;

			operands.Add(GetTypeNode(fieldType));
			operands.Add(CreateOffsetTag(FieldOffset(structType, i)));
		}

		node = LLVMValueRef.CreateMDNode([.. operands]);
		_structNodes[structType.Name] = node;
		return node;
	}

	public static bool IsScalar(TypeSymbol type) =>
		TypeSymbol.IsIntegerType(type) || TypeSymbol.IsFloatingPointType(type) || type == TypeSymbol.Bool;

	private static bool HasReferenceField(StructTypeSymbol structType)
	{
		foreach (var field in structType.Fields)
		{
			if (field.Type is PointerTypeSymbol or SliceTypeSymbol or InterfaceTypeSymbol)
				return true;
		}

		return false;
	}

	private LLVMValueRef ScalarRoot() =>
		_scalarRoot ??= LLVMValueRef.CreateMDNode(new[] { MdString("scalar") });

	private LLVMValueRef GetTypeNode(TypeSymbol type)
	{
		if (!_typeNodes.TryGetValue(type.Name, out var node))
		{
			node = LLVMValueRef.CreateMDNode(new[] { MdString(type.Name), ScalarRoot() });
			_typeNodes[type.Name] = node;
		}

		return node;
	}

	private static uint GetMdKindId(string name)
	{
		unsafe
		{
			var bytes = System.Text.Encoding.UTF8.GetBytes(name);
			fixed (byte* namePtr = bytes)
			{
				return LLVMSharp.Interop.LLVM.GetMDKindID((sbyte*)namePtr, (uint)bytes.Length);
			}
		}
	}

	private LLVMValueRef MdString(string value)
	{
		unsafe
		{
			var bytes = System.Text.Encoding.UTF8.GetBytes(value);
			fixed (byte* valuePtr = bytes)
			{
				return LLVMSharp.Interop.LLVM.MDStringInContext(_context, (sbyte*)valuePtr, (uint)bytes.Length);
			}
		}
	}
}