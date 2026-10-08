namespace Cvolo.Core.Diagnostics;

public class DiagnosticBag
{
	private readonly List<Diagnostic> _diagnostics = [];

	public IReadOnlyList<Diagnostic> Diagnostics => _diagnostics;
	public bool HasErrors => _diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);
	public bool HasWarnings => _diagnostics.Any(d => d.Severity == DiagnosticSeverity.Warning);

	public void Report(CompilationContext context, TextSpan span, string message, string diagnosticId)
	{
		_diagnostics.Add(new Diagnostic(context, span, message, DiagnosticSeverity.Error, diagnosticId));
	}

	public void ReportWarning(CompilationContext context, TextSpan span, string message, string id)
	{
		_diagnostics.Add(new Diagnostic(context, span, message, DiagnosticSeverity.Warning, id));
	}

	public void AddRange(DiagnosticBag other)
	{
		_diagnostics.AddRange(other._diagnostics);
	}

	/// <summary>
	/// Removes an already-reported diagnostic with the given stable id at the exact span.
	/// Used for diagnostic precedence: when a read is primarily an ownership / use-after-move
	/// failure, the associated possibly-uninitialized read diagnostic at the same span is
	/// suppressed so the ownership diagnostic is the single reported cause.
	/// </summary>
	public void Suppress(string diagnosticId, TextSpan span)
	{
		_diagnostics.RemoveAll(d => d.Id == diagnosticId
			&& d.Span.Start == span.Start
			&& d.Span.End == span.End);
	}
}
