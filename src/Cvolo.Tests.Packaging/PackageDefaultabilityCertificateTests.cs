using System.Diagnostics;
using Cvolo.Core.Packages;
using Cvolo.Drivers;
using Cvolo.Packaging;
using Cvolo.Tests.Shared;

namespace Cvolo.Tests.Packaging;

/// <summary>
/// the producer emits a build-scoped type-level defaultability certificate into
/// Sector 1 API metadata for each public non-generic struct/union, and consumers honor it rather than
/// re-deriving defaultability from rehydrated fields (which cannot see destructors/resource state).
/// </summary>
public sealed class PackageDefaultabilityCertificateTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "cvolo-default-cert-" + Guid.NewGuid().ToString("N"));

	public PackageDefaultabilityCertificateTests()
	{
		Directory.CreateDirectory(_root);
	}

	[Fact]
	public void Pack_EmitsEligibleAndIneligibleCertificates_InSector1Metadata()
	{
		RequireClang();

		var libraryDirectory = Path.Combine(_root, "libcert");
		Directory.CreateDirectory(libraryDirectory);
		WriteLibraryProject(libraryDirectory, "CertLib");
		File.WriteAllText(Path.Combine(libraryDirectory, "Types.cvl"), """
namespace CertLib;

public struct Point {
    public int X;
    public int Y;
}

public struct Handle {
    public int Value;
}

extension Handle {
    ~Handle() { }
}
""");

		var packagePath = PackLibrary(libraryDirectory, "CertLib");

		using var archive = CvlArchiveReader.Read(packagePath, allowUnsigned: true);
		var metadata = PackageApiMetadata.Read(archive);
		var certificates = metadata.GetDefaultabilityCertificates();

		// A plain data struct is proven eligible by the producer.
		Assert.True(certificates.TryGetValue("CertLib.Point", out var point), "expected a certificate for CertLib.Point");
		Assert.Equal(PackageApiDefaultability.Eligible, point!.State);
		Assert.False(string.IsNullOrEmpty(point.Fingerprint), "eligible certificate must be fingerprint-bound");

		// A struct with a destructor must be proven ineligible so the consumer never fabricates a
		// zero default that skips the resource lifecycle.
		Assert.True(certificates.TryGetValue("CertLib.Handle", out var handle), "expected a certificate for CertLib.Handle");
		Assert.Equal(PackageApiDefaultability.Ineligible, handle!.State);

		// Without an explicit certificate map the API metadata carries no proof at all; consumers
		// then fail closed (Unverified), never assuming eligibility.
		var bare = PackageApiMetadata.FromCompilationUnits(ParseUnits(Path.Combine(libraryDirectory, "Types.cvl")));
		Assert.Empty(bare.GetDefaultabilityCertificates());
	}

	[Fact]
	public void Build_AppDefaultingImportedEligibleStruct_Succeeds()
	{
		RequireClang();

		var libraryDirectory = Path.Combine(_root, "libeligible");
		Directory.CreateDirectory(libraryDirectory);
		WriteLibraryProject(libraryDirectory, "EligLib");
		File.WriteAllText(Path.Combine(libraryDirectory, "Point.cvl"), """
namespace EligLib;

public struct Point {
    public int X;
    public int Y;
}
""");
		var packagePath = PackLibrary(libraryDirectory, "EligLib");

		var appDirectory = WriteConsumerApp("EligLib", packagePath, "EligApp", """
using EligLib;

int main() {
    Point p = default(Point);
    return p.X + p.Y;
}
""");

		var (exitCode, output) = CompileApp(packagePath, appDirectory);
		Assert.True(exitCode == 0, output);
	}

	[Fact]
	public void Build_AppDefaultingImportedDestructorStruct_FailsWithProducerCause()
	{
		RequireClang();

		var libraryDirectory = Path.Combine(_root, "libresource");
		Directory.CreateDirectory(libraryDirectory);
		WriteLibraryProject(libraryDirectory, "ResLib");
		File.WriteAllText(Path.Combine(libraryDirectory, "Handle.cvl"), """
namespace ResLib;

public struct Handle {
    public int Value;
}

extension Handle {
    ~Handle() { }
}
""");
		var packagePath = PackLibrary(libraryDirectory, "ResLib");

		var appDirectory = WriteConsumerApp("ResLib", packagePath, "ResApp", """
using ResLib;

int main() {
    Handle h = default(Handle);
    return h.Value;
}
""");

		var (exitCode, output) = CompileApp(packagePath, appDirectory);
		Assert.True(exitCode != 0, "defaulting an imported resource struct must be rejected");
		Assert.Contains("CVL4196", output);
	}

	private static void WriteLibraryProject(string directory, string assemblyName)
	{
		File.WriteAllText(Path.Combine(directory, $"{assemblyName}.cvlproj"), $"""
<Project Sdk="Cvolo.Sdk">
  <PropertyGroup>
    <OutputType>Library</OutputType>
    <AssemblyName>{assemblyName}</AssemblyName>
    <PackageId>{assemblyName}</PackageId>
    <Version>1.0.0</Version>
  </PropertyGroup>
</Project>
""");
	}

	private string PackLibrary(string libraryDirectory, string packageId)
	{
		var signingKey = Path.Combine(_root, $"{packageId}.key");
		File.WriteAllBytes(signingKey, Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());
		var packagePath = Path.Combine(_root, $"{packageId}.1.0.0.cvlib");
		PackPipeline.Execute(libraryDirectory, new PackOptions
		{
			OutputPath = packagePath,
			SigningKeyPath = signingKey,
			Targets = [TargetTriple.HostTriple()]
		});
		return packagePath;
	}

	private string WriteConsumerApp(string packageId, string packagePath, string assemblyName, string mainSource)
	{
		var cache = new PackageCache(Path.Combine(_root, $"{assemblyName}-cache"));
		new PackageInstaller(cache).InstallFromFile(packagePath);

		var appDirectory = Path.Combine(_root, assemblyName);
		Directory.CreateDirectory(appDirectory);
		File.WriteAllText(Path.Combine(appDirectory, "App.cvlproj"), $"""
<Project Sdk="Cvolo.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <AssemblyName>{assemblyName}</AssemblyName>
    <PackageId>{assemblyName}</PackageId>
    <Version>1.0.0</Version>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="{packageId}" Version="1.0.0" />
  </ItemGroup>
</Project>
""");
		File.WriteAllText(Path.Combine(appDirectory, "Main.cvl"), mainSource);

		new LockFile
		{
			Sources = [],
			Packages = new Dictionary<string, LockedPackage>(StringComparer.OrdinalIgnoreCase)
			{
				[packageId] = new("1.0.0", LocalFeed.ComputeHash(packagePath), new Dictionary<string, string>())
			}
		}.Write(Path.Combine(appDirectory, "cvolo.lock.json"));

		return appDirectory;
	}

	private (int ExitCode, string Output) CompileApp(string packagePath, string appDirectory)
	{
		var cache = new PackageCache(Path.Combine(_root, $"{Path.GetFileNameWithoutExtension(appDirectory)}-cache"));
		var driverType = typeof(ICompilerDriver).Assembly.GetType("Cvolo.Drivers.CompilerDriver", throwOnError: true)!;
		var driver = Assert.IsAssignableFrom<ICompilerDriver>(Activator.CreateInstance(driverType, cache));
		var projectPath = Path.Combine(appDirectory, "App.cvlproj");

		var originalOut = Console.Out;
		var originalError = Console.Error;
		using var output = new StringWriter();
		using var error = new StringWriter();
		try
		{
			Console.SetOut(output);
			Console.SetError(error);
			var exitCode = driver.Compile(projectPath, llvmOnly: false, isShared: false, emitIr: false, optLevel: "O0", verbose: true);
			return (exitCode, output.ToString() + error.ToString());
		}
		finally
		{
			Console.SetOut(originalOut);
			Console.SetError(originalError);
		}
	}

	private static IEnumerable<Cvolo.Core.AST.Base.CompilationUnitSyntax> ParseUnits(string path)
	{
		var text = File.ReadAllText(path);
		var context = new Cvolo.Core.Diagnostics.CompilationContext(text, path);
		var unit = new Cvolo.Syntax.Antlr.AntlrSyntaxParser().Parse(context);
		return unit is null ? [] : [unit];
	}

	private static void RequireClang()
	{
		if (NativeToolchain.FindClang() is null)
			Assert.Skip("clang not available; package Sector 3 build integration requires clang/LLVM.");
	}

	public void Dispose()
	{
		try
		{
			if (Directory.Exists(_root))
				Directory.Delete(_root, recursive: true);
		}
		catch
		{
			// Best-effort cleanup; failed compiler output can remain locked briefly on Windows.
		}
	}
}
