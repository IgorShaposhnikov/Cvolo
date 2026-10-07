using Cvolo.Analysis.Symbols;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Core.AST.Expressions;

namespace Cvolo.Analysis.Semantics;

/// <summary>
/// Final semantic state of one binary-expression binding in a <see cref="BindingContext"/>.
/// </summary>
public enum BinaryBindingState
{
	Success,
	Error,
}

/// <summary>
/// Compiler-owned builtin operation selected for a binary expression.
/// User-defined operators use <see cref="ResolvedBinaryExpression.OperatorOverload"/> instead.
/// </summary>
public enum BuiltinBinaryOperatorKind
{
	None,
	Assignment,
	ConstantStringConcatenation,
	StringComparison,
	NativeDelegateEquality,
	IntegerArithmetic,
	FloatingArithmetic,
	IntegerComparison,
	FloatingComparison,
	BooleanComparison,
	BooleanLogicalAnd,
	BooleanLogicalOr,
	IntegerBitwise,
	FlagsBitwise,
	ShiftLeft,
	ShiftRightArithmetic,
	ShiftRightLogical,
	RawPointerAdd,
	RawPointerAddReversed,
	RawPointerSubtract,
	RawPointerComparison,
	EnumComparison,
}

/// <summary>Numeric conversion, if any, performed after an optional reference dereference.</summary>
public enum BinaryNumericConversionKind
{
	None,
	IntegerWidth,
	IntegerToFloating,
	FloatToDouble,
}

/// <summary>
/// Complete conversion selected for one operand of a binary expression. A safe reference may first
/// be dereferenced and then receive one numeric conversion; code generation consumes this descriptor
/// rather than independently inferring a promotion.
/// </summary>
public sealed record BinaryOperandConversion(
	TypeSymbol SourceType,
	TypeSymbol ValueType,
	TypeSymbol TargetType,
	bool Dereference,
	BinaryNumericConversionKind NumericKind)
{
	public bool IsIdentity => !Dereference && NumericKind == BinaryNumericConversionKind.None && SourceType.Equals(TargetType);
}

/// <summary>Reason retained by an error-bound binary expression for one-shot diagnostics.</summary>
public enum BinaryBindingErrorKind
{
	None,
	UnresolvedOperand,
	EnumIntegerMix,
	DynamicStringConcatenation,
	OperatorOverloadNoMatch,
	InvalidOperator,
}

/// <summary>
/// Canonical semantic operation for one <see cref="BinaryExpressionSyntax"/>. The descriptor owns
/// operand types, conversions, selected builtin/user-defined operation, and final result type.
/// </summary>
public sealed class ResolvedBinaryExpression
{
	public ResolvedBinaryExpression(
		BinaryExpressionSyntax syntax,
		BinaryBindingState state,
		TypeSymbol? leftType,
		TypeSymbol? rightType,
		BinaryOperandConversion? leftConversion,
		BinaryOperandConversion? rightConversion,
		TypeSymbol? operationType,
		TypeSymbol? resultType,
		BuiltinBinaryOperatorKind builtinOperatorKind,
		FunctionSymbol? operatorOverload = null,
		bool useUnsignedIntegerSemantics = false,
		BinaryBindingErrorKind errorKind = BinaryBindingErrorKind.None,
		TypeSymbol? errorOwnerType = null)
	{
		Syntax = syntax;
		State = state;
		LeftType = leftType;
		RightType = rightType;
		LeftConversion = leftConversion;
		RightConversion = rightConversion;
		OperationType = operationType;
		ResultType = resultType;
		BuiltinOperatorKind = builtinOperatorKind;
		OperatorOverload = operatorOverload;
		UseUnsignedIntegerSemantics = useUnsignedIntegerSemantics;
		ErrorKind = errorKind;
		ErrorOwnerType = errorOwnerType;
	}

	public BinaryExpressionSyntax Syntax { get; }
	public string Operator => Syntax.Operator;
	public BinaryBindingState State { get; }
	public TypeSymbol? LeftType { get; }
	public TypeSymbol? RightType { get; }
	public BinaryOperandConversion? LeftConversion { get; }
	public BinaryOperandConversion? RightConversion { get; }
	public TypeSymbol? OperationType { get; }
	public TypeSymbol? ResultType { get; }
	public BuiltinBinaryOperatorKind BuiltinOperatorKind { get; }
	public FunctionSymbol? OperatorOverload { get; }
	public bool UseUnsignedIntegerSemantics { get; }
	public BinaryBindingErrorKind ErrorKind { get; }
	public TypeSymbol? ErrorOwnerType { get; }
	public bool IsUserDefined => OperatorOverload is not null;
	public bool IsSuccess => State == BinaryBindingState.Success;

	/// <summary>
	/// Whether this builtin integer comparison orders by unsigned semantics. Derived solely from the
	/// canonical common integer type recorded during binding: an unsigned common type inside the
	/// numeric-integer domain selects unsigned LLVM predicates. char and enum operands are not part
	/// of that domain (see <see cref="TypeSymbol.IsNumericIntegerType"/>) and keep existing lowering.
	/// </summary>
	public bool IsUnsignedIntegerComparison =>
		BuiltinOperatorKind == BuiltinBinaryOperatorKind.IntegerComparison
		&& OperationType is { } operationType
		&& TypeSymbol.IsNumericIntegerType(operationType)
		&& !TypeSymbol.IsSignedIntegerType(operationType);
}
