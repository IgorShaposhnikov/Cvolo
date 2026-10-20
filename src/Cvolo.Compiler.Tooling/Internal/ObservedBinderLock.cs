using System.Diagnostics;
using Cvolo.Observability;
using Cvolo.Observability.Abstractions;

namespace Cvolo.Compiler.Tooling.Internal;

/// <summary>
/// Runs a body under a lock while recording the wait-for-acquisition and hold times as
/// observability spans (§8: binder_context.lock_wait, lock.wait.*, lock.hold.completed).
/// When the observer has the required depth disabled, the body runs under a plain lock
/// with no additional work, so disabled observability costs one boolean check.
/// </summary>
internal static class ObservedBinderLock
{
	internal static T Run<T>(ICompilerObserver observer, object gate, string stage, Func<T> body)
	{
		if (!observer.IsEnabled(CompilerEventKind.Phase, DiagnosticDetail.L1))
		{
			lock (gate)
			{
				return body();
			}
		}

		var wait = observer.BeginOperation(new OperationStart
		{
			Event = "lock.wait.started",
			Stage = stage,
			Kind = CompilerEventKind.Phase,
			Detail = DiagnosticDetail.L1,
		});

		lock (gate)
		{
			wait.Complete("acquired");
			var holdStart = Stopwatch.GetTimestamp();
			try
			{
				return body();
			}
			finally
			{
				observer.Emit(new CompilerEvent
				{
					Event = "lock.hold.completed",
					Kind = CompilerEventKind.Phase,
					Detail = DiagnosticDetail.L1,
					Stage = stage,
					Outcome = "released",
					ElapsedUs = CompilerObserver.TicksToUs(
						Stopwatch.GetTimestamp() - holdStart,
						Stopwatch.Frequency),
				});
			}
		}
	}
}
