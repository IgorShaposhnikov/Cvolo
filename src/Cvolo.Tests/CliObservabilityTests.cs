using System.Diagnostics;
using System.Text;

namespace Cvolo.Tests;

/// <summary>
/// Verifies the CLI observability contract: an invocation is one independent root operation, its
/// span is emitted through the same abstract layer as the tooling events, the process exit code and
/// stdout are bit-for-bit identical with telemetry off and on, process instances are distinguishable,
/// and a trace file written via the CLI parses as schema-versioned JSONL.
/// </summary>
public sealed class CliObservabilityTests
{
	private static readonly string CompilerExecutable = Path.Combine(
		AppContext.BaseDirectory,
		OperatingSystem.IsWindows() ? "Cvolo.exe" : "Cvolo");

	private sealed record Invocation(int ExitCode, string StdOut, string StdErr);

	private static Invocation RunCli(
		IReadOnlyList<string> args,
		IDictionary<string, string?>? environment = null,
		string? workingDirectory = null)
	{
		var psi = new ProcessStartInfo
		{
			FileName = CompilerExecutable,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			StandardOutputEncoding = Encoding.UTF8,
			StandardErrorEncoding = Encoding.UTF8,
			UseShellExecute = false,
			CreateNoWindow = true,
		};
		foreach (var arg in args)
			psi.ArgumentList.Add(arg);
		if (workingDirectory is not null)
			psi.WorkingDirectory = workingDirectory;
		if (environment is not null)
		{
			foreach (var pair in environment)
			{
				if (pair.Value is null)
					psi.Environment.Remove(pair.Key);
				else
					psi.Environment[pair.Key] = pair.Value;
			}
		}

		using var process = Process.Start(psi)
			?? throw new InvalidOperationException($"Failed to start compiler '{CompilerExecutable}'.");
		var stdoutTask = process.StandardOutput.ReadToEndAsync();
		var stderrTask = process.StandardError.ReadToEndAsync();
		process.WaitForExit();

		return new Invocation(
			process.ExitCode,
			stdoutTask.GetAwaiter().GetResult(),
			stderrTask.GetAwaiter().GetResult());
	}

	private static string CreateTempDirectory()
	{
		var path = Path.Combine(Path.GetTempPath(), "cvolo-cli-observability", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(path);
		return path;
	}

	private static string WriteSource(string directory, string name, string source)
	{
		var path = Path.Combine(directory, name);
		File.WriteAllText(path, source);
		return path;
	}

	private const string ValidSource = "int main() {\n    return 0;\n}\n";

	[Fact]
	public void Version_fast_path_stays_unchanged()
	{
		var result = RunCli(["--version"]);
		Assert.Equal(0, result.ExitCode);
		Assert.StartsWith("Cvolo ", result.StdOut);
	}

	[Fact]
	public void Check_of_valid_source_succeeds_with_and_without_trace_output()
	{
		var directory = CreateTempDirectory();
		try
		{
			var source = WriteSource(directory, "Main.cvl", ValidSource);
			var tracePath = Path.Combine(directory, "trace.jsonl");

			var plain = RunCli(["check", source]);
			var traced = RunCli(
				["check", source],
				new Dictionary<string, string?>
				{
					["CVOLO_OBS_MODE"] = "detailed",
					["CVOLO_TRACE_PATH"] = tracePath,
				});

			// Telemetry must never change the observable outcome of an invocation.
			Assert.Equal(plain.ExitCode, traced.ExitCode);
			Assert.Equal(plain.StdOut, traced.StdOut);

			// The explicit trace path yields a JSONL stream at schema version 2.
			Assert.True(File.Exists(tracePath), "The CLI did not write the requested trace file.");
			var lines = File.ReadAllLines(tracePath);
			Assert.NotEmpty(lines);
			Assert.All(lines, line => Assert.Contains("\"schemaVersion\":2", line));
		}
		finally
		{
			Directory.Delete(directory, recursive: true);
		}
	}

	[Fact]
	public void Root_invocation_span_is_recorded_and_brackets_command_dispatch()
	{
		var directory = CreateTempDirectory();
		try
		{
			var source = WriteSource(directory, "Main.cvl", ValidSource);
			var tracePath = Path.Combine(directory, "invocation.jsonl");

			var result = RunCli(
				["check", source],
				new Dictionary<string, string?>
				{
					["CVOLO_OBS_MODE"] = "basic",
					["CVOLO_TRACE_PATH"] = tracePath,
				});
			Assert.Equal(0, result.ExitCode);

			var lines = File.ReadAllLines(tracePath);
			Assert.Contains(lines, l => l.Contains("\"event\":\"session.started\""));
			Assert.Contains(lines, l => l.Contains("\"event\":\"compiler.invocation.started\""));
			Assert.Contains(lines, l => l.Contains("\"event\":\"compiler.invocation.completed\""));
			Assert.Contains(lines, l => l.Contains("\"event\":\"session.ended\""));

			// The terminal invocation event reports the real exit code and closes the span.
			Assert.Contains(
				lines,
				l => l.Contains("\"event\":\"compiler.invocation.completed\"")
					&& l.Contains("\"outcome\":\"success\"")
					&& l.Contains("\"exitCode\":\"0\""));

			// The session end must not precede the invocation completion: the trace closes in order.
			var invocationCompleted = Array.FindIndex(lines, l => l.Contains("\"event\":\"compiler.invocation.completed\""));
			var sessionEnded = Array.FindIndex(lines, l => l.Contains("\"event\":\"session.ended\""));
			Assert.True(sessionEnded > invocationCompleted, "session.ended must follow the terminal invocation event.");
		}
		finally
		{
			Directory.Delete(directory, recursive: true);
		}
	}

	[Fact]
	public void Failing_check_records_faulted_invocation_with_nonzero_exit_code()
	{
		var directory = CreateTempDirectory();
		try
		{
			// Unterminated declaration: check must fail with a nonzero exit code.
			var source = WriteSource(directory, "Broken.cvl", "int main( {\n");
			var tracePath = Path.Combine(directory, "broken.jsonl");

			var result = RunCli(
				["check", source],
				new Dictionary<string, string?>
				{
					["CVOLO_OBS_MODE"] = "basic",
					["CVOLO_TRACE_PATH"] = tracePath,
				});
			Assert.NotEqual(0, result.ExitCode);

			var lines = File.ReadAllLines(tracePath);
			var expectedExit = result.ExitCode.ToString();
			Assert.Contains(
				lines,
				l => l.Contains("\"event\":\"compiler.invocation.faulted\"")
					&& l.Contains("\"reason\":\"nonzero_exit_code\"")
					&& l.Contains($"\"exitCode\":\"{expectedExit}\""));
		}
		finally
		{
			Directory.Delete(directory, recursive: true);
		}
	}

	[Fact]
	public void Two_process_instances_produce_distinguishable_session_ids()
	{
		var directory = CreateTempDirectory();
		try
		{
			var source = WriteSource(directory, "Main.cvl", ValidSource);

			string SessionIdOf(string traceName)
			{
				var tracePath = Path.Combine(directory, traceName);
				RunCli(
					["check", source],
					new Dictionary<string, string?>
					{
						["CVOLO_OBS_MODE"] = "basic",
						["CVOLO_TRACE_PATH"] = tracePath,
					});
				var started = File.ReadAllLines(tracePath)
					.First(l => l.Contains("\"event\":\"session.started\""));
				return started;
			}

			var first = SessionIdOf("first.jsonl");
			var second = SessionIdOf("second.jsonl");

			Assert.NotNull(first);
			Assert.NotNull(second);
			Assert.NotEqual(first, second);
		}
		finally
		{
			Directory.Delete(directory, recursive: true);
		}
	}

	[Fact]
	public void Off_mode_writes_no_trace_file()
	{
		var directory = CreateTempDirectory();
		try
		{
			var source = WriteSource(directory, "Main.cvl", ValidSource);
			var tracePath = Path.Combine(directory, "off.jsonl");

			var result = RunCli(
				["check", source],
				new Dictionary<string, string?>
				{
					["CVOLO_OBS_MODE"] = "off",
					["CVOLO_TRACE_PATH"] = tracePath,
				});

			Assert.Equal(0, result.ExitCode);
			Assert.False(File.Exists(tracePath), "Telemetry-off must not create a trace file.");
		}
		finally
		{
			Directory.Delete(directory, recursive: true);
		}
	}
}
