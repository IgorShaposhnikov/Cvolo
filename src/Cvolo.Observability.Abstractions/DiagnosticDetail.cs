namespace Cvolo.Observability.Abstractions;

/// <summary>
/// Instrumentation depth (§31), independent of source-consent axis:
/// L0 = boundary (all public entry points / pipeline transitions),
/// L1 = phase (parser, binder passes, resolution, indexes, feature execution),
/// L2 = targeted forensic (bounded, TTL-limited inner validators).
/// </summary>
public enum DiagnosticDetail
{
	L0 = 0,
	L1 = 1,
	L2 = 2,
}
