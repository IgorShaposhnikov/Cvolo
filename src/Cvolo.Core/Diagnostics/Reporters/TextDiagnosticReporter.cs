namespace Cvolo.Core.Diagnostics.Reporters;

/// <summary>
/// Human-readable reporter: ANSI-colored diagnostics on stderr, keeping the
/// original CLI behavior. It does not claim stdout, so verbose chatter stays
/// enabled at the driver level.
/// </summary>
public sealed class TextDiagnosticReporter(bool suppressWarnings) : IDiagnosticReporter
{
	public bool Exclusive => false;

	public void ReportErrors(IReadOnlyList<Diagnostic> diagnostics, string headerWhenNoId)
	{
		foreach (var diag in diagnostics)
		{
			var label = diag.Id is null ? headerWhenNoId : $"Compile Error {diag.Id}";
			foreach (var line in diag.Context.FormatDiagnostic(label, diag.Message, diag.Span))
				Console.Error.WriteLine(line);
		}
	}

	public void ReportWarnings(IReadOnlyList<Diagnostic> diagnostics, HashSet<string>? suppressedIds)
	{
		if (suppressWarnings) return;

		foreach (var diag in diagnostics)
		{
			if (diag.Id is not null && suppressedIds?.Contains(diag.Id) == true)
				continue;

			var label = diag.Id is null ? "Analysis Warning" : $"Analysis Warning {diag.Id}";
			foreach (var line in diag.Context.FormatDiagnostic(label, diag.Message, diag.Span, compact: true))
				Console.Error.WriteLine(line);
		}
	}

	public void ReportSynthetic(string? id, string severity, string message, string file)
	{
		var prefix = severity.Equals("warning", StringComparison.OrdinalIgnoreCase) ? "Warning" : "Error";
		Console.Error.WriteLine(id is null ? $"{prefix}: {message}" : $"{prefix} {id}: {message}");
	}

	public void ReportCheckSuccess()
		=> Console.WriteLine("Check completed. Code is semantically correct.");
}
