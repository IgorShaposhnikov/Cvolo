using Cvolo.Packaging;
using Cvolo.Projects;

namespace Cvolo.Tests.Packaging;

public sealed class PackageSdkDependencyResolverTests
{
	[Theory]
	[InlineData("using System.Console;\nint F() { return 0; }", "System.Console")]
	[InlineData("using Sdk = System;\nint F() { Sdk.Console.WriteLine(\"x\"); return 0; }", "System.Console")]
	[InlineData("int F() { return System.Math.Int.Abs(-1); }", "System.Math")]
	public void SourceScan_FindsSdkNamespaceDependencies(string source, string expectedNamespace)
	{
		var namespaces = PackageSdkDependencyResolver.ExtractSystemNamespaces(source);

		Assert.Contains(expectedNamespace, namespaces);
	}

	[Fact]
	public void SourceScan_DoesNotTreatIdentifierSuffixAsRootedSystemNamespace()
	{
		var namespaces = PackageSdkDependencyResolver.ExtractSystemNamespaces(
			"int F() { return MySystem.Math.Int.Abs(-1); }");

		Assert.DoesNotContain("System.Math", namespaces);
	}

	[Fact]
	public void ArtifactCollection_UsesImplementationSourceBundleEvenWhenApiHasNoSdkImport()
	{
		var artifact = new ResolvedPackageArtifacts(
			PackageId: "SdkBackedPackage",
			Version: "1.0.0",
			NativeObjectPath: null,
			BitcodePath: "SdkBackedPackage.bc",
			ApiMetadata: new PackageApiMetadata(),
			NativeLibraries: [],
			TemplateUnits: [],
			SourceFallbackUnits: [],
			UsesSourceFallback: false,
			SourceFiles:
			[
				new PackageSourceFile(
					"SdkBackedPackage",
					"1.0.0",
					"Implementation.cvl",
					"/package/Implementation.cvl",
					"namespace Hidden; int F() { System.Console.WriteLine(\"package\"); return 0; }")
			]);

		var namespaces = PackageSdkDependencyResolver.Collect([artifact]);

		Assert.Contains("System.Console", namespaces);
	}
}
