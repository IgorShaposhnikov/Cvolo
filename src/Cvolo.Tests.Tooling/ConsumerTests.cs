using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;

namespace Cvolo.Tests.Tooling;

public sealed class ConsumerTests
{
	private static string ArtifactDir => ArtifactPaths.ArtifactDir;

	[Fact]
	public void StandaloneConsumer_BuildsAndRuns_AgainstArtifactOnly()
	{
		Timed.Out(() =>
		{
			using var fixture = TempProject.Create(("Main.cvl", "int main() { return 0; }"));
			using var consumer = CreateConsumerProject(fixture);

			var build = RunDotnet($"build \"{consumer.Dir}\" --nologo -v q --framework net10.0");
			Assert.Equal(0, build.ExitCode);

			var consumerBin = Path.Combine(consumer.Dir, "bin", "Debug", "net10.0");
			var run = RunDotnet($"exec \"{Path.Combine(consumerBin, "Consumer.dll")}\" \"{fixture.ProjectFilePath}\"");
			Assert.Equal(0, run.ExitCode);
			Assert.Equal("0", run.Output.Trim());
		}, seconds: 180);
	}

	[Fact]
	public void StandaloneConsumer_DetectsCompileErrors()
	{
		Timed.Out(() =>
		{
			using var fixture = TempProject.Create(("Main.cvl", "int main( {"));
			using var consumer = CreateConsumerProject(fixture);

			var build = RunDotnet($"build \"{consumer.Dir}\" --nologo -v q --framework net10.0");
			Assert.Equal(0, build.ExitCode);

			var consumerBin = Path.Combine(consumer.Dir, "bin", "Debug", "net10.0");
			var run = RunDotnet($"exec \"{Path.Combine(consumerBin, "Consumer.dll")}\" \"{fixture.ProjectFilePath}\"");
			Assert.Equal(0, run.ExitCode);
			var errors = int.Parse(run.Output.Trim());
			Assert.True(errors > 0, $"Expected non-zero errors, got {errors}.");
		}, seconds: 180);
	}

	[Fact]
	public void Consumer_ReferencesOnlyToolingAmongCvoloAssemblies()
	{
		Timed.Out(() =>
		{
			using var fixture = TempProject.Create(("Main.cvl", "int main() { return 0; }"));
			using var consumer = CreateConsumerProject(fixture);

			var build = RunDotnet($"build \"{consumer.Dir}\" --nologo -v q --framework net10.0");
			Assert.Equal(0, build.ExitCode);

			var consumerBin = Path.Combine(consumer.Dir, "bin", "Debug", "net10.0");
			var consumerAssembly = Path.Combine(consumerBin, "Consumer.dll");
			using var stream = File.OpenRead(consumerAssembly);
			using var peReader = new PEReader(stream);
			var metadataReader = peReader.GetMetadataReader();
			var references = metadataReader.AssemblyReferences
				.Select(handle => metadataReader.GetString(metadataReader.GetAssemblyReference(handle).Name))
				.Where(n => n.StartsWith("Cvolo", StringComparison.Ordinal))
				.OrderBy(n => n, StringComparer.Ordinal)
				.ToList();

			Assert.Equal(["Cvolo.Compiler.Tooling"], references);
		}, seconds: 180);
	}

	private sealed class ConsumerFixture(string dir) : IDisposable
	{
		public string Dir { get; } = dir;
		public void Dispose() => Directory.Delete(Dir, recursive: true);
	}

	private static ConsumerFixture CreateConsumerProject(TempProject fixture)
	{
		var dir = Path.Combine(Path.GetTempPath(), "cvolo-consumer-test", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(dir);

		File.WriteAllText(
			Path.Combine(dir, "Consumer.csproj"),
			$$"""
			<Project Sdk="Microsoft.NET.Sdk">
				<PropertyGroup>
					<OutputType>Exe</OutputType>
					<TargetFramework>net10.0</TargetFramework>
					<Nullable>enable</Nullable>
					<ImplicitUsings>enable</ImplicitUsings>
					<EnableDefaultCompileItems>false</EnableDefaultCompileItems>
				</PropertyGroup>
				<ItemGroup>
					<Compile Include="Program.cs" />
				</ItemGroup>
				<ItemGroup>
					<Reference Include="Cvolo.Compiler.Tooling">
						<HintPath>{{ArtifactDir.Replace("\\", "/")}}/Cvolo.Compiler.Tooling.dll</HintPath>
					</Reference>
				</ItemGroup>
			</Project>
			""",
			new UTF8Encoding(false));

		File.WriteAllText(
			Path.Combine(dir, "Program.cs"),
			"""
			using Cvolo.Compiler.Tooling;

			var projectPath = args[0];
			var workspace = CvoloWorkspace.Create();
			var project = workspace.OpenProject(projectPath);
			var snapshot = project.InitialSnapshot;
			var diagnostics = snapshot.Documents.Values
				.SelectMany(d => d.GetDiagnostics())
				.Where(d => d.Severity == DiagnosticSeverity.Error)
				.Count();
			Console.Write(diagnostics);
			""",
			new UTF8Encoding(false));

		return new ConsumerFixture(dir);
	}

	private static (int ExitCode, string Output, string Error) RunDotnet(string arguments)
	{
		var psi = new ProcessStartInfo
		{
			FileName = "dotnet",
			Arguments = arguments,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			WorkingDirectory = ArtifactPaths.RepoRoot,
			CreateNoWindow = true,
			UseShellExecute = false,
		};

		using var process = Process.Start(psi)!;
		var stdoutTask = process.StandardOutput.ReadToEndAsync();
		var stderrTask = process.StandardError.ReadToEndAsync();

		if (!process.WaitForExit(150_000))
		{
			try
			{
				process.Kill(entireProcessTree: true);
			}
			catch
			{
				// Best-effort cleanup; the timeout below is the authoritative failure.
			}

			throw new TimeoutException($"'dotnet {arguments}' did not exit within 150s.");
		}

		return (process.ExitCode, stdoutTask.GetAwaiter().GetResult(), stderrTask.GetAwaiter().GetResult());
	}
}
