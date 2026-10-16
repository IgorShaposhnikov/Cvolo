using Cvolo.Analysis;

namespace Cvolo.Tests;

public sealed class MonomorphizationIdentityTests
{
	[Fact]
	public void CanonicalKey_ComposesDefinitionAndOrderedTypeArguments()
	{
		var identity = MonomorphizationIdentity.For("Bank.IO.Make", ["Point", "int"]);
		Assert.Equal("Bank.IO.Make<Point,int>", identity.CanonicalKey);
	}

	[Fact]
	public void CanonicalKey_DistinguishesDefinitionsWithTheSameArguments()
	{
		var first = MonomorphizationIdentity.For("Bank.IO.Make", ["Point"]);
		var second = MonomorphizationIdentity.For("Bank.Other.Make", ["Point"]);

		Assert.NotEqual(first.CanonicalKey, second.CanonicalKey);
	}

	[Fact]
	public void CanonicalKey_DistinguishesArgumentOrder()
	{
		var first = MonomorphizationIdentity.For("Bank.IO.Pair", ["int", "bool"]);
		var second = MonomorphizationIdentity.For("Bank.IO.Pair", ["bool", "int"]);

		Assert.NotEqual(first.CanonicalKey, second.CanonicalKey);
	}
}
