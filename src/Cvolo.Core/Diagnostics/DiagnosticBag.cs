namespace Cvolo.Core.Diagnostics;

public class DiagnosticBag
{
	private readonly List<Diagnostic> _diagnostics = [];

	public IReadOnlyList<Diagnostic> Diagnostics => _diagnostics;
	public bool HasErrors => _diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);
	public bool HasWarnings => _diagnostics.Any(d => d.Severity == DiagnosticSeverity.Warning);

	public void Report(CompilationContext context, TextSpan span, string message)
	{
		_diagnostics.Add(new Diagnostic(context, span, message));
	}

	public void ReportWarning(CompilationContext context, TextSpan span, string message)
	{
		_diagnostics.Add(new Diagnostic(context, span, message, DiagnosticSeverity.Warning));
	}

	public void AddRange(DiagnosticBag other)
	{
		_diagnostics.AddRange(other._diagnostics);
	}
}
