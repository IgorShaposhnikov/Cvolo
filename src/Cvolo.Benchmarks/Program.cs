using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Order;
using BenchmarkDotNet.Running;

var config = ManualConfig.Create(DefaultConfig.Instance)
    .WithArtifactsPath(Path.Combine(AppContext.BaseDirectory, "BenchmarkDotNet.Artifacts"))
    .WithOrderer(new DefaultOrderer(SummaryOrderPolicy.FastestToSlowest, MethodOrderPolicy.Declared, JobOrderPolicy.Default));
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, config);
return 0;
