namespace Cvolo.Core.Packages;

/// <summary>
/// Thrown when a .cvlib archive violates structural bounds, the magic signature,
/// Merkle verification, or any other format contract. Format errors are non-recoverable
/// by contract. Derives from <see cref="System.IO.IOException"/> because
/// <see cref="System.IO.InvalidDataException"/> is sealed in modern .NET.
/// </summary>
public sealed class CvlFormatException(string code, long offset, string message, string? detail = null)
	: IOException($"{code}: {message}")
{
	/// <summary>The CVLF19xx diagnostic code identifying the exact format violation.</summary>
	public string Code { get; } = code;

	/// <summary>The physical byte offset where the anomaly was detected.</summary>
	public long Offset { get; } = offset;

	/// <summary>Extended technical context (expected vs actual values, etc.).</summary>
	public string? Detail { get; } = detail;

	public override string ToString()
	{
		var baseString = $"[{Code} at offset 0x{Offset:X8}]: {Message}";
		return Detail is not null ? $"{baseString}\nDetail: {Detail}" : baseString;
	}
}
