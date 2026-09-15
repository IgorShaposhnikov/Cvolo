using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using Blake3;
using NSec.Cryptography;

namespace Cvolo.Core.Packages;

/// <summary>
/// Validates and mounts a .cvlib archive.
/// Enforces all 13 structural checks of §1.7 BEFORE any memory mapping or allocation of views,
/// then verifies the Ed25519 signature (strictly over the Merkle root) and, by default,
/// the full BLAKE3 Merkle tree.
/// </summary>
public sealed class CvlArchiveReader
{
    private const ulong MaxContainerSize = 2UL * 1024 * 1024 * 1024; // 2 GiB
    private const ulong MaxSectors = 64;
    private const uint PageSize = 4096;

    private CvlArchiveReader() { }

    /// <summary>
    /// Opens, structurally validates, and cryptographically verifies a .cvlib archive.
    /// By default the full Merkle tree is re-verified (install/build time). Pass
    /// <paramref name="skipMerkleHashVerification"/> to skip the recomputation (plugin/LSP hot paths)
    /// and <paramref name="verifySignature"/> to skip the Ed25519 check (unsigned dev artifacts).
    /// </summary>
    public static unsafe CvlArchive Read(string path, bool skipMerkleHashVerification = false, bool verifySignature = true)
    {
        var fileInfo = new FileInfo(path);

        // 1. physical size >= a single 4096-byte page.
        if (fileInfo.Length < PageSize)
            throw new CvlFormatException(CvlFormatDiagnosticIds.HeaderMalformed, 0, "File size is smaller than a single 4096-byte page.");

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        var headerBytes = new byte[80];
        fs.ReadExactly(headerBytes);
        var header = CvlArchiveHeader.ReadFrom(headerBytes);

        // 4. Magic bytes match.
        if (!headerBytes.AsSpan(0, 6).SequenceEqual(CvlArchiveHeader.ExpectedMagic))
            throw new CvlFormatException(CvlFormatDiagnosticIds.HeaderMalformed, 0, "Invalid magic bytes. Not a .cvlib archive.");

        // 5. Reserved header bytes must be strictly zero.
        for (var i = 0; i < 6; i++)
        {
            if (header.Reserved[i] != 0)
                throw new CvlFormatException(CvlFormatDiagnosticIds.HeaderMalformed, 10 + i, "Reserved header bytes must be zero.");
        }

        // 2. Header FileSize must equal the physical file length.
        if (header.FileSize != (ulong)fileInfo.Length)
            throw new CvlFormatException(CvlFormatDiagnosticIds.FileSizeMismatch, 72, $"Header FileSize ({header.FileSize}) mismatches physical file size ({fileInfo.Length}).");

        // 3. Container size limit.
        if (header.FileSize > MaxContainerSize)
            throw new CvlFormatException(CvlFormatDiagnosticIds.FileSizeMismatch, 72, "File size exceeds the 2 GiB maximum container limit.");

        // 6. MerkleIndexTableOffset page-aligned and >= 4096.
        if (header.MerkleIndexTableOffset < PageSize || header.MerkleIndexTableOffset % PageSize != 0)
            throw new CvlFormatException(CvlFormatDiagnosticIds.SectorBoundsInvalid, 16, "MerkleIndexTableOffset must be page-aligned and >= 4096.");

        // 7. SignatureOffset page-aligned and >= 4096.
        if (header.SignatureOffset < PageSize || header.SignatureOffset % PageSize != 0)
            throw new CvlFormatException(CvlFormatDiagnosticIds.SectorBoundsInvalid, 24, "SignatureOffset must be page-aligned and >= 4096.");

        // Signatures must not be able to wrap the offset arithmetic.
        if (header.SignatureOffset > header.FileSize)
            throw new CvlFormatException(CvlFormatDiagnosticIds.SectorBoundsInvalid, 24, "SignatureOffset lies beyond the end of the file.");

        // 8. Signature block (96 bytes) must fit inside the file.
        if (header.SignatureOffset + 96 > header.FileSize)
            throw new CvlFormatException(CvlFormatDiagnosticIds.SectorBoundsInvalid, 24, "Signature block extends beyond the end of the file.");

        // 9. SectorCount limit.
        if (header.SectorCount > MaxSectors)
            throw new CvlFormatException(CvlFormatDiagnosticIds.SectorBoundsInvalid, 64, $"SectorCount ({header.SectorCount}) exceeds the maximum allowed ({MaxSectors}).");

        // The index table must not overlap the signature block.
        if (header.SignatureOffset < header.MerkleIndexTableOffset)
            throw new CvlFormatException(CvlFormatDiagnosticIds.SectorBoundsInvalid, 24, "SignatureOffset cannot precede MerkleIndexTableOffset.");

        var indexTableSize = header.SectorCount * 32;

        // 10. Index table must fit in the reserved region before the signature block.
        var availableIndexSpace = (header.SignatureOffset - header.MerkleIndexTableOffset + (PageSize - 1)) & ~(PageSize - 1);
        if (indexTableSize > availableIndexSpace)
            throw new CvlFormatException(CvlFormatDiagnosticIds.SectorBoundsInvalid, 16, "Index table size overflows the reserved space before the signature block.");

        // Read the index table.
        fs.Position = (long)header.MerkleIndexTableOffset;
        var indexBytes = new byte[(int)indexTableSize];
        fs.ReadExactly(indexBytes);

        var sectors = new CvlSectorIndexEntry[(int)header.SectorCount];
        ulong previousOffset = 0;
        ulong previousNonEmptyEnd = header.SignatureOffset + 96;

        for (var i = 0; i < sectors.Length; i++)
        {
            var entry = CvlSectorIndexEntry.ReadFrom(indexBytes.AsSpan(i * 32, 32));
            sectors[i] = entry;
            var entryBase = (long)header.MerkleIndexTableOffset + (i * 32);

            // 11. Index-entry reserved bytes must be strictly zero (§1.8).
            if (entry.Reserved != 0)
                throw new CvlFormatException(CvlFormatDiagnosticIds.SectorBoundsInvalid, entryBase, $"Sector {i + 1} reserved bytes must be zero.");

            // 11. Every entry offset is page-aligned.
            if (entry.Offset % PageSize != 0)
                throw new CvlFormatException(CvlFormatDiagnosticIds.SectorBoundsInvalid, entryBase, $"Sector {i + 1} offset is not page-aligned.");

            // 11. Every entry offset must start after the signature block.
            if (entry.Offset < header.SignatureOffset + 96)
                throw new CvlFormatException(CvlFormatDiagnosticIds.SectorBoundsInvalid, entryBase, $"Sector {i + 1} offset overlaps the header/signature blocks.");

            // 12. Offsets must be monotonically non-decreasing.
            if (entry.Offset < previousOffset)
                throw new CvlFormatException(CvlFormatDiagnosticIds.SectorOverlap, entryBase, $"Sector {i + 1} offset is out-of-order (decreases relative to the preceding sector).");

            previousOffset = entry.Offset;

            // Rule 11 applies to empty sectors too: their zero-length range must still begin
            // at or before FileSize. Keeping this outside the Length > 0 branch avoids
            // accepting an empty marker whose offset points beyond the physical container.
            if (entry.Offset > header.FileSize)
                throw new CvlFormatException(CvlFormatDiagnosticIds.SectorBoundsInvalid, entryBase, $"Sector {i + 1} offset lies beyond FileSize.");

            if (entry.Length > 0)
            {
                // Check the logical payload before alignment so hostile ulong lengths cannot
                // overflow either addition below. FileSize is capped at 2 GiB.
                if (entry.Length > header.FileSize - entry.Offset)
                    throw new CvlFormatException(CvlFormatDiagnosticIds.SectorBoundsInvalid, entryBase, $"Sector {i + 1} payload extends beyond FileSize.");

                var alignedLength = (entry.Length + (PageSize - 1)) & ~(PageSize - 1);

                // 11. The padded payload must fit inside the file.
                if (alignedLength > header.FileSize - entry.Offset)
                    throw new CvlFormatException(CvlFormatDiagnosticIds.SectorBoundsInvalid, entryBase, $"Sector {i + 1} padded payload extends beyond FileSize.");

                // 12. Non-empty sectors must not overlap.
                if (entry.Offset < previousNonEmptyEnd)
                    throw new CvlFormatException(CvlFormatDiagnosticIds.SectorOverlap, entryBase, $"Sector {i + 1} overlaps the previous non-empty sector.");

                previousNonEmptyEnd = entry.Offset + alignedLength;
            }
        }

        // 13. Parse and validate the slice manifest before any mmap/view is created (§1.7).
        if (sectors.Length == 0)
            throw new CvlFormatException(CvlFormatDiagnosticIds.SectorBoundsInvalid, 64, "A .cvlib must contain Sector 1 metadata.");

        var s1Entry = sectors[0];
        var maxSector1Length = 4UL + CvlSliceManifest.MaxManifestSize + CvlSliceManifest.MaxLayoutMetadataSize;
        if (s1Entry.Length > maxSector1Length)
        {
            throw new CvlFormatException(
                CvlFormatDiagnosticIds.ManifestTooLarge,
                (long)s1Entry.Offset,
                $"Sector 1 payload ({s1Entry.Length} bytes) exceeds the maximum manifest + layout metadata size ({maxSector1Length} bytes).");
        }

        var sector1Bytes = new byte[checked((int)s1Entry.Length)];
        fs.Position = (long)s1Entry.Offset;
        fs.ReadExactly(sector1Bytes);
        var s2Length = sectors.Length > 1 ? sectors[1].Length : 0;
        var s3Length = sectors.Length > 2 ? sectors[2].Length : 0;
        var manifest = CvlSliceManifest.Parse(sector1Bytes, s2Length, s3Length, (long)s1Entry.Offset);

        // Structural checks passed. Mount the file for cryptographic verification and payload access.
        var mmf = MemoryMappedFile.CreateFromFile(fs, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, false);
        var accessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

        byte* basePtr = null;
        accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref basePtr);

        try
        {
            if (verifySignature)
                VerifySignature(basePtr, header, sectors);

            if (!skipMerkleHashVerification)
                VerifyMerkleTree(basePtr, header, sectors);

            return new CvlArchive(mmf, accessor, basePtr, header, sectors, manifest);
        }
        catch
        {
            accessor.SafeMemoryMappedViewHandle.ReleasePointer();
            accessor.Dispose();
            mmf.Dispose();
            throw;
        }
    }

    private static unsafe void VerifySignature(byte* basePtr, CvlArchiveHeader header, CvlSectorIndexEntry[] sectors)
    {
        var sigBlock = new ReadOnlySpan<byte>(basePtr + header.SignatureOffset, 96);
        var publicKeyBytes = sigBlock.Slice(0, 32);
        var signatureBytes = sigBlock.Slice(32, 64);
        var rootHashSpan = new ReadOnlySpan<byte>(header.MerkleRootHash, 32);

        bool isValid;
        try
        {
            var algorithm = SignatureAlgorithm.Ed25519;
            var publicKey = PublicKey.Import(algorithm, publicKeyBytes, KeyBlobFormat.RawPublicKey);
            isValid = algorithm.Verify(publicKey, rootHashSpan, signatureBytes);
        }
        catch
        {
            isValid = false;
        }

        if (!isValid)
        {
            var isStripSource = sectors.Length >= 5 && sectors[4].Length == 0;
            var diagnosticId = isStripSource ? CvlFormatDiagnosticIds.FatalStripSource : CvlFormatDiagnosticIds.BitcodeTampered;

            throw new CvlFormatException(diagnosticId, (long)header.SignatureOffset, "Ed25519 signature verification failed.");
        }
    }

    private static unsafe void VerifyMerkleTree(byte* basePtr, CvlArchiveHeader header, CvlSectorIndexEntry[] sectors)
    {
        var leaves = new List<byte[]>();

        // 1. leaf_0 = BLAKE3(header_bytes[0..80]) without padding, with bytes 32..63 zeroed (§1.4).
        var headerBytes = new byte[80];
        new ReadOnlySpan<byte>(basePtr, 80).CopyTo(headerBytes);
        Array.Clear(headerBytes, 32, 32);
        leaves.Add(HashBytes(headerBytes));

        // 2. One leaf per 4096-byte page of each sector; empty sectors emit a domain-separated marker leaf.
        for (var i = 0; i < sectors.Length; i++)
        {
            var sector = sectors[i];
            var sectorId = (uint)(i + 1);

            if (sector.Length == 0)
            {
                leaves.Add(HashEmptyLeaf(sectorId));
                continue;
            }

            var pages = (sector.Length + (PageSize - 1)) / PageSize;
            var sectorStart = basePtr + sector.Offset;

            for (ulong p = 0; p < pages; p++)
            {
                var remaining = sector.Length - (p * PageSize);
                var chunkSize = (int)Math.Min(remaining, PageSize);
                var pageSpan = new ReadOnlySpan<byte>(sectorStart + (p * PageSize), chunkSize);

                if (chunkSize < PageSize)
                {
                    var paddedPage = new byte[PageSize];
                    pageSpan.CopyTo(paddedPage);
                    leaves.Add(HashBytes(paddedPage));
                }
                else
                {
                    leaves.Add(HashBytes(pageSpan));
                }
            }
        }

        // 3. Pairwise tree reduction; the odd trailing node is promoted unchanged.
        var computedRoot = ComputeRoot(leaves);
        var expectedRoot = new ReadOnlySpan<byte>(header.MerkleRootHash, 32);

        if (!computedRoot.AsSpan().SequenceEqual(expectedRoot))
        {
            var isStripSource = sectors.Length >= 5 && sectors[4].Length == 0;
            var diagnosticId = isStripSource ? CvlFormatDiagnosticIds.FatalStripSource : CvlFormatDiagnosticIds.BitcodeTampered;

            throw new CvlFormatException(diagnosticId, 32, "BLAKE3 Merkle root verification failed. The payload has been tampered with.");
        }
    }

    private static byte[] HashBytes(ReadOnlySpan<byte> data) => Hasher.Hash(data).AsSpan().ToArray();

    private static byte[] HashEmptyLeaf(uint sectorId)
    {
        var marker = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(marker.AsSpan(4, 4), sectorId);
        return HashBytes(marker);
    }

    private static byte[] ComputeRoot(List<byte[]> leaves)
    {
        while (leaves.Count > 1)
        {
            var nextLevel = new List<byte[]>((leaves.Count + 1) / 2);
            for (var i = 0; i < leaves.Count; i += 2)
            {
                if (i + 1 < leaves.Count)
                {
                    var concat = new byte[64];
                    leaves[i].CopyTo(concat, 0);
                    leaves[i + 1].CopyTo(concat, 32);
                    nextLevel.Add(HashBytes(concat));
                }
                else
                {
                    nextLevel.Add(leaves[i]);
                }
            }
            leaves = nextLevel;
        }

        return leaves[0];
    }
}