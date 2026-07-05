using Cvolo.Analysis.Passes;
using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Analysis;

public sealed class Binder
{
	private readonly BindingContext _context = new();
	public DiagnosticBag Diagnostics => _context.Diagnostics;

	public void Bind(IReadOnlyList<CompilationUnitSyntax> units)
	{
		// Pass 1: Gather all symbols (Cross-file visibility)
		new DeclarationPass(_context).Process(units);

		// Pass 2: Check types, function bodies, and logic
		new ValidationPass(_context).Process(units);

		// Pass 3: Check logic flow before safety rules
		new FlowAnalysisPass(_context).Process(units);

		// Pass 4: Enforce Memory Safety (Borrow Checker, Moves, Lifetimes)
		new SafetyPass(_context).Process(units);
	}
}
