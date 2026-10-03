using Cvolo.Analysis.Symbols.Base;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Analysis.Passes.Validation;

/// <summary>
/// Owns mutable state shared by validators during the single validation traversal.
/// </summary>
/// <remarks>
/// This context contains only traversal/function state. Semantic declarations, resolved symbols,
/// diagnostics, and compilation-wide registries remain owned by <see cref="BindingContext"/>.
/// Keeping these lifetimes separate lets specialized validators participate in one validation pass
/// without turning <c>ValidationPass</c> into the owner of every mutable subsystem.
/// </remarks>
internal sealed class ValidationContext
{
	/// <summary>
	/// Compilation units participating in the current validation run.
	/// </summary>
	public IReadOnlyList<CompilationUnitSyntax> Units { get; set; } = [];
	/// <summary>
	/// Current nesting depth of unsafe validation regions. A value greater than zero means unsafe-only
	/// operations are permitted at the current source location.
	/// </summary>
	public int UnsafeDepth { get; set; }
	/// <summary>
	/// Indicates that the currently validated function body is operating under unbound semantics.
	/// </summary>
	public bool InUnbound { get; set; }
	/// <summary>
	/// Delegate return type expected while validating a lambda block body. When null, return
	/// statements are validated against the enclosing function instead.
	/// </summary>
	public TypeSymbol? LambdaReturnType { get; set; }
	/// <summary>
	/// Function whose body currently owns the validation traversal. Lambda bodies reuse this function
	/// for statement-level context while applying their own return-type target through
	/// <see cref="LambdaReturnType"/>.
	/// </summary>
	public FunctionDeclarationSyntax? EnclosingFunction { get; set; }
	/// <summary>
	/// Lexical label sets for active block scopes, used to detect duplicate labels in the appropriate
	/// enclosing scope.
	/// </summary>
	public Stack<HashSet<string>> LabelScopes { get; } = new();
	/// <summary>
	/// Active loop labels ordered from the innermost loop outward. Null entries represent unlabeled
	/// loops while still contributing to loop-depth validation.
	/// </summary>
	public Stack<string?> LoopLabels { get; } = [];
	/// <summary>
	/// Active labeled blocks that can be targeted by labeled <c>break</c> statements.
	/// </summary>
	public Stack<string?> BlockLabels { get; } = [];
	/// <summary>
	/// Read-only foreach item names visible in the active body chain, used to issue the dedicated
	/// assignment diagnostic for non-mutable iteration bindings.
	/// </summary>
	public Stack<HashSet<string>> ReadOnlyForeachItems { get; } = new();
	/// <summary>
	/// Number of active switch case-body frames. This permits an unlabeled <c>break</c> inside a
	/// switch even when no loop is active, while <c>continue</c> remains loop-only.
	/// </summary>
	public int SwitchDepth { get; set; }
	/// <summary>
	/// Instance field names of the extended type while an associated (receiverless) extension body is
	/// being validated, otherwise null. An unresolved identifier that names one of these fields is an
	/// attempt to touch instance state without a receiver and is reported specifically.
	/// </summary>
	public IReadOnlySet<string>? AssociatedInstanceFields { get; set; }

	/// <summary>
	/// Semantic callable body whose operations are currently being validated. This is the
	/// <see cref="FunctionDeclarationSyntax"/> for ordinary/extension/constructor bodies and the
	/// <see cref="Cvolo.Core.AST.Expressions.LambdaExpressionSyntax"/> for lambda bodies, so unsafe
	/// usage is attributed to the body that actually owns the operation rather than a syntactically
	/// enclosing function.
	/// </summary>
	public object? CurrentCallableBody { get; set; }

	/// <summary>
	/// Canonical record of unsafe usages observed in each semantic callable body, keyed by
	/// <see cref="CurrentCallableBody"/>. Populated by <see cref="RequireUnsafe"/> during validation
	/// and consumed by the CVL1001 effectiveness check after the body has been fully validated.
	/// </summary>
	public Dictionary<object, HashSet<UnsafeUsageKind>> UnsafeUsagesByCallableBody { get; } = new();

	/// <summary>
	/// Records one unsafe usage for the current callable body. The usage is recorded regardless of
	/// whether unsafe capability is currently available: being inside an unsafe context suppresses
	/// the error, not the fact that an unsafe operation occurred.
	/// </summary>
	public void RecordUnsafeUsage(UnsafeUsageKind kind)
	{
		if (CurrentCallableBody is not { } body)
			return;

		if (!UnsafeUsagesByCallableBody.TryGetValue(body, out var usages))
			UnsafeUsagesByCallableBody[body] = usages = [];

		usages.Add(kind);
	}

	/// <summary>
	/// Single authority for operations that require unsafe capability. It records the usage for the
	/// current callable body and reports the operation-specific diagnostic only when no unsafe
	/// capability is available. Validator sites must route new unsafe operations through here so the
	/// CVL1001 effectiveness check automatically sees them.
	/// </summary>
	public void RequireUnsafe(
		BindingContext context,
		SyntaxNode syntax,
		UnsafeUsageKind kind,
		string message,
		string diagnosticId)
	{
		RecordUnsafeUsage(kind);

		if (UnsafeDepth > 0)
			return;

		context.Diagnostics.Report(context.FileContexts[context.CurrentUnit!], syntax.Span, message, diagnosticId);
	}

	/// <summary>Returns whether any unsafe usage was recorded for the given callable body.</summary>
	public bool HasUnsafeUsage(object callableBody) =>
		UnsafeUsagesByCallableBody.TryGetValue(callableBody, out var usages) && usages.Count > 0;
}
