using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Analysis.Passes.Declaration;

/// <summary>
/// Enforces the declaration-time trivial-copy rule for default generic type arguments.
/// </summary>
internal sealed class GenericDefaultCopyValidator(BindingContext context)
{
	private readonly ClassificationAnalyzer _classification = new(context);

	/// <summary>
	/// Validates default generic arguments declared by a generic struct template.
	/// </summary>
	public void Validate(StructDeclarationSyntax declaration)
		=> ValidateDefaults(declaration.GenericParameterDefaults, declaration);

	/// <summary>
	/// Validates default generic arguments declared by a generic union template.
	/// </summary>
	public void Validate(UnionDeclarationSyntax declaration)
		=> ValidateDefaults(declaration.GenericParameterDefaults, declaration);

	/// <summary>
	/// Validates default generic arguments declared by a generic extension template.
	/// </summary>
	public void Validate(ExtensionDeclarationSyntax declaration)
		=> ValidateDefaults(declaration.GenericParameterDefaults, declaration);

	/// <summary>
	/// Reports CVL1040 for each known default type whose copy classification is not
	/// <see cref="CopyKind.TrivialCopy"/>. Unknown types are left to the existing
	/// type-resolution diagnostics, preserving the previous declaration behavior.
	/// </summary>
	private void ValidateDefaults(IReadOnlyDictionary<string, string> defaults, SyntaxNode declaration)
	{
		foreach (var (parameterName, defaultTypeName) in defaults)
		{
			var defaultType = context.ResolveType(defaultTypeName);
			if (defaultType is null || _classification.Classify(defaultType) == CopyKind.TrivialCopy)
				continue;

			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(
				currentFileContext,
				declaration.Span,
				$"Default value for generic parameter '{parameterName}' must be a Trivial Copy Type",
				DiagnosticIds.DefaultMustBeTrivialCopy);
		}
	}
}
