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
	/// Re-checks every generic struct, union, and top-level function default against the
	/// declared constraints of the corresponding type parameter. This runs after extension
	/// registration because structural protocol conformance depends on the completed
	/// extension-method table.
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
				IReadOnlyDictionary<string, List<string>> constraints;
				IReadOnlyDictionary<string, string> defaults;
				TextSpan span;
				switch (member)
				{
					case StructDeclarationSyntax structDecl:
						constraints = structDecl.GenericParameterConstraints;
						defaults = structDecl.GenericParameterDefaults;
						span = structDecl.Span;
						break;
					case UnionDeclarationSyntax unionDecl:
						constraints = unionDecl.GenericParameterConstraints;
						defaults = unionDecl.GenericParameterDefaults;
						span = unionDecl.Span;
						break;
					case FunctionDeclarationSyntax functionDecl:
						constraints = functionDecl.GenericParameterConstraints;
						defaults = functionDecl.GenericParameterDefaults;
						span = functionDecl.Span;
						break;
					case ExtensionDeclarationSyntax extensionDecl:
						constraints = extensionDecl.GenericParameterConstraints;
						defaults = extensionDecl.GenericParameterDefaults;
						span = extensionDecl.Span;
						break;
					default:
						continue;
				}

				if (defaults.Count == 0 && constraints.Count == 0)
					continue;

				var currentFileContext = context.FileContexts[context.CurrentUnit!];

				// A constraint name is checked in its own right, independent of whether the
				// parameter also declares a default. Previously a constrained parameter with
				// no default was skipped entirely, so an unresolved contract vanished without
				// any diagnostic and left the parameter silently unbound.
				foreach (var (paramName, contractNames) in constraints)
				{
					foreach (var constraintName in contractNames)
					{
						var openBracket = constraintName.IndexOf('<');
						if (openBracket > 0)
						{
							// Constructed bound: only the contract head is resolvable before
							// instantiation — its arguments may name this declaration's own
							// generic parameters (e.g. `where T : Comparable<T>`), and resolving
							// the full text would emit a spurious "unknown type argument" finding.
							// Conformance of the substituted bound is checked at instantiation.
							if (context.ResolveType(constraintName[..openBracket]) is not null)
								continue;
						}
						else if (context.ResolveType(constraintName) is not null)
						{
							continue;
						}

						context.Diagnostics.Report(
							currentFileContext,
							span,
							$"Unknown contract '{constraintName}' in constraint for parameter '{paramName}'.",
							DiagnosticIds.UnknownContractInConstraintForParameter);
					}
				}

				foreach (var (paramName, defaultTypeName) in defaults)
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
							span,
							$"Unknown type '{defaultTypeName}' in default for generic parameter '{paramName}'.",
							DiagnosticIds.UnknownType);
						continue;
					}

					if (!constraints.TryGetValue(paramName, out var contractNames))
						continue;

					foreach (var constraintName in contractNames)
					{
						if (context.ResolveType(constraintName) is not { } constraintType)
							continue;

						if (!context.SatisfiesConstraintConformance(defaultType, constraintType))
						{
							context.Diagnostics.Report(
								currentFileContext,
								span,
								$"Default type '{defaultTypeName}' does not satisfy constraint '{constraintName}' of generic parameter '{paramName}'.",
								DiagnosticIds.DefaultTypeConstraintMismatch);
						}
					}
				}
			}
		}
	}
}
