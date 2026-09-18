using System.Reflection;
using Cvolo.CLI.Packages;
using Cvolo.Commands;
using Cvolo.Drivers;
using Cvolo.Packaging;
using Microsoft.Extensions.DependencyInjection;

// Stable CLI release contract. Keep this explicit instead of relying on the
// System.CommandLine built-in version formatting, which is an implementation detail.
if (args is ["--version"])
{
	var version = Assembly.GetEntryAssembly()?
		.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
		.InformationalVersion;

	if (string.IsNullOrWhiteSpace(version))
	{
		Console.Error.WriteLine("Unable to determine Cvolo compiler version.");
		return 1;
	}

	Console.WriteLine($"Cvolo {version}");
	return 0;
}

var services = new ServiceCollection();

services.AddSingleton<ICompilerDriver, CompilerDriver>();
services.AddSingleton<BuildCommand>();
services.AddSingleton<NewCommand>();
services.AddSingleton<RunCommand>();
services.AddSingleton<CheckCommand>();
services.AddSingleton<CleanCommand>();
services.AddSingleton<PackageRestoreService>();
services.AddSingleton<PackageBuildRestoreService>();
services.AddSingleton<RestoreCommand>();
services.AddSingleton<PackCommand>();
services.AddSingleton<PkgCommand>();
services.AddSingleton<PackageCache>();
services.AddSingleton<PackageInstaller>();
services.AddSingleton<CvoloRootCommand>();

using var serviceProvider = services.BuildServiceProvider();

var rootCommand = serviceProvider.GetRequiredService<CvoloRootCommand>();
return rootCommand.Parse(args).Invoke();
