using System.Reflection;
using Cvolo.CLI.Packages;
using Cvolo.Commands;
using Cvolo.Drivers;
using Cvolo.Observability.Abstractions;
using Cvolo.Packaging;
using Microsoft.Extensions.DependencyInjection;

namespace Cvolo;

/// <summary>
/// Command-line host. The CLI invocation is the root observability operation of the compiler
/// process: it wraps argument parsing and command dispatch, and it keeps the process exit code
/// and stdout behaviour unchanged whether telemetry is enabled or not. An observer can be
/// injected by tests to observe an invocation without any file-system or environment coupling.
/// </summary>
internal static class CliHost
{
	internal static int Run(string[] args, ICompilerObserver? observerOverride = null)
	{
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

		// The session outlives the invocation scope on purpose: its disposal flushes the writer
		// after the terminal invocation event has been emitted.
		using var session = observerOverride is null ? CliObservabilitySession.Start() : null;
		var observer = observerOverride ?? session!.Observer;

		var scope = observer.BeginOperation(new OperationStart
		{
			Event = "compiler.invocation.started",
			Stage = "compiler.invocation",
			Kind = CompilerEventKind.Phase,
			Detail = DiagnosticDetail.L0,
			Attributes =
			[
				new("argCount", args.Length.ToString()),
				new("processId", Environment.ProcessId.ToString()),
			],
		});

		// Every terminal path below funnels through CompleteOnce exactly once: the normal dispatch
		// return, the unhandled-exception path, and a last-resort safety net for code that still
		// terminates the process abruptly (for example Environment.FailFast or a second exit path).
		// The exit code is captured before completion so the recorded outcome and the recorded
		// exitCode attribute always match the code the process actually returns.
		var completed = 0;
		void CompleteOnce(string outcome, string? reason, int exitCode)
		{
			if (Interlocked.Exchange(ref completed, 1) != 0)
				return;
			scope.Complete(outcome, reason: reason, attributes: [new("exitCode", exitCode.ToString())]);
			session?.Dispose();
		}

		EventHandler? onProcessExit = null;
		if (observerOverride is null)
		{
			onProcessExit = (_, _) => CompleteOnce(Environment.ExitCode == 0 ? "success" : "faulted",
				Environment.ExitCode == 0 ? null : "nonzero_exit_code",
				Environment.ExitCode);
			AppDomain.CurrentDomain.ProcessExit += onProcessExit;
		}

		try
		{
			// Commands return their exit code instead of terminating the process, so the process
			// exit code is observed here and the root span closes with the real outcome.
			var exitCode = Invoke(args);

			if (exitCode == 0)
				CompleteOnce("success", null, exitCode);
			else
				CompleteOnce("faulted", "nonzero_exit_code", exitCode);

			return exitCode;
		}
		catch
		{
			CompleteOnce("faulted", "exception", -1);
			throw;
		}
		finally
		{
			if (onProcessExit is not null)
				AppDomain.CurrentDomain.ProcessExit -= onProcessExit;
		}
	}

	private static int Invoke(string[] args)
	{
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
	}
}
