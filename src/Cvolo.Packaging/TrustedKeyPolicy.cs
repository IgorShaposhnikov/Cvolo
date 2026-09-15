using System.Security.Cryptography;
using System.Text.Json;
using Cvolo.Core.Packages;

namespace Cvolo.Packaging;

/// <summary>
/// Flat Ed25519 public-key allow-list stored in ~/.cvolo/keys/trusted.json.
/// The file contains an array of 64-character hexadecimal raw public keys.
/// An absent or empty list means no trusted-key policy is configured.
/// </summary>
public sealed class TrustedKeyPolicy
{
	private readonly byte[][] _trustedKeys;

	private TrustedKeyPolicy(byte[][] trustedKeys) => _trustedKeys = trustedKeys;

	public bool IsConfigured => _trustedKeys.Length != 0;

	public static TrustedKeyPolicy Load(PackageCache cache)
	{
		ArgumentNullException.ThrowIfNull(cache);
		var path = Path.Combine(cache.RootPath, "keys", "trusted.json");
		if (!File.Exists(path))
			return new TrustedKeyPolicy([]);

		string[] entries;
		try
		{
			entries = JsonSerializer.Deserialize<string[]>(File.ReadAllText(path))
				?? throw new InvalidDataException("Trusted-key policy deserialized to null.");
		}
		catch (Exception ex) when (ex is JsonException or IOException or InvalidDataException)
		{
			throw new PackageException(
				PackageDiagnosticIds.InvalidTrustedKeysPolicy,
				$"Trusted-key policy '{path}' is invalid.",
				ex.Message);
		}

		var keys = new List<byte[]>();
		foreach (var entry in entries)
		{
			if (string.IsNullOrWhiteSpace(entry))
				continue;

			byte[] key;
			try
			{
				key = Convert.FromHexString(entry.Trim());
			}
			catch (FormatException ex)
			{
				throw new PackageException(
					PackageDiagnosticIds.InvalidTrustedKeysPolicy,
					$"Trusted-key policy '{path}' contains a non-hexadecimal public key.",
					ex.Message);
			}

			if (key.Length != 32)
			{
				throw new PackageException(
					PackageDiagnosticIds.InvalidTrustedKeysPolicy,
					$"Trusted-key policy '{path}' contains a {key.Length}-byte key; Ed25519 public keys must be 32 bytes.");
			}

			if (!keys.Any(existing => CryptographicOperations.FixedTimeEquals(existing, key)))
				keys.Add(key);
		}

		return new TrustedKeyPolicy(keys.ToArray());
	}

	public void Validate(CvlArchive archive, string packageDisplayName)
	{
		ArgumentNullException.ThrowIfNull(archive);
		if (!IsConfigured)
			return;

		if (archive.IsUnsigned)
		{
			throw new PackageException(
				PackageDiagnosticIds.UnsignedRejected,
				$"Package '{packageDisplayName}' is unsigned and trusted keys are configured.");
		}

		var signer = archive.SigningPublicKey;
		foreach (var key in _trustedKeys)
		{
			if (CryptographicOperations.FixedTimeEquals(key, signer))
				return;
		}

		throw new PackageException(
			PackageDiagnosticIds.UntrustedSigner,
			$"Package '{packageDisplayName}' is signed by a key that is not listed in keys/trusted.json.");
	}
}
