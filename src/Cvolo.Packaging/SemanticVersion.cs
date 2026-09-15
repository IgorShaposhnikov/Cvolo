using System.Text;

namespace Cvolo.Packaging;

/// <summary>
/// A strict Semantic Version 2.0.0 value (MAJOR.MINOR.PATCH with optional
/// prerelease and build metadata). Parsing is validated against the formal
/// grammar; an invalid value raises <see cref="PackageException"/> CVLP3002.
/// </summary>
public sealed class SemanticVersion : IComparable<SemanticVersion>
{
	public int Major { get; }
	public int Minor { get; }
	public int Patch { get; }
	public string Prerelease { get; }
	public string BuildMetadata { get; }

	internal SemanticVersion(int major, int minor, int patch, string prerelease = "", string buildMetadata = "")
	{
		Major = major;
		Minor = minor;
		Patch = patch;
		Prerelease = prerelease;
		BuildMetadata = buildMetadata;
	}

	/// <summary>
	/// Parses a SemVer 2.0 string, throwing CVLP3002 when it is not valid.
	/// </summary>
	public static SemanticVersion Parse(string value)
	{
		if (!TryParse(value, out var version))
			throw new PackageException(PackageDiagnosticIds.InvalidSemVer, $"{value} is not valid SemVer 2.0.", value);

		return version!;
	}

	/// <summary>
	/// Tries to parse a SemVer 2.0 string against the formal grammar.
	/// </summary>
	public static bool TryParse(string? value, out SemanticVersion? version)
	{
		version = null;
		if (string.IsNullOrWhiteSpace(value))
			return false;

		var core = value;
		var buildMetadata = string.Empty;
		var prerelease = string.Empty;
		var hasBuild = false;
		var hasPrerelease = false;

		var buildIndex = core.IndexOf('+');
		if (buildIndex >= 0)
		{
			hasBuild = true;
			buildMetadata = core[(buildIndex + 1)..];
			core = core[..buildIndex];
		}

		// An identifier after '+' must not be empty; absence of '+' is always fine.
		if (hasBuild && (buildMetadata.Length == 0 || !IsValidIdentifiers(buildMetadata, allowLeadingZeros: true)))
			return false;

		var prereleaseIndex = core.IndexOf('-');
		if (prereleaseIndex >= 0)
		{
			hasPrerelease = true;
			prerelease = core[(prereleaseIndex + 1)..];
			core = core[..prereleaseIndex];
		}

		// An identifier after '-' must not be empty; absence of '-' is always fine.
		if (hasPrerelease && (prerelease.Length == 0 || !IsValidIdentifiers(prerelease, allowLeadingZeros: false)))
			return false;

		var parts = core.Split('.');
		if (parts.Length != 3)
			return false;

		if (!int.TryParse(parts[0], out var major) || major < 0 ||
			!int.TryParse(parts[1], out var minor) || minor < 0 ||
			!int.TryParse(parts[2], out var patch) || patch < 0)
			return false;

		if (parts[0].Length > 1 && parts[0][0] == '0')
			return false;
		if (parts[1].Length > 1 && parts[1][0] == '0')
			return false;
		if (parts[2].Length > 1 && parts[2][0] == '0')
			return false;

		version = new SemanticVersion(major, minor, patch, prerelease, buildMetadata);
		return true;
	}

	private static bool IsValidIdentifiers(string text, bool allowLeadingZeros)
	{
		var identifiers = text.Split('.');
		foreach (var identifier in identifiers)
		{
			if (identifier.Length == 0)
				return false;

			foreach (var c in identifier)
			{
				var isAlphanumeric = c is >= '0' and <= '9' or >= 'a' and <= 'z' or >= 'A' and <= 'Z' || c == '-';
				if (!isAlphanumeric)
					return false;
			}

			var isNumeric = identifier.All(static c => c is >= '0' and <= '9');
			if (isNumeric && !allowLeadingZeros && identifier.Length > 1 && identifier[0] == '0')
				return false;
		}

		return true;
	}

	public override string ToString()
	{
		var sb = new StringBuilder();
		sb.Append(Major).Append('.').Append(Minor).Append('.').Append(Patch);
		if (Prerelease.Length > 0)
			sb.Append('-').Append(Prerelease);
		if (BuildMetadata.Length > 0)
			sb.Append('+').Append(BuildMetadata);
		return sb.ToString();
	}

	public override bool Equals(object? obj) => obj is SemanticVersion other && CompareCore(other) == 0 && string.Equals(Prerelease, other.Prerelease, StringComparison.Ordinal);

	public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, Prerelease);

	/// <summary>
	/// SemVer precedence: build metadata is ignored for ordering; prerelease < release.
	/// </summary>
	public int CompareTo(SemanticVersion? other)
	{
		ArgumentNullException.ThrowIfNull(other);
		return CompareCore(other);
	}

	private int CompareCore(SemanticVersion other)
	{
		if (Major != other.Major) return Major.CompareTo(other.Major);
		if (Minor != other.Minor) return Minor.CompareTo(other.Minor);
		if (Patch != other.Patch) return Patch.CompareTo(other.Patch);

		if (Prerelease.Length == 0 && other.Prerelease.Length == 0)
			return 0;
		if (Prerelease.Length == 0)
			return 1; // release > prerelease
		if (other.Prerelease.Length == 0)
			return -1;

		var a = Prerelease.Split('.');
		var b = other.Prerelease.Split('.');
		for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
		{
			if (i >= a.Length) return -1;
			if (i >= b.Length) return 1;

			var isNumA = long.TryParse(a[i], out var numA);
			var isNumB = long.TryParse(b[i], out var numB);

			if (isNumA && isNumB)
			{
				if (numA != numB) return numA.CompareTo(numB);
			}
			else if (isNumA)
			{
				return -1; // numeric identifiers have lower precedence
			}
			else if (isNumB)
			{
				return 1;
			}
			else
			{
				var cmp = string.CompareOrdinal(a[i], b[i]);
				if (cmp != 0) return cmp;
			}
		}

		return 0;
	}
}
