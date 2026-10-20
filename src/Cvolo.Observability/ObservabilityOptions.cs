using Cvolo.Observability.Abstractions;

namespace Cvolo.Observability;

/// <summary>
/// Runtime configuration for the observer/writer (§3 sizes: 32 MiB per file / 256 MiB total,
/// 180 s repro duration default). All limits are enforced by the writer; producers never block.
/// </summary>
public sealed class ObservabilityOptions
{
	public const long DefaultMaxFileSizeBytes = 32L * 1024 * 1024;
	public const long DefaultMaxTotalSizeBytes = 256L * 1024 * 1024;

	/// <summary>Initial recording mode (§3). <see cref="ObservabilityMode.Off"/> performs no work at all.</summary>
	public ObservabilityMode Mode { get; set; } = ObservabilityMode.Off;

	/// <summary>Emitter name written into the envelope (§4).</summary>
	public string Emitter { get; set; } = "tooling";

	/// <summary>Bounded queue capacity in events (§14). Full queue drops verbose-first and counts every loss.</summary>
	public int QueueCapacity { get; set; } = 16_384;

	/// <summary>Maximum events written per drain batch (§14).</summary>
	public int WriteBatchSize { get; set; } = 256;

	/// <summary>Background drain interval (§14 batch writes).</summary>
	public TimeSpan FlushInterval { get; set; } = TimeSpan.FromMilliseconds(200);

	/// <summary>Best-effort deadline for flushing remaining events on shutdown (§IO-004). Never a durability promise.</summary>
	public TimeSpan ShutdownFlushDeadline { get; set; } = TimeSpan.FromSeconds(2);

	/// <summary>Per-file rotation size (§3, §IO-003).</summary>
	public long MaxFileSizeBytes { get; set; } = DefaultMaxFileSizeBytes;

	/// <summary>Total retained size across rotated files (§3, §IO-003).</summary>
	public long MaxTotalSizeBytes { get; set; } = DefaultMaxTotalSizeBytes;

	/// <summary>Maximum rotated files kept per stream (retention, §IO-003).</summary>
	public int MaxRotatedFiles { get; set; } = 16;

	/// <summary>JSONL output file; null = in-memory/collector-only mode (§IO-013).</summary>
	public string? OutputPath { get; set; }

	/// <summary>Optional session id supplied by the client (§4: shared sessionId is passed client→server).</summary>
	public string? SessionId { get; set; }

	/// <summary>Optional server instance id supplied by the host process.</summary>
	public string? ServerInstanceId { get; set; }

	/// <summary>
	/// Maps the mode to the maximum recorded detail level (§31):
	/// basic→L0, detailed→L0+L1, repro→L0+L1+L2. Independent of source consent.
	/// </summary>
	public static DiagnosticDetail MaxDetailFor(ObservabilityMode mode) => mode switch
	{
		ObservabilityMode.Basic => DiagnosticDetail.L0,
		ObservabilityMode.Detailed => DiagnosticDetail.L1,
		ObservabilityMode.Repro => DiagnosticDetail.L2,
		_ => DiagnosticDetail.L0,
	};
}
