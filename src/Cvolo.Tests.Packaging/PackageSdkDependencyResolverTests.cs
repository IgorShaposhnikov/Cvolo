using Cvolo.Packaging;
using Cvolo.Projects;
using Cvolo.Tests.Shared;

namespace Cvolo.Tests.Packaging;

public sealed class PackageSdkDependencyResolverTests
{
	[Theory]
	[InlineData("PackageSdkDependencyResolver/UsingSystemConsole.cvl", "System.Console")]
	[InlineData("PackageSdkDependencyResolver/AliasSystem.cvl", "System.Console")]
	[InlineData("PackageSdkDependencyResolver/QualifiedMath.cvl", "System.Math")]
	public void SourceScan_FindsSdkNamespaceDependencies(string fixture, string expectedNamespace)
	{
		var namespaces = PackageSdkDependencyResolver.ExtractSystemNamespaces(TestCases.ReadText(fixture));

		Assert.Contains(expectedNamespace, namespaces);
	}

	[Fact]
	public void SourceScan_DoesNotTreatIdentifierSuffixAsRootedSystemNamespace()
	{
		var namespaces = PackageSdkDependencyResolver.ExtractSystemNamespaces(
			TestCases.ReadText("PackageSdkDependencyResolver/MySystemSuffix.cvl"));

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
					TestCases.ReadText("PackageSdkDependencyResolver/ArtifactImplementation.cvl"))
			]);

		var namespaces = PackageSdkDependencyResolver.Collect([artifact]);

		Assert.Contains("System.Console", namespaces);
	}
}
