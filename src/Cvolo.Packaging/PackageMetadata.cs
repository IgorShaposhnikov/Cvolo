using System.Buffers.Binary;
using System.Text.Json;
using Cvolo.Core.Packages;

namespace Cvolo.Packaging;

public sealed record PackageMetadata(string PackageId, string Version, IReadOnlyList<PackageReference> Dependencies)
{
	public static PackageMetadata Read(CvlArchive archive)
	{
		var payload = archive.GetSectorPayload(1);
		var length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(payload));
		var metadata = JsonSerializer.Deserialize<PackageMetadata>(payload.Slice(4, length))
			?? throw new InvalidDataException("Package metadata is missing. Repack the project.");
		ValidateIdentity(metadata.PackageId, metadata.Version);
		return metadata;
	}

	internal static void ValidateIdentity(string id, string version)
	{
		if (string.IsNullOrEmpty(id) || !char.IsAsciiLetterOrDigit(id[0]) ||
			id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '-' and not '_') || id.EndsWith('.'))
			throw new PackageException(PackageDiagnosticIds.PackageIdMismatch, "Package ID must be a portable filename starting with a letter or digit.");
		SemanticVersion.Parse(version);
		if (version.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '-' and not '+'))
			throw new PackageException(PackageDiagnosticIds.InvalidSemVer, "Invalid package version.");
	}
}
