using System.Security.Cryptography;
using System.Text;
using Cvolo.Core.Diagnostics;
using Cvolo.Packaging;
using Cvolo.Projects;
using Cvolo.Syntax.Antlr;

namespace Cvolo.Compiler.Tooling.Internal;

/// <summary>
/// Implements the <c>CaptureUniverseV1</c> / <c>BuildSnapshotFromCaptureV1</c> producer/consumer
/// contract: <see cref="Capture"/> discovers the full transitive semantic universe through an
/// <see cref="ICompilerInputReader"/> and records every byte it consumed; <see cref="Build"/>
/// reconstructs a generation-owned snapshot from that capture with no further input reads, parsing
/// every document and external unit fresh so no live AST object is shared across generations.
/// </summary>
internal static class ToolingCaptureService
{
	private const string HostTarget = "host";

	public static CaptureResult Capture(CvoloWorkspace workspace, ToolingCaptureRequest request, ICompilerInputReader reader)
	{
		ArgumentNullException.ThrowIfNull(request);
		ArgumentNullException.ThrowIfNull(reader);

		if (!IsSupportedTarget(request.RequestedTarget))
			return new CaptureResult.Unavailable(CaptureUnavailableReason.UnsupportedTarget, [request.RequestedTarget!]);

		try
		{
			var projectPath = Path.GetFullPath(request.ProjectPath);

			var (documents, externalUnits, packageSources, baseSourceFiles) = CompilerProjectAdapter.DiscoverDocuments(
				projectPath,
				workspace.AllocateDocumentId,
				request.LibraryPaths,
				packageCache: null,
				sourceOverrides: request.SourceOverrides,
				existingDocumentIds: null,
				allowRestore: request.AllowRestore,
				readText: reader.ReadAllText);

			var baseSet = new HashSet<string>(baseSourceFiles, PathComparer);
			var sources = documents.Values
				.Select(d => new CapturedSource(
					d.FilePath,
					d.Text.ToString(),
					MapOrigin(baseSet.Contains(d.FilePath) ? SourceOrigin.BaseSdk : SourceOrigin.Project)))
				.ToArray();

			var capturedExternal = externalUnits
				.Select(u => new CapturedExternalUnit(
					u.Unit.Context.FilePath,
					u.Unit.Context.Source,
					u.Kind == ExternalSemanticUnitKind.ExternalPackageApi
						? CapturedExternalUnitKind.ExternalPackageApi
						: CapturedExternalUnitKind.PackageTemplate,
					u.PackageId,
					u.PackageVersion,
					u.DefaultabilityCertificates is { Count: > 0 } certificates
						? certificates.ToDictionary(
							pair => pair.Key,
							pair => new CapturedDefaultabilityCertificate(pair.Value.State, pair.Value.Reason, pair.Value.Fingerprint),
							StringComparer.Ordinal)
						: null))
				.ToArray();

			var capturedPackageSources = packageSources
				.Select(p => new CapturedPackageSource(p.PackageId, p.Version, p.RelativePath, p.FilePath, p.Source))
				.ToArray();

			var projectAndLockFiles = ReadProjectAndLockFiles(projectPath, reader);

			var digest = ComputeDigest(projectPath, sources, projectAndLockFiles, capturedExternal, capturedPackageSources);

			var capture = new FrozenUniverseCapture(
				FrozenUniverseCapture.CurrentSchemaVersion,
				projectPath,
				HostTarget,
				sources,
				projectAndLockFiles,
				capturedExternal,
				capturedPackageSources,
				baseSourceFiles,
				digest);

			return new CaptureResult.Captured(capture);
		}
		catch (IOException ex)
		{
			return new CaptureResult.Failed(CaptureFailureReason.ReadFailed, ex.Message);
		}
		catch (UnauthorizedAccessException ex)
		{
			return new CaptureResult.Failed(CaptureFailureReason.ReadFailed, ex.Message);
		}
		catch (Exception ex)
		{
			return new CaptureResult.Failed(CaptureFailureReason.DiscoveryFailed, ex.Message);
		}
	}

	public static BuildResult Build(CvoloWorkspace workspace, FrozenUniverseCapture capture, CvoloProject? priorProject)
	{
		ArgumentNullException.ThrowIfNull(capture);

		if (capture.SchemaVersion != FrozenUniverseCapture.CurrentSchemaVersion)
			return new BuildResult.Unavailable($"Unsupported capture schema version {capture.SchemaVersion}.");

		var projectId = priorProject?.Id ?? workspace.AllocateProjectId();

		var priorIds = new Dictionary<string, DocumentId>(PathComparer);
		if (priorProject is not null)
		{
			foreach (var document in priorProject.InitialSnapshot.Documents.Values)
				priorIds[document.FilePath] = document.Id;
		}

		var documents = new Dictionary<DocumentId, DocumentSnapshot>(capture.Sources.Count);
		foreach (var source in capture.Sources)
		{
			var docId = priorIds.TryGetValue(source.Path, out var existing)
				? existing
				: workspace.AllocateDocumentId();
			documents[docId] = new DocumentSnapshot(docId, source.Path, SourceText.From(source.Text), null);
		}

		var externalUnits = new List<ExternalSemanticUnit>(capture.ExternalUnits.Count);
		foreach (var external in capture.ExternalUnits)
		{
			var context = new CompilationContext(external.SourceText, external.FilePath);
			var unit = new AntlrSyntaxParser().Parse(context);
			if (unit is null)
				continue;

			var kind = external.Kind == CapturedExternalUnitKind.ExternalPackageApi
				? ExternalSemanticUnitKind.ExternalPackageApi
				: ExternalSemanticUnitKind.PackageTemplate;
			externalUnits.Add(new ExternalSemanticUnit(
				unit,
				kind,
				external.PackageId,
				external.PackageVersion,
				external.DefaultabilityCertificates is { Count: > 0 } certificates
					? certificates.ToDictionary(
						pair => pair.Key,
						pair => new PackageApiDefaultability
						{
							State = pair.Value.State,
							Reason = pair.Value.Reason,
							Fingerprint = pair.Value.Fingerprint,
						},
						StringComparer.Ordinal)
					: null));
		}

		var packageSources = capture.PackageSources
			.Select(p => new PackageSourceDocument(p.PackageId, p.Version, p.RelativePath, p.FilePath, p.Source))
			.ToArray();

		// Generation-owned parse: fresh AST objects, no process-wide cache, no disk access.
		var preparsed = ParserAdapter.ParseFresh(documents);

		var snapshot = ProjectSnapshot.CreateOwned(
			projectId,
			documents,
			externalUnits,
			packageSources,
			capture.BaseSourceFiles,
			preparsed);

		var project = new CvoloProject(workspace, projectId, capture.ProjectPath, snapshot);

		return new BuildResult.Ready(project, snapshot, capture.CaptureDigest, capture.CaptureDigest);
	}

	private static CapturedSourceOrigin MapOrigin(SourceOrigin origin)
		=> origin switch
		{
			SourceOrigin.BaseSdk => CapturedSourceOrigin.BaseSdk,
			SourceOrigin.SystemSdk => CapturedSourceOrigin.SystemSdk,
			SourceOrigin.Package => CapturedSourceOrigin.Package,
			_ => CapturedSourceOrigin.Project,
		};

	private static bool IsSupportedTarget(string? target)
		=> string.IsNullOrWhiteSpace(target)
			|| string.Equals(target, "host", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(target, "default", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(target, "host/default", StringComparison.OrdinalIgnoreCase);

	private static IReadOnlyList<CapturedFile> ReadProjectAndLockFiles(string projectPath, ICompilerInputReader reader)
	{
		var files = new List<CapturedFile>();
		var directory = Directory.Exists(projectPath)
			? Path.GetFullPath(projectPath)
			: Path.GetDirectoryName(Path.GetFullPath(projectPath))!;

		void Capture(string path)
		{
			var full = Path.GetFullPath(path);
			if (!File.Exists(full))
				return;
			if (files.Any(f => string.Equals(f.Path, full, PathComparison)))
				return;
			files.Add(new CapturedFile(full, reader.ReadAllText(full)));
		}

		if (File.Exists(projectPath))
			Capture(projectPath);

		if (Directory.Exists(directory))
		{
			foreach (var project in Directory.GetFiles(directory, "*.cvlproj", SearchOption.TopDirectoryOnly))
			{
				Capture(project);
				Capture(Path.Combine(Path.GetDirectoryName(project)!, "cvolo.lock.json"));
			}

			Capture(Path.Combine(directory, "cvolo.lock.json"));
		}

		return files;
	}

	private static string ComputeDigest(
		string projectPath,
		IReadOnlyList<CapturedSource> sources,
		IReadOnlyList<CapturedFile> projectAndLockFiles,
		IReadOnlyList<CapturedExternalUnit> externalUnits,
		IReadOnlyList<CapturedPackageSource> packageSources)
	{
		var builder = new StringBuilder();
		builder.Append("schema=").Append(FrozenUniverseCapture.CurrentSchemaVersion).Append('\n');
		builder.Append("project=").Append(projectPath).Append('\n');

		foreach (var source in sources.OrderBy(s => s.Path, StringComparer.Ordinal))
			builder.Append("src:").Append(source.Path).Append('|').Append(source.Origin).Append('|').Append(source.Text).Append('\n');

		foreach (var file in projectAndLockFiles.OrderBy(f => f.Path, StringComparer.Ordinal))
			builder.Append("proj:").Append(file.Path).Append('|').Append(file.Text).Append('\n');

		foreach (var external in externalUnits.OrderBy(e => e.FilePath, StringComparer.Ordinal))
		{
			builder.Append("ext:").Append(external.FilePath).Append('|').Append(external.Kind).Append('|').Append(external.PackageId).Append('|').Append(external.PackageVersion).Append('|').Append(external.SourceText).Append('\n');

			// imported defaultability certificates are part of the frozen universe — a
			// capture that drops them would silently downgrade certified defaults to Unverified. Fold
			// them into the digest so a certificate change invalidates a cached capture.
			if (external.DefaultabilityCertificates is { Count: > 0 } certificates)
			{
				foreach (var (typeName, certificate) in certificates.OrderBy(c => c.Key, StringComparer.Ordinal))
					builder.Append("extcert:").Append(external.FilePath).Append('|').Append(typeName).Append('|').Append(certificate.State).Append('|').Append(certificate.Reason).Append('|').Append(certificate.Fingerprint).Append('\n');
			}
		}

		foreach (var source in packageSources.OrderBy(p => p.PackageId, StringComparer.Ordinal).ThenBy(p => p.RelativePath, StringComparer.Ordinal))
			builder.Append("pkg:").Append(source.PackageId).Append('|').Append(source.Version).Append('|').Append(source.RelativePath).Append('|').Append(source.Source).Append('\n');

		var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
		return Convert.ToHexString(bytes);
	}

	private static StringComparer PathComparer
		=> OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

	private static StringComparison PathComparison
		=> OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
