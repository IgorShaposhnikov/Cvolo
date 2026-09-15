using System.CommandLine;
using System.Text.Json;
using Cvolo.Packaging;

namespace Cvolo.CLI.Packages;

public sealed class PkgCommand : Command
{
	private readonly PackageCache _cache;
	private readonly PackageInstaller _installer;
	private readonly Func<string> _workingDirectory;
	private readonly TextWriter _stdout;
	private readonly TextWriter _stderr;

	public PkgCommand(PackageCache cache, PackageInstaller installer)
		: this(cache, installer, Directory.GetCurrentDirectory, Console.Out, Console.Error)
	{
	}

	public PkgCommand(
		PackageCache cache,
		PackageInstaller installer,
		Func<string> workingDirectory,
		TextWriter stdout,
		TextWriter stderr) : base("pkg", "Cvolo package manager.")
	{
		_cache = cache;
		_installer = installer;
		_workingDirectory = workingDirectory ?? throw new ArgumentNullException(nameof(workingDirectory));
		_stdout = stdout ?? throw new ArgumentNullException(nameof(stdout));
		_stderr = stderr ?? throw new ArgumentNullException(nameof(stderr));
		Add(CreateInstallCommand());
		Add(CreateAddCommand());
		Add(CreateRemoveCommand());
		Add(CreateListCommand());
		Add(CreateUpdateCommand());
		Add(CreateCacheCommand());
	}

	private Command CreateInstallCommand()
	{
		var command = new Command("install", "Install a local .cvlib archive, or install every package from cvolo.lock.json.");
		var path = new Argument<string?>("path") { Arity = ArgumentArity.ZeroOrOne, Description = "Local .cvlib archive. Omit to install from cvolo.lock.json." };
		command.Add(path);
		command.SetAction(parseResult => Run(() =>
		{
			var value = parseResult.GetValue(path);
			if (!string.IsNullOrWhiteSpace(value))
			{
				PrintInstall(_installer.InstallFromFile(value));
				return;
			}

			var manifest = ProjectManifest.Load(_workingDirectory());
			InstallFromLock(manifest);
		}));
		return command;
	}

	private Command CreateAddCommand()
	{
		var command = new Command("add", "Add a package reference, resolve, lock, and install.");
		var id = new Argument<string>("id") { Description = "Package ID." };
		var version = new Option<string>("--version", "*") { Description = "Version range (exact, ^, ~, 1.*, 1.2.*)." };
		command.Add(id);
		command.Add(version);
		command.SetAction(parseResult => Run(() =>
		{
			var manifest = ProjectManifest.Load(_workingDirectory());
			ProjectManifestWriter.AddPackageReference(manifest, parseResult.GetValue(id)!, parseResult.GetValue(version) ?? "*");
			manifest = ProjectManifest.Load(manifest.ProjectPath);
			ResolveWriteAndInstall(manifest);
		}));
		return command;
	}

	private Command CreateRemoveCommand()
	{
		var command = new Command("remove", "Remove a package reference and rewrite the lock file.");
		var id = new Argument<string>("id") { Description = "Package ID." };
		command.Add(id);
		command.SetAction(parseResult => Run(() =>
		{
			var manifest = ProjectManifest.Load(_workingDirectory());
			var value = parseResult.GetValue(id)!;
			if (!ProjectManifestWriter.RemovePackageReference(manifest, value))
				throw new PackageException(PackageDiagnosticIds.PackageNotReferenced, $"Project does not reference package '{value}'.");
			manifest = ProjectManifest.Load(manifest.ProjectPath);
			ResolveAndWriteLock(manifest);
		}));
		return command;
	}

	private Command CreateListCommand()
	{
		var command = new Command("list", "List direct and transitive packages from cvolo.lock.json.");
		command.SetAction(_ => Run(() =>
		{
			var manifest = ProjectManifest.Load(_workingDirectory());
			var lockFile = ReadValidatedLock(manifest);
			_stdout.WriteLine("Direct dependencies:");
			foreach (var dependency in manifest.Dependencies.OrderBy(d => d.Id, StringComparer.OrdinalIgnoreCase))
			{
				if (lockFile.Packages.TryGetValue(dependency.Id, out var locked))
					_stdout.WriteLine($"  {dependency.Id} {dependency.Version} -> {locked.Resolved}");
			}
			_stdout.WriteLine("Transitive dependencies:");
			var direct = manifest.Dependencies.Select(d => d.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
			foreach (var package in lockFile.Packages.Where(p => !direct.Contains(p.Key)).OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
				_stdout.WriteLine($"  {package.Key} {package.Value.Resolved} -> {package.Value.Resolved}");
		}));
		return command;
	}

	private Command CreateUpdateCommand()
	{
		var command = new Command("update", "Re-resolve dependencies and rewrite cvolo.lock.json.");
		var id = new Argument<string?>("id") { Arity = ArgumentArity.ZeroOrOne, Description = "Optional direct package ID to update." };
		command.Add(id);
		command.SetAction(parseResult => Run(() =>
		{
			var manifest = ProjectManifest.Load(_workingDirectory());
			var value = parseResult.GetValue(id);
			if (!string.IsNullOrWhiteSpace(value) && !manifest.Dependencies.Any(d => string.Equals(d.Id, value, StringComparison.OrdinalIgnoreCase)))
				throw new PackageException(PackageDiagnosticIds.PackageNotReferenced, $"Project does not reference package '{value}'.");

			ResolveWriteAndInstall(manifest);
		}));
		return command;
	}

	private Command CreateCacheCommand()
	{
		var command = new Command("cache", "Manage the local package cache.");
		command.Add(CreateCacheListCommand());
		command.Add(CreateCachePruneCommand());
		command.Add(CreateCacheClearCommand());
		return command;
	}

	private Command CreateCacheListCommand()
	{
		var command = new Command("list", "List installed packages in the local cache.");
		command.SetAction(_ => Run(() =>
		{
			foreach (var package in ReadCachedPackages())
				_stdout.WriteLine($"{package.Metadata.PackageId} {package.Metadata.Version} {package.Metadata.ContentHash}");
		}));
		return command;
	}

	private Command CreateCachePruneCommand()
	{
		var command = new Command("prune", "Remove cached packages; without --unused, removes all cached packages.");
		var unused = new Option<bool>("--unused") { Description = "Only remove packages not referenced by lock files under the current directory." };
		command.Add(unused);
		command.SetAction(parseResult => Run(() =>
		{
			var keep = parseResult.GetValue(unused) ? ReadLockedPackageKeys(_workingDirectory()) : [];
			var removed = 0;
			var touchedParents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (var package in ReadCachedPackages())
			{
				var key = PackageKey(package.Metadata.PackageId, package.Metadata.Version);
				if (keep.Contains(key))
					continue;

				touchedParents.Add(Path.GetDirectoryName(package.Directory)!);
				Directory.Delete(package.Directory, true);
				removed++;
			}

			foreach (var parent in touchedParents)
				RewritePackageIndex(parent);

			_stdout.WriteLine($"Removed {removed} cached package(s).");
		}));
		return command;
	}

	private Command CreateCacheClearCommand()
	{
		var command = new Command("clear", "Remove all cached packages.");
		command.SetAction(_ => Run(() =>
		{
			var directory = Path.Combine(_cache.RootPath, "pkg");
			if (Directory.Exists(directory))
				Directory.Delete(directory, true);
			_stdout.WriteLine("Package cache cleared.");
		}));
		return command;
	}

	private void ResolveWriteAndInstall(ProjectManifest manifest)
	{
		var graph = ResolveAndWriteLock(manifest);
		foreach (var package in graph.Packages.Values.OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase))
		{
			if (_installer.IsInstalled(package.Id, package.Version))
			{
				var installed = _installer.InstallFromCache(package.Id, package.Version);
				if (!string.Equals(installed.ContentHash, package.ContentHash, StringComparison.OrdinalIgnoreCase))
				{
					throw new PackageException(
						PackageDiagnosticIds.CachedContentMismatch,
						$"Cached package '{package.Id}@{package.Version}' has content hash {installed.ContentHash}, but the resolved feed now provides {package.ContentHash}. Remove/reinstall the cached package before updating the lock.");
				}

				continue;
			}

			PrintInstall(_installer.InstallFromFile(package.Path));
		}
	}

	private ResolvedGraph ResolveAndWriteLock(ProjectManifest manifest)
	{
		var feeds = LoadFeeds(manifest).ToList();
		var graph = new DependencyResolver().Resolve(manifest, feeds);
		DependencyResolver.ToLockFile(graph).Write(LockFile.GetPath(manifest));
		return graph;
	}

	private void InstallFromLock(ProjectManifest manifest)
	{
		var lockFile = ReadValidatedLock(manifest);
		var feeds = lockFile.Sources.Select(source => LocalFeed.Load(source, manifest.ProjectDirectory)).ToList();
		foreach (var package in lockFile.Packages.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
		{
			if (_installer.IsInstalled(package.Key, package.Value.Resolved))
			{
				var installed = _installer.InstallFromCache(package.Key, package.Value.Resolved);
				if (!string.Equals(installed.ContentHash, package.Value.ContentHash, StringComparison.OrdinalIgnoreCase))
					throw new PackageException(PackageDiagnosticIds.CachedContentMismatch, $"Cached package '{package.Key}@{package.Value.Resolved}' does not match the lock file.");
				continue;
			}

			var feedPackage = feeds.Select(f => f.FindBest(package.Key, package.Value.Resolved)).FirstOrDefault(p => p is not null)
				?? throw new PackageException(PackageDiagnosticIds.VersionConflict, $"Package '{package.Key}@{package.Value.Resolved}' was not found in locked feeds.");
			if (!string.Equals(feedPackage.Hash, package.Value.ContentHash, StringComparison.OrdinalIgnoreCase))
				throw new PackageException(PackageDiagnosticIds.CachedContentMismatch, $"Package '{package.Key}@{package.Value.Resolved}' feed hash does not match the lock file.");
			PrintInstall(_installer.InstallFromFile(feedPackage.FullPath));
		}
	}

	private static LockFile ReadValidatedLock(ProjectManifest manifest)
	{
		var lockFile = LockFile.Read(LockFile.GetPath(manifest));
		if (!PackageLockValidator.Validate(manifest, lockFile, out var message))
			throw new PackageException(PackageDiagnosticIds.LockOutOfSync, message);
		return lockFile;
	}

	private static IEnumerable<LocalFeed> LoadFeeds(ProjectManifest manifest)
	{
		if (!string.IsNullOrWhiteSpace(manifest.LocalFeed))
			yield return LocalFeed.Load(manifest.LocalFeed, manifest.ProjectDirectory);

		var global = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cvolo", "sources.json");
		if (File.Exists(global))
		{
			foreach (var source in JsonSerializer.Deserialize<string[]>(File.ReadAllText(global)) ?? [])
				yield return LocalFeed.Load(source, manifest.ProjectDirectory);
		}
	}

	private IReadOnlyList<(string Directory, InstalledPackageMetadata Metadata)> ReadCachedPackages()
	{
		var root = Path.Combine(_cache.RootPath, "pkg");
		if (!Directory.Exists(root))
			return [];

		var packages = new List<(string Directory, InstalledPackageMetadata Metadata)>();
		foreach (var path in Directory.GetFiles(root, ".metadata.json", SearchOption.AllDirectories))
		{
			try
			{
				var metadata = JsonSerializer.Deserialize<InstalledPackageMetadata>(File.ReadAllText(path))
					?? throw new InvalidDataException("Cache metadata deserialized to null.");
				PackageMetadata.ValidateIdentity(metadata.PackageId, metadata.Version);
				_ = SemanticVersion.Parse(metadata.Version);
				packages.Add((Path.GetDirectoryName(path)!, metadata));
			}
			catch (Exception ex) when (ex is JsonException or IOException or InvalidDataException or PackageException)
			{
				_stderr.WriteLine($"warning: skipping malformed cache metadata '{path}': {ex.Message}");
			}
		}

		return packages
			.OrderBy(package => package.Metadata.PackageId, StringComparer.OrdinalIgnoreCase)
			.ThenBy(package => SemanticVersion.Parse(package.Metadata.Version))
			.ToArray();
	}

	private static HashSet<string> ReadLockedPackageKeys(string root)
	{
		var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var path in Directory.GetFiles(root, "cvolo.lock.json", SearchOption.AllDirectories))
		{
			foreach (var package in LockFile.Read(path).Packages)
				keys.Add(PackageKey(package.Key, package.Value.Resolved));
		}

		return keys;
	}

	private static string PackageKey(string id, string version) => id.ToLowerInvariant() + "@" + version;

	private static void RewritePackageIndex(string packageDirectory)
	{
		if (!Directory.Exists(packageDirectory))
			return;

		var versions = Directory.GetDirectories(packageDirectory)
			.Where(d => File.Exists(Path.Combine(d, ".metadata.json")))
			.Select(Path.GetFileName)
			.Where(version => !string.IsNullOrWhiteSpace(version))
			.Order(StringComparer.Ordinal)
			.ToArray();
		var index = Path.Combine(packageDirectory, "index.json");
		if (versions.Length == 0)
		{
			if (File.Exists(index))
				File.Delete(index);
			return;
		}

		File.WriteAllText(index + ".tmp", JsonSerializer.Serialize(versions));
		File.Move(index + ".tmp", index, true);
	}

	private int Run(Action action)
	{
		try
		{
			action();
			return 0;
		}
		catch (PackageException ex)
		{
			_stderr.WriteLine($"error: {ex.Message}");
			if (ex.Detail is not null) _stderr.WriteLine(ex.Detail);
			return 1;
		}
		catch (Exception ex)
		{
			_stderr.WriteLine($"error: {ex.Message}");
			return 1;
		}
	}

	private void PrintInstall(InstallResult result)
	{
		_stdout.WriteLine(result.AlreadyInstalled ? "Already installed." : $"Installed {result.PackageId} {result.Version} -> {result.OutputPath}");
		_stdout.WriteLine($"  Source: {result.Source}");
		_stdout.WriteLine($"  Hash:   {result.ContentHash}");
	}
}
