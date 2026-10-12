namespace Cvolo.Compiler.Tooling;

/// <summary>
/// Opaque proof that a generation-owned <see cref="ProjectSnapshot"/> has been fully analyzed and is
/// ready to answer semantic queries. The underlying analyzed object is intentionally not exposed;
/// the LSP stores the handle and never depends on the internal analysis type.
/// </summary>
public sealed class AnalysisReadyHandleV1
{
	internal AnalysisReadyHandleV1(ProjectSnapshot snapshot)
	{
		Snapshot = snapshot;
	}

	/// <summary>
	/// The snapshot this handle certifies.
	/// </summary>
	public ProjectSnapshot Snapshot { get; }
}
