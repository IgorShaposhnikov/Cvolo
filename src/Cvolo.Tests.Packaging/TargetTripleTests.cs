using Cvolo.Packaging;

namespace Cvolo.Tests.Packaging;

public sealed class TargetTripleTests
{
	[Theory]
	[InlineData("win-x64", "x86_64-pc-windows-msvc")]
	[InlineData("linux-x64", "x86_64-pc-linux-gnu")]
	[InlineData("linux-arm64", "aarch64-pc-linux-gnu")]
	[InlineData("osx-x64", "x86_64-apple-darwin")]
	[InlineData("osx-arm64", "aarch64-apple-darwin")]
	public void Resolve_KnownPortableNames_MapToCanonicalTriples(string portable, string expected)
	{
		Assert.Equal(expected, TargetTriple.Resolve(portable));
	}

	[Fact]
	public void Resolve_IsCaseInsensitive()
	{
		Assert.Equal("x86_64-pc-windows-msvc", TargetTriple.Resolve("WIN-X64"));
	}

	[Fact]
	public void Resolve_UnknownValue_PassesThroughUnchanged()
	{
		const string triple = "riscv64-unknown-elf";
		Assert.Equal(triple, TargetTriple.Resolve(triple));
	}

	[Fact]
	public void Resolve_TrimsWhitespace()
	{
		Assert.Equal("x86_64-pc-windows-msvc", TargetTriple.Resolve("  win-x64  "));
	}

	[Fact]
	public void HostTriple_IsStableUnderResolve()
	{
		var host = TargetTriple.HostTriple();
		Assert.False(string.IsNullOrEmpty(host));
		Assert.Equal(host, TargetTriple.Resolve(host));
		Assert.Contains('-', host);
	}
}
