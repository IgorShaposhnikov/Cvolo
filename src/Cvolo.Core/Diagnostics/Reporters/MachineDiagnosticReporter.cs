namespace Cvolo.Core.Diagnostics.Reporters;

/// <summary>
/// Machine-readable reporter for line-oriented adapters (efm-langserver with
/// the errorformat <c>%f:%l:%c: %m</c>).
///
/// Contract:
///   * one diagnostic per line, always on stdout;
///   * no ANSI, no banners, no verbose chatter;
///   * paths use forward slashes (editor / LSP friendly);
///   * positions are 1-based (vim's %l/%c convention).
/// </summary>
public sealed class MachineDiagnosticReporter : IDiagnosticReporter
{
	public bool Exclusive => true;

	public void ReportErrors(IReadOnlyList<Diagnostic> diagnostics, string headerWhenNoId)
		=> WriteLines(diagnostics);

	public void ReportWarnings(IReadOnlyList<Diagnostic> diagnostics, HashSet<string>? suppressedIds)
		=> WriteLines(diagnostics.Where(d => d.Id is null || suppressedIds?.Contains(d.Id) != true));

	public void ReportSynthetic(string? id, string severity, string message, string file)
	{
		var prefix = id is null ? "" : id + ": ";
		Console.Out.WriteLine($"{Normalize(file)}:1:1: {prefix}{message}");
	}

	public void ReportCheckSuccess() { /* no news is good news */ }

	private static void WriteLines(IEnumerable<Diagnostic> diagnostics)
	{
		foreach (var diag in diagnostics)
		{
			var (line, col) = diag.Context.GetCoordinates(diag.Span.Start);
			var file = Normalize(diag.Context.FilePath);
			var prefix = diag.Id is null ? "" : diag.Id + ": ";
			Console.Out.WriteLine($"{file}:{line}:{col}: {prefix}{diag.Message}");
		}
	}

	private static string Normalize(string path) => path.Replace('\\', '/');
}
