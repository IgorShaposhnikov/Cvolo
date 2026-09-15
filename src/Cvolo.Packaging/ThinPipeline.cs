using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cvolo.Core.Packages;
using NSec.Cryptography;

namespace Cvolo.Packaging;

public static class ThinPipeline
{
	public static void Write(CvlArchive archive, string triple, string outputPath, Key key, bool stripSource = false, bool stripBinaries = false)
	{
		var slice = archive.Manifest.Slices.SingleOrDefault(s => s.Triple == triple)
			?? throw new PackageException(PackageDiagnosticIds.MissingHostSlice, $"No slice for host triple '{triple}'.");
		var payload = archive.GetSectorPayload(1);
		var length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(payload));
		var manifest = JsonNode.Parse(payload.Slice(4, length))!.AsObject();
		manifest["Slices"] = JsonSerializer.SerializeToNode(new[]
		{
			new CvlSliceEntry(triple, new(0, slice.Sector2.Length), new(0, slice.Sector3.Length))
		});
		var json = JsonSerializer.SerializeToUtf8Bytes(manifest);
		var metadata = new byte[4 + json.Length + payload.Length - 4 - length];
		BinaryPrimitives.WriteUInt32LittleEndian(metadata, (uint)json.Length);
		json.CopyTo(metadata, 4);
		payload[(4 + length)..].CopyTo(metadata.AsSpan(4 + json.Length));
		var writer = new CvlArchiveWriter
		{
			CompilerToolchainVersion = archive.Header.CompilerToolchainVersion,
			IntendedLlvmBackendVersion = archive.Header.IntendedLlvmBackendVersion
		};
		writer.SetSector(1, metadata);
		writer.SetSector(2, Extract(archive, 2, slice.Sector2));
		writer.SetSector(3, Extract(archive, 3, slice.Sector3));
		for (var sector = 4; sector <= archive.Sectors.Count; sector++)
			writer.SetSector(sector, (sector == 4 && stripBinaries) || (sector == 5 && stripSource)
				? Array.Empty<byte>() : archive.GetSectorPayload(sector).ToArray());
		writer.Write(outputPath, key);
	}

	private static byte[] Extract(CvlArchive archive, int sector, CvlSectorRange range) =>
		range.Length == 0 ? [] : archive.GetSectorPayload(sector).Slice(checked((int)range.Offset), checked((int)range.Length)).ToArray();
}
