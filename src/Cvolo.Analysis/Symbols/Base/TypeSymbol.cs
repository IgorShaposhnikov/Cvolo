using Cvolo.Core.AST.Base;

namespace Cvolo.Analysis.Symbols.Base;

public class TypeSymbol(string name) : IEquatable<TypeSymbol>
{
	public virtual string Name { get; } = name;

	/// <summary>Effective visibility of this type declaration (struct/union/enum/interface/protocol).</summary>
	public Visibility Visibility { get; set; } = Visibility.Internal;

	public static readonly TypeSymbol Void = new("void");
	public static readonly TypeSymbol Int = new("int");
	public static readonly TypeSymbol UInt = new("uint");
	public static readonly TypeSymbol Long = new("long");
	public static readonly TypeSymbol ULong = new("ulong");
	public static readonly TypeSymbol Short = new("short");
	public static readonly TypeSymbol UShort = new("ushort");
	public static readonly TypeSymbol Byte = new("byte");
	public static readonly TypeSymbol SByte = new("sbyte");
	public static readonly TypeSymbol NInt = new("nint");
	public static readonly TypeSymbol NUInt = new("nuint");
	public static readonly TypeSymbol Float = new("float");
	public static readonly TypeSymbol Double = new("double");
	public static readonly TypeSymbol Bool = new("bool");
	public static readonly TypeSymbol String = new("string");
	public static readonly TypeSymbol Char = new("char");
	public static readonly TypeSymbol Null = new("null");

	public static TypeSymbol? FromName(string name) => name switch
	{
		"void" => Void,
		"int" => Int,
		"uint" => UInt,
		"long" => Long,
		"ulong" => ULong,
		"short" => Short,
		"ushort" => UShort,
		"byte" => Byte,
		"sbyte" => SByte,
		"nint" => NInt,
		"nuint" => NUInt,
		"float" => Float,
		"double" => Double,
		"bool" => Bool,
		"string" => String,
		"char" => Char,
		"null" => Null,
		_ => null,
	};

	/// <summary>Implicit numeric conversions follow the C# widening matrix; unsafe does not bypass the rule.</summary>
public static bool IsImplicitNumericConversion(TypeSymbol from, TypeSymbol to)
{
    if (from.Equals(to))
        return true;
    return from.Name switch
    {
        "sbyte" => to.Name is "short" or "int" or "long" or "nint" or "float" or "double",
        "byte" => to.Name is "short" or "ushort" or "int" or "uint" or "long" or "ulong" or "nint" or "nuint" or "float" or "double",
        "short" => to.Name is "int" or "long" or "nint" or "float" or "double",
        "ushort" => to.Name is "int" or "uint" or "long" or "ulong" or "nint" or "nuint" or "float" or "double",
        "int" => to.Name is "long" or "nint" or "float" or "double",
        "uint" => to.Name is "long" or "ulong" or "nuint" or "float" or "double",
        "long" => to.Name is "float" or "double",
        "ulong" => to.Name is "float" or "double",
        "nint" => to.Name is "long" or "float" or "double",
        "nuint" => to.Name is "ulong" or "float" or "double",
        "float" => to.Name is "double",
        "char" => to.Name is "ushort" or "int" or "uint" or "long" or "ulong" or "nint" or "nuint" or "float" or "double",
        _ => false,
    };
}

/// <summary>Implicit conversion of an unsuffixed integer constant is allowed only when it fits.</summary>
public static bool IsImplicitIntegerConstant(Cvolo.Core.AST.Base.ExpressionSyntax expr, TypeSymbol to)
{
    if (!IsNumericIntegerType(to))
        return false;
    return expr switch
    {
        Cvolo.Core.AST.Expressions.IntegerLiteralExpressionSyntax literal
            => FitsIntLiteral(literal.Value, false, literal.LiteralType, to),
        Cvolo.Core.AST.Expressions.UnaryExpressionSyntax
        {
            Operator: "-",
            Operand: Cvolo.Core.AST.Expressions.IntegerLiteralExpressionSyntax literal
        } => FitsIntLiteral(literal.Value, true, literal.LiteralType, to),
        _ => false,
    };
}

private static bool FitsIntLiteral(ulong amount, bool negative, string? suffix, TypeSymbol to)
{
    // The parser currently labels large unsuffixed decimal literals as "long".
    // Preserve those direct initializers while rejecting explicitly suffixed small literals.
    // A future syntax model should distinguish inferred width from an L suffix.
    if (suffix is not null && !(suffix == "long" && amount > int.MaxValue))
        return false;
    var bits = IntegerBitWidth(to);
    if (bits <= 0 || bits > 64)
        return false;
    if (negative)
        return IsSignedIntegerType(to) && amount <= (1UL << (bits - 1));
    if (IsSignedIntegerType(to))
        return amount <= ((1UL << (bits - 1)) - 1);
    return bits == 64 || amount <= ((1UL << bits) - 1);
}

public static bool IsSignedIntegerType(TypeSymbol t) => t.Name switch
	{
		"sbyte" or "short" or "int" or "long" or "nint" => true,
		_ => false,
	};

	public static bool IsIntegerType(TypeSymbol t) => t.Name switch
	{
		"sbyte" or "byte" or "short" or "ushort" or "int" or "uint" or "long" or "ulong" or "nint" or "nuint" or "char" => true,
		_ => false,
	};

	public static bool IsNumericIntegerType(TypeSymbol t) => t.Name switch
	{
		"sbyte" or "byte" or "short" or "ushort" or "int" or "uint" or "long" or "ulong" or "nint" or "nuint" => true,
		_ => false,
	};

	public static bool IsFloatingPointType(TypeSymbol t) => t.Name switch
	{
		"float" or "double" => true,
		_ => false,
	};

	public static int IntegerBitWidth(TypeSymbol t) => t.Name switch
	{
		"sbyte" or "byte" or "char" => 8,
		"short" or "ushort" => 16,
		"int" or "uint" => 32,
		"long" or "ulong" or "nint" or "nuint" => 64,
		_ => 0,
	};

	public static int PrimitiveByteSize(TypeSymbol t) => t.Name switch
	{
		"bool" or "sbyte" or "byte" or "char" => 1,
		"short" or "ushort" => 2,
		"int" or "uint" or "float" => 4,
		"long" or "ulong" or "nint" or "nuint" or "double" or "string" => 8,
		_ => 0,
	};

	public bool Equals(TypeSymbol? other) => other is not null && Name == other.Name;
	public override bool Equals(object? obj) => Equals(obj as TypeSymbol);
	public override int GetHashCode() => Name.GetHashCode();
	public override string ToString() => Name;
}
