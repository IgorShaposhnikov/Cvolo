using Cvolo.Analysis.Symbols;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.AST.Expressions;

namespace Cvolo.Analysis.Semantics;

/// <summary>
/// How the selected optional-chain member is invoked.
/// </summary>
public enum OptionalChainMemberKind
{
	/// <summary>A field or variant read projected through the payload.</summary>
	Field,

	/// <summary>A method projected through the payload and called with the written arguments.</summary>
	Method,

	/// <summary>The compiler-owned synthesized <c>Delegate.Invoke</c> semantic member.</summary>
	DelegateInvoke,
}

public enum OptionalChainBindingErrorKind
{
	/// <summary>The receiver or an operand is not typed yet; binding is incomplete.</summary>
	UnresolvedOperand,

	/// <summary>The receiver is not an <c>Option&lt;T&gt;</c>.</summary>
	ReceiverNotOption,

	/// <summary>The member does not exist on the payload type.</summary>
	MemberNotFound,
}

/// <summary>
/// The resolved meaning of one <c>receiver?.member</c> or <c>receiver?.Method(args)</c> expression.
/// </summary>
/// <remarks>
/// Validation, tooling and code generation all consume this descriptor instead of re-deriving the
/// semantics from the syntax tree. In particular <see cref="PayloadType"/> is the type the member is
/// resolved against (the Option payload, never the Option itself) and <see cref="ResultType"/> is
/// already the lifted result, so the emitter never has to decide lifting again.
/// </remarks>
public sealed class ResolvedOptionalChainExpression
{
	private ResolvedOptionalChainExpression(
		OptionalChainExpressionSyntax syntax,
		UnionTypeSymbol? optionType,
		TypeSymbol? receiverType,
		TypeSymbol? payloadType,
		TypeSymbol? memberType,
		TypeSymbol? resultType,
		OptionalChainMemberKind memberKind,
		FunctionSymbol? method,
		DelegateTypeSymbol? delegateType,
		bool lifts,
		OptionalChainBindingErrorKind errorKind)
	{
		Syntax = syntax;
		OptionType = optionType;
		ReceiverType = receiverType;
		PayloadType = payloadType;
		MemberType = memberType;
		ResultType = resultType;
		MemberKind = memberKind;
		Method = method;
		DelegateType = delegateType;
		Lifts = lifts;
		ErrorKind = errorKind;
	}

	public OptionalChainExpressionSyntax Syntax { get; }
	public UnionTypeSymbol? OptionType { get; }
	public TypeSymbol? ReceiverType { get; }

	/// <summary>The Option payload the member is resolved against.</summary>
	public TypeSymbol? PayloadType { get; }

	/// <summary>The declared result type of the selected member, before lifting.</summary>
	public TypeSymbol? MemberType { get; }

	/// <summary>The expression result type, after lifting exactly one Option layer.</summary>
	public TypeSymbol? ResultType { get; }

	public OptionalChainMemberKind MemberKind { get; }
	public FunctionSymbol? Method { get; }
	public DelegateTypeSymbol? DelegateType { get; }

	/// <summary>
	/// False when the member returns <c>void</c>: the expression then produces no value and no
	/// <c>Option&lt;void&gt;</c> is synthesized.
	/// </summary>
	public bool Lifts { get; }

	public OptionalChainBindingErrorKind ErrorKind { get; }
	public bool IsSuccess => ErrorKind == OptionalChainBindingErrorKind.UnresolvedOperand && ResultType is not null;

	/// <summary>True when the receiver uses the flat-pointer (NPO) representation.</summary>
	public bool UsesNpoRepresentation => OptionType?.IsNpoEligible == true;

	/// <summary>
	/// Creates the descriptor for a <c>void</c> member, which produces no value and therefore no
	/// <c>Option&lt;void&gt;</c>. The selected member kind is retained so the emitter still lowers the
	/// underlying field read, method call, or delegate invocation.
	/// </summary>
	public static ResolvedOptionalChainExpression Void(
		OptionalChainExpressionSyntax syntax,
		UnionTypeSymbol? optionType,
		TypeSymbol? receiverType,
		TypeSymbol? payloadType,
		OptionalChainMemberKind memberKind,
		FunctionSymbol? method,
		DelegateTypeSymbol? delegateType)
		=> new(syntax, optionType, receiverType, payloadType, TypeSymbol.Void, TypeSymbol.Void, memberKind, method, delegateType, lifts: false, OptionalChainBindingErrorKind.UnresolvedOperand);

	public static ResolvedOptionalChainExpression Success(
		OptionalChainExpressionSyntax syntax,
		UnionTypeSymbol optionType,
		TypeSymbol receiverType,
		TypeSymbol payloadType,
		TypeSymbol memberType,
		UnionTypeSymbol resultOptionType,
		OptionalChainMemberKind memberKind,
		FunctionSymbol? method,
		DelegateTypeSymbol? delegateType,
		bool lifts)
		=> new(syntax, optionType, receiverType, payloadType, memberType, resultOptionType, memberKind, method, delegateType, lifts, OptionalChainBindingErrorKind.UnresolvedOperand);

	public static ResolvedOptionalChainExpression Error(
		OptionalChainExpressionSyntax syntax,
		OptionalChainBindingErrorKind errorKind,
		TypeSymbol? receiverType,
		TypeSymbol? payloadType)
		=> new(syntax, null, receiverType, payloadType, null, null, OptionalChainMemberKind.Field, null, null, lifts: false, errorKind);
}
