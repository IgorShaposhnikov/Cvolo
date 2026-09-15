using System.CommandLine;
using System.Text.Json;
using Cvolo.Packaging;

namespace Cvolo.CLI.Packages;

public sealed class PkgCommand : Command
{
	private readonly PackageInstaller _installer;

	public PkgCommand(InstallCommand installCommand, PackageInstaller installer) : base("pkg", "Cvolo package manager.")
	{
		_installer = installer;
		Add(CreateInstallCommand());
		Add(CreateAddCommand());
		Add(CreateRemoveCommand());
		Add(CreateListCommand());
		Add(CreateUpdateCommand());
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
			var lockFile = LockFile.Read(LockFile.GetPath(manifest));
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
		command.SetAction(_ => Run(() => ResolveWriteAndInstall(ProjectManifest.Load(Directory.GetCurrentDirectory()))));
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
		var lockFile = LockFile.Read(LockFile.GetPath(manifest));
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

	private static int Run(Action action)
	{
		try
		{
			action();
			return 0;
		}
		catch (PackageException ex)
		{
			Console.Error.WriteLine($"error {ex.Code}: {ex.Message}");
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
