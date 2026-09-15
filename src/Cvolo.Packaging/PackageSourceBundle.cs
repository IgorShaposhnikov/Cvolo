using System.Text;

namespace Cvolo.Packaging;

/// <summary>
/// Deterministic framing for Sector 5 source. Each original project file is kept intact so
/// package-template consumers can parse files independently instead of treating a concatenated
/// multi-namespace buffer as one compilation unit. Older unframed Sector 5 payloads remain
/// readable as a single synthetic source file.
/// </summary>
public static class PackageSourceBundle
{
	private const string Header = "/* cvolo-source-bundle-v1 */\n";
	private const string FilePrefix = "/* cvolo-source-file ";
	private const string FileSuffix = " */\n";

	public sealed record SourceFile(string RelativePath, string Source);

	public static string Build(IReadOnlyList<string> sourceFiles, string projectDirectory)
	{
		ArgumentNullException.ThrowIfNull(sourceFiles);
		ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);

		var builder = new StringBuilder(Header);
		foreach (var path in sourceFiles
			.OrderBy(path => Path.GetRelativePath(projectDirectory, path), StringComparer.Ordinal))
		{
			var relativePath = Path.GetRelativePath(projectDirectory, path).Replace('\\', '/');
			var source = File.ReadAllText(path);
			var encodedPath = Convert.ToBase64String(Encoding.UTF8.GetBytes(relativePath));
			builder.Append(FilePrefix)
				.Append(encodedPath)
				.Append(' ')
				.Append(source.Length)
				.Append(FileSuffix)
				.Append(source);
		}
		return builder.ToString();
	}

	public static IReadOnlyList<SourceFile> Parse(string sourceBuffer)
	{
		if (string.IsNullOrEmpty(sourceBuffer))
			return [];

		if (!sourceBuffer.StartsWith(Header, StringComparison.Ordinal))
			return [new SourceFile("package.cvl", sourceBuffer)];

		var files = new List<SourceFile>();
		var offset = Header.Length;
		while (offset < sourceBuffer.Length)
		{
			if (!sourceBuffer.AsSpan(offset).StartsWith(FilePrefix, StringComparison.Ordinal))
				throw new PackageException(PackageDiagnosticIds.InvalidSourceBundle, "Package Sector 5 source bundle is malformed.");

			var markerEnd = sourceBuffer.IndexOf(FileSuffix, offset, StringComparison.Ordinal);
			if (markerEnd < 0)
				throw new PackageException(PackageDiagnosticIds.InvalidSourceBundle, "Package Sector 5 source bundle has an unterminated file header.");

			var marker = sourceBuffer[(offset + FilePrefix.Length)..markerEnd];
			var separator = marker.LastIndexOf(' ');
			if (separator <= 0 || !int.TryParse(marker[(separator + 1)..], out var sourceLength) || sourceLength < 0)
				throw new PackageException(PackageDiagnosticIds.InvalidSourceBundle, "Package Sector 5 source bundle contains an invalid source length.");

			string relativePath;
			try
			{
				relativePath = Encoding.UTF8.GetString(Convert.FromBase64String(marker[..separator]));
			}
			catch (FormatException ex)
			{
				throw new PackageException(PackageDiagnosticIds.InvalidSourceBundle, "Package Sector 5 source bundle contains an invalid file path.", ex.Message);
			}

			var sourceStart = markerEnd + FileSuffix.Length;
			if (sourceLength > sourceBuffer.Length - sourceStart)
				throw new PackageException(PackageDiagnosticIds.InvalidSourceBundle, "Package Sector 5 source bundle is truncated.");

			files.Add(new SourceFile(relativePath, sourceBuffer.Substring(sourceStart, sourceLength)));
			offset = sourceStart + sourceLength;
		}

		return files;
	}
}
