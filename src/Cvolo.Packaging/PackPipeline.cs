using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Cvolo.Core.Packages;
using NSec.Cryptography;

namespace Cvolo.Packaging;

/// <summary>
/// The cvolo pack pipeline: resolves targets, compiles the project in-process to LLVM IR,
/// produces native object files and bitcode via clang, assembles the .cvlib sectors
/// (1 = slice manifest, 2 = objects, 3 = bitcode, 5 = source buffer), and writes the
/// signed or unsigned container.
/// </summary>
public sealed class PackPipeline
{
	private const string FormatId = "cvlib.slice-manifest.v1";

	public static PackResult Execute(string pathOrDirectory, PackOptions options)
	{
		var manifest = ProjectManifest.Load(pathOrDirectory);

		// 1. Target resolution: explicit --target list, else every declared framework when
		//    amalgamating, else the host triple.
		var requested = options.Amalgamate
			? manifest.TargetFrameworks
			: options.Targets is { Count: > 0 }
				? options.Targets
				: [TargetTriple.HostTriple()];

		if (requested.Count == 0)
			throw new InvalidOperationException("The project declares no <TargetFrameworks>; add TargetFrameworks to .cvlproj or pass --target.");

		var targets = requested
			.Select(TargetTriple.Resolve)
			.Distinct(StringComparer.Ordinal)
			.ToList();

		// 2. Output resolution and directory creation.
		var outputPath = string.IsNullOrEmpty(options.OutputPath)
			? Path.Combine(manifest.ProjectDirectory, "bin", $"{manifest.PackageId}.cvlib")
			: Path.GetFullPath(options.OutputPath);

		var outputDirectory = Path.GetDirectoryName(outputPath);
		if (!string.IsNullOrEmpty(outputDirectory))
			Directory.CreateDirectory(outputDirectory);

		// 3. In-process front-end compile.
		using var compile = PackCompilation.Compile(manifest, options.Verbose);

		if (options.Verbose)
		{
			Console.WriteLine($"Compiled {compile.SourceFiles.Count} project file(s) to LLVM IR.");
			Console.WriteLine($"Packing targets:");
			foreach (var target in targets)
				Console.WriteLine($"  -> {target}");
		}

		// 4. Produce per-target object code and shared bitcode.
		var objects = new Dictionary<string, byte[]>(StringComparer.Ordinal);
		foreach (var target in targets)
			objects[target] = compile.ProduceObjectFile(target);

		var bitcode = compile.ProduceBitcode(targets[0]);

		// 5. Assemble Sector 1 (slice manifest), Sector 2 (concatenated objects per triple),
		//    Sector 3 (bitcode ranges), Sector 5 (LZ4 source buffer unless stripped).
		var sector2Stream = new MemoryStream();
		var sector3Stream = new MemoryStream();
		var slices = new List<CvlSliceEntry>(targets.Count);

		foreach (var target in targets)
		{
			var obj = objects[target];
			var sector2Offset = (ulong)sector2Stream.Length;
			sector2Stream.Write(obj);

			var sector3Offset = (ulong)sector3Stream.Length;
			sector3Stream.Write(bitcode);

			slices.Add(new CvlSliceEntry(
				target,
				new CvlSectorRange(sector2Offset, (ulong)obj.Length),
				new CvlSectorRange(sector3Offset, (ulong)bitcode.Length)));
		}

		var manifestJson = JsonSerializer.Serialize(new
		{
			Format = FormatId,
			manifest.PackageId,
			manifest.Version,
			manifest.Dependencies,
			Slices = slices.Select(s => new
			{
				s.Triple,
				Sector2 = new { s.Sector2.Offset, s.Sector2.Length },
				Sector3 = new { s.Sector3.Offset, s.Sector3.Length }
			})
		});

		// Sector 1 = [uint32_le JSON length][JSON][layout metadata "{}"].
		var manifestBytes = Encoding.UTF8.GetBytes(manifestJson);
		var layoutMetadata = Encoding.ASCII.GetBytes("{}");
		var sector1 = new byte[4 + manifestBytes.Length + layoutMetadata.Length];
		BinaryPrimitives.WriteUInt32LittleEndian(sector1, (uint)manifestBytes.Length);
		manifestBytes.CopyTo(sector1, 4);
		layoutMetadata.CopyTo(sector1, 4 + manifestBytes.Length);

		// 6. Build the container.
		var sourceBuffer = options.StripSource ? null : BuildSourceBuffer(compile.SourceFiles, manifest.ProjectDirectory);

		var writer = new CvlArchiveWriter();
		writer.SetSector(1, sector1);
		writer.SetSector(2, sector2Stream.ToArray());
		writer.SetSector(3, sector3Stream.ToArray());
		if (sourceBuffer is not null)
			writer.SetSourceBuffer(sourceBuffer);

		var signed = !options.NoSign;
		if (options.NoSign)
		{
			writer.WriteUnsigned(outputPath);
		}
		else if (!string.IsNullOrEmpty(options.SigningKeyPath))
		{
			var seed = File.ReadAllBytes(options.SigningKeyPath);
			if (seed.Length != 32)
				throw new InvalidOperationException($"Signing key '{options.SigningKeyPath}' must contain exactly 32 raw Ed25519 seed bytes.");

			using var key = Key.Import(SignatureAlgorithm.Ed25519, seed, KeyBlobFormat.RawPrivateKey);
			writer.Write(outputPath, key);
		}
		else
		{
			// v0.2.6 requires install/build-time Ed25519 verification. Use the stable local
			// machine key for ordinary local packs; CI/publishers can supply --sign for a
			// pinned publisher key. --no-sign remains an explicit unsupported-by-install escape hatch.
			var packageCache = new PackageCache();
			using var key = packageCache.LoadSigningKey();
			writer.Write(outputPath, key);
		}

		// 7. Read back the container to report authoritative size and Merkle root.
		using var archive = CvlArchiveReader.Read(outputPath, verifySignature: signed);

		if (options.Verbose)
			Console.WriteLine($"Wrote {outputPath}");

		return new PackResult
		{
			PackageId = manifest.PackageId,
			Version = manifest.Version,
			OutputPath = outputPath,
			FileSize = archive.Header.FileSize,
			MerkleRootHex = Convert.ToHexStringLower(archive.MerkleRootHash),
			Targets = targets
		};
	}

	/// <summary>
	/// Concatenates the project's .cvl sources (already sorted by relative path in PackCompilation)
	/// separated by a newline; the writer applies LZ4 compression to Sector 5.
	/// </summary>
	private static string BuildSourceBuffer(IReadOnlyList<string> sourceFiles, string projectDirectory)
	{
		var builder = new StringBuilder();
		foreach (var file in sourceFiles)
		{
			if (builder.Length > 0)
				builder.Append('\n');

			builder.Append(File.ReadAllText(file));
		}

		return builder.ToString();
	}
}
