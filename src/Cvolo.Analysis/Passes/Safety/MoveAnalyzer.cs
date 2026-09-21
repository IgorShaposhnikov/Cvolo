using Cvolo.Analysis.Symbols;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Collections;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Expressions;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Analysis.Passes.Safety;

/// <summary>
/// Owns ordinary moved-value state transitions, by-value ownership transfer, and large-copy
/// diagnostics for one safety traversal. Safe-delegate capture and escape policy remain owned by
/// <see cref="Cvolo.Analysis.Passes.SafetyPass"/>.
/// </summary>
internal sealed class MoveAnalyzer(
	BindingContext context,
	BorrowTracker borrows,
	Func<ExpressionSyntax, SymbolTable, TypeSymbol?> resolveExpressionType)
{
	private ClassificationAnalyzer? _classification;
	private readonly Func<ExpressionSyntax, SymbolTable, TypeSymbol?> _resolveExpressionType = resolveExpressionType;
	private ClassificationAnalyzer Classification => _classification ??= new ClassificationAnalyzer(context);

	/// <summary>Reports a read of a local or global value that has already been moved.</summary>
	public void VerifyReadable(IdentifierExpressionSyntax identifier, SymbolTable scope)
	{
		if ((scope.Lookup(identifier.Name) as VariableSymbol ?? context.ResolveGlobalReference(identifier.Name, out _)) is { IsMoved: true })
			context.Diagnostics.Report(context.CurrentUnit!.Context, identifier.Span, $"Use of moved variable '{identifier.Name}'");
	}

	/// <summary>Returns whether the type uses move-only resource semantics.</summary>
	public bool IsMoveOnly(TypeSymbol type) => Classification.Classify(type) == CopyKind.ResourceMove;

	/// <summary>Marks a value as moved without adding any additional policy checks.</summary>
	public void MarkMoved(VariableSymbol symbol) => symbol.IsMoved = true;

	/// <summary>Marks a value initialized again after direct assignment.</summary>
	public void ResetMoved(VariableSymbol symbol) => symbol.IsMoved = false;

	/// <summary>
	/// Applies by-value argument transfer semantics. Move-only aggregates and slices consume an
	/// identifier argument after verifying that no field borrow keeps the source locked; large
	/// copyable aggregates keep their value and receive the existing performance warning.
	/// </summary>
	public void HandleByValueArgument(ExpressionSyntax argument, SymbolTable scope)
	{
		if (argument is StructInitializationExpressionSyntax or BorrowExpressionSyntax)
			return;

		var type = _resolveExpressionType(argument, scope);

		if (type is StructTypeSymbol or UnionTypeSymbol)
		{
			var kind = Classification.Classify(type);
			switch (kind)
			{
				case CopyKind.ResourceMove:
					if (argument is IdentifierExpressionSyntax identifier && scope.Lookup(identifier.Name) is VariableSymbol variable)
					{
						borrows.VerifyUnlocked(argument, "move");
						variable.IsMoved = true;
					}
					break;
				case CopyKind.LargeCopy:
					var size = Classification.CalculateByteSize(type);
					context.Diagnostics.ReportWarning(
						context.CurrentUnit!.Context, argument.Span,
						$"'{type.Name}' is {size} bytes. Copying by value duplicates the payload. Consider passing by 'ref'.",
						DiagnosticIds.LargeCopyWarning);
					break;
			}
		}
		else if (type is SliceTypeSymbol)
		{
			if (argument is IdentifierExpressionSyntax identifier && scope.Lookup(identifier.Name) is VariableSymbol variable)
			{
				borrows.VerifyUnlocked(argument, "move");
				variable.IsMoved = true;
			}
		}
	}

	/// <summary>
	/// Emits the existing large-copy warning for a struct initializer source while preserving the
	/// special case that constructing a fresh struct literal is not treated as a duplicated value.
	/// </summary>
	public void EmitLargeCopyWarningIfNeeded(ExpressionSyntax expression, SymbolTable scope)
	{
		if (expression is StructInitializationExpressionSyntax)
			return;

		var type = _resolveExpressionType(expression, scope);
		if (type is StructTypeSymbol structType)
		{
			var kind = Classification.Classify(structType);
			if (kind == CopyKind.LargeCopy)
			{
				var size = Classification.CalculateByteSize(structType);
				context.Diagnostics.ReportWarning(
					context.CurrentUnit!.Context, expression.Span,
					$"'{structType.Name}' is {size} bytes. Copying by value duplicates the payload. Consider passing by 'ref'.",
					DiagnosticIds.LargeCopyWarning);
			}
		}
	}

	/// <summary>Emits the existing large-copy warning for an identifier used on the right of assignment.</summary>
	public void HandleCopyAssignment(ExpressionSyntax rightExpression, SymbolTable scope)
	{
		if (rightExpression is not IdentifierExpressionSyntax identifier)
			return;

		var rightSymbol = scope.Lookup(identifier.Name) as VariableSymbol;
		if (rightSymbol == null || rightSymbol.Type is not StructTypeSymbol rightStruct)
			return;

		var kind = Classification.Classify(rightStruct);
		if (kind == CopyKind.LargeCopy)
		{
			var size = Classification.CalculateByteSize(rightStruct);
			context.Diagnostics.ReportWarning(
				context.CurrentUnit!.Context, identifier.Span,
				$"'{identifier.Name}' is {size} bytes. Copying by value duplicates the payload. Consider passing by 'ref'.",
				DiagnosticIds.LargeCopyWarning);
		}
	}
}
