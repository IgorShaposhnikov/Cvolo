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

	public CopyKind Classify(StructTypeSymbol structType)
	{
		if (_cache.TryGetValue(structType.Name, out var cached))
			return cached;

		if (_inProgress.Contains(structType.Name))
			return CopyKind.ResourceMove;

		_inProgress.Add(structType.Name);
		var result = ClassifyCore(structType);
		_inProgress.Remove(structType.Name);
		_cache[structType.Name] = result;
		return result;
	}

	private CopyKind ClassifyCore(StructTypeSymbol structType)
	{
		if (context.Destructors.ContainsKey(structType.Name))
			return CopyKind.ResourceMove;

		foreach (var field in structType.Fields)
		{
			if (field.Type is PointerTypeSymbol)
				return CopyKind.ResourceMove;

			if (field.Type is StructTypeSymbol nested && Classify(nested) == CopyKind.ResourceMove)
				return CopyKind.ResourceMove;
		}

		var size = CalculateByteSize(structType);
		return size <= 16 ? CopyKind.TrivialCopy : CopyKind.LargeCopy;
	}

	public int CalculateByteSize(TypeSymbol type)
	{
		return type switch
		{
			StructTypeSymbol s => s.Fields.Sum(f => CalculateByteSize(f.Type)),
			ArrayTypeSymbol a => CalculateByteSize(a.ElementType) * a.Size,
			SliceTypeSymbol => 16,
			PointerTypeSymbol => 8,
			_ => GetPrimitiveSize(type)
		};
	}

	private static int GetPrimitiveSize(TypeSymbol type)
	{
		if (type == TypeSymbol.Int) return 4;
		if (type == TypeSymbol.Double) return 8;
		if (type == TypeSymbol.Bool) return 1;
		if (type == TypeSymbol.Char) return 1;
		if (type == TypeSymbol.String) return 8;
		return 0;
	}
}
