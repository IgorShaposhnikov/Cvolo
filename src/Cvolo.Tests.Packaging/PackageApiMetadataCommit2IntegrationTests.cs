using System.Diagnostics;
using Cvolo.Core.Packages;
using Cvolo.Drivers;
using Cvolo.Packaging;

namespace Cvolo.Tests.Packaging;

public sealed class PackageApiMetadataCommit2IntegrationTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "cvolo-commit2-integration-" + Guid.NewGuid().ToString("N"));

	public PackageApiMetadataCommit2IntegrationTests()
	{
		Directory.CreateDirectory(_root);
	}

	[Fact]
	public void PackedProducerCvlib_CanBeConsumedWithNativeDelegateRawUnionAndForeignGlobal()
	{
		RequireClang();

		var producerDirectory = Path.Combine(_root, "producer");
		Directory.CreateDirectory(producerDirectory);
		File.WriteAllText(Path.Combine(producerDirectory, "NativeApi.cvlproj"), """
<Project Sdk="Cvolo.Sdk">
  <PropertyGroup>
    <OutputType>Library</OutputType>
    <AssemblyName>NativeApi</AssemblyName>
    <PackageId>NativeApi</PackageId>
    <Version>1.0.0</Version>
  </PropertyGroup>
</Project>
""");
		File.WriteAllText(Path.Combine(producerDirectory, "NativeApi.cvl"), """
namespace NativeApi;

public unsafe "C" delegate int Callback(int value);

public unsafe union NativeValue {
    public int Integer;
    public double Real;
}

[LibraryImport("NativeApi", linux: "./libnative_api.so", mac: "./libnative_api.dylib", win: "./native_api.lib")]
[ImportName("native_counter")]
public extern "C" global var int Counter;

public int Identity(int value) { return value; }
""");

		var signingKey = Path.Combine(_root, "commit2.key");
		File.WriteAllBytes(signingKey, Enumerable.Range(32, 32).Select(value => (byte)value).ToArray());
		var packagePath = Path.Combine(_root, "NativeApi.1.0.0.cvlib");
		PackPipeline.Execute(producerDirectory, new PackOptions
		{
			OutputPath = packagePath,
			SigningKeyPath = signingKey,
			Targets = [TargetTriple.HostTriple()],
			StripSource = true
		});

		// Read the API from the physical archive before installing it. This ensures the consumer
		// test cannot accidentally pass by reusing producer AST state from the pack compilation.
		using (var archive = CvlArchiveReader.Read(packagePath))
		{
			var api = PackageApiMetadata.Read(archive);
			var unit = Assert.Single(api.Units);
			var callback = Assert.Single(unit.Delegates);
			var rawUnion = Assert.Single(unit.Unions);
			var foreignGlobal = Assert.Single(unit.Globals);
			var library = Assert.Single(api.NativeLibraries);

			Assert.True(callback.IsNative);
			Assert.Equal("C", callback.CallingConvention);
			Assert.True(rawUnion.IsUnsafe);
			Assert.True(foreignGlobal.IsForeign);
			Assert.Equal("native_counter", foreignGlobal.ImportName);
			Assert.Equal("NativeApi", foreignGlobal.LibraryName);
			Assert.Equal("./libnative_api.so", library.LinuxPath);
			Assert.Equal("./libnative_api.dylib", library.MacPath);
			Assert.Equal("./native_api.lib", library.WinPath);
		}

		var cache = new PackageCache(Path.Combine(_root, "cache"));
		new PackageInstaller(cache).InstallFromFile(packagePath);

		var consumerDirectory = Path.Combine(_root, "consumer");
		Directory.CreateDirectory(consumerDirectory);
		var projectPath = Path.Combine(consumerDirectory, "Consumer.cvlproj");
		File.WriteAllText(projectPath, """
<Project Sdk="Cvolo.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <AssemblyName>Consumer</AssemblyName>
    <PackageId>Consumer</PackageId>
    <Version>1.0.0</Version>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="NativeApi" Version="1.0.0" />
  </ItemGroup>
</Project>
""");
		File.WriteAllText(Path.Combine(consumerDirectory, "Main.cvl"), """
using NativeApi;

int AcceptCallback(Callback callback) { return 0; }
int AcceptValue(NativeValue value) { return 0; }

int main() {
    return Identity(0);
}
""");

		new LockFile
		{
			Sources = [],
			Packages = new Dictionary<string, LockedPackage>(StringComparer.OrdinalIgnoreCase)
			{
				["NativeApi"] = new("1.0.0", LocalFeed.ComputeHash(packagePath), new Dictionary<string, string>())
			}
		}.Write(Path.Combine(consumerDirectory, "cvolo.lock.json"));

		var driverType = typeof(ICompilerDriver).Assembly.GetType("Cvolo.Drivers.CompilerDriver", throwOnError: true)!;
		var driver = Assert.IsAssignableFrom<ICompilerDriver>(Activator.CreateInstance(driverType, cache));
		var (exitCode, output) = CompileWithCapturedOutput(driver, projectPath);
		Assert.True(exitCode == 0, output);

		var executable = Path.Combine(consumerDirectory, "bin", "Debug", OperatingSystem.IsWindows() ? "Consumer.exe" : "Consumer");
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

	private static void RequireClang()
	{
		if (FindClang() is null)
			Assert.Skip("clang not available; Commit 2 package integration requires clang/LLVM.");
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
				// Ignore malformed or unreadable PATH entries while probing the test toolchain.
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
			// Compiler outputs can remain briefly locked on Windows; cleanup is best effort only.
		}
	}
}
