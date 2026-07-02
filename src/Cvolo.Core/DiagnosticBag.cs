namespace Cvolo.Core;

public class DiagnosticBag
{
	private readonly List<Diagnostic> _diagnostics = [];

	public IReadOnlyList<Diagnostic> Diagnostics => _diagnostics;
	public bool HasErrors => _diagnostics.Any(d => true);

	public void Report(TextSpan span, string message)
	{
		_diagnostics.Add(new Diagnostic(span, message));
	}

	public void AddRange(DiagnosticBag other)
	{
		_diagnostics.AddRange(other._diagnostics);
	}
}
