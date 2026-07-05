namespace Cvolo.Core.Diagnostics;

/// <summary>
/// An immutable bridge linking a pure syntax span to its physical source file.
/// </summary>
public sealed class Location(CompilationContext context, TextSpan span)
{
	public CompilationContext Context { get; } = context;
	public TextSpan Span { get; } = span;
}
