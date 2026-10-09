using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Expressions;

namespace Cvolo.Analysis.Constants;

/// <summary>
/// One canonical evaluator for restricted compile-time integer expressions, used by static array
/// extents (and available to any other compile-time integer context). It walks the ordinary
/// <see cref="ExpressionSyntax"/> tree the parser built, so operator precedence, associativity and
/// parentheses come from the ordinary expression grammar rather than a text splitter.
///
/// Supported: integer literals; unary <c>-</c>/<c>+</c>/<c>~</c>; the integer operators
/// <c>+ - * / %</c>; the layout operators <c>sizeof</c>/<c>alignof</c>/<c>offsetof</c>; and the enum
/// metadata members <c>Enum.Min</c>, <c>Enum.Max</c>, <c>Enum.Count</c>. Unsupported or invalid
/// forms (non-constant references, division/remainder by zero, invalid layout operands) return
/// false so the caller can produce a deterministic diagnostic instead of an unrelated
/// "unknown type".
/// </summary>
internal static class ConstantExpressionEvaluator
{
	public static bool TryEvaluateInteger(ExpressionSyntax expression, BindingContext context, out long value)
	{
		switch (expression)
		{
			case IntegerLiteralExpressionSyntax literal:
				value = unchecked((long)literal.Value);
				return true;

			case UnaryExpressionSyntax { Operator: "-" } negate:
				if (TryEvaluateInteger(negate.Operand, context, out var negated))
				{
					value = unchecked(-negated);
					return true;
				}

				break;

			case UnaryExpressionSyntax { Operator: "+" } positive:
				return TryEvaluateInteger(positive.Operand, context, out value);

			case UnaryExpressionSyntax { Operator: "~" } complement:
				if (TryEvaluateInteger(complement.Operand, context, out var bits))
				{
					value = ~bits;
					return true;
				}

				break;

			case SizeofExpressionSyntax:
			case AlignofExpressionSyntax:
			case OffsetofExpressionSyntax:
				return TryEvaluateLayout(expression, context, out value);

			case BinaryExpressionSyntax binary:
				if (TryEvaluateBinary(binary, context, out value))
					return true;
				break;

			case MemberAccessExpressionSyntax memberAccess:
				if (TryEvaluateEnumMetadata(memberAccess, context, out value))
					return true;
				break;
		}

		value = 0;
		return false;
	}

	private static bool TryEvaluateLayout(ExpressionSyntax expression, BindingContext context, out long value)
	{
		value = 0;

		switch (expression)
		{
			case SizeofExpressionSyntax sizeofExpression:
				if (!TryResolveLayoutType(sizeofExpression.TypeName, context, out var sizedType))
					return false;
				value = context.LayoutService.GetLayout(sizedType).Size;
				return true;

			case AlignofExpressionSyntax alignofExpression:
				if (!TryResolveLayoutType(alignofExpression.TypeName, context, out var alignedType))
					return false;
				value = context.LayoutService.GetLayout(alignedType).Alignment;
				return true;

			case OffsetofExpressionSyntax offsetofExpression:
				if (!TryResolveLayoutType(offsetofExpression.TypeName, context, out var offsetType))
					return false;

				var path = new List<string>(offsetofExpression.Members.Count);
				foreach (var member in offsetofExpression.Members)
					path.Add(member.Name);

				var offset = context.LayoutService.TryGetFieldOffset(offsetType, path);
				if (offset is null)
					return false;

				value = offset.Value;
				return true;

			default:
				return false;
		}
	}

	private static bool TryResolveLayoutType(string typeName, BindingContext context, out TypeSymbol type)
	{
		type = TypeSymbol.Void;
		var resolved = context.ResolveType(typeName);
		if (resolved is null
			|| resolved == TypeSymbol.Void
			|| Passes.Validation.ExpressionValidator.ContainsTypeParameter(resolved))
		{
			return false;
		}

		type = resolved;
		return true;
	}

	private static bool TryEvaluateBinary(BinaryExpressionSyntax binary, BindingContext context, out long value)
	{
		value = 0;
		if (binary.Operator is not ("+" or "-" or "*" or "/" or "%"))
			return false;

		if (!TryEvaluateInteger(binary.Left, context, out var left)
			|| !TryEvaluateInteger(binary.Right, context, out var right))
		{
			return false;
		}

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
					return false;
				value = left / right;
				return true;
			case "%":
				if (right == 0 || (left == long.MinValue && right == -1))
					return false;
				value = left % right;
				return true;
			default:
				return false;
		}
	}

	private static bool TryEvaluateEnumMetadata(MemberAccessExpressionSyntax memberAccess, BindingContext context, out long value)
	{
		value = 0;
		if (!TryGetDottedName(memberAccess, out var dottedName))
			return false;

		var dot = dottedName.LastIndexOf('.');
		if (dot <= 0 || dot == dottedName.Length - 1)
			return false;

		var enumName = dottedName[..dot];
		var memberName = dottedName[(dot + 1)..];
		if (context.ResolveType(enumName) is not EnumTypeSymbol enumType || enumType.Variants.Count == 0)
			return false;

		var hasNegative = enumType.Variants.Any(variant => variant.Value < 0);
		switch (memberName)
		{
			case "Min":
				if (hasNegative)
					return false;
				value = enumType.Variants.Min(variant => variant.Value);
				return true;
			case "Max":
				if (hasNegative)
					return false;
				value = enumType.Variants.Max(variant => variant.Value);
				return true;
			case "Count":
				value = enumType.IsFlags
					? enumType.Variants.Count(variant => variant.Value > 0 && (variant.Value & (variant.Value - 1)) == 0)
					: enumType.Variants.Count;
				return true;
			default:
				return false;
		}
	}

	private static bool TryGetDottedName(ExpressionSyntax expression, out string name)
	{
		switch (expression)
		{
			case IdentifierExpressionSyntax identifier:
				name = identifier.Name;
				return true;
			case MemberAccessExpressionSyntax memberAccess when TryGetDottedName(memberAccess.Expression, out var inner):
				name = $"{inner}.{memberAccess.MemberName}";
				return true;
			default:
				name = string.Empty;
				return false;
		}
	}
}
