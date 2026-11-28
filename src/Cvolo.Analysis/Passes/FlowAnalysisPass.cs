using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;

namespace Cvolo.Analysis.Passes;

/// <summary>
/// Coordinates flow analysis across concrete top-level and exported function bodies.
/// </summary>
public sealed class FlowAnalysisPass(BindingContext context)
{
	private readonly DefiniteAssignmentAnalyzer _definiteAssignment = new(context);

	/// <summary>
	/// Runs flow analysis while preserving compilation-unit namespace context for diagnostics and type resolution.
	/// </summary>
	public void Process(IEnumerable<CompilationUnitSyntax> units)
	{
		foreach (var unit in units)
		{
			context.CurrentUnit = unit;
			context.CurrentNamespace = unit.NamespaceDeclaration?.Name;
			var members = context.CurrentNamespace != null ? unit.NamespaceDeclaration!.Members : unit.Members;

			foreach (var member in members)
			{
				// Generic bodies are analyzed once as templates. Their concrete specializations are
				// synthesized later from the same spans, so skipping templates would leave active-
				// variant diagnostics unemitted for generic functions.
				if (member is FunctionDeclarationSyntax func)
				{
					_definiteAssignment.Analyze(func);
				}
				else if (member is ExposeExternBlockSyntax exportBlock)
				{
					foreach (var exportFunc in exportBlock.Functions)
					{
						_definiteAssignment.Analyze(exportFunc);
					}
				}
			}
		}
	}
}
