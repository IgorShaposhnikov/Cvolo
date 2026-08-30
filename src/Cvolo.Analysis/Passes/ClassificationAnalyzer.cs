using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Collections;
using Cvolo.Analysis.Symbols.Structs;

namespace Cvolo.Analysis.Passes;

public enum CopyKind
{
	TrivialCopy,
	LargeCopy,
	ResourceMove
}

public sealed class ClassificationAnalyzer(BindingContext context)
{
	private readonly Dictionary<string, CopyKind> _cache = [];
	private readonly HashSet<string> _inProgress = [];

	public CopyKind Classify(TypeSymbol type)
	{
		if (type is StructTypeSymbol s)
			return ClassifyStruct(s);
		if (type is UnionTypeSymbol u)
			return ClassifyUnion(u);
		return CopyKind.TrivialCopy;
	}

	public CopyKind ClassifyStruct(StructTypeSymbol structType)
	{
		if (_cache.TryGetValue(structType.Name, out var cached))
			return cached;

		if (_inProgress.Contains(structType.Name))
			return CopyKind.ResourceMove;

		_inProgress.Add(structType.Name);
		var result = ClassifyStructCore(structType);
		_inProgress.Remove(structType.Name);
		_cache[structType.Name] = result;
		return result;
	}

	private CopyKind ClassifyStructCore(StructTypeSymbol structType)
	{
		if (context.Destructors.ContainsKey(structType.Name))
			return CopyKind.ResourceMove;

		foreach (var field in structType.Fields)
		{
			if (field.Type is PointerTypeSymbol)
				return CopyKind.ResourceMove;

			if (Classify(field.Type) == CopyKind.ResourceMove)
				return CopyKind.ResourceMove;
		}

		var size = CalculateByteSize(structType);
		return size <= 16 ? CopyKind.TrivialCopy : CopyKind.LargeCopy;
	}

	public CopyKind ClassifyUnion(UnionTypeSymbol unionType)
	{
		if (_cache.TryGetValue(unionType.Name, out var cached))
			return cached;

		if (_inProgress.Contains(unionType.Name))
			return CopyKind.ResourceMove;

		_inProgress.Add(unionType.Name);
		var result = ClassifyUnionCore(unionType);
		_inProgress.Remove(unionType.Name);
		_cache[unionType.Name] = result;
		return result;
	}

	private CopyKind ClassifyUnionCore(UnionTypeSymbol unionType)
	{
		// Unions inherit classification from variants (ResourceMove if any active variant is ResourceMove)
		foreach (var field in unionType.Fields)
		{
			if (field.IsVoidVariant)
				continue;

			if (field.Type is PointerTypeSymbol)
				return CopyKind.ResourceMove;

			if (Classify(field.Type) == CopyKind.ResourceMove)
				return CopyKind.ResourceMove;
		}

		var size = CalculateByteSize(unionType);
		return size <= 16 ? CopyKind.TrivialCopy : CopyKind.LargeCopy;
	}

	public int CalculateByteSize(TypeSymbol type)
	{
		return type switch
		{
			StructTypeSymbol s => s.Fields.Sum(f => CalculateByteSize(f.Type)),
			UnionTypeSymbol u => u.IsNpoEligible
				? 8 // Null-Pointer Optimization: a flat Option<ref T> / <refvar T> is a single 8-byte pointer.
				: 1 + u.Fields.Where(f => !f.IsVoidVariant).Select(f => CalculateByteSize(f.Type)).DefaultIfEmpty(0).Max(),
			EnumTypeSymbol e => CalculateByteSize(e.StorageType),
			ArrayTypeSymbol a => CalculateByteSize(a.ElementType) * a.Size,
			SliceTypeSymbol => 16,
			PointerTypeSymbol => 8,
			_ => GetPrimitiveSize(type)
		};
	}

	private static int GetPrimitiveSize(TypeSymbol type)
	{
		return TypeSymbol.PrimitiveByteSize(type);
	}
}
