using Cvolo.Tests.Core;

namespace Cvolo.Tests;

/// <summary>
/// Verifies that <c>--debug</c> / <c>-g</c> produces LLVM debug metadata and that builds without it
/// stay byte-for-byte free of debug nodes.
/// </summary>
public sealed class DebugMetadataTests : CompilerTestBase
{
	private const string LinesCase = "DebugInfo/DebugLines.cvl";
	private const string MinimalCase = "DebugInfo/DebugMinimal.cvl";

	[Fact]
	public void WithoutDebug_Emits_NoDebugMetadata()
	{
		var (exitCode, stdout, stderr) = EmitIr(LinesCase);
		AssertCompilationSucceeded(exitCode, stdout, stderr, LinesCase);

		var ir = Normalize(stdout);
		Assert.DoesNotContain("!llvm.dbg.cu", ir);
		Assert.DoesNotContain("Debug Info Version", ir);
		Assert.DoesNotContain("Dwarf Version", ir);
		Assert.DoesNotContain("CodeView", ir);
		Assert.DoesNotContain("DISubprogram", ir);
		Assert.DoesNotContain("DILocation", ir);
		Assert.DoesNotContain("!dbg", ir);
	}

	[Fact]
	public void WithDebug_EmitsCompileUnitAndModuleFlags()
	{
		var (exitCode, stdout, stderr) = EmitIr(LinesCase, "--debug");
		AssertCompilationSucceeded(exitCode, stdout, stderr, LinesCase);

		var ir = Normalize(stdout);
		Assert.Contains("!llvm.dbg.cu =", ir);
		Assert.Contains("DICompileUnit(", ir);
		Assert.Contains("!llvm.module.flags =", ir);
		Assert.Contains("\"Debug Info Version\", i32 3", ir);
		Assert.Contains("DW_LANG_C_plus_plus", ir);

		// Windows/MSVC targets use CodeView; every other target uses DWARF 5.
		Assert.True(
			ir.Contains("\"CodeView\", i32 1", StringComparison.Ordinal)
				|| ir.Contains("\"Dwarf Version\", i32 5", StringComparison.Ordinal),
			"Expected either the CodeView or the Dwarf Version module flag.");
	}

	[Fact]
	public void WithDebug_EmitsFileEntryForTheCompiledSource()
	{
		var (exitCode, stdout, stderr) = EmitIr(LinesCase, "--debug");
		AssertCompilationSucceeded(exitCode, stdout, stderr, LinesCase);

		var ir = Normalize(stdout);
		Assert.Contains("!DIFile(filename: \"DebugLines.cvl\"", ir);
	}

	[Fact]
	public void WithDebug_EmitsSubprogramForEverySourceFunction()
	{
		var (exitCode, stdout, stderr) = EmitIr(LinesCase, "--debug");
		AssertCompilationSucceeded(exitCode, stdout, stderr, LinesCase);

		var ir = Normalize(stdout);
		AssertSubprogramAttached(ir, "main");
		AssertSubprogramAttached(ir, "Add");
		AssertSubprogramAttached(ir, "Scale");
	}

	[Fact]
	public void WithDebug_EmitsStatementLevelLocations()
	{
		var (exitCode, stdout, stderr) = EmitIr(LinesCase, "--debug");
		AssertCompilationSucceeded(exitCode, stdout, stderr, LinesCase);

		var ir = Normalize(stdout);
		Assert.Contains("DILocation(", ir);
		Assert.Contains(", !dbg !", ir);

		// 'main' has statements on distinct lines, so it must reference more than one location.
		var main = ExtractFunctionBody(ir, "@main");
		Assert.True(
			CountOccurrences(main, "!dbg !") >= 2,
			"Expected 'main' to carry a distinct !dbg location per statement.");
	}

	[Fact]
	public void WithDebug_EmitsBasicTypesForPrimitiveSignatures()
	{
		var (exitCode, stdout, stderr) = EmitIr(LinesCase, "--debug");
		AssertCompilationSucceeded(exitCode, stdout, stderr, LinesCase);

		var ir = Normalize(stdout);
		Assert.Contains("!DIBasicType(name: \"int\", size: 32", ir);
		Assert.Contains("!DIBasicType(name: \"double\", size: 64", ir);
	}

	[Fact]
	public void WithDebug_ModuleVerifies_And_RunsCorrectly()
	{
		// A module that references an undeclared DISubprogram or an invalid !dbg scope fails the
		// LLVM verifier, which the driver surfaces as a non-zero exit code.
		var (exitCode, stdout, stderr) = RunCompiler(LinesCase, "--debug");
		AssertCompilationSucceeded(exitCode, stdout, stderr, LinesCase);
		Assert.DoesNotContain("Broken module found", stderr);

		var (runCode, runStdout) = ExecuteBinary("DebugLines", "DebugInfo");
		Assert.Equal(0, runCode);
		Assert.Contains("Sum: 5", runStdout);
		Assert.Contains("Scaled: 3", runStdout);
	}

	[Fact]
	public void WithDebug_OnWindows_ProducesProgramDatabase()
	{
		var (exitCode, stdout, stderr) = RunCompiler(LinesCase, "--debug");
		AssertCompilationSucceeded(exitCode, stdout, stderr, LinesCase);

		if (!OperatingSystem.IsWindows())
			return;

		var binDirectory = GetCompilationArtifactPath(LinesCase, "bin", "Debug");
		Assert.True(
			Directory.EnumerateFiles(binDirectory, "*.pdb").Any(),
			$"Expected a .pdb next to the executable in '{binDirectory}'.");
	}

	[Fact]
	public void WithoutDebug_ProducesNoProgramDatabase()
	{
		var (exitCode, stdout, stderr) = RunCompiler(LinesCase);
		AssertCompilationSucceeded(exitCode, stdout, stderr, LinesCase);

		if (!OperatingSystem.IsWindows())
			return;

		var binDirectory = GetCompilationArtifactPath(LinesCase, "bin", "Debug");
		Assert.False(
			Directory.EnumerateFiles(binDirectory, "*.pdb").Any(),
			"A build without --debug must not produce a program database.");
	}

	[Fact]
	public void WithDebug_DisablesTbaaFreePath_AndWorksWithNoTbaa()
	{
		var (exitCode, stdout, stderr) = EmitIr(LinesCase, "--debug", "--no-tbaa");
		AssertCompilationSucceeded(exitCode, stdout, stderr, LinesCase);

		var ir = Normalize(stdout);
		Assert.DoesNotContain("!tbaa", ir);
		Assert.Contains("!llvm.dbg.cu =", ir);
	}

	[Theory]
	[InlineData("-O1")]
	[InlineData("-O2")]
	[InlineData("-O3")]
	[InlineData("-Os")]
	[InlineData("-Oz")]
	public void Debug_RejectsExplicitOptimizedProfile(string opt)
	{
		var (exitCode, _, stderr) = RunCompiler(MinimalCase, "--debug", opt);

		Assert.Equal(1, exitCode);
		Assert.Contains("CVL3004", stderr);
		Assert.Contains("Debug builds require -O0", stderr);
	}

	[Fact]
	public void Debug_RejectsReleaseConfiguration()
	{
		var (exitCode, _, stderr) = RunCompiler(MinimalCase, "--debug", "--configuration", "Release");

		Assert.Equal(1, exitCode);
		Assert.Contains("CVL3004", stderr);
		Assert.Contains("Debug builds require --configuration Debug", stderr);
	}

	[Fact]
	public void Debug_AcceptsExplicitO0()
	{
		var (exitCode, stdout, stderr) = RunCompiler(MinimalCase, "--llvm", "--emit-ir", "-O0", "--debug");
		AssertCompilationSucceeded(exitCode, stdout, stderr, MinimalCase);
		Assert.Contains("!llvm.dbg.cu =", Normalize(stdout));
	}

	[Fact]
	public void Debug_DefaultsToO0()
	{
		var (exitCode, stdout, stderr) = EmitIr(MinimalCase, "-g");
		AssertCompilationSucceeded(exitCode, stdout, stderr, MinimalCase);

		var ir = Normalize(stdout);
		Assert.Contains("!llvm.dbg.cu =", ir);
		// The optimization pipeline is skipped at -O0, so the unoptimized body is still present.
		Assert.Contains("encoding: DW_ATE_signed", ir);
	}

	private (int ExitCode, string StdOut, string StdErr) EmitIr(string fileName, params string[] extraArgs) =>
		RunCompiler(fileName, ["--llvm", "--emit-ir", "-O0", .. extraArgs]);

	private static string Normalize(string text) => text.Replace("\r\n", "\n");

	private static void AssertSubprogramAttached(string ir, string symbolName)
	{
		var header = ExtractDefineHeader(ir, "@" + symbolName);
		var at = header.IndexOf("!dbg ", StringComparison.Ordinal);
		Assert.True(at >= 0, $"Expected '{symbolName}' to carry a !dbg attachment.");

		var id = header[(at + "!dbg ".Length)..]
			.Split([' ', '{', '\t'], StringSplitOptions.RemoveEmptyEntries)
			.FirstOrDefault()?
			.TrimStart('!');
		Assert.False(string.IsNullOrEmpty(id), $"Expected '{symbolName}' to reference a debug metadata node.");

		var definition = $"!{id} = distinct !DISubprogram(name: \"{symbolName}\"";
		Assert.Contains(definition, ir);

		var node = ir.Split('\n').First(line => line.StartsWith(definition, StringComparison.Ordinal));
		Assert.Contains("linkageName:", node);
		Assert.Contains("file: !", node);
		Assert.Contains("DISPFlagDefinition", node);
	}

	private static string ExtractDefineHeader(string ir, string symbol)
	{
		foreach (var line in ir.Split('\n'))
		{
			if (line.StartsWith("define", StringComparison.Ordinal) && line.Contains(symbol, StringComparison.Ordinal))
				return line;
		}

		throw new Xunit.Sdk.XunitException($"Expected the emitted IR to define '{symbol}'.");
	}

	private static string ExtractFunctionBody(string ir, string symbol)
	{
		var lines = ir.Split('\n');
		var start = Array.FindIndex(lines, line =>
			line.StartsWith("define", StringComparison.Ordinal) && line.Contains(symbol, StringComparison.Ordinal));
		if (start < 0)
			throw new Xunit.Sdk.XunitException($"Expected the emitted IR to define '{symbol}'.");

		var end = start + 1;
		while (end < lines.Length && lines[end] != "}")
			end++;

		return string.Join("\n", lines[start..Math.Min(end + 1, lines.Length)]);
	}

	private static int CountOccurrences(string text, string value)
	{
		var count = 0;
		var index = text.IndexOf(value, StringComparison.Ordinal);
		while (index >= 0)
		{
			count++;
			index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal);
		}

		return count;
	}
}