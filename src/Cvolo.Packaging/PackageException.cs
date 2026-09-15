namespace Cvolo.Packaging;

/// <summary>
/// Thrown when a packaging operation violates the package-manager contract.
/// The <see cref="Code"/> identifies the exact CVLP3xxx diagnostic.
/// </summary>
public sealed class PackageException(string code, string message, string? detail = null)
	: Exception($"{code}: {message}")
{
	/// <summary>The CVLP3xxx diagnostic code identifying the failure.</summary>
	public string Code { get; } = code;

	/// <summary>Extended technical context (e.g. expected vs actual values).</summary>
	public string? Detail { get; } = detail;

	public override string ToString()
	{
		var baseString = $"[{Code}]: {Message}";
		return Detail is not null ? $"{baseString}\nDetail: {Detail}" : baseString;
	}
}
