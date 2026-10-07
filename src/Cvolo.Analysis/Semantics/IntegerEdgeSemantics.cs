using Cvolo.Analysis.Symbols.Base;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Expressions;

namespace Cvolo.Analysis.Semantics;

/// <summary>
/// Classification of a statically known integer-edge failure for a bound builtin integer operation.
/// The shared frontend service behind compile-time rejection (§13 of the integer-edge INC).
/// </summary>
public enum IntegerEdgeConstantFailure
{
	None,
	DivisionByZero,
	SignedMinDivMinusOne,
	InvalidShiftCount,
}

/// <summary>
/// Shared constant-edge classification for the defined integer edge semantics INC. Consumes the
/// canonical <see cref="ResolvedBinaryExpression"/> — never a second binder, promotion resolver, or
/// operand-conversion cache — and evaluates the statically known operands with the same rules the
/// runtime guards use, so constant folding and runtime lowering cannot diverge.
/// </summary>
public static class IntegerEdgeSemantics
{
	/// <summary>
	/// Classifies a statically known invalid integer edge. Returns <see cref="IntegerEdgeConstantFailure.None"/>
	/// when no guard applies or the invalid input is not statically provable.
	/// </summary>
	public static IntegerEdgeConstantFailure ClassifyConstantEdge(ResolvedBinaryExpression resolved)
	{
		if (!resolved.IsSuccess || resolved.IsUserDefined || !resolved.HasIntegerEdgeGuards)
			return IntegerEdgeConstantFailure.None;

		if (resolved.IntegerEdgeCheckDivisorNonZero)
		{
			if (resolved.RightConversion is not { } rightConversion
				|| !TryEvaluateIntegerConstant(resolved.Syntax.Right, out var divisor))
			{
				return IntegerEdgeConstantFailure.None;
			}

			if (divisor == 0)
				return IntegerEdgeConstantFailure.DivisionByZero;

			if (resolved.IntegerEdgeCheckSignedMinMinusOne)
			{
				var width = resolved.IntegerEdgeValueBitWidth;
				if (TryEvaluateIntegerConstant(resolved.Syntax.Left, out var dividend)
					&& MatchesSignedMin(dividend, width)
					&& MatchesMinusOne(divisor, width))
				{
					return IntegerEdgeConstantFailure.SignedMinDivMinusOne;
				}
			}
		}
		else if (resolved.IntegerEdgeCheckShiftCountDomain)
		{
			if (resolved.RightConversion is not { } shiftConversion
				|| !TryEvaluateIntegerConstant(resolved.Syntax.Right, out var count))
			{
				return IntegerEdgeConstantFailure.None;
			}

			var width = resolved.IntegerEdgeValueBitWidth;
			if (TypeSymbol.IsSignedIntegerType(shiftConversion.ValueType) && count < 0)
				return IntegerEdgeConstantFailure.InvalidShiftCount;
			if ((ulong)count >= (ulong)width)
				return IntegerEdgeConstantFailure.InvalidShiftCount;
		}

		return IntegerEdgeConstantFailure.None;
	}

	/// <summary>
	/// Evaluates a compile-time constant integer expression (literals, unary minus, and the builtin
	/// integer operators <c>+ - * / %</c>) with wrap-around host <see cref="long"/> math. Returns
	/// false when the expression is not statically evaluable with defined semantics (including
	/// division/remainder by zero and the host <c>long.MinValue / -1</c> trap).
	/// </summary>
	public static bool TryEvaluateIntegerConstant(ExpressionSyntax expression, out long value)
	{
		switch (expression)
		{
			case IntegerLiteralExpressionSyntax literal:
				value = unchecked((long)literal.Value);
				return true;
			case UnaryExpressionSyntax { Operator: "-" } negate:
				if (TryEvaluateIntegerConstant(negate.Operand, out var negateValue))
				{
					value = unchecked(-negateValue);
					return true;
				}
				break;
			case BinaryExpressionSyntax binary:
				if (binary.Operator is "+" or "-" or "*" or "/" or "%"
					&& TryEvaluateIntegerConstant(binary.Left, out var left)
					&& TryEvaluateIntegerConstant(binary.Right, out var right))
				{
					switch (binary.Operator)
					{
						case "+":
							value = unchecked(left + right);
							return true;
						case "-":
							value = unchecked(left - right);
							return true;
						case "*":
							value = unchecked(left * right);
							return true;
case "/":
						if (right == 0 || (left == long.MinValue && right == -1))
						{
							value = 0;
							return false;
						}
						value = left / right;
						return true;
					case "%":
						if (right == 0 || (left == long.MinValue && right == -1))
						{
							value = 0;
							return false;
						}
						value = left % right;
						return true;
					}
				}
				break;
		}

		value = 0;
		return false;
	}

	private static bool MatchesSignedMin(long value, int width)
	{
		// The runtime guard compares the converted left operand against the MIN constant of width W.
		var mask = width == 64 ? ulong.MaxValue : (1UL << width) - 1;
		return ((ulong)value & mask) == (1UL << (width - 1));
	}

	private static bool MatchesMinusOne(long value, int width)
	{
		var mask = width == 64 ? ulong.MaxValue : (1UL << width) - 1;
		return ((ulong)value & mask) == mask;
	}
}