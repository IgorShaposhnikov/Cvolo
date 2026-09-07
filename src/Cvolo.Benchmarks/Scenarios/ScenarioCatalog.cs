namespace Cvolo.Benchmarks;

/// <summary>One paired benchmark program: Cvolo source, structurally equivalent Rust source, expected checksum.</summary>
internal sealed record Scenario(string Name, string Expected, bool SkipO0 = false)
{
    public string CvlFileName => $"{Name}.cvl";
    public string RustFileName => "src.rs";
}

/// <summary>The scenario catalog.</summary>
internal static class ScenarioCatalog
{
    public static readonly Scenario[] All =
    {
        new("ScalarLoop", "805032704"),
        new("MatrixSum", "1044480000"),
        new("LinkedList", "49995000"),
        new("RingBuffer", "1499850000", SkipO0: true),
		new("Nbody", "19999"),
	};
}

/// <summary>On-disk layout of the scenario inputs (mirrored to the build output by the csproj).</summary>
internal static class ScenarioPaths
{
    public static string Directory(string baseDir, Scenario s) => Path.Combine(baseDir, "Scenarios", s.Name);
    public static string Cvl(string baseDir, Scenario s) => Path.Combine(Directory(baseDir, s), s.CvlFileName);
    public static string Rust(string baseDir, Scenario s) => Path.Combine(Directory(baseDir, s), s.RustFileName);
}
