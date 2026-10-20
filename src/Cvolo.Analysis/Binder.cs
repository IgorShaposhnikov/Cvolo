using System.Collections.Generic;
using Cvolo.Analysis.Passes;
using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;
using Cvolo.Observability.Abstractions;

namespace Cvolo.Analysis;

public sealed class Binder
{
	private readonly BindingContext _context = new();

	public BindingContext Context => _context;

	public DiagnosticBag Diagnostics => _context.Diagnostics;

	/// <summary>
	/// Binds without observability (standalone compiler default; §30: disabled telemetry
	/// must not change compiler semantics).
	/// </summary>
	public void Bind(IReadOnlyList<CompilationUnitSyntax> units)
		=> Bind(units, NoopCompilerObserver.Instance, parentOperationId: null);

	/// <summary>
	/// Binds with observability: one <c>bind.pass</c> span per compiler pass, in pass
	/// order, parented to <paramref name="parentOperationId"/> when provided (§38).
	/// A throwing pass records its own faulted terminal and rethrows — semantics unchanged.
	/// </summary>
	public void Bind(IReadOnlyList<CompilationUnitSyntax> units, ICompilerObserver observer, string? parentOperationId = null)
	{
		ArgumentNullException.ThrowIfNull(observer);

		void RunPass(string passName, Action run)
		{
			using var scope = observer.BeginOperation(new OperationStart
			{
				Event = "bind.pass.started",
				Stage = $"bind.pass.{passName}",
				Kind = CompilerEventKind.Phase,
				Detail = DiagnosticDetail.L1,
				ParentOperationId = parentOperationId,
				Attributes = [new KeyValuePair<string, string>("pass", passName)],
			});

			try
			{
				run();
			}
			catch (OperationCanceledException)
			{
				scope.Complete("cancelled", reason: "client_cancelled");
				throw;
			}
			catch (Exception)
			{
				// Honest faulted terminal; exception facts are recorded by the adapter's
				// analysis.faulted event. Never swallow or mask (§12).
				scope.Complete("faulted", reason: "exception");
				throw;
			}

			scope.Complete("success");
		}

		// Pass 1: Gather all symbols (Cross-file visibility)
		RunPass("declaration", () => new DeclarationPass(_context).Process(units));

		// Pass 2: Check types, function bodies, and logic
		RunPass("validation", () => new ValidationPass(_context).Process(units));

		// Pass 3: Check logic flow before safety rules
		RunPass("flow", () => new FlowAnalysisPass(_context).Process(units));

		// Pass 4: Enforce Memory Safety (Borrow Checker, Moves, Lifetimes)
		RunPass("safety", () => new SafetyPass(_context).Process(units));

		// Pass 5: Bind flow-sensitive ownership decisions for replacement and partial-move cleanup
		RunPass("ownership", () => new OwnershipPass(_context).Process(units));
	}
}
