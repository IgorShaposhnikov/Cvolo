using System.Text.RegularExpressions;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Exporters;

namespace Cvolo.Benchmarks;

/// <summary>
/// BenchmarkDotNet compares Cvolo against Rust across optimization levels and
/// TBAA on/off. BenchmarkDotNet automatically determines the number of warm-up
/// and measurement iterations (typically dozens to a few hundred attempts) to
/// reach statistically stable timings, so no hand-rolled loop count is needed.
/// </summary>
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
[MemoryDiagnoser]
[Config(typeof(Config))]
public class BenchmarkSuite
{
	private class Config : ManualConfig
	{
		public Config()
		{
			AddExporter(MarkdownExporter.GitHub);
			WithArtifactsPath(Path.Combine(AppContext.BaseDirectory, "BenchmarkDotNet.Artifacts"));
		}
	}

	private static readonly string BaseDir = Toolchains.Root;

	// ---- scalar-loop -----------------------------------------------------------------

	[Benchmark, BenchmarkCategory("ScalarLoop")]
	public string ScalarLoop_Cvolo_O0_Tbaa() => RunCvolo(Scenario("ScalarLoop"), "O0", false);

	[Benchmark, BenchmarkCategory("ScalarLoop")]
	public string ScalarLoop_Cvolo_O0_NoTbaa() => RunCvolo(Scenario("ScalarLoop"), "O0", true);

	[Benchmark]
	public string ScalarLoop_Cvolo_O2_Tbaa() => RunCvolo(Scenario("ScalarLoop"), "O2", false);

	[Benchmark, BenchmarkCategory("ScalarLoop")]
	public string ScalarLoop_Cvolo_O2_NoTbaa() => RunCvolo(Scenario("ScalarLoop"), "O2", true);

	[Benchmark, BenchmarkCategory("ScalarLoop")]
	public string ScalarLoop_Cvolo_O3_Tbaa() => RunCvolo(Scenario("ScalarLoop"), "O3", false);

	[Benchmark, BenchmarkCategory("ScalarLoop")]
	public string ScalarLoop_Cvolo_O3_NoTbaa() => RunCvolo(Scenario("ScalarLoop"), "O3", true);

	[Benchmark, BenchmarkCategory("ScalarLoop")]
	public string ScalarLoop_Rust_O3() => RunRust(Scenario("ScalarLoop"));

	// ---- matrix-sum -----------------------------------------------------------------

	[Benchmark, BenchmarkCategory("MatrixSum")]
	public string MatrixSum_Cvolo_O0() => RunCvolo(Scenario("MatrixSum"), "O0", false);

	[Benchmark, BenchmarkCategory("MatrixSum")]
	public string MatrixSum_Cvolo_O2_Tbaa() => RunCvolo(Scenario("MatrixSum"), "O2", false);

	[Benchmark, BenchmarkCategory("MatrixSum")]
	public string MatrixSum_Cvolo_O2_NoTbaa() => RunCvolo(Scenario("MatrixSum"), "O2", true);

	[Benchmark, BenchmarkCategory("MatrixSum")]
	public string MatrixSum_Cvolo_O3_Tbaa() => RunCvolo(Scenario("MatrixSum"), "O3", false);

	[Benchmark, BenchmarkCategory("MatrixSum")]
	public string MatrixSum_Cvolo_O3_NoTbaa() => RunCvolo(Scenario("MatrixSum"), "O3", true);

	[Benchmark, BenchmarkCategory("MatrixSum")]
	public string MatrixSum_Rust_O3() => RunRust(Scenario("MatrixSum"));

	// ---- linked-list ----------------------------------------------------------------

	[Benchmark, BenchmarkCategory("LinkedList")]
	public string LinkedList_Cvolo_O0() => RunCvolo(Scenario("LinkedList"), "O0", false);

	[Benchmark, BenchmarkCategory("LinkedList")]
	public string LinkedList_Cvolo_O2_Tbaa() => RunCvolo(Scenario("LinkedList"), "O2", false);

	[Benchmark, BenchmarkCategory("LinkedList")]
	public string LinkedList_Cvolo_O2_NoTbaa() => RunCvolo(Scenario("LinkedList"), "O2", true);

	[Benchmark, BenchmarkCategory("LinkedList")]
	public string LinkedList_Cvolo_O3_Tbaa() => RunCvolo(Scenario("LinkedList"), "O3", false);

	[Benchmark, BenchmarkCategory("LinkedList")]
	public string LinkedList_Cvolo_O3_NoTbaa() => RunCvolo(Scenario("LinkedList"), "O3", true);

	[Benchmark, BenchmarkCategory("LinkedList")]
	public string LinkedList_Rust_O3() => RunRust(Scenario("LinkedList"));

	// ---- ring-buffer ----------------------------------------------------------------

	[Benchmark, BenchmarkCategory("RingBuffer")]
	public string RingBuffer_Cvolo_O2_Tbaa() => RunCvolo(Scenario("RingBuffer"), "O2", false);

	[Benchmark, BenchmarkCategory("RingBuffer")]
	public string RingBuffer_Cvolo_O2_NoTbaa() => RunCvolo(Scenario("RingBuffer"), "O2", true);

	[Benchmark, BenchmarkCategory("RingBuffer")]
	public string RingBuffer_Cvolo_O3_Tbaa() => RunCvolo(Scenario("RingBuffer"), "O3", false);

	[Benchmark, BenchmarkCategory("RingBuffer")]
	public string RingBuffer_Cvolo_O3_NoTbaa() => RunCvolo(Scenario("RingBuffer"), "O3", true);

	[Benchmark, BenchmarkCategory("RingBuffer")]
	public string RingBuffer_Rust_O3() => RunRust(Scenario("RingBuffer"));

	// ---- n-body ---------------------------------------------------------------------

	[Benchmark, BenchmarkCategory("Nbody")]
	public string NBody_Cvolo_O0() => RunCvolo(Scenario("Nbody"), "O0", false);

	[Benchmark, BenchmarkCategory("Nbody")]
	public string NBody_Cvolo_O3() => RunCvolo(Scenario("Nbody"), "O3", false);

	[Benchmark, BenchmarkCategory("Nbody")]
	public string NBody_Rust_O3() => RunRust(Scenario("Nbody"));

	// ---- ptr-mix (TBAA pointer-aliasing showcase) -----------------------------------

	[Benchmark, BenchmarkCategory("PtrMix")]
	public string PtrMix_Cvolo_O3_Tbaa() => RunCvolo(Scenario("PtrMix"), "O3", false);

	[Benchmark, BenchmarkCategory("PtrMix")]
	public string PtrMix_Cvolo_O3_NoTbaa() => RunCvolo(Scenario("PtrMix"), "O3", true);

	[Benchmark, BenchmarkCategory("PtrMix")]
	public string PtrMix_Rust_O3() => RunRust(Scenario("PtrMix"));

	private static Scenario Scenario(string name) => ScenarioCatalog.All.First(s => s.Name == name);

	private static string RunCvolo(Scenario s, string opt, bool noTbaa)
	{
		var exe = Toolchains.BuildCvolo(BaseDir, s, opt, noTbaa);
		var stdout = Toolchains.RunExe(BaseDir, s, exe);
		AssertAnswer(s, stdout);
		return stdout;
	}

	private static string RunRust(Scenario s)
	{
		var exe = Toolchains.BuildRust(BaseDir, s);
		var stdout = Toolchains.RunExe(BaseDir, s, exe);
		AssertAnswer(s, stdout);
		return stdout;
	}

	private static void AssertAnswer(Scenario s, string stdout)
	{
		var m = Regex.Match(stdout, @"Answer:\s*(-?\d+)");
		if (!m.Success || m.Groups[1].Value != s.Expected)
			throw new InvalidOperationException(
				$"{s.Name}: expected 'Answer: {s.Expected}', got '{stdout}'");
	}
}
