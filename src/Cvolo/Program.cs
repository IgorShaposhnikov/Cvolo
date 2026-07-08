using Cvolo.Commands;
using Cvolo.Drivers;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();

services.AddSingleton<ICompilerDriver, CompilerDriver>();
services.AddSingleton<BuildCommand>();
services.AddSingleton<NewCommand>();
services.AddSingleton<RunCommand>();
services.AddSingleton<CheckCommand>();
services.AddSingleton<CleanCommand>();
services.AddSingleton<CvoloRootCommand>();

using var serviceProvider = services.BuildServiceProvider();

var rootCommand = serviceProvider.GetRequiredService<CvoloRootCommand>();
rootCommand.Parse(args).Invoke();
