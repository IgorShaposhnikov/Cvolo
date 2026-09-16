namespace Cvolo.Packaging;

/// <summary>
/// Packaging linkage profile. MVP only supports StripBinaries (Profile B);
/// SystemLink (Profile C) maps onto the same pipeline for now.
/// </summary>
public enum LinkageProfile
{
	StripBinaries = 0
}

/// <summary>
/// Options controlling the <see cref="PackPipeline"/> run.
/// </summary>
public sealed class PackOptions
{
	/// <summary>The explicit target triples to pack (empty = host-only unless amalgamating).</summary>
	public IReadOnlyList<string> Targets { get; init; } = [];

	/// <summary>When true, pack every target framework declared in the manifest.</summary>
	public bool Amalgamate { get; init; }

	/// <summary>The output .cvlib path. Empty = the pipeline's default (bin/&lt;PackageId&gt;.cvlib).</summary>
	public string OutputPath { get; init; } = string.Empty;

	/// <summary>The linkage profile (MVP: StripBinaries only).</summary>
	public LinkageProfile Profile { get; init; } = LinkageProfile.StripBinaries;

	/// <summary>When true, Sector 5 (source buffer) is omitted.</summary>
	public bool StripSource { get; init; }

	/// <summary>Optional raw 32-byte Ed25519 private-key seed. When omitted, the local machine signing key is used.</summary>
	public string? SigningKeyPath { get; init; }

	/// <summary>When true, the archive is deliberately written unsigned (96 zero signature bytes).</summary>
	public bool NoSign { get; init; }

	/// <summary>Enables progress logging to the console.</summary>
	public bool Verbose { get; init; }

	/// <summary>Build configuration used for compiler intermediates during developer builds.</summary>
	public string Configuration { get; init; } = BuildOutputLayout.DefaultConfiguration;
}

/// <summary>
/// The outcome of a successful <see cref="PackPipeline"/> run.
/// </summary>
public sealed class PackResult
{
	public required string PackageId { get; init; }

	public required string Version { get; init; }

	public required string OutputPath { get; init; }

	public required ulong FileSize { get; init; }

	public required string MerkleRootHex { get; init; }

	public required IReadOnlyList<string> Targets { get; init; }
}
