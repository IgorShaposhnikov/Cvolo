using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Analysis.Passes.Validation;

/// <summary>
/// Validates source-level visibility relationships that are independent of statement or expression traversal.
/// </summary>
/// <remarks>
/// This validator currently owns the generic-instantiation exposure rule (CVL1038). Member-access
/// accessibility remains handled by the existing visibility checks at the point where members are resolved.
/// </remarks>
internal sealed class VisibilityValidator(BindingContext context)
{
	/// <summary>
	/// Reports CVL1038 when a public API exposes a generic type instantiation whose immediate type
	/// argument resolves to a non-public struct, union, or enum.
	/// </summary>
	/// <remarks>
	/// The parsing intentionally preserves the previous validation behavior: it inspects the
	/// comma-separated type argument tokens embedded in <see cref="TypeSymbol.Name"/> rather than
	/// introducing a new generic-type parser during this refactoring step.
	/// </remarks>
	/// <param name="span">Source location associated with the exposed declaration.</param>
	/// <param name="type">Resolved semantic type being exposed by the public API.</param>
	public void CheckGenericVisibilityLeak(TextSpan span, TypeSymbol? type)
	{
		if (type is null)
			return;

		if (type is PointerTypeSymbol pointer)
			type = pointer.ReferencedType;

		if (!type.Name.Contains('<'))
			return;

		var openBracket = type.Name.IndexOf('<');
		var closeBracket = type.Name.LastIndexOf('>');
		if (openBracket <= 0 || closeBracket <= openBracket)
			return;

		var arguments = type.Name.Substring(openBracket + 1, closeBracket - openBracket - 1);
		foreach (var rawArgument in arguments.Split(','))
		{
			var argumentType = context.ResolveType(rawArgument.Trim());
			if (argumentType is null)
				continue;

			if (argumentType is StructTypeSymbol structType && structType.Visibility < Visibility.Public)
			{
				ReportLeak(span, type, structType.Name);
			}
			else if (argumentType is UnionTypeSymbol unionType && unionType.Visibility < Visibility.Public)
			{
				ReportLeak(span, type, unionType.Name);
			}
			else if (argumentType is EnumTypeSymbol enumType && enumType.Visibility < Visibility.Public)
			{
				ReportLeak(span, type, enumType.Name);
			}
		}
	}

	/// <summary>
	/// Emits the existing generic visibility leak diagnostic for one inaccessible type argument.
	/// </summary>
	private void ReportLeak(TextSpan span, TypeSymbol exposedType, string argumentName)
	{
		var currentFileContext = context.FileContexts[context.CurrentUnit!];
		context.Diagnostics.Report(
			currentFileContext,
			span,
			$"The visibility of generic type instantiation '{exposedType.Name}' exceeds the visibility of its type argument '{argumentName}'. Upgrade the argument visibility or restrict the parent declaration.",
			DiagnosticIds.GenericVisibilityLeak);
	}
}
