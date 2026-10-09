using System.Numerics;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Expressions;

namespace Cvolo.Analysis.Semantics;

/// <summary>
/// Canonical numeric conversion rules for implicit integer-to-floating coercion.
/// Runtime conversions are allowed only when the entire source integer domain is exactly
/// representable. Proven integer constants may additionally convert when that concrete value is
/// exactly representable in the target IEEE format.
/// </summary>
public static class NumericConversionSemantics
{
	private const int FloatPrecision = 24;
	private const int DoublePrecision = 53;

	public static bool IsImplicitConversionAllowed(TypeSymbol target, TypeSymbol source, ExpressionSyntax? expression = null, BindingContext? context = null)
	{
		if (target.Equals(source))
			return true;

		if (TypeSymbol.IsNumericIntegerType(target) && TypeSymbol.IsNumericIntegerType(source))
			return true;

		if (target.Equals(TypeSymbol.Double) && TypeSymbol.IsFloatingPointType(source))
			return true;

		if (TypeSymbol.IsFloatingPointType(target) && TypeSymbol.IsNumericIntegerType(source))
		{
			if (expression is not null && TryEvaluateIntegerConstant(expression, context, out var constant, out var typedSource))
			{
				if (typedSource is not null && (typedSource.Equals(TypeSymbol.NInt) || typedSource.Equals(TypeSymbol.NUInt)))
					return false;

				return IsExactlyRepresentable(constant, target);
			}

			if (source.Equals(TypeSymbol.NInt) || source.Equals(TypeSymbol.NUInt))
				return false;

			return IsIntegerDomainExactlyRepresentable(source, target);
		}

		return false;
	}

	public static bool TryEvaluateIntegerConstant(ExpressionSyntax expression, BindingContext? context, out BigInteger value, out TypeSymbol? typedSource)
	{
		switch (expression)
		{
			case IntegerLiteralExpressionSyntax literal:
				value = literal.Value;
				typedSource = LiteralType(literal.LiteralType);
				return true;

			case UnaryExpressionSyntax { Operator: "+" } unaryPlus:
				return TryEvaluateIntegerConstant(unaryPlus.Operand, context, out value, out typedSource);

			case UnaryExpressionSyntax { Operator: "-" } unaryMinus:
				if (TryEvaluateIntegerConstant(unaryMinus.Operand, context, out var operand, out typedSource))
				{
					value = -operand;
					return true;
				}
				break;

			case UnaryExpressionSyntax cast when TryParseCastOperator(cast.Operator, out var castTypeName):
				if (context?.ResolveType(castTypeName) is { } castTarget
					&& TypeSymbol.IsNumericIntegerType(castTarget)
					&& TryEvaluateIntegerConstant(cast.Operand, context, out var castOperand, out _))
				{
					value = TruncateIntegerToType(castOperand, castTarget);
					typedSource = castTarget;
					return true;
				}
				break;

			case SizeofExpressionSyntax or AlignofExpressionSyntax or OffsetofExpressionSyntax:
				if (context is not null)
				{
					value = context.EvaluateLayoutConstant(expression);
					typedSource = null;
					return true;
				}
				break;

			case BinaryExpressionSyntax binary when binary.Operator is "+" or "-" or "*" or "/" or "%":
				if (!TryEvaluateIntegerConstant(binary.Left, context, out var left, out _)
					|| !TryEvaluateIntegerConstant(binary.Right, context, out var right, out _))
				{
					break;
				}

				if ((binary.Operator is "/" or "%") && right.IsZero)
					break;

				value = binary.Operator switch
				{
					"+" => left + right,
					"-" => left - right,
					"*" => left * right,
					"/" => left / right,
					"%" => left % right,
					_ => BigInteger.Zero
				};

				// Post-overflow signed normalization (§8.2.1): truncate the magnitude to the resolved
				// operand width and reinterpret it under the operands' signedness, so a legal wrapping
				// operation yields the semantically correct value before exactness is checked.
				if (context?.ResolvedBinaryExpressions.TryGetValue(binary, out var binaryBinding) == true
					&& binaryBinding.IsSuccess
					&& binaryBinding.BuiltinOperatorKind == BuiltinBinaryOperatorKind.IntegerArithmetic
					&& binaryBinding.OperationType is { } operationType
					&& TypeSymbol.IsNumericIntegerType(operationType))
				{
					value = TruncateIntegerToType(value, operationType);
					typedSource = operationType;
				}
				else
				{
					typedSource = null;
				}

				return true;
		}

		value = BigInteger.Zero;
		typedSource = null;
		return false;
	}

	public static bool TryGetFloatingConstant(ExpressionSyntax expression, TypeSymbol target, BindingContext? context, out double value)
	{
		// An explicit numeric cast to a floating type is always legal and may round; it is a
		// compile-time constant whenever its operand is one. Round through binary32 when the cast
		// target is `float` so the emitted value matches the explicit cast's defined semantics.
		if (expression is UnaryExpressionSyntax castExpression
			&& TryParseCastOperator(castExpression.Operator, out var castTypeName)
			&& context?.ResolveType(castTypeName) is { } castTarget
			&& TypeSymbol.IsFloatingPointType(castTarget))
		{
			var roundToFloat = castTarget.Equals(TypeSymbol.Float);
			if (TryEvaluateIntegerConstant(castExpression.Operand, context, out var castInteger, out _))
			{
				var asDouble = (double)castInteger;
				value = roundToFloat ? (float)asDouble : asDouble;
				return true;
			}

			if (TryGetFloatingConstant(castExpression.Operand, castTarget, context, out var castFloating))
			{
				value = castFloating;
				return true;
			}
		}

		if (TypeSymbol.IsFloatingPointType(target)
			&& TryEvaluateIntegerConstant(expression, context, out var integer, out var typedSource)
			&& (typedSource is null || (!typedSource.Equals(TypeSymbol.NInt) && !typedSource.Equals(TypeSymbol.NUInt)))
			&& IsExactlyRepresentable(integer, target))
		{
			value = (double)integer;
			return true;
		}

		if (expression is DoubleLiteralExpressionSyntax doubleLiteral)
		{
			if (target.Equals(TypeSymbol.Double) || (target.Equals(TypeSymbol.Float) && doubleLiteral.IsFloat))
			{
				value = doubleLiteral.Value;
				return true;
			}
		}

		if (expression is UnaryExpressionSyntax { Operator: "-", Operand: DoubleLiteralExpressionSyntax negativeDouble })
		{
			if (target.Equals(TypeSymbol.Double) || (target.Equals(TypeSymbol.Float) && negativeDouble.IsFloat))
			{
				value = -negativeDouble.Value;
				return true;
			}
		}

		value = 0;
		return false;
	}

	private static bool IsIntegerDomainExactlyRepresentable(TypeSymbol source, TypeSymbol target)
	{
		if (!TypeSymbol.IsFloatingPointType(target))
			return false;

		var precision = target.Equals(TypeSymbol.Float) ? FloatPrecision : DoublePrecision;
		return source.Name switch
		{
			"sbyte" or "byte" or "short" or "ushort" => true,
			"int" or "uint" => precision >= 53,
			_ => false,
		};
	}

	private static bool IsExactlyRepresentable(BigInteger value, TypeSymbol target)
	{
		var precision = target.Equals(TypeSymbol.Float) ? FloatPrecision : DoublePrecision;
		var magnitude = BigInteger.Abs(value);
		if (magnitude.IsZero)
			return true;

		var bitLength = GetBitLength(magnitude);
		if (bitLength <= precision)
			return true;

		var discardedBits = bitLength - precision;
		var mask = (BigInteger.One << discardedBits) - BigInteger.One;
		return (magnitude & mask).IsZero;
	}

	private static int GetBitLength(BigInteger value)
	{
		var bits = 0;
		while (value > BigInteger.Zero)
		{
			value >>= 1;
			bits++;
		}

		return bits;
	}

	/// <summary>
	/// Recognizes a cast written as a unary operator of the form <c>(typeName)</c>, which is how the
	/// frontend encodes an explicit numeric cast.
	/// </summary>
	public static bool TryParseCastOperator(string op, out string typeName)
	{
		if (op.Length >= 2 && op[0] == '(' && op[^1] == ')')
		{
			typeName = op[1..^1];
			return typeName.Length > 0;
		}

		typeName = string.Empty;
		return false;
	}

	/// <summary>
	/// Applies an explicit cast's defined wrapping semantics: truncate to the target integer width
	/// and reinterpret the bit pattern under the target's signedness (§8.2.1).
	/// </summary>
	public static BigInteger TruncateIntegerToType(BigInteger value, TypeSymbol type)
	{
		var width = TypeSymbol.IntegerBitWidth(type);
		var modulus = BigInteger.One << width;
		var truncated = value % modulus;
		if (truncated.Sign < 0)
			truncated += modulus;

		if (TypeSymbol.IsSignedIntegerType(type) && (truncated & (BigInteger.One << (width - 1))) != BigInteger.Zero)
			truncated -= modulus;

		return truncated;
	}

	private static TypeSymbol? LiteralType(string? literalType) => literalType switch
	{
		"uint" => TypeSymbol.UInt,
		"long" => TypeSymbol.Long,
		"ulong" => TypeSymbol.ULong,
		"nint" => TypeSymbol.NInt,
		"nuint" => TypeSymbol.NUInt,
		_ => null
	};
}
