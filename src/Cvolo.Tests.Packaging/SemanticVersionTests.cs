using Cvolo.Packaging;

namespace Cvolo.Tests.Packaging;

public sealed class SemanticVersionTests
{
	[Theory]
	[InlineData("1.2.3")]
	[InlineData("0.0.0")]
	[InlineData("10.200.3000")]
	[InlineData("1.2.3-alpha")]
	[InlineData("1.2.3-alpha.1.beta")]
	[InlineData("1.2.3-0.3.7")]
	[InlineData("1.2.3+build.5")]
	[InlineData("1.2.3-rc.1+build.5")]
	public void TryParse_ValidVersions_Succeeds(string value)
	{
		Assert.True(SemanticVersion.TryParse(value, out var version));
		Assert.NotNull(version);
		Assert.Equal(value, version!.ToString());
	}

	[Theory]
	[InlineData("1.2")]
	[InlineData("1")]
	[InlineData("1.2.3.4")]
	[InlineData("01.2.3")]
	[InlineData("1.02.3")]
	[InlineData("1.2.03")]
	[InlineData("1.2.3-alpha.01")] // leading zeros not allowed in numeric prerelease identifiers
	[InlineData("1.2.3-")]
	[InlineData("1.2.3+")]
	[InlineData("1.2.3-alpha..1")]
	[InlineData("v1.2.3")]
	[InlineData("1.2.3-alpha@beta")]
	[InlineData("1.2.3+build metadata")]
	[InlineData("")]
	[InlineData(null)]
	public void TryParse_InvalidVersions_Fails(string? value)
	{
		Assert.False(SemanticVersion.TryParse(value, out var version));
		Assert.Null(version);
	}

	[Fact]
	public void Parse_InvalidValue_ThrowsCVLP3002()
	{
		var ex = Assert.Throws<PackageException>(() => SemanticVersion.Parse("1.2"));
		Assert.Equal(PackageDiagnosticIds.InvalidSemVer, ex.Code);
	}

	[Fact]
	public void Parse_ValidValue_ReturnsVersion()
	{
		var version = SemanticVersion.Parse("1.2.3-rc.2");
		Assert.Equal(1, version.Major);
		Assert.Equal(2, version.Minor);
		Assert.Equal(3, version.Patch);
		Assert.Equal("rc.2", version.Prerelease);
	}

	[Fact]
	public void CompareTo_FollowsSemverPrecedence()
	{
		var ordered = new[]
		{
			SemanticVersion.Parse("1.0.0-alpha"),
			SemanticVersion.Parse("1.0.0-alpha.1"),
			SemanticVersion.Parse("1.0.0-alpha.beta"),
			SemanticVersion.Parse("1.0.0-beta"),
			SemanticVersion.Parse("1.0.0-rc.1"),
			SemanticVersion.Parse("1.0.0"),
			SemanticVersion.Parse("1.0.1"),
			SemanticVersion.Parse("1.1.0")
		};

		for (var i = 0; i < ordered.Length - 1; i++)
			Assert.True(ordered[i].CompareTo(ordered[i + 1]) < 0, $"{ordered[i]} should sort before {ordered[i + 1]}");
	}

	[Fact]
	public void CompareTo_IgnoresBuildMetadata()
	{
		var a = SemanticVersion.Parse("1.0.0+a");
		var b = SemanticVersion.Parse("1.0.0+b");
		Assert.Equal(0, a.CompareTo(b));
		Assert.True(a.Equals(b));
	}

	[Fact]
	public void CompareTo_PrereleaseSortsBeforeRelease()
	{
		Assert.True(SemanticVersion.Parse("2.0.0-rc.1").CompareTo(SemanticVersion.Parse("2.0.0")) < 0);
	}

	[Fact]
	public void CompareTo_MajorMinorPatch_OrderCorrectly()
	{
		Assert.True(SemanticVersion.Parse("0.9.0").CompareTo(SemanticVersion.Parse("1.0.0")) < 0);
		Assert.True(SemanticVersion.Parse("1.10.0").CompareTo(SemanticVersion.Parse("1.2.0")) > 0);
		Assert.True(SemanticVersion.Parse("1.0.10").CompareTo(SemanticVersion.Parse("1.0.2")) > 0);
	}
}
