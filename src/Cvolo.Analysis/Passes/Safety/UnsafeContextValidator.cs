using Cvolo.Analysis.Symbols;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Expressions;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Analysis.Passes.Safety;

/// <summary>
/// Owns the active safety-tier stack and diagnostics for operations that require an
/// <c>unsafe</c> context. The validator deliberately does not own borrow, move, lifetime,
/// unbound, or safe-delegate semantics.
/// </summary>
internal sealed class UnsafeContextValidator(BindingContext context)
{
	private readonly Stack<SafetyTier> _tiers = [];

	/// <summary>Returns the active safety tier, defaulting to safe when no frame is present.</summary>
	public SafetyTier CurrentTier => _tiers.Count > 0 ? _tiers.Peek() : SafetyTier.Safe;

	/// <summary>
	/// Returns whether an unbound frame exists anywhere in the active tier stack, including below
	/// nested unsafe or temporary safe frames.
	/// </summary>
	public bool IsInsideUnbound => _tiers.Contains(SafetyTier.Unbound);

	/// <summary>Replaces all tier state with the entry tier of a new function body.</summary>
	public void Reset(SafetyTier tier)
	{
		_tiers.Clear();
		_tiers.Push(tier);
	}

	/// <summary>Pushes a nested safety tier.</summary>
	public void Push(SafetyTier tier) => _tiers.Push(tier);

	/// <summary>Pops the current tier frame.</summary>
	public void Pop() => _tiers.Pop();

	/// <summary>
	/// Pops and returns the current tier, or returns <see cref="SafetyTier.Safe"/> when the stack is
	/// empty. This preserves the previous lambda-body tier transition behavior exactly.
	/// </summary>
	public SafetyTier PopOrSafe() => _tiers.Count > 0 ? _tiers.Pop() : SafetyTier.Safe;

	/// <summary>Reports CVL1005-style rejection of raw-pointer locals outside unsafe code.</summary>
	public void ValidateRawPointerDeclaration(VariableSymbol symbol, TextSpan span)
	{
		if (symbol.Type is not RawPointerTypeSymbol || CurrentTier == SafetyTier.Unsafe)
			return;

		context.Diagnostics.Report(context.CurrentUnit!.Context, span,
			"Raw pointer variables cannot be declared outside unsafe context.");
	}

	/// <summary>Rejects the null literal outside unsafe code, preserving CVL1104 behavior.</summary>
	public void ValidateNullLiteral(TextSpan span)
	{
		if (CurrentTier == SafetyTier.Unsafe)
			return;

		context.Diagnostics.Report(context.CurrentUnit!.Context, span,
			"null is not allowed in safe code. Use Option.None instead.",
			DiagnosticIds.NullForOptionalType);
	}

	/// <summary>
	/// Rejects raw dereference and address-of operators outside unsafe code while preserving the
	/// existing CVL1006/CVL1007 diagnostic text.
	/// </summary>
	public void ValidateUnaryOperation(UnaryExpressionSyntax unary)
	{
		if (CurrentTier == SafetyTier.Unsafe)
			return;

		if (unary.Operator == "*")
		{
			context.Diagnostics.Report(context.CurrentUnit!.Context, unary.Span,
				"Cannot dereference outside unsafe context.");
		}
		else if (unary.Operator == "&")
		{
			context.Diagnostics.Report(context.CurrentUnit!.Context, unary.Span,
				"Cannot take address outside unsafe context.");
		}
	}
}
