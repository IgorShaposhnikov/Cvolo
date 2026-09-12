using System.Text.Json.Serialization;

namespace Cvolo.Core.Diagnostics.Serialization;

/// <summary>
/// Machine-readable diagnostic shape consumed by tooling (LSP, CI, IDEs).
/// Positions follow the LSP convention: 0-based line, 0-based character.
/// </summary>
public sealed class DiagnosticEntry
{
	[JsonPropertyName("id")] public string? Id { get; init; }
	[JsonPropertyName("severity")] public string Severity { get; init; } = "error";
	[JsonPropertyName("message")] public string Message { get; init; } = "";
	[JsonPropertyName("file")] public string File { get; init; } = "";
	[JsonPropertyName("range")] public DiagnosticRange Range { get; init; } = new();
}

public sealed class DiagnosticRange
{
	[JsonPropertyName("start")] public DiagnosticPosition Start { get; init; } = new();
	[JsonPropertyName("end")] public DiagnosticPosition End { get; init; } = new();
}

public sealed class DiagnosticPosition
{
	[JsonPropertyName("line")] public int Line { get; init; }
	[JsonPropertyName("character")] public int Character { get; init; }
}

/// <summary>Envelope emitted on stdout for `cvolo check --format json`.</summary>
public sealed class DiagnosticReport
{
	[JsonPropertyName("success")] public bool Success { get; init; }
	[JsonPropertyName("diagnostics")] public IReadOnlyList<DiagnosticEntry> Diagnostics { get; init; } = [];
}
