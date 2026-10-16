namespace Cvolo.Analysis;

/// <summary>
/// The canonical identity of a monomorphized specialization: the selected definition together with
/// the ordered concrete type arguments it was instantiated with.
/// </summary>
/// <remarks>
/// A specialization's cache key must be derived from this identity rather than assembled ad hoc from a
/// display name. Two distinct definitions with the same spelling, or two instantiations with the same
/// name but different argument order, must never collapse onto one key. Routing every registration and
/// lookup through <see cref="CanonicalKey"/> keeps that identity in a single place.
/// </remarks>
public readonly record struct MonomorphizationIdentity(string Definition, string TypeArguments)
{
	/// <summary>
	/// Builds an identity from a fully qualified, already-mangled definition name and the ordered
	/// names of the concrete type arguments filling its parameters.
	/// </summary>
	public static MonomorphizationIdentity For(string definition, IEnumerable<string> typeArgumentNames)
	{
		ArgumentNullException.ThrowIfNull(definition);
		ArgumentNullException.ThrowIfNull(typeArgumentNames);
		return new MonomorphizationIdentity(definition, string.Join(",", typeArgumentNames));
	}

	/// <summary>
	/// The unique key for this specialization. Whitespace is not significant; callers still run the
	/// result through the binder's generic-name normalizer when they write it into a symbol table.
	/// </summary>
	public string CanonicalKey => $"{Definition}<{TypeArguments}>";
}
