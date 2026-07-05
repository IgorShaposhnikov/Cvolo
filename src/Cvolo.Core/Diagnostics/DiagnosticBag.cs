namespace Cvolo.Core.Diagnostics;

public class DiagnosticBag
{
	private readonly List<Diagnostic> _diagnostics = [];

	public IReadOnlyList<Diagnostic> Diagnostics => _diagnostics;
	public bool HasErrors => _diagnostics.Any(d => true);

	public void Report(CompilationContext context, TextSpan span, string message)
	{
		_diagnostics.Add(new Diagnostic(context, span, message));
	}

	public void AddRange(DiagnosticBag other)
	{
		_diagnostics.AddRange(other._diagnostics);
	}
}
