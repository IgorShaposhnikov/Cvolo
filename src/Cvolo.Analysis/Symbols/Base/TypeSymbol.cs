namespace Cvolo.Analysis.Symbols.Base;

public class TypeSymbol(string name) : IEquatable<TypeSymbol>
{
	public virtual string Name { get; } = name;

	public static readonly TypeSymbol Void = new("void");
	public static readonly TypeSymbol Int = new("int");
	public static readonly TypeSymbol Double = new("double");
	public static readonly TypeSymbol Bool = new("bool");
	public static readonly TypeSymbol String = new("string");
	public static readonly TypeSymbol Char = new("char");
	public static readonly TypeSymbol Null = new("null");

	public static TypeSymbol? FromName(string name) => name switch
	{
		"void" => Void,
		"int" => Int,
		"double" => Double,
		"bool" => Bool,
		"string" => String,
		"char" => Char,
		"null" => Null,
		_ => null,
	};

	public bool Equals(TypeSymbol? other) => other is not null && Name == other.Name;
	public override bool Equals(object? obj) => Equals(obj as TypeSymbol);
	public override int GetHashCode() => Name.GetHashCode();
	public override string ToString() => Name;
}
