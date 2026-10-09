using Cvolo.Analysis.Symbols;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.AST.Expressions;

using Cvolo.Core.Diagnostics;
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
		=> ResolveOrdinaryCall(call, argumentTypes, scope, out _, out _, out _);

	/// <summary>
	/// Resolves a non-generic ordinary call and reports any dotted-call form that disagreed with the
	/// declared callable kind, so the caller can emit the associated/instance-specific diagnostic.
	/// </summary>
	public FunctionSymbol? ResolveOrdinaryCall(
		CallExpressionSyntax call,
		IReadOnlyList<TypeSymbol> argumentTypes,
		SymbolTable scope,
		out DottedCallMismatch mismatch,
		out string? mismatchOwnerName)
		=> ResolveOrdinaryCall(call, argumentTypes, scope, out mismatch, out mismatchOwnerName, out _);

	/// <summary>
	/// Resolves a non-generic ordinary call and additionally reports whether two or more distinct
	/// applicable overloads were equally best, so the caller can report an ambiguity rather than a
	/// generic no-overload mismatch.
	/// </summary>
	public FunctionSymbol? ResolveOrdinaryCall(
		CallExpressionSyntax call,
		IReadOnlyList<TypeSymbol> argumentTypes,
		SymbolTable scope,
		out DottedCallMismatch mismatch,
		out string? mismatchOwnerName,
		out bool ambiguous)
	{
		mismatch = DottedCallMismatch.None;
		mismatchOwnerName = null;
		var synthesized = TryResolveEnumName(call, argumentTypes, scope)
			?? TryResolveFlagsHasFlag(call, argumentTypes, scope);
		if (synthesized is not null)
		{
			ambiguous = false;
			return synthesized;
		}

		return overloads.Resolve(call.FunctionName, argumentTypes, scope, call, out mismatch, out mismatchOwnerName, out ambiguous);
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
	/// Resolves invocation of a delegate-typed variable or delegate-typed struct/union field,
	/// including the synthesized <c>Delegate.Invoke</c> member.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Arity diagnostics are preserved here because they are intrinsic to resolving the delegate
	/// callable shape. Argument compatibility and target typing remain with validation.
	/// </para>
	/// <para>
	/// The written callee is first interpreted as a complete delegate-valued path, so a real member
	/// spelled <c>Invoke</c> still wins. Only when that fails, and only when the path ends in
	/// <c>.Invoke</c>, does the trailing segment bind to the synthesized member of a delegate-typed
	/// receiver, which makes <c>handler.Invoke(42)</c> and <c>handler(42)</c> equivalent.
	/// </para>
	/// </remarks>
	public bool TryResolveDelegateInvocation(CallExpressionSyntax call, SymbolTable scope, out DelegateTypeSymbol? delegateType)
	{
		delegateType = null;
		if (!TryResolveDelegateTarget(call.FunctionName, scope, out var targetPath, out var delegateMemberType))
			return false;

		delegateType = delegateMemberType;
		context.ResolvedDelegateTargets[call] = targetPath;
		if (call.Arguments.Count != delegateMemberType!.Parameters.Count)
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(
				currentFileContext,
				call.ArgumentListSpan,
				$"Delegate '{delegateMemberType.Name}' expects {delegateMemberType.Parameters.Count} argument(s) but received {call.Arguments.Count}", DiagnosticIds.DelegateExpectsArgumentSButReceived);
		}

		return true;
	}

	/// <summary>
	/// Resolves a dotted callee to the value path that holds a delegate value.
	/// </summary>
	/// <remarks>
	/// Every segment of a nested path such as <c>state.Dispatch.Callback</c> is walked in order, and the
	/// fully resolved path is returned so the emitter can locate the same storage slot.
	/// </remarks>
	private bool TryResolveDelegateTarget(
		string calleePath,
		SymbolTable scope,
		out string targetPath,
		out DelegateTypeSymbol? delegateType)
	{
		delegateType = null;
		targetPath = string.Empty;

		// A fully-qualified global native/safe delegate (e.g. Native.Callback(...)) is
		// itself the callable value. Resolve that exact value path before interpreting the
		// final segment as a struct-field invocation. The call-site scope is handed down so a
		// local named like a namespace alias keeps value-path precedence over alias expansion.
		if (context.ResolveGlobalReference(calleePath, scope, out _) is VariableSymbol qualifiedGlobal
			&& qualifiedGlobal.Type is DelegateTypeSymbol qualifiedDelegate)
		{
			targetPath = calleePath;
			delegateType = qualifiedDelegate;
			return true;
		}

		var segments = calleePath.Split('.');
		if (segments.Any(string.IsNullOrEmpty))
			return false;

		var first = segments[0];
		var owner = scope.Lookup(first) as VariableSymbol
			?? context.ResolveGlobalReference(first, out _) as VariableSymbol;
		if (owner is null)
			return false;

		var lastIndex = segments.Length - 1;
		if (lastIndex == 0)
		{
			// A bare `handler(...)` names the delegate value itself.
			if (Dereference(owner.Type) is not DelegateTypeSymbol bareDelegate)
				return false;

			targetPath = first;
			delegateType = bareDelegate;
			return true;
		}

		var currentType = Dereference(owner.Type);
		var currentPath = first;

		for (var i = 1; i <= lastIndex; i++)
		{
			var isFinalSegment = i == lastIndex;
			var isSynthesizedInvoke = isFinalSegment && segments[i] == "Invoke";
			TypeSymbol? nextType = isSynthesizedInvoke
				? SynthesizedInvokeMemberType(currentType)
				: MemberType(currentType, segments[i]);

			if (nextType is null)
				return false;

			if (isFinalSegment)
			{
				if (nextType is not DelegateTypeSymbol stepDelegate)
					return false;

				// A real member contributes its own storage segment, while the synthesized `Invoke`
				// member has no storage of its own: the delegate lives at the receiver path.
				targetPath = isSynthesizedInvoke ? currentPath : $"{currentPath}.{segments[i]}";
				delegateType = stepDelegate;
				return true;
			}

			// A non-final segment must be an aggregate to keep walking the path.
			currentType = Dereference(nextType);
			currentPath = $"{currentPath}.{segments[i]}";
		}

		return false;
	}

	/// <summary>
	/// Returns the type of a named member of an aggregate, or <see langword="null"/> when absent.
	/// </summary>
	private static TypeSymbol? MemberType(TypeSymbol ownerType, string memberName)
		=> ownerType switch
		{
			StructTypeSymbol structType => structType.FindField(memberName)?.Type,
			UnionTypeSymbol unionType => unionType.FindField(memberName)?.Type,
			_ => null,
		};

	/// <summary>
	/// Returns the delegate type of the synthesized <c>Invoke</c> member, which exists on every
	/// delegate type and carries that delegate's own signature.
	/// </summary>
	private static DelegateTypeSymbol? SynthesizedInvokeMemberType(TypeSymbol ownerType)
		=> ownerType as DelegateTypeSymbol;

	/// <summary>
	/// Strips an optional <c>ref</c>/<c>refvar</c> pointer wrapper so members are looked up on the
	/// aggregate a pointer refers to.
	/// </summary>
	private static TypeSymbol Dereference(TypeSymbol type)
		=> type is PointerTypeSymbol pointer ? pointer.ReferencedType : type;

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
			context.Diagnostics.Report(currentFileContext, call.Span, "Name expects no arguments.", DiagnosticIds.NameExpectsNoArguments);
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
				$"HasFlag expects exactly one argument of the same [Flags] enum type '{flagsEnum.Name}'.", DiagnosticIds.HasFlagExpectsExactlyOneArgumentOfTheSameFlagsEnumType);
		}

		var receiverParameter = new ParameterSymbol("this", new PointerTypeSymbol(flagsEnum, isMutable: false));
		var flagParameter = new ParameterSymbol("flag", flagsEnum);
		return new FunctionSymbol($"$HasFlag${flagsEnum.Name}", TypeSymbol.Bool, [receiverParameter, flagParameter]);
	}
}
