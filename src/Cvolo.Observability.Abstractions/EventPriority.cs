namespace Cvolo.Observability.Abstractions;

/// <summary>
/// Queue priority class (§25). On overflow the queue drops verbose (P2) first,
/// then P1; P0 is evicted last and only to make room for another P0.
/// </summary>
public enum EventPriority
{
	/// <summary>Causality-critical, small-volume: session/recording transitions, request terminal outcomes, loss markers.</summary>
	P0 = 0,

	/// <summary>Normal operation/phase events and decision provenance.</summary>
	P1 = 1,

	/// <summary>Verbose/detailed diagnostics safe to lose under pressure; loss must still be counted.</summary>
	P2 = 2,
}
