using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Analysis.Passes.Declaration;

/// <summary>
/// Validates declared default generic arguments after extension methods and contract
/// evidence have been registered.
/// </summary>
internal sealed class GenericDefaultConstraintValidator(BindingContext context)
{
	/// <summary>
	/// Re-checks every generic struct default against the declared constraints of the
	/// corresponding type parameter. This runs after extension registration because
	/// structural protocol conformance depends on the completed extension-method table.
	/// </summary>
	public void Validate(IEnumerable<CompilationUnitSyntax> units)
	{
		foreach (var unit in units)
		{
			context.CurrentUnit = unit;
			context.CurrentNamespace = unit.NamespaceDeclaration?.Name;

			var members = context.CurrentNamespace != null ? unit.NamespaceDeclaration!.Members : unit.Members;
			foreach (var member in members)
			{
				if (member is not StructDeclarationSyntax structDecl)
					continue;

				if (structDecl.GenericParameterDefaults.Count == 0)
					continue;

				foreach (var (paramName, defaultTypeName) in structDecl.GenericParameterDefaults)
				{
					if (!structDecl.GenericParameterConstraints.TryGetValue(paramName, out var constraints))
						continue;

					var defaultType = context.ResolveType(defaultTypeName);
					if (defaultType is null)
						continue;

					foreach (var constraintName in constraints)
					{
						var constraintType = context.ResolveType(constraintName);
						if (constraintType is not null && !context.TypeSatisfiesContract(defaultType, constraintType))
						{
							var currentFileContext = context.FileContexts[context.CurrentUnit!];
							context.Diagnostics.Report(
								currentFileContext,
								structDecl.Span,
								$"Default type '{defaultTypeName}' does not satisfy constraint '{constraintName}' of generic parameter '{paramName}'.",
								DiagnosticIds.DefaultTypeConstraintMismatch);
						}
					}
				}
			}
		}
	}
}
