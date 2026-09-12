namespace Cvolo.Core.Diagnostics.Reporters;

/// <summary>
/// Abstracts how compiler diagnostics reach the outside world. The pipeline
/// is unaware of format, colors, or transport — it only calls into a reporter.
/// Each concrete reporter owns its own convention (ANSI to stderr, flat lines
/// to stdout, and in the future a JSON envelope for an LSP server).
/// </summary>
public interface IDiagnosticReporter
{
	/// <summary>
	/// When true the reporter claims stdout/stderr entirely; the compiler
	/// driver must not write any other text (verbose chatter, banners).
	/// </summary>
	bool Exclusive { get; }

	/// <summary>Reports hard errors. <paramref name="headerWhenNoId"/> is the fallback label used when a diagnostic has no id.</summary>
	void ReportErrors(IReadOnlyList<Diagnostic> diagnostics, string headerWhenNoId);

	/// <summary>Reports warnings. <paramref name="suppressedIds"/> is applied before the reporter's own policy.</summary>
	void ReportWarnings(IReadOnlyList<Diagnostic> diagnostics, HashSet<string>? suppressedIds);

	/// <summary>Reports a diagnostic with no source span (project load, missing entry point, rewriter crash).</summary>
	void ReportSynthetic(string? id, string severity, string message, string file);

	/// <summary>Notifies the reporter that <c>check</c> finished successfully.</summary>
	void ReportCheckSuccess();
}
