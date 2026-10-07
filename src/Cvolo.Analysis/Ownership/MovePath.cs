namespace Cvolo.Analysis.Ownership;

/// <summary>
/// The kind of a single projection step in a <see cref="MovePath"/>.
/// </summary>
public enum ProjectionStepKind
{
	/// <summary>A named struct/union field projection.</summary>
	Field,

	/// <summary>A statically-identifiable fixed element index projection.</summary>
	FixedIndex
}

/// <summary>
/// One projection applied to a root variable to reach an owned storage location.
/// <see cref="Field"/> steps carry a field name; <see cref="FixedIndex"/> steps carry a
/// non-negative constant index. The representation is intentionally flat so that path
/// identity comparisons are structural.
/// </summary>
public readonly record struct ProjectionStep(ProjectionStepKind Kind, string? Name, int Index)
{
	public static ProjectionStep Field(string name) => new(ProjectionStepKind.Field, name, -1);

	public static ProjectionStep FixedIndex(int index) => new(ProjectionStepKind.FixedIndex, null, index);

	public override string ToString() => Kind == ProjectionStepKind.Field ? $".{Name}" : $"[{Index}]";
}

/// <summary>
/// The static relationship between a destination move path and a source move path, as
/// classified before code generation. Mirrors the frozen ownership contract's relation set.
/// </summary>
public enum MovePathRelation
{
	/// <summary>No relationship was computed (e.g. one side is untracked).</summary>
	None,

	/// <summary>Both paths designate exactly the same owned storage.</summary>
	Identical,

	/// <summary>Both paths are provably distinct storage locations.</summary>
	Disjoint,

	/// <summary>One path is a strict ancestor or descendant of the other.</summary>
	AncestorDescendantOverlap,

	/// <summary>Aliasing cannot be excluded by the available static information.</summary>
	UnknownAliasOverlap
}

/// <summary>
/// A canonical ownership identity for an owned storage location: a root variable plus an
/// ordered sequence of statically-stable field and fixed-index projections. This is the
/// unit the flow-sensitive ownership state and overlap analysis are keyed on.
/// </summary>
public sealed class MovePath : IEquatable<MovePath>
{
	private readonly ProjectionStep[] _steps;

	public MovePath(string root, ProjectionStep[] steps)
	{
		Root = root;
		_steps = steps;
	}

	public MovePath(string root)
		: this(root, [])
	{
	}

	/// <summary>The root variable or receiver name this path starts from.</summary>
	public string Root { get; }

	/// <summary>The ordered projections from <see cref="Root"/> to the owned location.</summary>
	public IReadOnlyList<ProjectionStep> Steps => _steps;

	public int Depth => _steps.Length;

	/// <summary>Returns a new path extended by one projection step.</summary>
	public MovePath Append(ProjectionStep step)
	{
		var next = new ProjectionStep[_steps.Length + 1];
		Array.Copy(_steps, next, _steps.Length);
		next[^1] = step;
		return new MovePath(Root, next);
	}

	/// <summary>True when this path is equal to or a strict prefix of <paramref name="other"/>.</summary>
	public bool IsPrefixOf(MovePath other)
	{
		if (!string.Equals(Root, other.Root, StringComparison.Ordinal) || Depth > other.Depth)
			return false;

		for (var i = 0; i < _steps.Length; i++)
			if (!_steps[i].Equals(other._steps[i]))
				return false;

		return true;
	}

	/// <summary>True when this path is a strict prefix of <paramref name="other"/>.</summary>
	public bool IsStrictAncestorOf(MovePath other) => Depth < other.Depth && IsPrefixOf(other);

	public bool Equals(MovePath? other)
	{
		if (other is null)
			return false;
		if (ReferenceEquals(this, other))
			return true;
		if (!string.Equals(Root, other.Root, StringComparison.Ordinal) || Depth != other.Depth)
			return false;

		for (var i = 0; i < _steps.Length; i++)
			if (!_steps[i].Equals(other._steps[i]))
				return false;

		return true;
	}

	public override bool Equals(object? obj) => obj is MovePath path && Equals(path);

	public override int GetHashCode()
	{
		var hash = new HashCode();
		hash.Add(Root, StringComparer.Ordinal);
		foreach (var step in _steps)
			hash.Add(step);
		return hash.ToHashCode();
	}

	public override string ToString()
	{
		var builder = new System.Text.StringBuilder(Root);
		foreach (var step in _steps)
			builder.Append(step.ToString());
		return builder.ToString();
	}

	/// <summary>
	/// Returns the dotted field path (<c>root.field.sub</c>) for field-only projections, or
	/// <c>null</c> when the path contains a fixed-index step and therefore cannot be represented
	/// as a nested field path.
	/// </summary>
	public string? ToFieldPathString()
	{
		if (_steps.Length == 0)
			return Root;

		var builder = new System.Text.StringBuilder(Root);
		foreach (var step in _steps)
		{
			if (step.Kind != ProjectionStepKind.Field)
				return null;
			builder.Append('.').Append(step.Name);
		}

		return builder.ToString();
	}

	/// <summary>
	/// Classifies the static relationship between two tracked move paths. Distinct roots are
	/// disjoint; paths sharing a root are identical, ancestor/descendant, or disjoint siblings
	/// depending on where their projection sequences first diverge.
	/// </summary>
	public static MovePathRelation Classify(MovePath destination, MovePath source)
	{
		if (!string.Equals(destination.Root, source.Root, StringComparison.Ordinal))
			return MovePathRelation.Disjoint;

		var shared = Math.Min(destination.Depth, source.Depth);
		for (var i = 0; i < shared; i++)
			if (!destination._steps[i].Equals(source._steps[i]))
				return MovePathRelation.Disjoint;

		if (destination.Depth == source.Depth)
			return MovePathRelation.Identical;

		return MovePathRelation.AncestorDescendantOverlap;
	}
}
