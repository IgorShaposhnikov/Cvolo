namespace Cvolo.Core.Diagnostics;

public sealed class Diagnostic(CompilationContext context, TextSpan span, string message)
{
	public CompilationContext Context { get; } = context;
	public TextSpan Span { get; } = span;
	public string Message { get; } = message;

	public override string ToString() => $"[{Context.FilePath}]: ({Span.Start}-{Span.End}): {Message}";
}
