using System.Security.Cryptography;
using System.Text.Json;

namespace Cvolo.Tests.Tooling;

public sealed class ArtifactTests
{
	private static readonly string[] RequiredFiles =
	[
		"Antlr4.Runtime.Standard.dll",
		"Cvolo.Analysis.dll",
		"Cvolo.Compiler.Tooling.dll",
		"Cvolo.Core.dll",
		"Cvolo.Syntax.Antlr.dll",
		"Cvolo.Syntax.dll",
		"tooling.manifest.json",
		"docs/README.md",
	];

	private static string ArtifactDir => ArtifactPaths.ArtifactDir;

	[Fact]
	public void Bundle_ContainsRequiredFiles()
	{
		Timed.Out(() =>
		{
			var bundle = Directory.EnumerateFiles(ArtifactDir, "*", SearchOption.AllDirectories)
				.Select(ToRelative)
				.ToHashSet(StringComparer.Ordinal);

			foreach (var file in RequiredFiles)
				Assert.True(bundle.Contains(file), $"Bundle is missing '{file}'.");
		});
	}

	[Fact]
	public void Manifest_DeclaresCompleteMetadata()
	{
		Timed.Out(() =>
		{
			var manifestPath = Path.Combine(ArtifactDir, "tooling.manifest.json");
			using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
			var root = doc.RootElement;

			var toolingVersion = root.GetProperty("ToolingVersion").GetString();
			Assert.Equal(ArtifactPaths.ArtifactVersion, toolingVersion);
			Assert.Matches(@"^(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)$", toolingVersion);
			Assert.Equal("0.0", root.GetProperty("CompilerCompatibilityLine").GetString());
			Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("BuiltFromCompilerVersion").GetString()));
			Assert.Equal("net10.0", root.GetProperty("TargetFramework").GetString());
			Assert.Equal(JsonValueKind.Null, root.GetProperty("RuntimeIdentifier").ValueKind);
			Assert.NotNull(root.GetProperty("Commit").GetString());
		});
	}

	[Fact]
	public void Checksums_ListEveryFileExactlyOnce_SortedOrdinal()
	{
		Timed.Out(() =>
		{
			var entries = ReadChecksums();

			var actual = Directory.EnumerateFiles(ArtifactDir, "*", SearchOption.AllDirectories)
				.Select(ToRelative)
				.Where(p => !p.Equals("SHA256SUMS.txt", StringComparison.Ordinal))
				.OrderBy(p => p, StringComparer.Ordinal)
				.ToList();

			Assert.Equal(actual, entries.Select(e => e.Path).ToList());

			var sorted = entries.Select(e => e.Path).OrderBy(p => p, StringComparer.Ordinal).ToList();
			Assert.Equal(sorted, entries.Select(e => e.Path).ToList());

			Assert.Contains("docs/README.md", entries.Select(e => e.Path));
		});
	}

	[Fact]
	public void Checksums_AreLowerCaseHexWithTwoSpaceSeparator_AndForwardSlashPaths()
	{
		Timed.Out(() =>
		{
			var lines = File.ReadAllLines(Path.Combine(ArtifactDir, "SHA256SUMS.txt"));

			foreach (var line in lines)
			{
				var parts = line.Split("  ");
				Assert.True(parts.Length == 2, $"Malformed line '{line}'.");
				Assert.Matches("^[0-9a-f]{64}$", parts[0]);
				Assert.DoesNotMatch("\\\\", parts[1]);
				Assert.DoesNotContain('\t', parts[1]);
			}
		});
	}

	[Fact]
	public void Checksums_AreUtf8NoBom_LfOnly_NoHeaders()
	{
		Timed.Out(() =>
		{
			var bytes = File.ReadAllBytes(Path.Combine(ArtifactDir, "SHA256SUMS.txt"));

			Assert.NotEqual(0xEF, bytes[0]);
			Assert.Equal(0x00, bytes.Count(b => b == 0x0D));
		});
	}

	[Fact]
	public void Checksums_VerifyAgainstActualFileContents()
	{
		Timed.Out(() =>
		{
			var entries = ReadChecksums();

			foreach (var entry in entries)
			{
				var filePath = Path.Combine(ArtifactDir, entry.Path);
				Assert.True(File.Exists(filePath), $"Checksum path '{entry.Path}' has no file.");

				var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(filePath))).ToLowerInvariant();
				Assert.Equal(entry.Hash, hash);
			}
		});
	}

	private static List<(string Hash, string Path)> ReadChecksums()
	{
		var lines = File.ReadAllLines(Path.Combine(ArtifactDir, "SHA256SUMS.txt"));
		return lines
			.Select(line =>
			{
				var split = line.IndexOf("  ", StringComparison.Ordinal);
				Assert.True(split > 0, $"Malformed checksum line '{line}'.");
				return (Hash: line[..split], Path: line[(split + 2)..]);
			})
			.ToList();
	}

	private static string ToRelative(string fullPath)
	{
		var path = fullPath[(ArtifactDir.Length + 1)..];
		return path.Replace('\\', '/');
	}
}
