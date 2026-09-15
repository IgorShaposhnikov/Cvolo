using NSec.Cryptography;

namespace Cvolo.Packaging;

public sealed class PackageCache
{
	public string RootPath { get; }

	public PackageCache(string? rootPath = null)
	{
		RootPath = Path.GetFullPath(rootPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cvolo"));
	}

	public string GetPackageDirectory(string id, string version)
	{
		PackageMetadata.ValidateIdentity(id, version);
		return Path.Combine(RootPath, "pkg", id.ToLowerInvariant(), version);
	}

	internal Key LoadSigningKey()
	{
		var directory = Path.Combine(RootPath, "keys");
		Directory.CreateDirectory(directory);
		var path = Path.Combine(directory, "machine.key");
		if (!File.Exists(path))
		{
			using var key = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
			var temporary = path + "." + Guid.NewGuid().ToString("N");
			try
			{
				File.WriteAllBytes(temporary, key.Export(KeyBlobFormat.RawPrivateKey));
				if (!OperatingSystem.IsWindows())
					File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
				try { File.Move(temporary, path); }
				catch (IOException) when (File.Exists(path)) { }
			}
			finally { if (File.Exists(temporary)) File.Delete(temporary); }
		}
		return Key.Import(SignatureAlgorithm.Ed25519, File.ReadAllBytes(path), KeyBlobFormat.RawPrivateKey);
	}
}
