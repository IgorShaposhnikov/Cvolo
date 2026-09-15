namespace Cvolo.Packaging;

public static class PackageLockValidator
{
	public static bool Validate(ProjectManifest manifest, LockFile lockFile, out string message)
	{
		foreach (var dependency in manifest.Dependencies)
		{
			if (!IsLockedVersionAllowed(lockFile, dependency.Id, dependency.Version))
			{
				message = $"cvolo.lock.json is out of sync for package '{dependency.Id}'; run 'cvolo pkg update'.";
				return false;
			}
		}

		foreach (var package in lockFile.Packages)
		{
			foreach (var dependency in package.Value.Dependencies)
			{
				if (!IsLockedVersionAllowed(lockFile, dependency.Key, dependency.Value))
				{
					message = $"cvolo.lock.json is out of sync for transitive package '{dependency.Key}' required by '{package.Key}'; run 'cvolo pkg update'.";
					return false;
				}
			}
		}

		message = string.Empty;
		return true;
	}

	private static bool IsLockedVersionAllowed(LockFile lockFile, string id, string range)
	{
		return lockFile.Packages.TryGetValue(id, out var locked)
			&& VersionRange.Parse(range).Allows(SemanticVersion.Parse(locked.Resolved));
	}
}
