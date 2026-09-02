namespace Cvolo.Core.Diagnostics;

public class DiagnosticBag
{
	private readonly List<Diagnostic> _diagnostics = [];

	public IReadOnlyList<Diagnostic> Diagnostics => _diagnostics;
	public bool HasErrors => _diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);
	public bool HasWarnings => _diagnostics.Any(d => d.Severity == DiagnosticSeverity.Warning);

	public void Report(CompilationContext context, TextSpan span, string message, string? diagnosticId = null)
	{
		_diagnostics.Add(new Diagnostic(context, span, message, DiagnosticSeverity.Error, diagnosticId));
	}

	public void ReportWarning(CompilationContext context, TextSpan span, string message, string? id = null)
	{
		_diagnostics.Add(new Diagnostic(context, span, message, DiagnosticSeverity.Warning, id));
	}

	public void AddRange(DiagnosticBag other)
	{
		_diagnostics.AddRange(other._diagnostics);
	}
}
