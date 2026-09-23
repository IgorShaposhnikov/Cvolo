using System.Diagnostics;
using Cvolo.Tests.Core;

namespace Cvolo.Tests;

public sealed class UnsafeCAbiFixtureTests : CompilerTestBase
{
	[Fact]
	public void ScalarAbi_ObjectSizes_MatchPlatformCCompiler()
	{
		var clang = FindClangOrSkip();
		var cOutput = CompileAndRunCFixture(clang, "scalar_abi.c");

		var (exitCode, stdout, stderr) = RunCompiler("UnsafeCAbi/ScalarAbiFixture.cvl");
		AssertCompilationSucceeded(exitCode, stdout, stderr, "UnsafeCAbi/ScalarAbiFixture.cvl");

		var (_, cvoloOutput) = ExecuteBinary("ScalarAbiFixture", "UnsafeCAbi");
		Assert.Equal(NormalizeLines(cOutput), NormalizeLines(cvoloOutput));
	}

	[Fact]
	public void RawUnion_SizeStrideAndContainingLayout_MatchPlatformCCompiler()
	{
		var clang = FindClangOrSkip();
		var cValues = NormalizeLines(CompileAndRunCFixture(clang, "raw_union_layout.c"))
			.Select(int.Parse)
			.ToArray();

		var (exitCode, stdout, stderr) = RunCompiler("UnsafeCAbi/RawUnionCLayoutFixture.cvl");
		AssertCompilationSucceeded(exitCode, stdout, stderr, "UnsafeCAbi/RawUnionCLayoutFixture.cvl");

		var (_, cvoloOutput) = ExecuteBinary("RawUnionCLayoutFixture", "UnsafeCAbi");
		var cvoloValues = NormalizeLines(cvoloOutput).Select(int.Parse).ToArray();

		Assert.Equal(5, cValues.Length);
		Assert.Equal(3, cvoloValues.Length);

		// C fixture: union size, union align, union[2] size, containing-field offset, container size.
		// Cvolo fixture: union size, union[2] size, container size.
		Assert.Equal(cValues[0], cvoloValues[0]);
		Assert.Equal(cValues[2], cvoloValues[1]);
		Assert.Equal(cValues[4], cvoloValues[2]);

		// The fixture shape also independently verifies the expected natural alignment/offset:
		// Value's double member requires 8-byte alignment on the supported desktop targets.
		Assert.Equal(cValues[0], cValues[1]);
		Assert.Equal(cValues[1], cValues[3]);
	}


	[Fact]
	public void CallbackBoolAndForeignGlobals_CrossARealCObjectBoundary()
	{
		var clang = FindClangOrSkip();
		const string caseFile = "UnsafeCAbi/NativeCallbackForeignGlobalFixture.cvl";
		const string binaryName = "NativeCallbackForeignGlobalFixture";

		var objectPath = PrepareCObjectForIsolatedCase(
			clang,
			caseFile,
			"callback_globals.c",
			OperatingSystem.IsWindows() ? "callback_globals.obj" : "callback_globals.o");

		Assert.True(File.Exists(objectPath), $"Native fixture object was not created: {objectPath}");

		var (exitCode, stdout, stderr) = RunCompiler(caseFile);
		AssertCompilationSucceeded(exitCode, stdout, stderr, caseFile);

		var (programExitCode, programOutput) = ExecuteBinary(binaryName, "UnsafeCAbi");
		Assert.True(programExitCode == 0,
			$"C callback/foreign-global smoke fixture exited with {programExitCode}.\n{programOutput}");
	}

	private static string FindClangOrSkip()
	{
		var assemblyDir = Path.GetDirectoryName(typeof(UnsafeCAbiFixtureTests).Assembly.Location)!;
		var local = Path.Combine(assemblyDir, OperatingSystem.IsWindows() ? "clang.exe" : "clang");
		if (File.Exists(local))
			return local;

		var executable = OperatingSystem.IsWindows() ? "clang.exe" : "clang";
		try
		{
			using var probe = Process.Start(new ProcessStartInfo
			{
				FileName = executable,
				Arguments = "--version",
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true,
			});
			if (probe is not null && probe.WaitForExit(5000) && probe.ExitCode == 0)
				return executable;
		}
		catch
		{
		}

		Assert.Skip("clang not available; Unsafe C ABI C-fixture verification requires a platform C compiler.");
		return executable;
	}


	private static string PrepareCObjectForIsolatedCase(
		string clang,
		string caseFile,
		string fixtureName,
		string objectFileName)
	{
		var assemblyDir = Path.GetDirectoryName(typeof(UnsafeCAbiFixtureTests).Assembly.Location)!;
		var source = Path.Combine(assemblyDir, TestCasesDirectory, "UnsafeCAbi", "Fixtures", fixtureName);

		var categoryDir = Path.GetDirectoryName(caseFile) ?? string.Empty;
		var caseName = Path.GetFileNameWithoutExtension(caseFile);
		var isolatedDir = Path.GetFullPath(Path.Combine(
			assemblyDir, TestCasesDirectory, "_isolated", categoryDir, caseName));
		Directory.CreateDirectory(isolatedDir);

		var objectPath = Path.Combine(isolatedDir, objectFileName);

		using var compile = Process.Start(new ProcessStartInfo
		{
			FileName = clang,
			Arguments = $"-std=c11 -O0 -c \"{source}\" -o \"{objectPath}\"",
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true,
		})!;

		var stderr = compile.StandardError.ReadToEnd();
		compile.WaitForExit();

		Assert.True(compile.ExitCode == 0,
			$"C fixture object '{fixtureName}' failed to compile:\n{stderr}");

		return objectPath;
	}

	private static string CompileAndRunCFixture(string clang, string fixtureName)
	{
		var assemblyDir = Path.GetDirectoryName(typeof(UnsafeCAbiFixtureTests).Assembly.Location)!;
		var source = Path.Combine(assemblyDir, TestCasesDirectory, "UnsafeCAbi", "Fixtures", fixtureName);
		var tempDir = Path.Combine(Path.GetTempPath(), "cvolo-cabi-fixtures", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(tempDir);

		var exe = Path.Combine(tempDir, Path.GetFileNameWithoutExtension(fixtureName) +
			(OperatingSystem.IsWindows() ? ".exe" : string.Empty));

		try
		{
			using (var compile = Process.Start(new ProcessStartInfo
			{
				FileName = clang,
				Arguments = $"\"{source}\" -std=c11 -O0 -o \"{exe}\"",
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true,
			})!)
			{
				var stderr = compile.StandardError.ReadToEnd();
				compile.WaitForExit();
				Assert.True(compile.ExitCode == 0, $"C fixture '{fixtureName}' failed to compile:\n{stderr}");
			}

			using var run = Process.Start(new ProcessStartInfo
			{
				FileName = exe,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true,
			})!;
			var stdout = run.StandardOutput.ReadToEnd();
			var stderrRun = run.StandardError.ReadToEnd();
			run.WaitForExit();
			Assert.True(run.ExitCode == 0, $"C fixture '{fixtureName}' failed:\n{stderrRun}");
			return stdout;
		}
		finally
		{
			try { Directory.Delete(tempDir, recursive: true); } catch { }
		}
	}

	private static string[] NormalizeLines(string value) =>
		value.Replace("\r", string.Empty, StringComparison.Ordinal)
			.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
