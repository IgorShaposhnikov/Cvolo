using System.Threading;
using Cvolo.Observability.Abstractions;

namespace Cvolo.Observability;

/// <summary>
/// Operation scope with exactly-one-terminal semantics (§30):
/// the first <see cref="Complete"/> or <see cref="Dispose"/> wins, later terminals are
/// ignored. Elapsed time comes from the observer's monotonic clock — never UTC math (§4).
/// </summary>
internal sealed class OperationScope : IOperationScope
{
	private readonly CompilerObserver _observer;
	private readonly string _operationId;
	private readonly OperationStart _start;
	private readonly long _startTimestamp;

	private int _completed;

	public OperationScope(CompilerObserver observer, string operationId, in OperationStart start)
	{
		_observer = observer;
		_operationId = operationId;
		_start = start;
		_startTimestamp = observer.ClockTimestamp();
	}

	public string OperationId => _operationId;

	public bool IsCompleted => Volatile.Read(ref _completed) != 0;

	public void Complete(string outcome, string? reason = null, long? elapsedUs = null, IReadOnlyList<KeyValuePair<string, string>>? attributes = null)
	{
		if (!TryClaimTerminal())
		{
			return; // exactly one terminal per scope (§30)
		}

		EmitTerminal(outcome, reason, elapsedUs, attributes);
	}

	public void Dispose()
	{
		if (!TryClaimTerminal())
		{
			return;
		}

		// Implicit terminal: honest label, never claiming an observed success (§30).
		EmitTerminal("incomplete", "scope_disposed_without_completion", elapsedUs: null, attributes: null);
	}

	private bool TryClaimTerminal() => Interlocked.Exchange(ref _completed, 1) == 0;

	private void EmitTerminal(string outcome, string? reason, long? elapsedUs, IReadOnlyList<KeyValuePair<string, string>>? attributes)
	{
		var eventName = DeriveTerminalEvent(_start.Event, outcome);

		var terminal = new CompilerEvent
		{
			Event = eventName,
			Kind = _start.Kind,
			Detail = _start.Detail,
			Priority = _start.Priority,
			Stage = _start.Stage,
			Outcome = outcome,
			Reason = reason,
			ElapsedUs = elapsedUs ?? _observer.ElapsedUs(_startTimestamp),
			OperationId = _operationId,
			ParentOperationId = _start.ParentOperationId,
			RequestUid = _start.RequestUid,
			RpcRequestId = _start.RpcRequestId,
			AnalysisId = _start.AnalysisId,
			SnapshotId = _start.SnapshotId,
			DocumentSessionId = _start.DocumentSessionId,
			DocumentVersion = _start.DocumentVersion,
			Attributes = attributes,
		};
		_observer.EmitTerminal(eventName, terminal);
	}

	/// <summary>
	/// Stage-specific spans close with their own event family (§38): <c>bind.pass.started</c>
	/// closes as <c>bind.pass.completed/cancelled/faulted</c>; the generic
	/// <c>operation.started</c> closes as <c>operation.completed</c> etc.
	/// </summary>
	private static string DeriveTerminalEvent(string startEvent, string outcome)
	{
		var suffix = outcome switch
		{
			"cancelled" => "cancelled",
			"faulted" => "faulted",
			_ => "completed",
		};

		const string startedSuffix = ".started";
		if (startEvent.EndsWith(startedSuffix, StringComparison.Ordinal))
		{
			return startEvent[..^startedSuffix.Length] + "." + suffix;
		}

		return "operation." + suffix;
	}
}
