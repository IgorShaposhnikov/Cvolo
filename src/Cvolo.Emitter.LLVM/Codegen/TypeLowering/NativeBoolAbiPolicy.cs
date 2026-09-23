namespace Cvolo.Emitter.LLVM.Codegen.TypeLowering;

/// <summary>
/// Target policy for the scalar C <c>_Bool</c> ABI contract.
/// </summary>
/// <remarks>
/// Cvolo keeps ordinary booleans as internal LLVM <c>i1</c> values.  On the currently supported
/// desktop C ABI target families the scalar C boundary also uses <c>i1</c>, but the value is
/// zero-extended by the caller/callee contract.  Object storage remains a separate one-byte rule.
/// Unknown target families deliberately do not receive guessed ABI attributes.
/// </remarks>
internal static class NativeBoolAbiPolicy
{
	/// <summary>
	/// Returns whether the target family has an explicitly supported/verified zero-extension rule
	/// for scalar C <c>_Bool</c> parameters and returns.
	/// </summary>
	public static bool UsesZeroExtension(string? triple)
	{
		if (string.IsNullOrWhiteSpace(triple))
		{
			return false;
		}

		var t = triple.ToLowerInvariant();
		var supportedOs = t.Contains("windows", StringComparison.Ordinal)
			|| t.Contains("linux", StringComparison.Ordinal)
			|| t.Contains("darwin", StringComparison.Ordinal)
			|| t.Contains("apple", StringComparison.Ordinal)
			|| t.Contains("macos", StringComparison.Ordinal);

		if (!supportedOs)
		{
			return false;
		}

		return t.Contains("x86_64", StringComparison.Ordinal)
			|| t.Contains("amd64", StringComparison.Ordinal)
			|| t.Contains("i686", StringComparison.Ordinal)
			|| t.Contains("i386", StringComparison.Ordinal)
			|| t.StartsWith("x86-", StringComparison.Ordinal)
			|| t.Contains("aarch64", StringComparison.Ordinal)
			|| t.Contains("arm64", StringComparison.Ordinal);
	}
}
