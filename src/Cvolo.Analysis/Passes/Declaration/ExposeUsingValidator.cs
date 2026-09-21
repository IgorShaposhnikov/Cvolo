using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Directives;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Analysis.Passes.Declaration;

/// <summary>
/// Builds namespace re-export metadata and validates <c>expose using</c> directives.
/// </summary>
internal sealed class ExposeUsingValidator(BindingContext context)
{
	/// <summary>
	/// Registers all declared namespaces, records namespace-level re-exports, and validates
	/// placement and target existence for every <c>expose using</c> directive.
	/// </summary>
	public void Process(IEnumerable<CompilationUnitSyntax> units)
	{
		context.DeclaredNamespaces.Clear();
		context.NamespaceReExports.Clear();

		GatherDeclaredNamespaces(units);
		var exposeDirectives = RegisterReExports(units);
		ValidateTargets(exposeDirectives);
	}

	/// <summary>Collects every namespace declared by the current compilation.</summary>
	private void GatherDeclaredNamespaces(IEnumerable<CompilationUnitSyntax> units)
	{
		foreach (var unit in units)
		{
			if (unit.NamespaceDeclaration is not null)
				context.DeclaredNamespaces.Add(unit.NamespaceDeclaration.Name);
		}
	}

	/// <summary>
	/// Rejects file-level <c>expose using</c> directives and records valid namespace-level
	/// re-export edges for later target validation.
	/// </summary>
	private List<(CompilationContext FileContext, UsingDirectiveSyntax Directive)> RegisterReExports(IEnumerable<CompilationUnitSyntax> units)
	{
		var exposeDirectives = new List<(CompilationContext FileContext, UsingDirectiveSyntax Directive)>();

		foreach (var unit in units)
		{
			context.CurrentUnit = unit;
			var fileContext = context.FileContexts[unit];

			foreach (var directive in unit.Usings)
			{
				if (!directive.IsExposed)
					continue;

				context.Diagnostics.Report(
					fileContext,
					directive.Span,
					"'expose using' can only be used inside a namespace.",
					DiagnosticIds.ExposeUsingOutsideNamespace);
			}

			if (unit.NamespaceDeclaration is null)
				continue;

			var currentNamespace = unit.NamespaceDeclaration.Name;
			foreach (var directive in unit.NamespaceDeclaration.Usings)
			{
				if (!directive.IsExposed)
					continue;

				if (!context.NamespaceReExports.TryGetValue(currentNamespace, out var reExports))
				{
					reExports = new HashSet<string>(StringComparer.Ordinal);
					context.NamespaceReExports[currentNamespace] = reExports;
				}

				reExports.Add(directive.NamespaceName);
				exposeDirectives.Add((fileContext, directive));
			}
		}

		return exposeDirectives;
	}

	/// <summary>Reports re-export directives whose target namespace does not exist.</summary>
	private void ValidateTargets(IEnumerable<(CompilationContext FileContext, UsingDirectiveSyntax Directive)> exposeDirectives)
	{
		foreach (var (fileContext, directive) in exposeDirectives)
		{
			if (context.DeclaredNamespaces.Contains(directive.NamespaceName))
				continue;

			context.Diagnostics.Report(
				fileContext,
				directive.Span,
				$"Target namespace '{directive.NamespaceName}' of 'expose using' does not exist.",
				DiagnosticIds.ExposeUsingNamespaceNotFound);
		}
	}
}
