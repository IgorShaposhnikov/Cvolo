namespace Cvolo.Core.Diagnostics;

public enum DiagnosticSeverity
{
	Error,
	Warning
}

public sealed class Diagnostic(CompilationContext context, TextSpan span, string message, DiagnosticSeverity severity = DiagnosticSeverity.Error, string? id = null)
{
	public CompilationContext Context { get; } = context;
	public TextSpan Span { get; } = span;
	public string Message { get; } = message;
	public DiagnosticSeverity Severity { get; } = severity;

	/// <summary>Stable family id (e.g. CVL1001) used by --nowarn and [SuppressWarning].</summary>
	public string? Id { get; } = id;

	public override string ToString() => $"[{Context.FilePath}]: ({Span.Start}-{Span.End}): {Message}";
}
