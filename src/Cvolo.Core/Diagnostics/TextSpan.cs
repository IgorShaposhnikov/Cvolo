namespace Cvolo.Core.Diagnostics;

public readonly struct TextSpan(int start, int length)
{
	public int Start { get; } = start;
	public int Length { get; } = length;
	public int End => Start + Length;

	public static TextSpan FromBounds(int start, int end) => new(start, end - start);
}
