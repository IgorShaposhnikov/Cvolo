using Cvolo.Analysis.Symbols;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Analysis.VisibilityChecks;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using Cvolo.Core.AST.Expressions;
using Cvolo.Core.AST.Statements;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Analysis.Passes.Validation;

/// <summary>
/// Validates enum and union switch semantics during the single validation traversal.
/// </summary>
/// <remarks>
/// This validator owns target classification, case promotion, exhaustiveness, non-exhaustive enum
/// consumer rules, switch-depth tracking, and payload visibility checks. Case bodies are delegated
/// back to the shared statement traversal, so extracting this service does not introduce another
/// AST pass.
/// </remarks>
/// <remarks>
/// Creates a switch validator over the active binding and traversal state.
/// </remarks>
/// <param name="context">Compilation-wide semantic state and diagnostics.</param>
/// <param name="validation">Mutable state shared by the single validation traversal.</param>
/// <param name="validateExpression">Validates the switch target through the shared expression validator.</param>
/// <param name="getExpressionType">Returns the semantic type of the switch target expression.</param>
/// <param name="validateBlock">Validates one case body through the shared statement traversal.</param>
internal sealed class SwitchValidator(
	BindingContext context,
	ValidationContext validation,
	Action<ExpressionSyntax, SymbolTable> validateExpression,
	Func<ExpressionSyntax, SymbolTable, TypeSymbol?> getExpressionType,
	Action<BlockStatementSyntax?, SymbolTable, FunctionDeclarationSyntax> validateBlock)
{

	/// <summary>
	/// Validates an enum or union switch, including promoted case bindings and exhaustiveness rules.
	/// </summary>
	/// <param name="statement">Switch statement being validated.</param>
	/// <param name="scope">Lexical scope visible at the switch site.</param>
	/// <param name="currentFunction">Function whose body owns the switch.</param>
	public void Validate(SwitchStatementSyntax statement, SymbolTable scope, FunctionDeclarationSyntax currentFunction)
	{
		validateExpression(statement.Expression, scope);
		var expressionType = getExpressionType(statement.Expression, scope);
		if (expressionType is null)
			return;

		if (expressionType is PointerTypeSymbol pointer)
			expressionType = pointer.ReferencedType;

		if (expressionType is EnumTypeSymbol enumType)
		{
			ValidateEnumSwitch(statement, enumType, scope, currentFunction);
			return;
		}

		if (expressionType is not UnionTypeSymbol unionType)
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, statement.Expression.Span, "Switch statement target must be a union type.");
			return;
		}

		ValidateUnionSwitch(statement, unionType, scope, currentFunction);
	}

	/// <summary>
	/// Validates union cases, promoted payload bindings, payload visibility, and exhaustive coverage.
	/// </summary>
	private void ValidateUnionSwitch(
		SwitchStatementSyntax statement,
		UnionTypeSymbol unionType,
		SymbolTable scope,
		FunctionDeclarationSyntax currentFunction)
	{
		var matchedVariants = new HashSet<string>();
		var hasDefault = false;

		validation.SwitchDepth++;
		try
		{
			foreach (var @case in statement.Cases)
			{
				if (@case.IsDefault || @case.VariantName == "_")
				{
					hasDefault = true;
					validateBlock(new BlockStatementSyntax(@case.Span, @case.Body), new SymbolTable(scope), currentFunction);
					continue;
				}

				matchedVariants.Add(@case.VariantName);
				var variant = unionType.FindField(@case.VariantName);
				if (variant is null)
				{
					var currentFileContext = context.FileContexts[context.CurrentUnit!];
					context.Diagnostics.Report(currentFileContext, @case.Span, $"Union '{unionType.Name}' does not contain variant '{@case.VariantName}'");
					continue;
				}

				if (!context.LegacyVisibility
					&& @case.VariableName is not null
					&& !VisibilityChecker.IsAccessible(variant.Visibility, context.CurrentUnit, GetDeclaringUnit(unionType)))
				{
					var currentFileContext = context.FileContexts[context.CurrentUnit!];
					context.Diagnostics.Report(
						currentFileContext,
						@case.Span,
						$"Pattern matching binding failed for type '{unionType.Name}'. Payload variant field '{variant.Name}' is obscured by visibility constraints.",
						DiagnosticIds.HiddenPayloadMatch);
				}

				var caseScope = new SymbolTable(scope);
				if (@case.VariableName is not null)
				{
					if (variant.IsVoidVariant)
					{
						var currentFileContext = context.FileContexts[context.CurrentUnit!];
						context.Diagnostics.Report(currentFileContext, @case.Span, $"Void variant '{@case.VariantName}' cannot carry a promoted variable.");
						continue;
					}

					TypeSymbol promotedType;
					if (unionType.IsNpoEligible)
					{
						if (variant.Type is not PointerTypeSymbol)
						{
							promotedType = variant.Type;
						}
						else if (getExpressionType(statement.Expression, scope) is not PointerTypeSymbol)
						{
							var currentFileContext = context.FileContexts[context.CurrentUnit!];
							context.Diagnostics.Report(
								currentFileContext,
								@case.Span,
								$"Cannot pattern-match '{@case.VariantName} {@case.VariableName}' by value on a nullable reference option; switch on 'ref'/'refvar' to extract the reference safely.");
							continue;
						}
						else
						{
							promotedType = variant.Type;
						}
					}
					else if (getExpressionType(statement.Expression, scope) is PointerTypeSymbol targetPointer)
					{
						promotedType = new PointerTypeSymbol(variant.Type, isMutable: targetPointer.IsMutable);
					}
					else
					{
						promotedType = variant.Type;
					}

					caseScope.Declare(new VariableSymbol(@case.VariableName, promotedType, isMutable: false) { IsInitialized = true });
				}

				validateBlock(new BlockStatementSyntax(@case.Span, @case.Body), caseScope, currentFunction);
			}
		}
		finally
		{
			validation.SwitchDepth--;
		}

		if (!hasDefault)
		{
			foreach (var variant in unionType.Fields)
			{
				if (!matchedVariants.Contains(variant.Name))
				{
					var currentFileContext = context.FileContexts[context.CurrentUnit!];
					context.Diagnostics.Report(currentFileContext, statement.Span, $"Switch statement is not exhaustive. Missing case for variant '{variant.Name}'.");
				}
			}
		}
	}

	/// <summary>
	/// Validates enum cases, flags/non-exhaustive coverage rules, and terminal default behavior.
	/// </summary>
	private void ValidateEnumSwitch(
		SwitchStatementSyntax statement,
		EnumTypeSymbol enumType,
		SymbolTable scope,
		FunctionDeclarationSyntax currentFunction)
	{
		var matchedVariants = new HashSet<string>();
		var hasDefault = false;

		validation.SwitchDepth++;
		try
		{
			foreach (var @case in statement.Cases)
			{
				if (@case.IsDefault || @case.VariantName == "_")
				{
					hasDefault = true;
					validateBlock(new BlockStatementSyntax(@case.Span, @case.Body), new SymbolTable(scope), currentFunction);
					continue;
				}

				if (@case.VariableName is not null)
				{
					var currentFileContext = context.FileContexts[context.CurrentUnit!];
					context.Diagnostics.Report(currentFileContext, @case.Span, "Enum variants cannot carry a promoted variable.");
					continue;
				}

				matchedVariants.Add(@case.VariantName);
				var variant = enumType.FindVariant(@case.VariantName);
				if (variant is null)
				{
					var currentFileContext = context.FileContexts[context.CurrentUnit!];
					context.Diagnostics.Report(currentFileContext, @case.Span, $"Enum '{enumType.Name}' does not contain variant '{@case.VariantName}'");
					continue;
				}

				validateBlock(new BlockStatementSyntax(@case.Span, @case.Body), new SymbolTable(scope), currentFunction);
			}
		}
		finally
		{
			validation.SwitchDepth--;
		}

		var isExternalConsumer = enumType.IsNonExhaustive
			&& context.SymbolUnits.TryGetValue(enumType.Name, out var declaringUnit)
			&& !ReferenceEquals(declaringUnit, context.CurrentUnit);

		if (!hasDefault && !enumType.IsFlags && !isExternalConsumer)
		{
			foreach (var variant in enumType.Variants)
			{
				if (!matchedVariants.Contains(variant.Name))
				{
					var currentFileContext = context.FileContexts[context.CurrentUnit!];
					context.Diagnostics.Report(currentFileContext, statement.Span, $"Switch statement is not exhaustive. Missing case for variant '{variant.Name}'.");
				}
			}
		}

		if (isExternalConsumer && !hasDefault)
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(
				currentFileContext,
				statement.Span,
				$"[NonExhaustive] enum '{enumType.Name}' is consumed from another unit and requires an explicit 'default' or 'case _' branch.");
		}

		if (enumType.IsNonExhaustive
			&& currentFunction.ReturnType != "void"
			&& hasDefault
			&& currentFunction.Body is BlockStatementSyntax functionBody
			&& functionBody.Statements.Count > 0
			&& ReferenceEquals(functionBody.Statements[^1], statement))
		{
			var defaultCase = statement.Cases.First(@case => @case.IsDefault || @case.VariantName == "_");
			if (!EndsWithDivergingStatement(defaultCase.Body))
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(
					currentFileContext,
					defaultCase.Span,
					$"The 'default' branch of a switch over [NonExhaustive] enum '{enumType.Name}' must terminate with a 'return' but ends in non-terminating statement(s).");
			}
		}
	}

	/// <summary>
	/// Returns whether a case body ends in a return or one of the existing diverging calls accepted
	/// by non-void non-exhaustive enum validation.
	/// </summary>
	private static bool EndsWithDivergingStatement(IReadOnlyList<SyntaxNode> body) => body.Count > 0 && body[^1] switch
	{
		ReturnStatementSyntax => true,
		ExpressionStatementSyntax { Expression: CallExpressionSyntax call } when call.FunctionName == "exit" || call.FunctionName == "panic" => true,
		_ => false,
	};

	/// <summary>
	/// Resolves the compilation unit that declared a semantic type, falling back from a concrete
	/// generic instantiation name to its template name when necessary.
	/// </summary>
	private CompilationUnitSyntax? GetDeclaringUnit(TypeSymbol type)
	{
		foreach (var name in ExpandTemplateNames(type))
		{
			if (context.SymbolUnits.TryGetValue(name, out var unit))
				return unit;
		}

		return null;
	}

	/// <summary>
	/// Enumerates the concrete semantic type name and, for generic instances, the unspecialized
	/// template name used by declaration-unit lookup.
	/// </summary>
	private static IEnumerable<string> ExpandTemplateNames(TypeSymbol type)
	{
		yield return type.Name;
		var name = type.Name;
		var openBracket = name.IndexOf('<');
		if (openBracket > 0)
			yield return name[..openBracket];
	}
}
