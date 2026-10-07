using Cvolo.Analysis.Symbols.Base;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Expressions;

namespace Cvolo.Analysis.Ownership;

/// <summary>
/// Flow-sensitive ownership availability of a single owned projection at one point in the
/// canonical control-flow graph.
/// </summary>
public enum OwnershipState
{
	/// <summary>Declared but never assigned an owned value; nothing to destroy.</summary>
	Uninitialized,

	/// <summary>Holds an owned resource-move value that must be destroyed on this path.</summary>
	Live,

	/// <summary>Ownership was transferred out; the storage must not be destroyed.</summary>
	Moved,

	/// <summary>Already destroyed on this path; must not be destroyed again.</summary>
	Disposed,

	/// <summary>An aggregate whose owned children are a mix of live and unavailable.</summary>
	Partial
}

/// <summary>
/// The ownership state of an assignment destination as bound before code generation.
/// </summary>
public enum DestinationOwnershipState
{
	/// <summary>Nothing live at the destination (uninitialized, moved, or disposed).</summary>
	Unavailable,

	/// <summary>The whole destination holds a live owned value.</summary>
	WholeLive,

	/// <summary>The destination aggregate has some live owned children and some unavailable.</summary>
	Partial,

	/// <summary>Liveness depends on the runtime path reaching this operation.</summary>
	Conditional
}

/// <summary>
/// Exactly what may be inspected and destroyed at a destination before installing a new value.
/// </summary>
public enum DestinationCleanupPlan
{
	/// <summary>Destroy nothing.</summary>
	None,

	/// <summary>Destroy the whole live value at the destination.</summary>
	WholeLive,

	/// <summary>Destroy only the live owned projections of a partially moved aggregate.</summary>
	ProjectionAware,

	/// <summary>Conditionally destroy live projections on the runtime path.</summary>
	ConditionalProjectionAware,

	/// <summary>Inspect the active union tag and destroy the active payload if it owns resources.</summary>
	TagAwareUnionCleanup
}

/// <summary>How a replacement value is installed at the destination.</summary>
public enum ReplacementKind
{
	/// <summary>Ordinary store/construct replacement.</summary>
	Ordinary,

	/// <summary>Whole-root safe-union variant replacement (publishes the tag).</summary>
	TagAwareUnionReplace
}

/// <summary>The ownership action applied to the assignment source.</summary>
public enum SourceOwnershipAction
{
	/// <summary>The source value is copied; ownership stays with the source.</summary>
	Copy,

	/// <summary>The source value is moved; the source becomes unavailable.</summary>
	Move
}

/// <summary>
/// A dynamic destination region: every owned element a runtime index might select inside a
/// statically-identifiable array/aggregate root. The runtime selected element is not a single
/// stable identity, so ownership legality for a write is decided against every candidate.
/// </summary>
public sealed class DynamicDestinationRegion(MovePath arrayPath, ExpressionSyntax indexExpression, int elementCount)
{
	/// <summary>The stable path of the containing aggregate root.</summary>
	public MovePath ArrayPath { get; } = arrayPath;

	/// <summary>The runtime index expression whose selected element is unknown statically.</summary>
	public ExpressionSyntax IndexExpression { get; } = indexExpression;

	/// <summary>The number of candidate elements, or <c>-1</c> when not statically known.</summary>
	public int ElementCount { get; } = elementCount;

	/// <summary>Enumerates the candidate fixed-index element paths of the region.</summary>
	public IEnumerable<MovePath> EnumerateCandidatePaths()
	{
		if (ElementCount < 0)
			yield break;

		for (var i = 0; i < ElementCount; i++)
			yield return ArrayPath.Append(ProjectionStep.FixedIndex(i));
	}
}

/// <summary>
/// The semantic ownership decision for one assignment, bound before code generation. The
/// emitter consumes this record and must not rediscover ownership legality or overlap.
/// </summary>
public sealed record ResolvedAssignmentOperation(
	ExpressionSyntax Destination,
	TypeSymbol DestinationType,
	MovePath? DestinationMovePath,
	DynamicDestinationRegion? DestinationOwnershipRegion,
	DestinationOwnershipState DestinationOwnershipState,
	DestinationCleanupPlan DestinationCleanupPlan,
	ReplacementKind ReplacementKind,
	SourceOwnershipAction SourceOwnershipAction,
	MovePath? SourceMovePath,
	MovePathRelation MovePathRelation);
