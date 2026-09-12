using System.Text.Encodings.Web;
using System.Text.Json;

namespace Cvolo.Core.Diagnostics.Serialization;

/// <summary>
/// Canonical JSON serialization for compiler diagnostics. Two options:
/// compact (single-line, for LSP / line-oriented transports) and pretty
/// (indented, for CI logs / human inspection). Ordering of entries is
/// preserved — callers decide sorting.
/// </summary>
public static class DiagnosticSerializer
{
	private static readonly JsonSerializerOptions _compact = new()
	{
		WriteIndented = false,
		Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
	};

	private static readonly JsonSerializerOptions _pretty = new()
	{
		WriteIndented = true,
		Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
	};

	/// <summary>
	/// Serializes a report from already-projected entries.
	/// </summary>
	public static string ToJson(IReadOnlyList<DiagnosticEntry> entries, bool success, bool indented = false)
		=> JsonSerializer.Serialize(
			new DiagnosticReport { Success = success, Diagnostics = entries },
			indented ? _pretty : _compact);

	/// <summary>
	/// Convenience overload: projects the diagnostics and serializes them in one call.
	/// </summary>
	public static string ToJson(IEnumerable<Diagnostic> diagnostics, string fallbackFile, bool success, bool indented = false)
	{
		var entries = diagnostics
			.Select(d => DiagnosticMapper.ToEntry(d, fallbackFile))
			.ToList();

		return ToJson(entries, success, indented);
	}

	/// <summary>
	/// Convenience overload for the common <see cref="DiagnosticBag"/> case.
	/// </summary>
	public static string ToJson(DiagnosticBag bag, string fallbackFile, bool indented = false)
		=> ToJson(bag.Diagnostics, fallbackFile, success: !bag.HasErrors, indented: indented);
}
