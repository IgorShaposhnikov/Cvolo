using System.CommandLine;
using System.Text.Json;
using Cvolo.Packaging;

namespace Cvolo.CLI.Packages;

public sealed class PkgCommand : Command
{
	private readonly PackageCache _cache;
	private readonly PackageInstaller _installer;

	public PkgCommand(PackageCache cache, PackageInstaller installer) : base("pkg", "Cvolo package manager.")
	{
		_cache = cache;
		_installer = installer;
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

			var manifest = ProjectManifest.Load(Directory.GetCurrentDirectory());
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
			var manifest = ProjectManifest.Load(Directory.GetCurrentDirectory());
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
			var manifest = ProjectManifest.Load(Directory.GetCurrentDirectory());
			ProjectManifestWriter.RemovePackageReference(manifest, parseResult.GetValue(id)!);
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
			var manifest = ProjectManifest.Load(Directory.GetCurrentDirectory());
			var lockFile = ReadValidatedLock(manifest);
			Console.WriteLine("Direct dependencies:");
			foreach (var dependency in manifest.Dependencies.OrderBy(d => d.Id, StringComparer.OrdinalIgnoreCase))
			{
				if (lockFile.Packages.TryGetValue(dependency.Id, out var locked))
					Console.WriteLine($"  {dependency.Id} {dependency.Version} -> {locked.Resolved}");
			}
			Console.WriteLine("Transitive dependencies:");
			var direct = manifest.Dependencies.Select(d => d.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
			foreach (var package in lockFile.Packages.Where(p => !direct.Contains(p.Key)).OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
				Console.WriteLine($"  {package.Key} {package.Value.Resolved} -> {package.Value.Resolved}");
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
			var manifest = ProjectManifest.Load(Directory.GetCurrentDirectory());
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
				Console.WriteLine($"{package.Metadata.PackageId} {package.Metadata.Version} {package.Metadata.ContentHash}");
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
			var keep = parseResult.GetValue(unused) ? ReadLockedPackageKeys(Directory.GetCurrentDirectory()) : [];
			var removed = 0;
			foreach (var package in ReadCachedPackages())
			{
				var key = PackageKey(package.Metadata.PackageId, package.Metadata.Version);
				if (keep.Contains(key))
					continue;

				Directory.Delete(package.Directory, true);
				removed++;
			}

			Console.WriteLine($"Removed {removed} cached package(s).");
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
			Console.WriteLine("Package cache cleared.");
		}));
		return command;
	}

	private void ResolveWriteAndInstall(ProjectManifest manifest)
	{
		var graph = ResolveAndWriteLock(manifest);
		foreach (var package in graph.Packages.Values.OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase))
		{
			if (!_installer.IsInstalled(package.Id, package.Version))
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
				continue;

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

		return Directory.GetFiles(root, ".metadata.json", SearchOption.AllDirectories)
			.Select(path => (Directory: Path.GetDirectoryName(path)!, Metadata: JsonSerializer.Deserialize<InstalledPackageMetadata>(File.ReadAllText(path))))
			.Where(package => package.Metadata is not null)
			.Select(package => (package.Directory, Metadata: package.Metadata!))
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

	private static int Run(Action action)
	{
		try
		{
			action();
			return 0;
		}
		catch (PackageException ex)
		{
			Console.Error.WriteLine($"error: {ex.Message}");
			if (ex.Detail is not null) Console.Error.WriteLine(ex.Detail);
			return 1;
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine($"error: {ex.Message}");
			return 1;
		}
	}

	private static void PrintInstall(InstallResult result)
	{
		if (result.WasUnsigned)
			Console.Error.WriteLine($"warning {PackageDiagnosticIds.UnsignedWarning}: Package '{result.PackageId}@{result.Version}' is unsigned; signature verification skipped (Merkle integrity verified).");
		Console.WriteLine(result.AlreadyInstalled ? "Already installed." : $"Installed {result.PackageId} {result.Version} -> {result.OutputPath}");
		Console.WriteLine($"  Source: {result.Source}");
		Console.WriteLine($"  Hash:   {result.ContentHash}");
	}
}
