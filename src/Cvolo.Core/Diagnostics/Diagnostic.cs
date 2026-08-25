namespace Cvolo.Core.Diagnostics;

public enum DiagnosticSeverity
{
	Error,
	Warning
}

public sealed class Diagnostic(CompilationContext context, TextSpan span, string message, DiagnosticSeverity severity = DiagnosticSeverity.Error)
{
	public CompilationContext Context { get; } = context;
	public TextSpan Span { get; } = span;
	public string Message { get; } = message;
	public DiagnosticSeverity Severity { get; } = severity;

	public override string ToString() => $"[{Context.FilePath}]: ({Span.Start}-{Span.End}): {Message}";
}
