using System.Diagnostics;
using Cvolo.Core.Diagnostics;
using Cvolo.Drivers;
using Cvolo.Packaging;
using Cvolo.Tests.Shared;

namespace Cvolo.Tests.Packaging;

/// <summary>
/// Application-Native-Library-Resolution INC: end-to-end .cvlproj NativeLibraries behavior —
/// CVL1705 fallback warning, CVL1704 resolution failures (both shapes, verbose gating),
/// and CVL1706/CVL1707 runtime sidecar staging.
/// </summary>
public sealed class NativeLibrariesIntegrationTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "cvolo-native-libraries-tests", Guid.NewGuid().ToString("N"));

	public NativeLibrariesIntegrationTests() => Directory.CreateDirectory(_root);

	private static string CurrentTarget =>
		OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";

	private static string SourceWithImport(string libraryName) =>
		$"[LibraryImport(\"{libraryName}\")]\nextern \"C\" {{\n    void CvoloNativeProbe();\n}}\n\nint main() {{\n    return 0;\n}}\n";

	private string CreateApp(string name, string source, string? nativeLibrariesXml = null, string assemblyName = "NativeLibApp")
	{
		var directory = Path.Combine(_root, name);
		Directory.CreateDirectory(directory);

		var projectLines = new List<string>
		{
			"<Project Sdk=\"Cvolo.Sdk\">",
			"  <PropertyGroup>",
			"    <OutputType>Exe</OutputType>",
			$"    <AssemblyName>{assemblyName}</AssemblyName>",
			"  </PropertyGroup>"
		};
		if (nativeLibrariesXml is not null)
			projectLines.Add(nativeLibrariesXml);
		projectLines.Add("</Project>");

		File.WriteAllText(Path.Combine(directory, assemblyName + ".cvlproj"), string.Join("\n", projectLines));
		File.WriteAllText(Path.Combine(directory, "Main.cvl"), source);
		return directory;
	}

	private ICompilerDriver CreateDriver()
	{
		var cache = new PackageCache(Path.Combine(_root, "cache"));
		var driverType = typeof(ICompilerDriver).Assembly.GetType("Cvolo.Drivers.CompilerDriver", throwOnError: true)!;
		return Assert.IsAssignableFrom<ICompilerDriver>(Activator.CreateInstance(driverType, cache));
	}

	private static (int ExitCode, string Output) Compile(ICompilerDriver driver, string path, bool verbose = false, bool runAfterCompile = false)
	{
		var originalOut = Console.Out;
		var originalError = Console.Error;
		using var output = new StringWriter();
		using var error = new StringWriter();
		try
		{
			Console.SetOut(output);
			Console.SetError(error);
			var exitCode = driver.Compile(path, llvmOnly: false, isShared: false, emitIr: false, optLevel: "O0", verbose: verbose, runAfterCompile: runAfterCompile);
			return (exitCode, output.ToString() + error.ToString());
		}
		finally
		{
			Console.SetOut(originalOut);
			Console.SetError(originalError);
		}
	}

	private static string CreateLinkableObject(string directory, string baseName)
	{
		Directory.CreateDirectory(directory);
		var clang = NativeToolchain.FindClang()!;
		var sourcePath = Path.Combine(directory, "stub.c");
		File.WriteAllText(sourcePath, "int cvolo_native_stub_marker(void) { return 0; }\n");
		var objectPath = Path.Combine(directory, OperatingSystem.IsWindows() ? baseName + ".obj" : baseName + ".o");

		var startInfo = new ProcessStartInfo
		{
			FileName = clang,
			Arguments = $"-c \"{sourcePath}\" -o \"{objectPath}\"",
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true
		};
		using var process = Process.Start(startInfo);
		Assert.NotNull(process);
		var stderr = process!.StandardError.ReadToEnd();
		process.WaitForExit(60_000);
		Assert.True(process.ExitCode == 0 && File.Exists(objectPath), $"clang object creation failed: {stderr}");
		return objectPath;
	}

	private static void RequireClang()
	{
		if (NativeToolchain.FindClang() is null)
			Assert.Skip("clang not available; native-library link integration requires clang/LLVM.");
	}

	private static string ExecutablePath(string projectDirectory, string assemblyName)
		=> Path.Combine(projectDirectory, "bin", "Debug", OperatingSystem.IsWindows() ? assemblyName + ".exe" : assemblyName);

	private static string OutputDirectory(string projectDirectory)
		=> Path.Combine(projectDirectory, "bin", "Debug");

	// Project configuration validation surfaces as a build error

	[Fact]
	public void Build_UnknownNativeTarget_FailsWithCVLP3101()
	{
		var app = CreateApp("UnknownTarget", SourceWithImport("glfw3"), """
			  <NativeLibraries Target="windwos">
			    <NativeLibrary For="glfw3" Path="native/glfw3.lib" />
			  </NativeLibraries>
			""");

		var (exitCode, output) = Compile(CreateDriver(), app);

		Assert.NotEqual(0, exitCode);
		Assert.Contains(PackageDiagnosticIds.NativeLibrariesUnknownTarget, output, StringComparison.Ordinal);
		Assert.Contains("windwos", output, StringComparison.Ordinal);
	}

	// CVL1705: mapped-on-other-target system fallback

	[Fact]
	public void Build_SystemFallbackAfterOtherTargetMapping_Warns_CVL1705()
	{
		RequireClang();
		var app = CreateApp("SystemFallback", SourceWithImport("cvolotest-defmissing-xyz"), $"""
			  <NativeLibraries Target="{(CurrentTarget == "windows" ? "linux" : "windows")}">
			    <NativeLibrary For="cvolotest-defmissing-xyz" Path="native/not-probed-on-this-target.lib" />
			  </NativeLibraries>
			""");

		var (exitCode, output) = Compile(CreateDriver(), app);

		Assert.NotEqual(0, exitCode);
		Assert.Contains(DiagnosticIds.NativeLibraryFallbackWarning, output, StringComparison.Ordinal);
		Assert.Contains("cvolotest-defmissing-xyz", output, StringComparison.Ordinal);
		// The inactive target's path must never be probed or reported.
		Assert.DoesNotContain("not-probed-on-this-target", output, StringComparison.Ordinal);
		Assert.Contains(DiagnosticIds.NativeLibraryUnresolved, output, StringComparison.Ordinal);
	}

	// CVL1704 shape (b): configured path failed

	[Fact]
	public void Build_CVL1704_MappedPathFailureShowsPath()
	{
		RequireClang();
		var app = CreateApp("MappedPathFailure", SourceWithImport("glfw3"), $"""
			  <NativeLibraries Target="{CurrentTarget}">
			    <NativeLibrary For="glfw3" Path="native/missing-glfw3.lib" />
			  </NativeLibraries>
			""");

		var (exitCode, output) = Compile(CreateDriver(), app, verbose: false);

		Assert.NotEqual(0, exitCode);
		Assert.Contains(DiagnosticIds.NativeLibraryUnresolved, output, StringComparison.Ordinal);
		var expectedPath = Path.GetFullPath(Path.Combine(app, "native", "missing-glfw3.lib"));
		Assert.Contains(expectedPath, output, StringComparison.Ordinal);
		Assert.Contains(CurrentTarget + "/", output, StringComparison.Ordinal);
		Assert.DoesNotContain("Add a mapping", output, StringComparison.Ordinal);
		// Non-verbose: raw clang/lld output stays hidden for recognized missing-native failures.
		Assert.DoesNotContain("clang: error", output, StringComparison.Ordinal);
		Assert.DoesNotContain("lld-link", output, StringComparison.Ordinal);
	}

	[Fact]
	public void Build_CVL1704_VerboseLinkerOutputPrintedFirst()
	{
		RequireClang();
		var app = CreateApp("VerboseLinkerOutput", SourceWithImport("glfw3"), $"""
			  <NativeLibraries Target="{CurrentTarget}">
			    <NativeLibrary For="glfw3" Path="native/missing-glfw3.lib" />
			  </NativeLibraries>
			""");

		var (exitCode, output) = Compile(CreateDriver(), app, verbose: true);

		Assert.NotEqual(0, exitCode);
		Assert.Contains("\x1b[", output, StringComparison.Ordinal);
		Assert.Contains("Native libraries selected for build", output, StringComparison.Ordinal);
		Assert.Contains("  -> glfw3:", output, StringComparison.Ordinal);
		Assert.Contains("native", output, StringComparison.Ordinal);
		Assert.Contains("used by:", output, StringComparison.Ordinal);
		Assert.Contains("Main.cvl", output, StringComparison.Ordinal);
		var cvl1704Index = output.IndexOf(DiagnosticIds.NativeLibraryUnresolved, StringComparison.Ordinal);
		Assert.True(cvl1704Index >= 0, output);
		var rawIndex = output.IndexOf("error:", StringComparison.Ordinal);
		Assert.True(rawIndex >= 0, $"Expected raw linker output in verbose mode.\n{output}");
		Assert.True(rawIndex < cvl1704Index, $"Raw linker output must precede CVL1704 in verbose mode.\n{output}");
	}

	// CVL1704 shape (a): no mapping, system lookup failed

	[Fact]
	public void Build_CVL1704_MissingMappingShowsProjectFix()
	{
		RequireClang();
		var app = CreateApp("MissingMapping", SourceWithImport("cvolotest-defmissing-xyz"));

		var (exitCode, output) = Compile(CreateDriver(), app);

		Assert.NotEqual(0, exitCode);
		Assert.Contains(DiagnosticIds.NativeLibraryUnresolved, output, StringComparison.Ordinal);
		Assert.Contains("cvolotest-defmissing-xyz", output, StringComparison.Ordinal);
		Assert.Contains("Add a mapping to the application .cvlproj", output, StringComparison.Ordinal);
		Assert.Contains("<NativeLibraries", output, StringComparison.Ordinal);
		Assert.Contains(CurrentTarget, output, StringComparison.Ordinal);
		// Completely unmapped system libraries must NOT warn (no mapping exists on any target).
		Assert.DoesNotContain(DiagnosticIds.NativeLibraryFallbackWarning, output, StringComparison.Ordinal);
	}

	// CVL1704 must not misclassify non-native link failures

	[Fact]
	public void Build_CVL1704_NonNativeLinkerFailureNotMisclassified()
	{
		RequireClang();
		var directory = Path.Combine(_root, "GarbageLinkInput");
		Directory.CreateDirectory(Path.Combine(directory, "native"));
		File.WriteAllText(Path.Combine(directory, "native", "garbage.obj"), "this is definitely not a linkable object file\n");
		var app = CreateApp("GarbageLinkInput", SourceWithImport("cvolotest-garbage-lib"), $"""
			  <NativeLibraries Target="{CurrentTarget}">
			    <NativeLibrary For="cvolotest-garbage-lib" Path="native/garbage.obj" />
			  </NativeLibraries>
			""");

		var (exitCode, output) = Compile(CreateDriver(), app);

		Assert.NotEqual(0, exitCode);
		Assert.DoesNotContain(DiagnosticIds.NativeLibraryUnresolved, output, StringComparison.Ordinal);
		Assert.Contains("Linking failed", output, StringComparison.Ordinal);
	}

	// Logical names are opaque and case-sensitive

	[Fact]
	public void Build_LogicalNameCaseSensitivity_NotSilentlyMerged()
	{
		RequireClang();
		var app = CreateApp("LogicalCase", SourceWithImport("TestLib"), $"""
			  <NativeLibraries Target="{CurrentTarget}">
			    <NativeLibrary For="testlib" Path="native/testlib.lib" />
			  </NativeLibraries>
			""");

		var (exitCode, output) = Compile(CreateDriver(), app);

		Assert.NotEqual(0, exitCode);
		// 'TestLib' has no mapping of its own (For is ordinal/case-sensitive) and must neither
		// pick up 'testlib' nor warn about it; the system lookup failure is the only diagnostic.
		Assert.DoesNotContain(DiagnosticIds.NativeLibraryFallbackWarning, output, StringComparison.Ordinal);
		Assert.Contains("TestLib", output, StringComparison.Ordinal);
		Assert.Contains(DiagnosticIds.NativeLibraryUnresolved, output, StringComparison.Ordinal);
	}

	// Runtime sidecar staging

	[Fact]
	public void Build_RuntimeFileExplicitCopy_SiblingDllNotScanned()
	{
		RequireClang();
		var directory = Path.Combine(_root, "RuntimeExplicit");
		var nativeDirectory = Path.Combine(directory, "native");
		var objectPath = CreateLinkableObject(nativeDirectory, "nativestub");
		var runtimeFile = Path.Combine(nativeDirectory, "helper.dll");
		File.WriteAllBytes(runtimeFile, new byte[] { 1, 2, 3, 4 });
		File.WriteAllBytes(Path.Combine(nativeDirectory, "decoy.dll"), new byte[] { 9, 9, 9 });

		var relativeObject = Path.GetRelativePath(directory, objectPath).Replace('\\', '/');
		var relativeRuntime = Path.GetRelativePath(directory, runtimeFile).Replace('\\', '/');
		var app = CreateApp("RuntimeExplicit", SourceWithImport("cvolotest-runtime-lib"), $"""
			  <NativeLibraries Target="{CurrentTarget}">
			    <NativeLibrary For="cvolotest-runtime-lib" Path="{relativeObject}">
			      <RuntimeFile Path="{relativeRuntime}" />
			    </NativeLibrary>
			  </NativeLibraries>
			""");

		var (exitCode, output) = Compile(CreateDriver(), app);

		Assert.True(exitCode == 0, output);
		var staged = Path.Combine(OutputDirectory(directory), "helper.dll");
		Assert.True(File.Exists(staged), $"Expected staged runtime file '{staged}'.\n{output}");
		Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(staged));
		// Project-level mappings never scan the link library's directory for sibling DLLs.
		Assert.False(File.Exists(Path.Combine(OutputDirectory(directory), "decoy.dll")), "Sibling DLL must not be copied for project-level mappings.");
	}

	[Fact]
	public void Build_RuntimeFileMissing_IsBuildError_CVL1707()
	{
		RequireClang();
		var directory = Path.Combine(_root, "RuntimeMissing");
		var objectPath = CreateLinkableObject(Path.Combine(directory, "native"), "nativestub");
		var relativeObject = Path.GetRelativePath(directory, objectPath).Replace('\\', '/');

		var app = CreateApp("RuntimeMissing", SourceWithImport("cvolotest-runtime-lib"), $"""
			  <NativeLibraries Target="{CurrentTarget}">
			    <NativeLibrary For="cvolotest-runtime-lib" Path="{relativeObject}">
			      <RuntimeFile Path="native/absent-helper.dll" />
			    </NativeLibrary>
			  </NativeLibraries>
			""");

		var (exitCode, output) = Compile(CreateDriver(), app);

		Assert.NotEqual(0, exitCode);
		Assert.Contains(DiagnosticIds.NativeRuntimeUnstageable, output, StringComparison.Ordinal);
		Assert.Contains("cvolotest-runtime-lib", output, StringComparison.Ordinal);
		Assert.Contains("absent-helper.dll", output, StringComparison.Ordinal);
		Assert.False(File.Exists(Path.Combine(OutputDirectory(directory), "absent-helper.dll")), "Missing runtime file must not be staged.");
	}

	[Fact]
	public void Build_RuntimeCollision_SameBasenameDifferentSources_CVL1706()
	{
		RequireClang();
		var directory = Path.Combine(_root, "RuntimeCollision");
		var objectPath = CreateLinkableObject(Path.Combine(directory, "native"), "nativestub");
		var relativeObject = Path.GetRelativePath(directory, objectPath).Replace('\\', '/');
		Directory.CreateDirectory(Path.Combine(directory, "native", "a"));
		Directory.CreateDirectory(Path.Combine(directory, "native", "b"));
		File.WriteAllBytes(Path.Combine(directory, "native", "a", "helper.dll"), new byte[] { 1 });
		File.WriteAllBytes(Path.Combine(directory, "native", "b", "helper.dll"), new byte[] { 2 });

		var app = CreateApp("RuntimeCollision", SourceWithImport("cvolotest-runtime-lib"), $"""
			  <NativeLibraries Target="{CurrentTarget}">
			    <NativeLibrary For="cvolotest-runtime-lib" Path="{relativeObject}">
			      <RuntimeFile Path="native/a/helper.dll" />
			      <RuntimeFile Path="native/b/helper.dll" />
			    </NativeLibrary>
			  </NativeLibraries>
			""");

		var (exitCode, output) = Compile(CreateDriver(), app);

		Assert.NotEqual(0, exitCode);
		Assert.Contains(DiagnosticIds.NativeRuntimeDestinationCollision, output, StringComparison.Ordinal);
		Assert.Contains("helper.dll", output, StringComparison.Ordinal);
		Assert.False(File.Exists(Path.Combine(OutputDirectory(directory), "helper.dll")), "Colliding runtime files must not be staged at all.");
	}

	[Fact]
	public void Build_RuntimeSameNormalizedSource_Deduplicated()
	{
		RequireClang();
		var directory = Path.Combine(_root, "RuntimeDedup");
		var objectPath = CreateLinkableObject(Path.Combine(directory, "native"), "nativestub");
		var relativeObject = Path.GetRelativePath(directory, objectPath).Replace('\\', '/');
		var runtimeFile = Path.Combine(directory, "native", "helper.dll");
		File.WriteAllBytes(runtimeFile, new byte[] { 7, 7, 7 });

		var app = CreateApp("RuntimeDedup", SourceWithImport("cvolotest-runtime-lib"), $"""
			  <NativeLibraries Target="{CurrentTarget}">
			    <NativeLibrary For="cvolotest-runtime-lib" Path="{relativeObject}">
			      <RuntimeFile Path="native/helper.dll" />
			      <RuntimeFile Path="native/./sub/../helper.dll" />
			      <RuntimeFile Path="native/helper.dll" />
			    </NativeLibrary>
			  </NativeLibraries>
			""");

		var (exitCode, output) = Compile(CreateDriver(), app);

		Assert.True(exitCode == 0, output);
		var staged = Path.Combine(OutputDirectory(directory), "helper.dll");
		Assert.True(File.Exists(staged), $"Expected staged runtime file '{staged}'.\n{output}");
		Assert.Equal(new byte[] { 7, 7, 7 }, File.ReadAllBytes(staged));
	}

	[Fact]
	public void Build_RuntimeCollisionWithGeneratedOutput_CVL1706()
	{
		RequireClang();
		var directory = Path.Combine(_root, "RuntimeVsOutput");
		var objectPath = CreateLinkableObject(Path.Combine(directory, "native"), "nativestub");
		var relativeObject = Path.GetRelativePath(directory, objectPath).Replace('\\', '/');
		var outputName = OperatingSystem.IsWindows() ? "NativeLibApp.exe" : "NativeLibApp";
		File.WriteAllBytes(Path.Combine(directory, "native", outputName), new byte[] { 0x4D, 0x5A });

		var app = CreateApp("RuntimeVsOutput", SourceWithImport("cvolotest-runtime-lib"), $"""
			  <NativeLibraries Target="{CurrentTarget}">
			    <NativeLibrary For="cvolotest-runtime-lib" Path="{relativeObject}">
			      <RuntimeFile Path="native/{outputName}" />
			    </NativeLibrary>
			  </NativeLibraries>
			""");

		var (exitCode, output) = Compile(CreateDriver(), app);

		Assert.NotEqual(0, exitCode);
		Assert.Contains(DiagnosticIds.NativeRuntimeDestinationCollision, output, StringComparison.Ordinal);
		Assert.Contains(outputName, output, StringComparison.Ordinal);
	}

	[Fact]
	public void Build_RuntimeSymlink_CopiesPayloadWithDeclaredBasename()
	{
		RequireClang();
		var directory = Path.Combine(_root, "RuntimeSymlink");
		var nativeDirectory = Path.Combine(directory, "native");
		var objectPath = CreateLinkableObject(nativeDirectory, "nativestub");
		var relativeObject = Path.GetRelativePath(directory, objectPath).Replace('\\', '/');

		var payloadPath = Path.Combine(nativeDirectory, "real-payload.dll");
		File.WriteAllBytes(payloadPath, new byte[] { 4, 3, 2, 1 });
		var linkPath = Path.Combine(nativeDirectory, "declared-link.dll");
		try
		{
			File.CreateSymbolicLink(linkPath, payloadPath);
		}
		catch (Exception)
		{
			Assert.Skip("Symbolic links are not available on this machine.");
		}

		var relativeLink = Path.GetRelativePath(directory, linkPath).Replace('\\', '/');
		var app = CreateApp("RuntimeSymlink", SourceWithImport("cvolotest-runtime-lib"), $"""
			  <NativeLibraries Target="{CurrentTarget}">
			    <NativeLibrary For="cvolotest-runtime-lib" Path="{relativeObject}">
			      <RuntimeFile Path="{relativeLink}" />
			    </NativeLibrary>
			  </NativeLibraries>
			""");

		var (exitCode, output) = Compile(CreateDriver(), app);

		Assert.True(exitCode == 0, output);
		var staged = Path.Combine(OutputDirectory(directory), "declared-link.dll");
		Assert.True(File.Exists(staged), $"Expected staged runtime file '{staged}'.\n{output}");
		Assert.False(File.Exists(staged) && (File.GetAttributes(staged) & FileAttributes.ReparsePoint) != 0, "Staged file must be a real payload copy, not a symlink.");
		Assert.Equal(new byte[] { 4, 3, 2, 1 }, File.ReadAllBytes(staged));
		Assert.False(File.Exists(Path.Combine(OutputDirectory(directory), "real-payload.dll")), "Only the declared basename may be staged.");
	}

	[Fact]
	public void Build_RuntimeSymlinkBroken_CVL1707()
	{
		RequireClang();
		var directory = Path.Combine(_root, "RuntimeSymlinkBroken");
		var nativeDirectory = Path.Combine(directory, "native");
		var objectPath = CreateLinkableObject(nativeDirectory, "nativestub");
		var relativeObject = Path.GetRelativePath(directory, objectPath).Replace('\\', '/');

		var linkPath = Path.Combine(nativeDirectory, "broken-link.dll");
		try
		{
			File.CreateSymbolicLink(linkPath, Path.Combine(nativeDirectory, "does-not-exist.dll"));
		}
		catch (Exception)
		{
			Assert.Skip("Symbolic links are not available on this machine.");
		}

		var relativeLink = Path.GetRelativePath(directory, linkPath).Replace('\\', '/');
		var app = CreateApp("RuntimeSymlinkBroken", SourceWithImport("cvolotest-runtime-lib"), $"""
			  <NativeLibraries Target="{CurrentTarget}">
			    <NativeLibrary For="cvolotest-runtime-lib" Path="{relativeObject}">
			      <RuntimeFile Path="{relativeLink}" />
			    </NativeLibrary>
			  </NativeLibraries>
			""");

		var (exitCode, output) = Compile(CreateDriver(), app);

		Assert.NotEqual(0, exitCode);
		Assert.Contains(DiagnosticIds.NativeRuntimeUnstageable, output, StringComparison.Ordinal);
		Assert.Contains("broken-link.dll", output, StringComparison.Ordinal);
	}

	// build / run parity

	[Fact]
	public void BuildAndRun_Parity_CVL1704()
	{
		RequireClang();
		var app = CreateApp("BuildRunParity", SourceWithImport("glfw3"), $"""
			  <NativeLibraries Target="{CurrentTarget}">
			    <NativeLibrary For="glfw3" Path="native/missing-glfw3.lib" />
			  </NativeLibraries>
			""");

		var (exitCode, output) = Compile(CreateDriver(), app, runAfterCompile: true);

		Assert.NotEqual(0, exitCode);
		Assert.Contains(DiagnosticIds.NativeLibraryUnresolved, output, StringComparison.Ordinal);
		Assert.DoesNotContain("Add a mapping", output, StringComparison.Ordinal);
	}

	// package-origin requirements (INC §6 step 2/§9): metadata paths must not leak, origin reported

	private (PackageCache Cache, string PackagePath) CreateLeakProbePackage(string cacheName)
	{
		var producerDirectory = Path.Combine(_root, "producer-" + cacheName);
		Directory.CreateDirectory(producerDirectory);
		File.WriteAllText(Path.Combine(producerDirectory, "LeakProbe.cvlproj"), """
<Project Sdk="Cvolo.Sdk">
  <PropertyGroup>
    <OutputType>Library</OutputType>
    <AssemblyName>LeakProbe</AssemblyName>
    <PackageId>LeakProbe</PackageId>
    <Version>1.0.0</Version>
  </PropertyGroup>
</Project>
""");
		File.WriteAllText(Path.Combine(producerDirectory, "LeakProbe.cvl"), """
namespace LeakProbe;

[LibraryImport("cvolotest-leak-lib", linux: "./leaked_from_package.so", mac: "./leaked_from_package.dylib", win: "./leaked_from_package.lib")]
[ImportName("cvolotest_leak_probe")]
public extern "C" global var int Counter;

public int Identity(int value) { return value; }
""");

		var signingKey = Path.Combine(_root, cacheName + ".key");
		File.WriteAllBytes(signingKey, Enumerable.Range(32, 32).Select(value => (byte)value).ToArray());
		var packagePath = Path.Combine(_root, "LeakProbe.1.0.0.cvlib");
		PackPipeline.Execute(producerDirectory, new PackOptions
		{
			OutputPath = packagePath,
			SigningKeyPath = signingKey,
			Targets = [TargetTriple.HostTriple()],
			StripSource = true
		});

		var cache = new PackageCache(Path.Combine(_root, cacheName));
		new PackageInstaller(cache).InstallFromFile(packagePath);
		return (cache, packagePath);
	}

	private static void WriteLeakProbeConsumer(string consumerDirectory, string packagePath, string? nativeLibrariesXml)
	{
		Directory.CreateDirectory(consumerDirectory);
		var projectLines = new List<string>
		{
			"<Project Sdk=\"Cvolo.Sdk\">",
			"  <PropertyGroup>",
			"    <OutputType>Exe</OutputType>",
			"    <AssemblyName>LeakConsumer</AssemblyName>",
			"    <PackageId>LeakConsumer</PackageId>",
			"    <Version>1.0.0</Version>",
			"  </PropertyGroup>"
		};
		if (nativeLibrariesXml is not null)
			projectLines.Add(nativeLibrariesXml);
		projectLines.Add("  <ItemGroup>");
		projectLines.Add("    <PackageReference Include=\"LeakProbe\" Version=\"1.0.0\" />");
		projectLines.Add("  </ItemGroup>");
		projectLines.Add("</Project>");
		File.WriteAllText(Path.Combine(consumerDirectory, "LeakConsumer.cvlproj"), string.Join("\n", projectLines));
		File.WriteAllText(Path.Combine(consumerDirectory, "Main.cvl"), "using LeakProbe;\n\nint main() {\n    return Identity(0);\n}\n");

		new LockFile
		{
			Sources = [],
			Packages = new Dictionary<string, LockedPackage>(StringComparer.OrdinalIgnoreCase)
			{
				["LeakProbe"] = new("1.0.0", LocalFeed.ComputeHash(packagePath), new Dictionary<string, string>())
			}
		}.Write(Path.Combine(consumerDirectory, "cvolo.lock.json"));
	}

	[Fact]
	public void PackageRequirement_ReportsOriginPackage_AndDoesNotLeakPackageSourcePaths()
	{
		RequireClang();
		var (cache, packagePath) = CreateLeakProbePackage("origin-cache");

		var consumerDirectory = Path.Combine(_root, "origin-consumer");
		WriteLeakProbeConsumer(consumerDirectory, packagePath, nativeLibrariesXml: null);

		var driverType = typeof(ICompilerDriver).Assembly.GetType("Cvolo.Drivers.CompilerDriver", throwOnError: true)!;
		var driver = Assert.IsAssignableFrom<ICompilerDriver>(Activator.CreateInstance(driverType, cache));
		var (exitCode, output) = Compile(driver, Path.Combine(consumerDirectory, "LeakConsumer.cvlproj"));

		Assert.NotEqual(0, exitCode);
		Assert.Contains(DiagnosticIds.NativeLibraryUnresolved, output, StringComparison.Ordinal);
		Assert.Contains("cvolotest-leak-lib", output, StringComparison.Ordinal);
		Assert.Contains("Package 'LeakProbe'", output, StringComparison.Ordinal);
		Assert.Contains("Add a mapping to the application .cvlproj", output, StringComparison.Ordinal);
		// Package-owned source-level paths must never surface on the consumer side.
		Assert.DoesNotContain("leaked_from_package", output, StringComparison.Ordinal);
		Assert.DoesNotContain(DiagnosticIds.NativeLibraryFallbackWarning, output, StringComparison.Ordinal);
	}

	[Fact]
	public void ApplicationMapping_SatisfiesPackageNativeRequirement()
	{
		RequireClang();
		var (cache, packagePath) = CreateLeakProbePackage("mapping-cache");

		var consumerDirectory = Path.Combine(_root, "mapping-consumer");
		var objectPath = CreateLinkableObject(Path.Combine(consumerDirectory, "native"), "nativestub");
		var relativeObject = Path.GetRelativePath(consumerDirectory, objectPath).Replace('\\', '/');
		WriteLeakProbeConsumer(consumerDirectory, packagePath, $"""
			  <NativeLibraries Target="{CurrentTarget}">
			    <NativeLibrary For="cvolotest-leak-lib" Path="{relativeObject}" />
			  </NativeLibraries>
			""");

		var driverType = typeof(ICompilerDriver).Assembly.GetType("Cvolo.Drivers.CompilerDriver", throwOnError: true)!;
		var driver = Assert.IsAssignableFrom<ICompilerDriver>(Activator.CreateInstance(driverType, cache));
		var (exitCode, output) = Compile(driver, Path.Combine(consumerDirectory, "LeakConsumer.cvlproj"), verbose: true);

		Assert.True(exitCode == 0, output);
		Assert.Contains("\x1b[", output, StringComparison.Ordinal);
		Assert.Contains("Package dependencies selected for build:", output, StringComparison.Ordinal);
		Assert.Contains("  -> ", output, StringComparison.Ordinal);
		Assert.Contains("LeakProbe@1.0.0", output, StringComparison.Ordinal);
		Assert.Contains("Native libraries selected for build", output, StringComparison.Ordinal);
		Assert.Contains("  -> cvolotest-leak-lib:", output, StringComparison.Ordinal);
		Assert.Contains("origin:", output, StringComparison.Ordinal);
		Assert.Contains("LeakProbe", output, StringComparison.Ordinal);
		Assert.Contains("used by:", output, StringComparison.Ordinal);
		Assert.Contains("Main.cvl", output, StringComparison.Ordinal);
		Assert.DoesNotContain(DiagnosticIds.NativeLibraryUnresolved, output, StringComparison.Ordinal);
		Assert.DoesNotContain(DiagnosticIds.NativeLibraryFallbackWarning, output, StringComparison.Ordinal);
		Assert.DoesNotContain("leaked_from_package", output, StringComparison.Ordinal);
		Assert.True(File.Exists(ExecutablePath(consumerDirectory, "LeakConsumer")), $"Expected compiler output. {output}");
	}

	public void Dispose()
	{
		try
		{
			Directory.Delete(_root, recursive: true);
		}
		catch { }
	}
}
