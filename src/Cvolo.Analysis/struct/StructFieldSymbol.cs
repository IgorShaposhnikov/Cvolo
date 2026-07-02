namespace Cvolo.Analysis.@struct
{
    public sealed class StructFieldSymbol(string name, TypeSymbol type)
    {
        public string Name { get; } = name;
        public TypeSymbol Type { get; } = type;
    }
}
