using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;

var config = ManualConfig.Create(DefaultConfig.Instance)
    .WithArtifactsPath(Path.Combine(AppContext.BaseDirectory, "BenchmarkDotNet.Artifacts"));
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, config);
return 0;
