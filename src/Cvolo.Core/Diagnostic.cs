namespace Cvolo.Core;

public class Diagnostic(TextSpan span, string message)
{
    public TextSpan Span { get; } = span;
    public string Message { get; } = message;

    public override string ToString() => $"({Span.Start}-{Span.End}): {Message}";
}
