using Cvolo.Analysis.Symbols.Base;

namespace Cvolo.Analysis.Symbols.Structs;

public sealed class PointerTypeSymbol(TypeSymbol referencedType, bool isMutable)
: TypeSymbol($"ref {(isMutable ? "var" : "val")} {referencedType.Name}")
{
	public TypeSymbol ReferencedType { get; } = referencedType;
	public bool IsMutable { get; } = isMutable;
}
