using System.Buffers.Binary;
using System.Text.Json;
using Cvolo.Core.Packages;
using Cvolo.Packaging;
using NSec.Cryptography;

namespace Cvolo.Tests.Packaging;

public sealed class PackageInstallerTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "cvolo-install-tests-" + Guid.NewGuid().ToString("N"));
	private readonly PackageCache _cache;
	private readonly PackageInstaller _installer;

	public PackageInstallerTests()
	{
		Directory.CreateDirectory(_root);
		_cache = new PackageCache(Path.Combine(_root, "cache"));
		_installer = new PackageInstaller(_cache);
	}

	[Fact]
	public void SignedSingleTarget_PreservesBytesAndReinstallIsNoOp()
	{
		var source = CreateArchive();
		var installed = _installer.InstallFromFile(source);
		Assert.False(installed.WasUnsigned);
		Assert.False(installed.AlreadyInstalled);
		Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(installed.OutputPath));
		Assert.StartsWith("blake3:", installed.ContentHash);
		Assert.True(_installer.InstallFromFile(source).AlreadyInstalled);
		Assert.True(_installer.InstallFromCache("foo", "1.0.0").AlreadyInstalled);
		Assert.Contains("1.0.0", File.ReadAllText(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(installed.OutputPath))!, "index.json")));
	}

	[Fact]
	public void UnsignedPackage_WithoutTrustedKeys_IsAcceptedAndPreservedAsUnsigned()
	{
		var installed = _installer.InstallFromFile(CreateArchive(signed: false));

		Assert.True(installed.WasUnsigned);
		using var cached = CvlArchiveReader.Read(installed.OutputPath, allowUnsigned: true);
		Assert.True(cached.IsUnsigned);
	}

	[Fact]
	public void UnsignedPackage_WithTrustedKeys_IsRejectedWithCVLP3040()
	{
		using var trustedKey = Key.Create(SignatureAlgorithm.Ed25519);
		WriteTrustedKeys(trustedKey);

		var error = Assert.Throws<PackageException>(() => _installer.InstallFromFile(CreateArchive(signed: false)));

		Assert.Equal(PackageDiagnosticIds.UnsignedRejected, error.Code);
	}

	[Fact]
	public void SignedPackage_WithMatchingTrustedKey_IsAccepted()
	{
		using var trustedKey = Key.Create(SignatureAlgorithm.Ed25519);
		WriteTrustedKeys(trustedKey);

		var installed = _installer.InstallFromFile(CreateArchive(signingKey: trustedKey));

		Assert.False(installed.WasUnsigned);
	}

	[Fact]
	public void SignedPackage_WithUntrustedKey_IsRejectedWithCVLP3041()
	{
		using var trustedKey = Key.Create(SignatureAlgorithm.Ed25519);
		using var packageKey = Key.Create(SignatureAlgorithm.Ed25519);
		WriteTrustedKeys(trustedKey);

		var error = Assert.Throws<PackageException>(() => _installer.InstallFromFile(CreateArchive(signingKey: packageKey)));

		Assert.Equal(PackageDiagnosticIds.UntrustedSigner, error.Code);
	}

	[Fact]
	public void CachedSignedPackage_IsRecheckedAgainstCurrentTrustedKeys()
	{
		using var packageKey = Key.Create(SignatureAlgorithm.Ed25519);
		var installed = _installer.InstallFromFile(CreateArchive(signingKey: packageKey));
		using var differentTrustedKey = Key.Create(SignatureAlgorithm.Ed25519);
		WriteTrustedKeys(differentTrustedKey);

		var error = Assert.Throws<PackageException>(() => _installer.InstallFromCache(installed.PackageId, installed.Version));

		Assert.Equal(PackageDiagnosticIds.UntrustedSigner, error.Code);
	}

	[Fact]
	public void MalformedTrustedKeyPolicy_IsRejectedWithCVLP3042()
	{
		var keysDirectory = Path.Combine(_cache.RootPath, "keys");
		Directory.CreateDirectory(keysDirectory);
		File.WriteAllText(Path.Combine(keysDirectory, "trusted.json"), "[\"001122\"]");

		var error = Assert.Throws<PackageException>(() => _installer.InstallFromFile(CreateArchive()));

		Assert.Equal(PackageDiagnosticIds.InvalidTrustedKeysPolicy, error.Code);
	}

	[Fact]
	public void FatArchive_ThinsRebasesSignsAndPreservesOtherSectors()
	{
		var installed = _installer.InstallFromFile(CreateArchive(fat: true));
		using var archive = CvlArchiveReader.Read(installed.OutputPath);
		var slice = Assert.Single(archive.Manifest.Slices);
		Assert.Equal(TargetTriple.HostTriple(), slice.Triple);
		Assert.Equal(0UL, slice.Sector2.Offset);
		Assert.Equal(new byte[] { 3, 4 }, archive.GetSectorPayload(2).ToArray());
		Assert.Equal(new byte[] { 7, 8 }, archive.GetSectorPayload(3).ToArray());
		Assert.Equal(new byte[] { 9 }, archive.GetSectorPayload(4).ToArray());
		Assert.Equal("source", archive.ReadSourceBuffer());
		Assert.Equal("Foo", PackageMetadata.Read(archive).PackageId);
		Assert.Equal("Bar", Assert.Single(PackageMetadata.Read(archive).Dependencies).Id);
		Assert.True(File.Exists(Path.Combine(_cache.RootPath, "keys", "machine.key")));
	}

	[Fact]
	public void CachedFatArchive_IsRejectedBecauseInstallCacheMustBeSingleTarget()
	{
		var source = CreateArchive(fat: true);
		using var sourceArchive = CvlArchiveReader.Read(source);
		var metadata = PackageMetadata.Read(sourceArchive);
		var directory = _cache.GetPackageDirectory(metadata.PackageId, metadata.Version);
		Directory.CreateDirectory(directory);
		File.Copy(source, Path.Combine(directory, metadata.PackageId + ".cvlib"));
		File.WriteAllText(Path.Combine(directory, ".metadata.json"), JsonSerializer.Serialize(
			new InstalledPackageMetadata(metadata.PackageId, metadata.Version, source, LocalFeed.ComputeHash(source), DateTimeOffset.UtcNow)));

		var error = Assert.Throws<PackageException>(() => _installer.InstallFromCache(metadata.PackageId, metadata.Version));
		Assert.Equal(PackageDiagnosticIds.CacheNotThinned, error.Code);
	}

	[Fact]
	public void DifferentContentForSameVersion_IsRejected()
	{
		_installer.InstallFromFile(CreateArchive());
		var error = Assert.Throws<PackageException>(() => _installer.InstallFromFile(CreateArchive(fat: true)));
		Assert.Contains(PackageDiagnosticIds.CachedContentMismatch, error.Message);
	}

	[Fact]
	public void MissingHost_IsRejectedWithCVLP3010WithoutCacheEntry()
	{
		var error = Assert.Throws<PackageException>(() => _installer.InstallFromFile(CreateArchive(missingHost: true)));
		Assert.Equal(PackageDiagnosticIds.MissingHostSlice, error.Code);
		Assert.False(Directory.Exists(_cache.GetPackageDirectory("Foo", "1.0.0")));
	}

	[Fact]
	public void TamperedPayload_IsRejected()
	{
		var source = CreateArchive();
		long offset;
		using (var archive = CvlArchiveReader.Read(source)) offset = (long)archive.Sectors[1].Offset;
		using (var file = File.OpenWrite(source)) { file.Position = offset; file.WriteByte(99); }
		var error = Assert.Throws<CvlFormatException>(() => _installer.InstallFromFile(source));
		Assert.Equal(CvlFormatDiagnosticIds.BitcodeTampered, error.Code);
	}

	[Fact]
	public void InvalidNonzeroSignature_IsRejected()
	{
		var source = CreateArchive();
		long offset;
		using (var archive = CvlArchiveReader.Read(source)) offset = (long)archive.Header.SignatureOffset;
		using (var file = new FileStream(source, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
		{
			file.Position = offset + 32;
			var original = file.ReadByte();
			Assert.NotEqual(-1, original);
			file.Position = offset + 32;
			file.WriteByte((byte)(original ^ 0xFF));
		}
		Assert.Throws<CvlFormatException>(() => _installer.InstallFromFile(source));
	}

	[Theory]
	[InlineData("../escape")]
	[InlineData("C:foo")]
	[InlineData("foo/bar")]
	public void UnsafeIdentity_IsRejected(string id)
	{
		Assert.Throws<PackageException>(() => _installer.InstallFromFile(CreateArchive(id: id)));
	}

	private string CreateArchive(bool fat = false, bool signed = true, bool missingHost = false, string id = "Foo", Key? signingKey = null)
	{
		var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".cvlib");
		var slices = new List<CvlSliceEntry>();
		if (fat) slices.Add(new("other-target", new(0, 2), new(0, 2)));
		slices.Add(new(missingHost ? "missing-target" : TargetTriple.HostTriple(), new(fat ? 2UL : 0, 2), new(fat ? 2UL : 0, 2)));
		var json = JsonSerializer.SerializeToUtf8Bytes(new
		{
			Format = "cvlib.slice-manifest.v1",
			PackageId = id,
			Version = "1.0.0",
			Dependencies = new[] { new PackageReference("Bar", "^1.0.0") },
			Slices = slices
		});
		var sector = new byte[json.Length + 6];
		BinaryPrimitives.WriteUInt32LittleEndian(sector, (uint)json.Length);
		json.CopyTo(sector, 4);
		"{}"u8.CopyTo(sector.AsSpan(4 + json.Length));
		var writer = new CvlArchiveWriter();
		writer.SetSector(1, sector);
		writer.SetSector(2, fat ? new byte[] { 1, 2, 3, 4 } : new byte[] { 3, 4 });
		writer.SetSector(3, fat ? new byte[] { 5, 6, 7, 8 } : new byte[] { 7, 8 });
		writer.SetSector(4, new byte[] { 9 });
		writer.SetSourceBuffer("source");
		if (signed)
		{
			if (signingKey is not null)
			{
				writer.Write(path, signingKey);
			}
			else
			{
				using var key = Key.Create(SignatureAlgorithm.Ed25519);
				writer.Write(path, key);
			}
		}
		else
		{
			writer.WriteUnsigned(path);
		}
		return path;
	}

	private void WriteTrustedKeys(params Key[] keys)
	{
		var directory = Path.Combine(_cache.RootPath, "keys");
		Directory.CreateDirectory(directory);
		var publicKeys = keys
			.Select(key => Convert.ToHexString(key.Export(KeyBlobFormat.RawPublicKey)))
			.ToArray();
		File.WriteAllText(Path.Combine(directory, "trusted.json"), JsonSerializer.Serialize(publicKeys));
	}

	public void Dispose() => Directory.Delete(_root, true);
}
