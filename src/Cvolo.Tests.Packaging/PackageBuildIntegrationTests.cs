using System.Diagnostics;
using Cvolo.CLI.Packages;
using Cvolo.Drivers;
using Cvolo.Packaging;

namespace Cvolo.Tests.Packaging;

public sealed class PackageBuildIntegrationTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "cvolo-package-build-" + Guid.NewGuid().ToString("N"));

	public PackageBuildIntegrationTests()
	{
		Directory.CreateDirectory(_root);
	}

	[Fact]
	public void Build_AppUsingPackedLibrary_ImportsCvoloModuleWithoutCAbi()
	{
		RequireClang();

		var libraryDirectory = Path.Combine(_root, "libfoo");
		Directory.CreateDirectory(libraryDirectory);
		File.WriteAllText(Path.Combine(libraryDirectory, "Foo.cvlproj"), """
<Project Sdk="Cvolo.Sdk">
  <PropertyGroup>
    <OutputType>Library</OutputType>
    <AssemblyName>Foo</AssemblyName>
    <PackageId>Foo</PackageId>
    <Version>1.0.0</Version>
  </PropertyGroup>
</Project>
""");
		File.WriteAllText(Path.Combine(libraryDirectory, "Foo.cvl"), """
namespace Foo;

public struct Pair {
    public int X;
    public int Y;
}

public enum Mode : byte {
    Slow = 0,
    Fast = 1
}

public global int Bias = 5;

public int AddPair(Pair pair) {
    return pair.X + pair.Y;
}
""");

		var signingKey = Path.Combine(_root, "dev.key");
		File.WriteAllBytes(signingKey, Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());
		var packagePath = Path.Combine(_root, "Foo.1.0.0.cvlib");
		PackPipeline.Execute(libraryDirectory, new PackOptions
		{
			OutputPath = packagePath,
			SigningKeyPath = signingKey,
			Targets = [TargetTriple.HostTriple()],
			StripSource = true
		});

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
    <PackageReference Include="Foo" Version="1.0.0" />
  </ItemGroup>
</Project>
""");
		File.WriteAllText(Path.Combine(appDirectory, "Main.cvl"), """
using Foo;

int main() {
    Pair pair = Pair { X: 2, Y: 3 };
    Mode mode = Mode.Fast;
    return AddPair(pair) + Bias + (int)mode - 11;
}
""");

		new LockFile
		{
			Sources = [],
			Packages = new Dictionary<string, LockedPackage>(StringComparer.OrdinalIgnoreCase)
			{
				["Foo"] = new("1.0.0", LocalFeed.ComputeHash(packagePath), new Dictionary<string, string>())
			}
		}.Write(Path.Combine(appDirectory, "cvolo.lock.json"));

		var driverType = typeof(ICompilerDriver).Assembly.GetType("Cvolo.Drivers.CompilerDriver", throwOnError: true)!;
		var driver = Assert.IsAssignableFrom<ICompilerDriver>(Activator.CreateInstance(driverType, cache));
		var (exitCode, buildOutput) = CompileWithCapturedOutput(driver, projectPath);
		Assert.True(exitCode == 0, buildOutput);

		var executable = Path.Combine(appDirectory, "bin", "Debug", OperatingSystem.IsWindows() ? "App.exe" : "App");
		Assert.True(File.Exists(executable), $"Expected compiler output '{executable}'.");
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
	}

	[Fact]
	public void LocalWorkflow_PackAddBuildRun_UsesCvoloModuleImport()
	{
		RequireClang();

		var feedDirectory = Path.Combine(_root, "feed");
		Directory.CreateDirectory(feedDirectory);

		var libraryDirectory = Path.Combine(_root, "libfoo-cli");
		Directory.CreateDirectory(libraryDirectory);
		File.WriteAllText(Path.Combine(libraryDirectory, "Foo.cvlproj"), """
<Project Sdk="Cvolo.Sdk">
  <PropertyGroup>
    <OutputType>Library</OutputType>
    <AssemblyName>Foo</AssemblyName>
    <PackageId>Foo</PackageId>
    <Version>1.0.0</Version>
  </PropertyGroup>
</Project>
""");
		File.WriteAllText(Path.Combine(libraryDirectory, "Foo.cvl"), """
namespace Foo;

public struct Pair {
    public int X;
    public int Y;
}

public enum Mode : byte {
    Slow = 0,
    Fast = 1
}

public global int Bias = 5;

public int AddPair(Pair pair) {
    return pair.X + pair.Y;
}
""");

		var signingKey = Path.Combine(_root, "workflow.key");
		File.WriteAllBytes(signingKey, Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
		var packagePath = Path.Combine(feedDirectory, "Foo.1.0.0.cvlib");
		using var packOutput = new StringWriter();
		using var packError = new StringWriter();
		var pack = new PackCommand(packOutput, packError);

		var packExitCode = pack.Parse($"\"{libraryDirectory}\" --output \"{packagePath}\" --sign \"{signingKey}\" --strip-source").Invoke();

		Assert.Equal(0, packExitCode);
		Assert.True(File.Exists(packagePath));
		Assert.Contains("Packed Foo 1.0.0", packOutput.ToString());
		Assert.Equal(string.Empty, packError.ToString());

		var appDirectory = Path.Combine(_root, "app-cli");
		Directory.CreateDirectory(appDirectory);
		var projectPath = Path.Combine(appDirectory, "App.cvlproj");
		File.WriteAllText(projectPath, """
<Project Sdk="Cvolo.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <AssemblyName>App</AssemblyName>
    <PackageId>App</PackageId>
    <Version>1.0.0</Version>
    <LocalFeed>../feed</LocalFeed>
  </PropertyGroup>
</Project>
""");
		File.WriteAllText(Path.Combine(appDirectory, "Main.cvl"), """
using Foo;

int main() {
    Pair pair = Pair { X: 20, Y: 22 };
    Mode mode = Mode.Fast;
    return AddPair(pair) + Bias + (int)mode - 48;
}
""");

		var cache = new PackageCache(Path.Combine(_root, "workflow-cache"));
		using var pkgOutput = new StringWriter();
		using var pkgError = new StringWriter();
		var pkg = new PkgCommand(cache, new PackageInstaller(cache), () => appDirectory, pkgOutput, pkgError);

		var addExitCode = pkg.Parse("add Foo --version ^1.0.0").Invoke();

		Assert.Equal(0, addExitCode);
		Assert.Equal(string.Empty, pkgError.ToString());
		Assert.True(File.Exists(Path.Combine(appDirectory, "cvolo.lock.json")));
		Assert.True(File.Exists(Path.Combine(cache.GetPackageDirectory("Foo", "1.0.0"), "Foo.cvlib")));
		var manifest = ProjectManifest.Load(projectPath);
		var reference = Assert.Single(manifest.Dependencies);
		Assert.Equal("Foo", reference.Id);
		Assert.Equal("^1.0.0", reference.Version);

		var lockFile = LockFile.Read(Path.Combine(appDirectory, "cvolo.lock.json"));
		Assert.True(lockFile.Packages.TryGetValue("Foo", out var locked));
		Assert.Equal("1.0.0", locked!.Resolved);
		Assert.Equal(LocalFeed.ComputeHash(packagePath), locked.ContentHash);

		var driverType = typeof(ICompilerDriver).Assembly.GetType("Cvolo.Drivers.CompilerDriver", throwOnError: true)!;
		var driver = Assert.IsAssignableFrom<ICompilerDriver>(Activator.CreateInstance(driverType, cache));
		var (buildExitCode, buildOutput) = CompileWithCapturedOutput(driver, projectPath);
		Assert.True(buildExitCode == 0, buildOutput);

		var executable = Path.Combine(appDirectory, "bin", "Debug", OperatingSystem.IsWindows() ? "App.exe" : "App");
		Assert.True(File.Exists(executable), $"Expected compiler output '{executable}'.");
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
	}

	private static void RequireClang()
	{
		if (FindClang() is null)
			Assert.Skip("clang not available; package Sector 3 build integration requires clang/LLVM.");
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
			var exitCode = driver.Compile(projectPath, llvmOnly: false, isShared: false, emitIr: false, optLevel: "O0");
			return (exitCode, output + error.ToString());
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
			try
			{
				var candidate = Path.Combine(directory, executable);
				if (File.Exists(candidate))
					return candidate;
			}
			catch (Exception ex) when (ex is ArgumentException or PathTooLongException or UnauthorizedAccessException)
			{
				// Ignore malformed/unreadable PATH entries.
			}
		}

		return null;
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
