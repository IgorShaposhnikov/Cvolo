namespace Cvolo.Observability.Abstractions;

/// <summary>
/// User-facing recording mode (§3). Modes are switchable at runtime without restart.
/// Mapping to <see cref="DiagnosticDetail"/> is applied by the runtime observer:
/// basic → L0, detailed → L0+L1, repro → L0+L1+L2 (forensic depth is an independent axis
/// from source capture consent, §31).
/// </summary>
public enum ObservabilityMode
{
	/// <summary>No events, no queues, no extra computation (§3, §39).</summary>
	Off = 0,

	/// <summary>Structural boundary events without source content (§3, §25).</summary>
	Basic = 1,

	/// <summary>Phase timings and decision provenance, no source content (§3, §7).</summary>
	Detailed = 2,

	/// <summary>Detailed + targeted forensic depth; source capture still needs separate consent (§3, §31).</summary>
	Repro = 3,
}
