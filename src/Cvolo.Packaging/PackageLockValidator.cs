namespace Cvolo.Packaging;

public static class PackageLockValidator
{
	public static bool Validate(ProjectManifest manifest, LockFile lockFile, out string message)
	{
		foreach (var package in lockFile.Packages)
		{
			if (!IsValidContentHash(package.Value.ContentHash))
			{
				message = $"cvolo.lock.json has an invalid content hash for package '{package.Key}'.";
				return false;
			}

			try
			{
				SemanticVersion.Parse(package.Value.Resolved);
			}
			catch (PackageException)
			{
				message = $"cvolo.lock.json has an invalid resolved version for package '{package.Key}'.";
				return false;
			}
		}

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
				if (!lockFile.Packages.ContainsKey(dependency.Key))
				{
					message = $"cvolo.lock.json is missing transitive package '{dependency.Key}' required by '{package.Key}'; run 'cvolo pkg update'.";
					return false;
				}

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

	private static bool IsValidContentHash(string hash)
	{
		const string prefix = "blake3:";
		return hash.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
			&& hash.Length == prefix.Length + 64
			&& hash[prefix.Length..].All(Uri.IsHexDigit);
	}
}
