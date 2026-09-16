using System.Xml;
using System.Xml.Linq;

namespace Cvolo.Packaging;

/// <summary>
/// Bridges normal build/run commands with package restore. Direct source-file
/// compilations and projects without PackageReference entries keep their existing
/// compiler-only behavior; package projects are restored before the build graph is
/// fingerprinted or compilation starts.
/// </summary>
public sealed class PackageBuildRestoreService(PackageRestoreService restoreService)
{
	public RestoreResult? RestoreIfRequired(string pathOrDirectory, bool noRestore)
	{
		if (noRestore || IsDirectSourceFile(pathOrDirectory))
			return null;

		var projectPath = FindProjectFile(pathOrDirectory);
		if (projectPath is null || !HasPackageReferences(projectPath))
			return null;

		return restoreService.Restore(ProjectManifest.Load(projectPath));
	}

	private static bool IsDirectSourceFile(string pathOrDirectory) =>
		File.Exists(pathOrDirectory)
		&& string.Equals(Path.GetExtension(pathOrDirectory), ".cvl", StringComparison.OrdinalIgnoreCase);

	private static string? FindProjectFile(string pathOrDirectory)
	{
		if (File.Exists(pathOrDirectory)
			&& string.Equals(Path.GetExtension(pathOrDirectory), ".cvlproj", StringComparison.OrdinalIgnoreCase))
		{
			return Path.GetFullPath(pathOrDirectory);
		}

		if (!Directory.Exists(pathOrDirectory))
			return null;

		var candidates = Directory.GetFiles(Path.GetFullPath(pathOrDirectory), "*.cvlproj", SearchOption.TopDirectoryOnly);
		if (candidates.Length > 1)
			throw new InvalidOperationException($"Multiple .cvlproj files found in '{pathOrDirectory}'. Pass the project file explicitly.");
		return candidates.SingleOrDefault();
	}

	private static bool HasPackageReferences(string projectPath)
	{
		try
		{
			return XDocument.Load(projectPath).Descendants("PackageReference").Any();
		}
		catch (Exception ex) when (ex is IOException or XmlException or InvalidDataException)
		{
			throw new PackageException(
				PackageDiagnosticIds.LockOutOfSync,
				$"Cannot inspect package references in '{Path.GetFileName(projectPath)}'.",
				ex.Message);
		}
	}
}
