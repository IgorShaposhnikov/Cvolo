using Cvolo.Analysis.Symbols;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Expressions;

namespace Cvolo.Analysis.Semantics;

/// <summary>
/// Selects the single semantic operation represented by a binary-expression syntax node.
/// It is intentionally side-effect free: the validation layer decides when the declaration universe
/// is stable, stores the returned descriptor in <see cref="BindingContext.ResolvedBinaryExpressions"/>,
/// and reports any diagnostic exactly once.
/// </summary>
internal static class BinaryExpressionBinder
{
	internal static ResolvedBinaryExpression Bind(
		BindingContext context,
		BinaryExpressionSyntax binary,
		TypeSymbol? leftType,
		TypeSymbol? rightType)
	{
		if (binary.Operator == "=")
		{
			// Discard assignment intentionally has no semantic left type. Every other assignment
			// whose operand typing is incomplete is an error-bound operation rather than a
			// fabricated successful primitive assignment.
			var isDiscard = binary.Left is IdentifierExpressionSyntax { Name: "_" };
			if ((!isDiscard && leftType is null) || rightType is null)
				return Error(binary, leftType, rightType, BinaryBindingErrorKind.UnresolvedOperand);

			return Success(
				binary,
				leftType,
				rightType,
				IdentityOrNull(leftType),
				IdentityOrNull(rightType),
				leftType ?? rightType,
				leftType ?? rightType,
				BuiltinBinaryOperatorKind.Assignment);
		}

		if (leftType is null || rightType is null)
			return Error(binary, leftType, rightType, BinaryBindingErrorKind.UnresolvedOperand);

		// User-defined operators retain precedence over builtin meanings, matching the existing
		// validation/emitter order. The selected function and the conversions to its parameters are
		// captured now so later phases do not repeat overload lookup or conversion inference.
		if (context.FindOperatorOverload(binary.Operator, leftType, rightType) is { } overload)
		{
			var leftConversion = ClassifyOperatorArgumentConversion(leftType, overload.Parameters[0].Type);
			var rightConversion = ClassifyOperatorArgumentConversion(rightType, overload.Parameters[1].Type);
			if (leftConversion is null || rightConversion is null)
				return Error(binary, leftType, rightType, BinaryBindingErrorKind.InvalidOperator);

			return Success(binary, leftType, rightType, leftConversion, rightConversion, null, overload.ReturnType,
				BuiltinBinaryOperatorKind.None, overload);
		}

		var left = ValueOperand(leftType);
		var right = ValueOperand(rightType);

		// Constant string concatenation is the only builtin string binary operation currently
		// supported. Dynamic concatenation retains its dedicated validation diagnostic.
		if (binary.Operator == "+" && IsConstantStringExpression(binary.Left) && IsConstantStringExpression(binary.Right))
		{
			return Success(binary, leftType, rightType,
				Conversion(leftType, left, TypeSymbol.String),
				Conversion(rightType, right, TypeSymbol.String),
				TypeSymbol.String, TypeSymbol.String,
				BuiltinBinaryOperatorKind.ConstantStringConcatenation);
		}
		if (binary.Operator == "+" && (left.Equals(TypeSymbol.String) || right.Equals(TypeSymbol.String)))
			return Error(binary, leftType, rightType, BinaryBindingErrorKind.DynamicStringConcatenation);

		// Existing string equality compares the lowered string pointers. Keep that operation explicit
		// in the semantic descriptor instead of letting codegen infer it from LLVM pointer values.
		if (left.Equals(TypeSymbol.String) && right.Equals(TypeSymbol.String) && binary.Operator is "==" or "!=")
		{
			return Success(binary, leftType, rightType,
				Conversion(leftType, left, TypeSymbol.String), Conversion(rightType, right, TypeSymbol.String),
				TypeSymbol.String, TypeSymbol.Bool, BuiltinBinaryOperatorKind.StringComparison);
		}

		if (TryBindNullEquality(binary, leftType, rightType, left, right) is { } nullEquality)
			return nullEquality;

		// Preserve the existing explicit prohibition on implicit enum/integer mixing before any
		// primitive integer rule is considered.
		if (IsEnumIntegerMix(left, right))
			return Error(binary, leftType, rightType, BinaryBindingErrorKind.EnumIntegerMix);

		if (TryBindRawPointer(binary, leftType, rightType, left, right) is { } pointer)
			return pointer;

		if (TryBindEnum(binary, leftType, rightType, left, right) is { } enumBinding)
			return enumBinding;

		if (TryBindBoolean(binary, leftType, rightType, left, right) is { } boolean)
			return boolean;

		if (TryBindNumeric(binary, leftType, rightType, left, right) is { } numeric)
			return numeric;

		var errorOwner = context.DeclaresOperatorOverload(binary.Operator, leftType, unary: false)
			? leftType
			: context.DeclaresOperatorOverload(binary.Operator, rightType, unary: false)
				? rightType
				: null;

		return Error(
			binary,
			leftType,
			rightType,
			errorOwner is null ? BinaryBindingErrorKind.InvalidOperator : BinaryBindingErrorKind.OperatorOverloadNoMatch,
			errorOwner);
	}

	private static ResolvedBinaryExpression? TryBindNullEquality(
		BinaryExpressionSyntax binary,
		TypeSymbol leftSource,
		TypeSymbol rightSource,
		TypeSymbol left,
		TypeSymbol right)
	{
		if (binary.Operator is not ("==" or "!="))
			return null;

		if (left is RawPointerTypeSymbol leftPointer && right.Equals(TypeSymbol.Null))
		{
			return Success(binary, leftSource, rightSource,
				Conversion(leftSource, left, leftPointer), Conversion(rightSource, right, leftPointer),
				leftPointer, TypeSymbol.Bool, BuiltinBinaryOperatorKind.RawPointerComparison);
		}

		if (right is RawPointerTypeSymbol rightPointer && left.Equals(TypeSymbol.Null))
		{
			return Success(binary, leftSource, rightSource,
				Conversion(leftSource, left, rightPointer), Conversion(rightSource, right, rightPointer),
				rightPointer, TypeSymbol.Bool, BuiltinBinaryOperatorKind.RawPointerComparison);
		}

		if (left is DelegateTypeSymbol { IsNative: true } leftDelegate && right.Equals(TypeSymbol.Null))
		{
			return Success(binary, leftSource, rightSource,
				Conversion(leftSource, left, leftDelegate), Conversion(rightSource, right, leftDelegate),
				leftDelegate, TypeSymbol.Bool, BuiltinBinaryOperatorKind.NativeDelegateEquality);
		}

		if (right is DelegateTypeSymbol { IsNative: true } rightDelegate && left.Equals(TypeSymbol.Null))
		{
			return Success(binary, leftSource, rightSource,
				Conversion(leftSource, left, rightDelegate), Conversion(rightSource, right, rightDelegate),
				rightDelegate, TypeSymbol.Bool, BuiltinBinaryOperatorKind.NativeDelegateEquality);
		}

		if (left is DelegateTypeSymbol { IsNative: true } leftNative
			&& right is DelegateTypeSymbol { IsNative: true } rightNative
			&& leftNative.Equals(rightNative))
		{
			return Success(binary, leftSource, rightSource,
				Conversion(leftSource, left, leftNative), Conversion(rightSource, right, leftNative),
				leftNative, TypeSymbol.Bool, BuiltinBinaryOperatorKind.NativeDelegateEquality);
		}

		return null;
	}

	private static ResolvedBinaryExpression? TryBindRawPointer(
		BinaryExpressionSyntax binary,
		TypeSymbol leftSource,
		TypeSymbol rightSource,
		TypeSymbol left,
		TypeSymbol right)
	{
		if (left is RawPointerTypeSymbol leftPointer && TypeSymbol.IsIntegerType(right))
		{
			var kind = binary.Operator switch
			{
				"+" => BuiltinBinaryOperatorKind.RawPointerAdd,
				"-" => BuiltinBinaryOperatorKind.RawPointerSubtract,
				_ => BuiltinBinaryOperatorKind.None,
			};
			if (kind != BuiltinBinaryOperatorKind.None)
			{
				return Success(binary, leftSource, rightSource,
					Conversion(leftSource, left, left), Conversion(rightSource, right, right),
					leftPointer, leftPointer, kind);
			}
		}

		if (right is RawPointerTypeSymbol rightPointer && TypeSymbol.IsIntegerType(left) && binary.Operator == "+")
		{
			return Success(binary, leftSource, rightSource,
				Conversion(leftSource, left, left), Conversion(rightSource, right, right),
				rightPointer, rightPointer, BuiltinBinaryOperatorKind.RawPointerAddReversed);
		}

		// LLVM lowering has historically supported raw-pointer comparisons directly. Keep that
		// existing family canonical as well; no pointer-difference or other pointer arithmetic is
		// introduced here. Opaque LLVM pointers make the comparison representation independent of
		// the pointee type, exactly as the previous emitter path was.
		if (left is RawPointerTypeSymbol leftRaw && right is RawPointerTypeSymbol
			&& binary.Operator is "==" or "!=" or "<" or "<=" or ">" or ">=")
		{
			return Success(binary, leftSource, rightSource,
				Conversion(leftSource, left, left), Conversion(rightSource, right, right),
				leftRaw, TypeSymbol.Bool, BuiltinBinaryOperatorKind.RawPointerComparison);
		}

		return null;
	}

	private static ResolvedBinaryExpression? TryBindEnum(
		BinaryExpressionSyntax binary,
		TypeSymbol leftSource,
		TypeSymbol rightSource,
		TypeSymbol left,
		TypeSymbol right)
	{
		if (left is not EnumTypeSymbol leftEnum || right is not EnumTypeSymbol rightEnum || !leftEnum.Equals(rightEnum))
			return null;

		if ((binary.Operator is "|" or "&" or "^") && leftEnum.IsFlags)
		{
			return Success(binary, leftSource, rightSource,
				Conversion(leftSource, left, leftEnum), Conversion(rightSource, right, rightEnum),
				leftEnum, leftEnum, BuiltinBinaryOperatorKind.FlagsBitwise);
		}

		if (binary.Operator is "==" or "!=" or "<" or "<=" or ">" or ">=")
		{
			return Success(binary, leftSource, rightSource,
				Conversion(leftSource, left, leftEnum), Conversion(rightSource, right, rightEnum),
				leftEnum, TypeSymbol.Bool, BuiltinBinaryOperatorKind.EnumComparison);
		}

		return null;
	}

	private static ResolvedBinaryExpression? TryBindBoolean(
		BinaryExpressionSyntax binary,
		TypeSymbol leftSource,
		TypeSymbol rightSource,
		TypeSymbol left,
		TypeSymbol right)
	{
		if (!left.Equals(TypeSymbol.Bool) || !right.Equals(TypeSymbol.Bool))
			return null;

		var kind = binary.Operator switch
		{
			"&&" => BuiltinBinaryOperatorKind.BooleanLogicalAnd,
			"||" => BuiltinBinaryOperatorKind.BooleanLogicalOr,
			"==" or "!=" => BuiltinBinaryOperatorKind.BooleanComparison,
			_ => BuiltinBinaryOperatorKind.None,
		};
		if (kind == BuiltinBinaryOperatorKind.None)
			return null;

		return Success(binary, leftSource, rightSource,
			Conversion(leftSource, left, TypeSymbol.Bool), Conversion(rightSource, right, TypeSymbol.Bool),
			TypeSymbol.Bool, TypeSymbol.Bool, kind);
	}

	private static ResolvedBinaryExpression? TryBindNumeric(
		BinaryExpressionSyntax binary,
		TypeSymbol leftSource,
		TypeSymbol rightSource,
		TypeSymbol left,
		TypeSymbol right)
	{
		var leftInteger = TypeSymbol.IsIntegerType(left);
		var rightInteger = TypeSymbol.IsIntegerType(right);
		var leftFloat = TypeSymbol.IsFloatingPointType(left);
		var rightFloat = TypeSymbol.IsFloatingPointType(right);
		if (!(leftInteger || leftFloat) || !(rightInteger || rightFloat))
			return null;

		var arithmetic = binary.Operator is "+" or "-" or "*" or "/" or "%";
		var comparison = binary.Operator is "==" or "!=" or "<" or "<=" or ">" or ">=";
		var bitwise = binary.Operator is "&" or "|" or "^";
		var shift = binary.Operator is "<<" or ">>" or ">>>";

		if ((leftFloat || rightFloat) && !arithmetic && !comparison)
			return null;
		if ((bitwise || shift) && (!leftInteger || !rightInteger))
			return null;
		if (!arithmetic && !comparison && !bitwise && !shift)
			return null;

		TypeSymbol operationType;
		if (leftFloat || rightFloat)
		{
			operationType = left.Equals(TypeSymbol.Double) || right.Equals(TypeSymbol.Double)
				? TypeSymbol.Double
				: TypeSymbol.Float;
		}
		else
		{
			var leftWidth = TypeSymbol.IntegerBitWidth(left);
			var rightWidth = TypeSymbol.IntegerBitWidth(right);
			operationType = rightWidth > leftWidth ? right : left;
		}

		var kind = comparison
			? leftFloat || rightFloat ? BuiltinBinaryOperatorKind.FloatingComparison : BuiltinBinaryOperatorKind.IntegerComparison
			: bitwise ? BuiltinBinaryOperatorKind.IntegerBitwise
			: shift ? binary.Operator switch
			{
				"<<" => BuiltinBinaryOperatorKind.ShiftLeft,
				">>" => BuiltinBinaryOperatorKind.ShiftRightArithmetic,
				_ => BuiltinBinaryOperatorKind.ShiftRightLogical,
			}
			: leftFloat || rightFloat ? BuiltinBinaryOperatorKind.FloatingArithmetic : BuiltinBinaryOperatorKind.IntegerArithmetic;

		var useUnsignedIntegerSemantics = leftInteger && rightInteger
			&& (!TypeSymbol.IsSignedIntegerType(left) || !TypeSymbol.IsSignedIntegerType(right));

		return Success(binary, leftSource, rightSource,
			Conversion(leftSource, left, operationType), Conversion(rightSource, right, operationType),
			operationType, comparison ? TypeSymbol.Bool : operationType, kind,
			useUnsignedIntegerSemantics: useUnsignedIntegerSemantics);
	}

	internal static BinaryOperandConversion? ClassifyOperatorArgumentConversion(TypeSymbol source, TypeSymbol target)
	{
		var value = ValueOperand(source);
		if (target.Equals(source))
			return new BinaryOperandConversion(source, value, target, Dereference: false, BinaryNumericConversionKind.None);
		if (TypeSymbol.IsIntegerType(source) && TypeSymbol.IsIntegerType(target))
			return new BinaryOperandConversion(source, source, target, Dereference: false, BinaryNumericConversionKind.IntegerWidth);
		if (TypeSymbol.IsIntegerType(source) && TypeSymbol.IsFloatingPointType(target))
			return new BinaryOperandConversion(source, source, target, Dereference: false, BinaryNumericConversionKind.IntegerToFloating);
		return null;
	}

	private static BinaryOperandConversion Conversion(TypeSymbol source, TypeSymbol value, TypeSymbol target)
	{
		var dereference = source is PointerTypeSymbol;
		var numeric = BinaryNumericConversionKind.None;
		if (!value.Equals(target))
		{
			if (TypeSymbol.IsIntegerType(value) && TypeSymbol.IsIntegerType(target))
				numeric = BinaryNumericConversionKind.IntegerWidth;
			else if (TypeSymbol.IsIntegerType(value) && TypeSymbol.IsFloatingPointType(target))
				numeric = BinaryNumericConversionKind.IntegerToFloating;
			else if (value.Equals(TypeSymbol.Float) && target.Equals(TypeSymbol.Double))
				numeric = BinaryNumericConversionKind.FloatToDouble;
		}

		return new BinaryOperandConversion(source, value, target, dereference, numeric);
	}

	private static BinaryOperandConversion? IdentityOrNull(TypeSymbol? type)
		=> type is null ? null : new BinaryOperandConversion(type, ValueOperand(type), type, false, BinaryNumericConversionKind.None);

	private static TypeSymbol ValueOperand(TypeSymbol type)
		=> type is PointerTypeSymbol pointer ? pointer.ReferencedType : type;

	private static bool IsEnumIntegerMix(TypeSymbol left, TypeSymbol right)
		=> (left is EnumTypeSymbol && TypeSymbol.IsIntegerType(right))
			|| (right is EnumTypeSymbol && TypeSymbol.IsIntegerType(left));

	private static bool IsConstantStringExpression(ExpressionSyntax expression)
		=> expression switch
		{
			StringLiteralExpressionSyntax => true,
			BinaryExpressionSyntax binary when binary.Operator == "+" =>
				IsConstantStringExpression(binary.Left) && IsConstantStringExpression(binary.Right),
			_ => false,
		};

	private static ResolvedBinaryExpression Success(
		BinaryExpressionSyntax binary,
		TypeSymbol? leftType,
		TypeSymbol? rightType,
		BinaryOperandConversion? leftConversion,
		BinaryOperandConversion? rightConversion,
		TypeSymbol? operationType,
		TypeSymbol? resultType,
		BuiltinBinaryOperatorKind kind,
		FunctionSymbol? overload = null,
		bool useUnsignedIntegerSemantics = false)
		=> new(binary, BinaryBindingState.Success, leftType, rightType, leftConversion, rightConversion,
			operationType, resultType, kind, overload, useUnsignedIntegerSemantics);

	private static ResolvedBinaryExpression Error(
		BinaryExpressionSyntax binary,
		TypeSymbol? leftType,
		TypeSymbol? rightType,
		BinaryBindingErrorKind errorKind,
		TypeSymbol? errorOwnerType = null)
		=> new(binary, BinaryBindingState.Error, leftType, rightType, null, null, null, null,
			BuiltinBinaryOperatorKind.None, errorKind: errorKind, errorOwnerType: errorOwnerType);
}
