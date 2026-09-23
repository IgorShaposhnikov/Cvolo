using Cvolo.Analysis.Symbols;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.AST.Expressions;

namespace Cvolo.Analysis.Resolution;

/// <summary>
/// Resolves callable targets that are independent of validation traversal: ordinary overloads,
/// synthesized enum helpers, and invocation of delegate-typed values.
/// </summary>
/// <remarks>
/// Interface/protocol conformance, generic instantiation, target-typed lambda validation, and
/// diagnostic traversal remain outside this service and are intentionally extracted separately.
/// </remarks>
/// <remarks>
/// Creates a callable-target resolver over the shared binding state and overload service.
/// </remarks>
/// <param name="context">Binding context that owns semantic symbols and call-resolution tables.</param>
/// <param name="overloads">Shared overload resolver used for ordinary function selection.</param>
internal sealed class CallResolver(BindingContext context, OverloadResolver overloads)
{

	/// <summary>
	/// Resolves a non-generic ordinary call, preferring synthesized enum helpers before the standard
	/// overload set exactly as the legacy validation path did.
	/// </summary>
	public FunctionSymbol? ResolveOrdinaryCall(CallExpressionSyntax call, IReadOnlyList<TypeSymbol> argumentTypes, SymbolTable scope)
	{
		return TryResolveEnumName(call, argumentTypes, scope)
			?? TryResolveFlagsHasFlag(call, argumentTypes, scope)
			?? overloads.Resolve(call.FunctionName, argumentTypes, scope);
	}

	/// <summary>
	/// Returns whether an identifier denotes a local, parameter, global, or implicit receiver value
	/// rather than a function group.
	/// </summary>
	public bool IsKnownVariable(IdentifierExpressionSyntax identifier, SymbolTable scope)
	{
		if (scope.Lookup(identifier.Name) is VariableSymbol)
			return true;
		if (context.ResolveGlobalReference(identifier.Name, out _) is VariableSymbol)
			return true;
		return identifier.Name is "self" or "this";
	}

	/// <summary>
	/// Resolves invocation of a delegate-typed variable or delegate-typed struct/union field.
	/// </summary>
	/// <remarks>
	/// Arity diagnostics are preserved here because they are intrinsic to resolving the delegate
	/// callable shape. Argument compatibility and target typing remain with validation.
	/// </remarks>
	public bool TryResolveDelegateInvocation(CallExpressionSyntax call, SymbolTable scope, out DelegateTypeSymbol? delegateType)
	{
		delegateType = null;
		TypeSymbol? delegateMemberType = null;

		// A fully-qualified global native/safe delegate (e.g. Native.Callback(...)) is
		// itself the callable value. Resolve that exact value path before interpreting the
		// final segment as a struct-field invocation.
		if (context.ResolveGlobalReference(call.FunctionName, out _) is VariableSymbol qualifiedGlobal
			&& qualifiedGlobal.Type is DelegateTypeSymbol qualifiedDelegate)
		{
			delegateMemberType = qualifiedDelegate;
		}
		else if (call.FunctionName.Contains('.'))
		{
			var lastDot = call.FunctionName.LastIndexOf('.');
			var receiverName = call.FunctionName[..lastDot];
			var memberName = call.FunctionName[(lastDot + 1)..];

			var receiver = scope.Lookup(receiverName) as VariableSymbol
				?? context.ResolveGlobalReference(receiverName, out _) as VariableSymbol;
			if (receiver is null)
				return false;

			var receiverType = receiver.Type;
			if (receiverType is PointerTypeSymbol pointer)
				receiverType = pointer.ReferencedType;

			delegateMemberType = receiverType switch
			{
				StructTypeSymbol structType => structType.FindField(memberName)?.Type,
				UnionTypeSymbol unionType => unionType.FindField(memberName)?.Type,
				_ => null,
			};
		}
		else
		{
			var variable = scope.Lookup(call.FunctionName) as VariableSymbol
				?? context.ResolveGlobalReference(call.FunctionName, out _) as VariableSymbol;
			if (variable is null)
				return false;
			delegateMemberType = variable.Type;
		}

		if (delegateMemberType is not DelegateTypeSymbol callableDelegate)
			return false;

		delegateType = callableDelegate;
		if (call.Arguments.Count != callableDelegate.Parameters.Count)
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(
				currentFileContext,
				call.ArgumentListSpan,
				$"Delegate '{callableDelegate.Name}' expects {callableDelegate.Parameters.Count} argument(s) but received {call.Arguments.Count}");
		}

		return true;
	}

	/// <summary>
	/// Resolves the synthesized zero-argument <c>Enum.Name()</c> helper and reports invalid arity.
	/// </summary>
	private FunctionSymbol? TryResolveEnumName(CallExpressionSyntax call, IReadOnlyList<TypeSymbol> argumentTypes, SymbolTable scope)
	{
		if (!call.FunctionName.Contains('.') || !call.FunctionName.EndsWith(".Name", StringComparison.Ordinal))
			return null;

		var receiverName = call.FunctionName[..call.FunctionName.IndexOf('.')];
		if (scope.Lookup(receiverName) is not VariableSymbol receiverSymbol)
			return null;

		var receiverType = receiverSymbol.Type;
		if (receiverType is PointerTypeSymbol receiverPointer)
			receiverType = receiverPointer.ReferencedType;
		if (receiverType is not EnumTypeSymbol enumType)
			return null;

		if (argumentTypes.Count != 0)
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, call.Span, "Name expects no arguments.");
		}

		return new FunctionSymbol(
			$"$Name${receiverName}",
			TypeSymbol.String,
			[new ParameterSymbol("this", new PointerTypeSymbol(enumType, isMutable: false))]);
	}

	/// <summary>
	/// Resolves the synthesized <c>[Flags] Enum.HasFlag()</c> helper and reports invalid argument shape.
	/// </summary>
	private FunctionSymbol? TryResolveFlagsHasFlag(CallExpressionSyntax call, IReadOnlyList<TypeSymbol> argumentTypes, SymbolTable scope)
	{
		if (!call.FunctionName.Contains('.') || !call.FunctionName.EndsWith(".HasFlag", StringComparison.Ordinal))
			return null;

		var receiverName = call.FunctionName[..call.FunctionName.IndexOf('.')];
		if (scope.Lookup(receiverName) is not VariableSymbol receiverSymbol)
			return null;

		var receiverType = receiverSymbol.Type;
		if (receiverType is PointerTypeSymbol receiverPointer)
			receiverType = receiverPointer.ReferencedType;
		if (receiverType is not EnumTypeSymbol { IsFlags: true } flagsEnum)
			return null;

		if (argumentTypes.Count != 1 || argumentTypes[0] != flagsEnum)
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(
				currentFileContext,
				call.Span,
				$"HasFlag expects exactly one argument of the same [Flags] enum type '{flagsEnum.Name}'.");
		}

		var receiverParameter = new ParameterSymbol("this", new PointerTypeSymbol(flagsEnum, isMutable: false));
		var flagParameter = new ParameterSymbol("flag", flagsEnum);
		return new FunctionSymbol($"$HasFlag${flagsEnum.Name}", TypeSymbol.Bool, [receiverParameter, flagParameter]);
	}
}
