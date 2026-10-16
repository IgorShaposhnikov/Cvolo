namespace Cvolo.Analysis;

/// <summary>
/// Lifecycle state of a monomorphized specialization.
/// <para>
/// <c>Absent</c> means no entry exists. A specialization is registered as <c>Pending</c>
/// before its body is validated, promoted to <c>Ready</c> when validation completes with no
/// new errors, and marked <c>Rejected</c> when validation produced new errors. A rejected
/// entry is retained so the failure is reported once, but it must never be emitted or treated
/// as a usable definition. A previously <c>Ready</c> entry that stops being valid becomes
/// <c>Invalidated</c>.
/// </para>
/// </summary>
public enum MonomorphizationState
{
	Absent = 0,
	Pending = 1,
	Ready = 2,
	Rejected = 3,
	Invalidated = 4,
}
