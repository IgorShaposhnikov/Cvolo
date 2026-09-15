namespace Cvolo.Packaging;

public sealed class VersionRange
{
	private readonly string _text;
	private readonly SemanticVersion? _lower;
	private readonly SemanticVersion? _upper;
	private readonly bool _floating;
	private readonly int? _major;
	private readonly int? _minor;

	private VersionRange(string text, SemanticVersion? lower, SemanticVersion? upper, bool floating = false, int? major = null, int? minor = null)
	{
		_text = text;
		_lower = lower;
		_upper = upper;
		_floating = floating;
		_major = major;
		_minor = minor;
	}

	public static VersionRange Parse(string? text)
	{
		var range = string.IsNullOrWhiteSpace(text) ? "*" : text.Trim();
		if (range == "*") return new VersionRange(range, null, null, floating: true);
		if (range.EndsWith(".*", StringComparison.Ordinal))
		{
			var parts = range[..^2].Split('.');
			if (parts.Length is < 1 or > 2 || !int.TryParse(parts[0], out var major))
				throw new PackageException(PackageDiagnosticIds.InvalidSemVer, $"Invalid version range '{range}'.");
			int? minor = null;
			if (parts.Length == 2)
			{
				if (!int.TryParse(parts[1], out var parsedMinor))
					throw new PackageException(PackageDiagnosticIds.InvalidSemVer, $"Invalid version range '{range}'.");
				minor = parsedMinor;
			}
			return new VersionRange(range, null, null, floating: true, major: major, minor: minor);
		}

		if (range[0] == '^')
		{
			var lower = SemanticVersion.Parse(range[1..]);
			return new VersionRange(range, lower, new SemanticVersion(lower.Major + 1, 0, 0));
		}

		if (range[0] == '~')
		{
			var lower = SemanticVersion.Parse(range[1..]);
			return new VersionRange(range, lower, new SemanticVersion(lower.Major, lower.Minor + 1, 0));
		}

		var exact = SemanticVersion.Parse(range);
		return new VersionRange(range, exact, exact);
	}

	public bool Allows(SemanticVersion version)
	{
		if (_floating)
			return (!_major.HasValue || version.Major == _major.Value) && (!_minor.HasValue || version.Minor == _minor.Value);
		if (_lower is null) return true;
		if (_upper is not null && _lower.Equals(_upper)) return version.Equals(_lower);
		return version.CompareTo(_lower) >= 0 && (_upper is null || version.CompareTo(_upper) < 0);
	}

	public override string ToString() => _text;
}
