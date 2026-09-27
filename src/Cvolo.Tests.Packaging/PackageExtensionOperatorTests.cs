using System.Diagnostics;
using Cvolo.Core.AST.Declarations;
using Cvolo.Core.Packages;
using Cvolo.Drivers;
using Cvolo.Packaging;

namespace Cvolo.Tests.Packaging;

/// <summary>
/// A package's public extension API has to survive packing: a receiverless associated function and
/// an operator overload are not free functions, so the consumer can only reach them if the whole
/// extension block - with the operator's own syntax - comes back out of the package. The API
/// metadata channel deliberately carries types and free functions only, so the extension block
/// travels as package template source, and these tests pin that path end to end: what the template
/// source rehydrates, and that a consuming app compiles against it and runs.
/// </summary>
public sealed class PackageExtensionOperatorTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "cvolo-pkg-operators-" + Guid.NewGuid().ToString("N"));

	public PackageExtensionOperatorTests()
	{
		Directory.CreateDirectory(_root);
	}

	[Fact]
	public void TemplateSource_RehydratesAssociatedFunctionsAndOperatorOverloads()
	{
		var (packagePath, _) = PackGeometry();

		using var archive = CvlArchiveReader.Read(packagePath);
		var templateUnit = Assert.Single(PackageTemplateSource.Read(archive, "Geometry", "1.0.0"));

		var extension = Assert.Single(
			templateUnit.NamespaceDeclaration!.Members
				.OfType<ExtensionDeclarationSyntax>()
				.Where(declaration => declaration.ExtendedTypeName == "Vec2"));

		// The operator comes back as an operator declaration, not as a free function named `+`:
		// the spelling is what the consumer's expression resolves through.
		var add = Assert.Single(extension.Operators);
		Assert.Equal("+", add.Operator);
		Assert.Equal("op_add", add.OperatorToken);
		Assert.Equal("Vec2", add.ReturnType);
		Assert.False(add.IsUnary);
		Assert.Equal(["Vec2", "Vec2"], add.Parameters.Select(parameter => parameter.Type).ToArray());

		// Both callable forms of an extension member stay distinguishable: `Scalar` is receiverless
		// (leading dot) while `Norm` is receiver-backed.
		Assert.Equal(["Scalar", "Norm"], extension.Methods.Select(method => method.Name).ToArray());
		Assert.True(extension.Methods[0].IsAssociated);
		Assert.False(extension.Methods[1].IsAssociated);
	}

	[Fact]
	public void PackagedOperatorsAndAssociatedFunctions_StayCallableFromAConsumerApp()
	{
		if (FindClang() is null)
			Assert.Skip("clang not available; the consuming app needs clang/LLVM to link.");

		var (packagePath, libraryDirectory) = PackGeometry();

		var cache = new PackageCache(Path.Combine(_root, "cache"));
		new PackageInstaller(cache).InstallFromFile(packagePath);

		var appDirectory = Path.Combine(_root, "app");
		Directory.CreateDirectory(appDirectory);
		var projectPath = Path.Combine(appDirectory, "App.cvlproj");
		File.WriteAllText(projectPath, """
<Project Sdk="Cvolo.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <AssemblyName>App</AssemblyName>
    <PackageId>App</PackageId>
    <Version>1.0.0</Version>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Geometry" Version="1.0.0" />
  </ItemGroup>
</Project>
""");
		File.WriteAllText(Path.Combine(appDirectory, "Main.cvl"), """
using Geometry;

int main() {
    Vec2 a = Vec2 { X: 1, Y: 2 };
    Vec2 b = Vec2 { X: 3, Y: 5 };
    Vec2 sum = a + b;
    int scalar = Vec2.Scalar(sum);
    int norm = sum.Norm(sum);
    return scalar + norm - 69;
}
""");

		new LockFile
		{
			Sources = [],
			Packages = new Dictionary<string, LockedPackage>(StringComparer.OrdinalIgnoreCase)
			{
				["Geometry"] = new("1.0.0", LocalFeed.ComputeHash(packagePath), new Dictionary<string, string>())
			}
		}.Write(Path.Combine(appDirectory, "cvolo.lock.json"));

		var driverType = typeof(ICompilerDriver).Assembly.GetType("Cvolo.Drivers.CompilerDriver", throwOnError: true)!;
		var driver = Assert.IsAssignableFrom<ICompilerDriver>(Activator.CreateInstance(driverType, cache));
		var (exitCode, buildOutput) = CompileWithCapturedOutput(driver, projectPath);
		Assert.True(exitCode == 0, buildOutput);

		// The extension block is delivered as package template source, so the operator and the
		// associated function are implemented in the package's own code: no C ABI export, and the
		// running program is the proof that all three call forms resolved to the packaged bodies.
		var executable = Path.Combine(appDirectory, "bin", "Debug", OperatingSystem.IsWindows() ? "App.exe" : "App");
		Assert.True(File.Exists(executable));
		using var process = Process.Start(new ProcessStartInfo
		{
			FileName = executable,
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true
		});
		Assert.NotNull(process);
		Assert.True(process!.WaitForExit(10_000), $"Compiled app '{executable}' did not exit within 10 seconds.");
		Assert.Equal(0, process.ExitCode);
		Assert.True(Directory.Exists(libraryDirectory));
	}

	[Fact]
	public void Consumer_Adding_An_Operator_To_A_Packaged_Type_Is_Rejected()
	{
		if (FindClang() is null)
			Assert.Skip("clang not available; the consuming app needs clang/LLVM to link.");

		var (packagePath, _) = PackGeometry();

		var cache = new PackageCache(Path.Combine(_root, "hijack-cache"));
		new PackageInstaller(cache).InstallFromFile(packagePath);

		var appDirectory = Path.Combine(_root, "hijack-app");
		Directory.CreateDirectory(appDirectory);
		var projectPath = Path.Combine(appDirectory, "App.cvlproj");
		File.WriteAllText(projectPath, """
<Project Sdk="Cvolo.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <AssemblyName>Hijack</AssemblyName>
    <PackageId>Hijack</PackageId>
    <Version>1.0.0</Version>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Geometry" Version="1.0.0" />
  </ItemGroup>
</Project>
""");
		// A named associated function on someone else's type is fine - that is the whole point of
		// the leading dot. Redefining an operator is not: the meaning of `a + b` would then depend
		// on which packages a file happens to import (spec §16).
		File.WriteAllText(Path.Combine(appDirectory, "Main.cvl"), """
using Geometry;

public extension Vec2 {
    public int .Extra(Vec2 value) {
        return value.X;
    }

    public Vec2 operator -(Vec2 value) {
        return value;
    }
}

int main() {
    Vec2 a = Vec2 { X: 1, Y: 2 };
    return a.Extra() - 1;
}
""");

		new LockFile
		{
			Sources = [],
			Packages = new Dictionary<string, LockedPackage>(StringComparer.OrdinalIgnoreCase)
			{
				["Geometry"] = new("1.0.0", LocalFeed.ComputeHash(packagePath), new Dictionary<string, string>())
			}
		}.Write(Path.Combine(appDirectory, "cvolo.lock.json"));

		var driverType = typeof(ICompilerDriver).Assembly.GetType("Cvolo.Drivers.CompilerDriver", throwOnError: true)!;
		var driver = Assert.IsAssignableFrom<ICompilerDriver>(Activator.CreateInstance(driverType, cache));
		var (exitCode, buildOutput) = CompileWithCapturedOutput(driver, projectPath);

		Assert.NotEqual(0, exitCode);
		Assert.Contains("Operator '-' for 'Vec2' must be declared in the same package or project that declares the type.", buildOutput);
	}

	/// <summary>
	/// Packs a library whose public extension contributes a receiverless associated function, a
	/// receiver-backed method, and a binary operator overload. Returns the package path and the
	/// library directory that was packed.
	/// </summary>
	private (string PackagePath, string LibraryDirectory) PackGeometry()
	{
		var libraryDirectory = Path.Combine(_root, "libgeometry");
		Directory.CreateDirectory(libraryDirectory);
		File.WriteAllText(Path.Combine(libraryDirectory, "Geometry.cvlproj"), """
<Project Sdk="Cvolo.Sdk">
  <PropertyGroup>
    <OutputType>Library</OutputType>
    <AssemblyName>Geometry</AssemblyName>
    <PackageId>Geometry</PackageId>
    <Version>1.0.0</Version>
  </PropertyGroup>
</Project>
""");
		File.WriteAllText(Path.Combine(libraryDirectory, "Geometry.cvl"), """
namespace Geometry;

public struct Vec2 {
    public int X;
    public int Y;
}

public extension Vec2 {
    public Vec2 operator +(Vec2 left, Vec2 right) {
        val Vec2 result(X: left.X + right.X, Y: left.Y + right.Y);
        return result;
    }

    public int .Scalar(Vec2 value) {
        return value.X;
    }

    public int Norm(refvar this, Vec2 value) {
        return value.X * value.X + value.Y * value.Y;
    }
}
""");

		var signingKey = Path.Combine(_root, "geometry.key");
		File.WriteAllBytes(signingKey, Enumerable.Range(64, 32).Select(i => (byte)i).ToArray());
		var packagePath = Path.Combine(_root, "Geometry.1.0.0.cvlib");
		PackPipeline.Execute(libraryDirectory, new PackOptions
		{
			OutputPath = packagePath,
			SigningKeyPath = signingKey,
			Targets = [TargetTriple.HostTriple()]
		});

		return (packagePath, libraryDirectory);
	}

	private static (int ExitCode, string Output) CompileWithCapturedOutput(ICompilerDriver driver, string projectPath)
	{
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

	private static string? FindClang()
	{
		var local = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "clang.exe" : "clang");
		if (File.Exists(local))
			return local;

		var executable = OperatingSystem.IsWindows() ? "clang.exe" : "clang";
		foreach (var directory in Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [])
		{
			if (string.IsNullOrWhiteSpace(directory))
				continue;

			var candidate = Path.Combine(directory, executable);
			if (File.Exists(candidate))
				return candidate;
		}

		return null;
	}

	public void Dispose()
	{
		GC.SuppressFinalize(this);
		if (Directory.Exists(_root))
			Directory.Delete(_root, recursive: true);
	}
}
