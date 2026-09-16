using System.CommandLine;
using Cvolo.Packaging;

namespace Cvolo.CLI.Packages;

public sealed class RestoreCommand : Command
{
	private readonly PackageRestoreService _restoreService;
	private readonly Func<string> _workingDirectory;
	private readonly TextWriter _stdout;
	private readonly TextWriter _stderr;

	public RestoreCommand(PackageRestoreService restoreService)
		: this(restoreService, Directory.GetCurrentDirectory, Console.Out, Console.Error)
	{
	}

	public RestoreCommand(
		PackageRestoreService restoreService,
		Func<string> workingDirectory,
		TextWriter stdout,
		TextWriter stderr)
		: base("restore", "Restore project package dependencies from local feeds and cvolo.lock.json.")
	{
		_restoreService = restoreService ?? throw new ArgumentNullException(nameof(restoreService));
		_workingDirectory = workingDirectory ?? throw new ArgumentNullException(nameof(workingDirectory));
		_stdout = stdout ?? throw new ArgumentNullException(nameof(stdout));
		_stderr = stderr ?? throw new ArgumentNullException(nameof(stderr));

		SetAction(_ => Run());
	}

	private int Run()
	{
		try
		{
			var manifest = ProjectManifest.Load(_workingDirectory());
			var result = _restoreService.Restore(manifest);
			_stdout.WriteLine(result.LockFileUpdated
				? $"Restored {manifest.PackageId}: updated {Path.GetFileName(result.LockFilePath)}."
				: $"Restored {manifest.PackageId}: lock file is up to date.");
			_stdout.WriteLine($"  Installed: {result.InstalledPackages}");
			_stdout.WriteLine($"  Cached:    {result.CachedPackages}");
			return 0;
		}
		catch (PackageException ex)
		{
			_stderr.WriteLine($"error: {ex.Message}");
			if (ex.Detail is not null)
				_stderr.WriteLine(ex.Detail);
			return 1;
		}
		catch (Exception ex)
		{
			_stderr.WriteLine($"error: {ex.Message}");
			return 1;
		}
	}
}
