using System.Diagnostics;
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
	public void Build_AppUsingPackedLibrary_ConsumesSector3AndRuns()
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
expose extern "C" {
    public int Add(int a, int b) {
        return a + b;
    }
}
""");

		var signingKey = Path.Combine(_root, "dev.key");
		File.WriteAllBytes(signingKey, Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());
		var packagePath = Path.Combine(_root, "Foo.1.0.0.cvlib");
		PackPipeline.Execute(libraryDirectory, new PackOptions
		{
			OutputPath = packagePath,
			SigningKeyPath = signingKey,
			Targets = [TargetTriple.HostTriple()]
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
[LibraryImport("Foo")]
extern "C" {
    int Add(int a, int b);
}

int main() {
    return Add(2, 3) - 5;
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
		var exitCode = driver.Compile(projectPath, llvmOnly: false, isShared: false, emitIr: false, optLevel: "O0");
		Assert.Equal(0, exitCode);

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
		process!.WaitForExit();
		Assert.Equal(0, process.ExitCode);
	}

	private static void RequireClang()
	{
		if (FindClang() is null)
			Assert.Skip("clang not available; package Sector 3 build integration requires clang/LLVM.");
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
