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

				if (structDecl.GenericParameterDefaults.Count == 0 &&
					structDecl.GenericParameterConstraints.Count == 0)
					continue;

				var currentFileContext = context.FileContexts[context.CurrentUnit!];

				// A constraint name is checked in its own right, independent of whether the
				// parameter also declares a default. Previously a constrained parameter with
				// no default was skipped entirely, so an unresolved contract vanished without
				// any diagnostic and left the parameter silently unbound.
				foreach (var (paramName, constraints) in structDecl.GenericParameterConstraints)
				{
					foreach (var constraintName in constraints)
					{
						if (context.ResolveType(constraintName) is not null)
							continue;

						context.Diagnostics.Report(
							currentFileContext,
							structDecl.Span,
							$"Unknown contract '{constraintName}' in constraint for parameter '{paramName}'.",
							DiagnosticIds.UnknownContractInConstraintForParameter);
					}
				}

				foreach (var (paramName, defaultTypeName) in structDecl.GenericParameterDefaults)
				{
					// An unresolved name must never be swallowed. Report a stable id so the
					// diagnostic survives from the binder to the editor instead of silently
					// leaving the generic parameter unbound (which is what hid the failure
					// behind an unrelated overload error, or no error at all).
					var defaultType = context.ResolveType(defaultTypeName);
					if (defaultType is null)
					{
						context.Diagnostics.Report(
							currentFileContext,
							structDecl.Span,
							$"Unknown type '{defaultTypeName}' in default for generic parameter '{paramName}'.",
							DiagnosticIds.UnknownType);
						continue;
					}

					if (!structDecl.GenericParameterConstraints.TryGetValue(paramName, out var constraints))
						continue;

					foreach (var constraintName in constraints)
					{
						if (context.ResolveType(constraintName) is not { } constraintType)
							continue;

						if (!context.TypeSatisfiesContract(defaultType, constraintType))
						{
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
