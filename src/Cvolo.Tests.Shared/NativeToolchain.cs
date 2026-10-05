namespace Cvolo.Tests.Shared;

/// <summary>Shared native-tool discovery used by integration tests that require clang/lld.</summary>
public static class NativeToolchain
{
	public static string? FindClang() => FindExecutable(OperatingSystem.IsWindows()
		? ["clang.exe", "clang"]
		: ["clang"]);

	public static string? FindLldLink() => FindExecutable(OperatingSystem.IsWindows()
		? ["lld-link.exe", "lld-link"]
		: ["ld.lld", "lld"]);

	private static string? FindExecutable(IReadOnlyList<string> names)
	{
		foreach (var name in names)
		{
			var besideTests = Path.Combine(AppContext.BaseDirectory, name);
			if (File.Exists(besideTests))
				return besideTests;
		}

		foreach (var directory in Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [])
		{
			if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
				continue;

			foreach (var name in names)
			{
				var candidate = Path.Combine(directory, name);
				if (File.Exists(candidate))
					return candidate;
			}
		}

		return null;
	}
}
