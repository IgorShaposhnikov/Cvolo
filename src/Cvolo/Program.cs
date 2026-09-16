using Cvolo.CLI.Packages;
using Cvolo.Commands;
using Cvolo.Drivers;
using Cvolo.Packaging;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();

services.AddSingleton<ICompilerDriver, CompilerDriver>();
services.AddSingleton<BuildCommand>();
services.AddSingleton<NewCommand>();
services.AddSingleton<RunCommand>();
services.AddSingleton<CheckCommand>();
services.AddSingleton<CleanCommand>();
services.AddSingleton<PackageRestoreService>();
services.AddSingleton<RestoreCommand>();
services.AddSingleton<PackCommand>();
services.AddSingleton<PkgCommand>();
services.AddSingleton<PackageCache>();
services.AddSingleton<PackageInstaller>();
services.AddSingleton<CvoloRootCommand>();

using var serviceProvider = services.BuildServiceProvider();

var rootCommand = serviceProvider.GetRequiredService<CvoloRootCommand>();
return rootCommand.Parse(args).Invoke();
