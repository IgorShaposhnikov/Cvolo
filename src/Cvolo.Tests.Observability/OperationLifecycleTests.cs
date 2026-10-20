using Cvolo.Observability;
using Cvolo.Observability.Abstractions;

namespace Cvolo.Tests.Observability;

/// <summary>Operation spans: exactly one terminal per scope, honest labels, crashed sessions detectable.</summary>
public class OperationLifecycleTests
{
	[Fact]
	public void Scope_emits_started_then_single_completed_with_elapsed_from_monotonic_clock()
	{
		var clock = new FakeClock();
		var observer = ObservabilityTestSupport.CreateObserver(ObservabilityMode.Detailed, out var queue, clock: clock);

		using (var scope = observer.BeginOperation(new OperationStart
		{
			Event = "operation.started",
			Stage = "bind.project",
			Kind = CompilerEventKind.Phase,
			Detail = DiagnosticDetail.L1,
		}))
		{
			Assert.False(string.IsNullOrEmpty(scope.OperationId));
			Assert.False(scope.IsCompleted);
			clock.AdvanceMicroseconds(2500);
			scope.Complete("success");
			Assert.True(scope.IsCompleted);
		}

		var events = queue.DrainAll();
		Assert.Equal(2, events.Count);
		Assert.Equal("operation.started", events[0].Event.Event);
		Assert.Equal("started", events[0].Event.Outcome);
		Assert.Equal("operation.completed", events[1].Event.Event);
		Assert.Equal("success", events[1].Event.Outcome);
		Assert.Equal(2500, events[1].Event.ElapsedUs); // monotonic ticks → µs on fake clock
		Assert.Equal(events[0].Event.OperationId, events[1].Event.OperationId);
	}

	[Fact]
	public void Second_terminal_is_ignored_so_time_is_never_double_counted()
	{
		var clock = new FakeClock();
		var observer = ObservabilityTestSupport.CreateObserver(ObservabilityMode.Detailed, out var queue, clock: clock);

		var scope = observer.BeginOperation(new OperationStart
		{
			Event = "operation.started",
			Kind = CompilerEventKind.Phase,
			Detail = DiagnosticDetail.L1,
		});
		scope.Complete("success");
		scope.Complete("faulted", reason: "late");
		scope.Dispose(); // must not add a third terminal

		var events = queue.DrainAll();
		Assert.Equal(2, events.Count);
		Assert.Equal("operation.completed", events[1].Event.Event);
		Assert.Equal("success", events[1].Event.Outcome);
	}

	[Fact]
	public void Dispose_without_completion_records_an_incomplete_terminal_not_a_success()
	{
		var clock = new FakeClock();
		var observer = ObservabilityTestSupport.CreateObserver(ObservabilityMode.Detailed, out var queue, clock: clock);

		var scope = observer.BeginOperation(new OperationStart
		{
			Event = "operation.started",
			Kind = CompilerEventKind.Phase,
			Detail = DiagnosticDetail.L1,
		});
		scope.Dispose();

		var events = queue.DrainAll();
		var terminal = events[^1];
		Assert.Equal("operation.completed", terminal.Event.Event);
		Assert.Equal("incomplete", terminal.Event.Outcome);
		Assert.Equal("scope_disposed_without_completion", terminal.Event.Reason);
	}

	[Fact]
	public void Cancelled_and_faulted_outcomes_use_their_own_terminal_event_names()
	{
		var clock = new FakeClock();
		var observer = ObservabilityTestSupport.CreateObserver(ObservabilityMode.Detailed, out var queue, clock: clock);

		var cancelled = observer.BeginOperation(new OperationStart
		{
			Event = "operation.started",
			Kind = CompilerEventKind.Phase,
			Detail = DiagnosticDetail.L1,
		});
		cancelled.Complete("cancelled", reason: "client_cancelled");

		var faulted = observer.BeginOperation(new OperationStart
		{
			Event = "operation.started",
			Kind = CompilerEventKind.Phase,
			Detail = DiagnosticDetail.L1,
		});
		faulted.Complete("faulted", reason: "exception");

		var events = queue.DrainAll();
		var cancelledTerminal = events.Single(e => e.Event.Event == "operation.cancelled");
		var faultedTerminal = events.Single(e => e.Event.Event == "operation.faulted");
		Assert.Equal("cancelled", cancelledTerminal.Event.Outcome);
		Assert.Equal("faulted", faultedTerminal.Event.Outcome);
	}

	[Fact]
	public void Crash_left_behind_is_visible_as_unterminated_started_operations()
	{
		var clock = new FakeClock();
		var observer = ObservabilityTestSupport.CreateObserver(ObservabilityMode.Detailed, out var queue, clock: clock);

		var survivor = observer.BeginOperation(new OperationStart
		{
			Event = "operation.started",
			Kind = CompilerEventKind.Phase,
			Detail = DiagnosticDetail.L1,
			OperationId = "op-alive",
		});
		survivor.Complete("success");

		_ = observer.BeginOperation(new OperationStart
		{
			Event = "operation.started",
			Kind = CompilerEventKind.Phase,
			Detail = DiagnosticDetail.L1,
			OperationId = "op-crashed",
		}); // never completed: simulated crash

		var text = string.Join(
			"\n",
			queue.DrainAll().Select(e =>
				System.Text.Encoding.UTF8.GetString(EventJson.SerializeLine(e, observer.Identity, clock.ClockId))));
		var result = JsonlTraceReader.ReadText(text);

		var unterminated = Assert.Single(result.UnterminatedOperations());
		Assert.Equal("op-crashed", unterminated);
	}

	[Fact]
	public void Disabled_mode_returns_the_shared_noop_scope_without_recording()
	{
		var observer = ObservabilityTestSupport.CreateObserver(ObservabilityMode.Off, out var queue);

		var scope = observer.BeginOperation(new OperationStart
		{
			Event = "operation.started",
			Kind = CompilerEventKind.Phase,
			Detail = DiagnosticDetail.L0,
		});
		scope.Complete("success");

		Assert.Same(NoopOperationScope.Instance, scope);
		Assert.Empty(queue.DrainAll());
	}
}
