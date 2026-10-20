using Cvolo.Observability;
using Cvolo.Observability.Abstractions;

namespace Cvolo.Tests.Observability;

/// <summary>Runtime mode switching without restart; recording transitions stay traceable.</summary>
public class ModeTransitionTests
{
	[Fact]
	public void Off_mode_gates_every_event_and_allocates_no_scope()
	{
		var observer = ObservabilityTestSupport.CreateObserver(ObservabilityMode.Off, out var queue);

		Assert.False(observer.IsEnabled(CompilerEventKind.Phase, DiagnosticDetail.L0));
		observer.Emit(ObservabilityTestSupport.Event("session.started"));
		Assert.Empty(queue.DrainAll());
	}

	[Fact]
	public void Basic_mode_records_L0_but_not_L1_or_L2()
	{
		var observer = ObservabilityTestSupport.CreateObserver(ObservabilityMode.Basic, out _);

		Assert.True(observer.IsEnabled(CompilerEventKind.Phase, DiagnosticDetail.L0));
		Assert.False(observer.IsEnabled(CompilerEventKind.Phase, DiagnosticDetail.L1));
		Assert.False(observer.IsEnabled(CompilerEventKind.Phase, DiagnosticDetail.L2));
	}

	[Fact]
	public void Detailed_and_repro_extend_depth_without_restart()
	{
		var observer = ObservabilityTestSupport.CreateObserver(ObservabilityMode.Basic, out _);

		observer.SetMode(ObservabilityMode.Detailed);
		Assert.True(observer.IsEnabled(CompilerEventKind.Phase, DiagnosticDetail.L1));
		Assert.False(observer.IsEnabled(CompilerEventKind.Phase, DiagnosticDetail.L2));

		observer.SetMode(ObservabilityMode.Repro);
		Assert.True(observer.IsEnabled(CompilerEventKind.Phase, DiagnosticDetail.L2));
	}

	[Fact]
	public void Mode_switch_takes_effect_for_subsequent_events_only()
	{
		var observer = ObservabilityTestSupport.CreateObserver(ObservabilityMode.Basic, out var queue);
		var l1Event = ObservabilityTestSupport.Event(
			"bind.pass.started", detail: DiagnosticDetail.L1);

		observer.Emit(l1Event); // gated: detailed-only
		observer.SetMode(ObservabilityMode.Detailed);
		observer.Emit(l1Event); // now recorded

		var events = queue.DrainAll();
		Assert.Equal(2, events.Count); // one mode_changed + one L1 event
		Assert.Equal("recording.mode_changed", events[0].Event.Event);
		Assert.Equal("bind.pass.started", events[1].Event.Event);
	}

	[Fact]
	public void Transition_into_off_is_still_recorded_so_a_recording_ends_traceably()
	{
		var observer = ObservabilityTestSupport.CreateObserver(ObservabilityMode.Detailed, out var queue);

		observer.SetMode(ObservabilityMode.Off);

		var events = queue.DrainAll();
		var modeChanged = events.FirstOrDefault(e => e.Event.Event == "recording.mode_changed");
		Assert.Equal("recording.mode_changed", modeChanged.Event.Event);
		Assert.Equal("off", modeChanged.Event.Outcome);
	}

	[Fact]
	public void Setting_the_same_mode_again_is_a_no_op()
	{
		var observer = ObservabilityTestSupport.CreateObserver(ObservabilityMode.Detailed, out var queue);

		observer.SetMode(ObservabilityMode.Detailed);

		Assert.Empty(queue.DrainAll());
	}

	[Fact]
	public void Each_mode_switch_advances_the_recording_epoch()
	{
		var observer = ObservabilityTestSupport.CreateObserver(ObservabilityMode.Basic, out var queue);
		var epoch0 = observer.Identity.RecordingEpoch;

		observer.SetMode(ObservabilityMode.Detailed);
		var epoch1 = observer.Identity.RecordingEpoch;
		observer.SetMode(ObservabilityMode.Off);
		var epoch2 = observer.Identity.RecordingEpoch;

		Assert.True(epoch1 > epoch0);
		Assert.True(epoch2 > epoch1);

		var transitions = queue.DrainAll()
			.Where(e => e.Event.Event == "recording.mode_changed")
			.ToList();
		Assert.Equal(2, transitions.Count);
		Assert.All(transitions, t => Assert.True(t.RecordingEpoch > 0));
		Assert.Contains(transitions, t => t.Event.Outcome == "detailed");
		Assert.Contains(transitions, t => t.Event.Outcome == "off");
	}
}
