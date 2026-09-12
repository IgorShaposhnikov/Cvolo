using Cvolo.Core.Diagnostics.Serialization;

namespace Cvolo.Core.Diagnostics.Reporters;

/// <summary>
/// JSON reporter: emits a single machine-readable envelope on stdout per
/// reported batch. Suitable for LSP clients, CI tooling, and editor plugins
/// that consume structured diagnostics. No ANSI, no banners, no verbose
/// chatter — stdout is claimed exclusively.
/// </summary>
public sealed class JsonDiagnosticReporter : IDiagnosticReporter
{
	public bool Exclusive => true;

	public void ReportErrors(IReadOnlyList<Diagnostic> diagnostics, string headerWhenNoId)
		=> Emit(diagnostics, success: false);

	public void ReportWarnings(IReadOnlyList<Diagnostic> diagnostics, HashSet<string>? suppressedIds)
	{
		var filtered = diagnostics
			.Where(d => d.Id is null || suppressedIds?.Contains(d.Id) != true)
			.ToList();
		Emit(filtered, success: true);
	}

	public void ReportSynthetic(string? id, string severity, string message, string file)
	{
		var entry = DiagnosticMapper.Simple(id, severity, message, file);
		Console.Out.WriteLine(
			DiagnosticSerializer.ToJson([entry], success: false));
	}

	public void ReportCheckSuccess()
		=> Console.Out.WriteLine(
			DiagnosticSerializer.ToJson([], success: true));

	private static void Emit(IEnumerable<Diagnostic> diagnostics, bool success)
		=> Console.Out.WriteLine(
			DiagnosticSerializer.ToJson(diagnostics, fallbackFile: "", success: success));
}
