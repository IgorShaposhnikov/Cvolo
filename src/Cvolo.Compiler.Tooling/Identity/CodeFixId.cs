namespace Cvolo.Compiler.Tooling;

/// <summary>
/// Opaque identity of one compiler-provided code fix within one immutable project snapshot.
/// Equality is meaningful only for ids obtained from the same snapshot; the backing
/// representation is not public API and must not be persisted or serialized as a durable identity.
/// </summary>
public readonly record struct CodeFixId
{
	internal Guid SnapshotToken { get; }
	internal int Value { get; }

	internal CodeFixId(Guid snapshotToken, int value)
	{
		SnapshotToken = snapshotToken;
		Value = value;
	}

	/// <inheritdoc />
	public override string ToString() => $"code-fix:{Value}";
}
