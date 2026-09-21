using Cvolo.Analysis.Symbols;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using Cvolo.Core.AST.Expressions;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Analysis.Passes.Validation;

/// <summary>
/// Validates compiler-recognized intrinsic declarations and intrinsic expression forms.
/// </summary>
/// <remarks>
/// This service centralizes the existing <c>[Intrinsic]</c> declaration marker together with
/// <c>nameof</c> and <c>typeof</c> semantic checks. It reuses the active expression validator through
/// callbacks and does not introduce a separate AST traversal.
/// </remarks>
internal sealed class IntrinsicValidator(
	BindingContext context,
	Action<ExpressionSyntax, SymbolTable> validateExpression,
	Func<ExpressionSyntax, SymbolTable, TypeSymbol?> getExpressionType,
	Func<ExpressionSyntax, string?> getBaseIdentifierName)
{
	/// <summary>
	/// Returns whether a bodyless function is decorated with one of the accepted intrinsic markers.
	/// </summary>
	public bool IsIntrinsicDeclaration(FunctionDeclarationSyntax function)
	{
		return function.Attributes.Any(attribute =>
			attribute.Name is "Intrinsic" or "System.Intrinsic" or "IntrinsicAttribute");
	}

	/// <summary>
	/// Validates a <c>nameof</c> expression and its static or runtime member target.
	/// </summary>
	public void ValidateNameof(NameofExpressionSyntax nameofExpression, SymbolTable scope)
	{
		var currentFileContext = context.FileContexts[context.CurrentUnit!];
		var argument = nameofExpression.Argument;

		if (GetNameofFoldedName(argument) is null)
		{
			context.Diagnostics.Report(currentFileContext, nameofExpression.Span,
				"Operator `nameof` cannot be applied to an expression with an empty identifier node.",
				DiagnosticIds.NameofExpressionInvalid);
			return;
		}

		// Static/type receiver: `nameof(StructName.Field)` must not bind the base as a variable.
		if (argument is MemberAccessExpressionSyntax member
			&& getBaseIdentifierName(member.Expression) is { } baseName
			&& scope.Lookup(baseName) is not VariableSymbol
			&& context.ResolveType(baseName) is { } staticType)
		{
			if (!TryValidateStaticNameof(staticType, member))
			{
				context.Diagnostics.Report(currentFileContext, member.Span,
					$"The name {member.MemberName} does not exist in the current context. Cannot evaluate `nameof`.",
					DiagnosticIds.NameofInvalidSymbolError);
			}

			return;
		}

		// Bare type name (e.g. `nameof(Point)`): nothing needs instance binding.
		var isBareTypeName = argument is IdentifierExpressionSyntax bareId
			&& scope.Lookup(bareId.Name) is not VariableSymbol
			&& context.ResolveType(bareId.Name) is not null;
		if (!isBareTypeName)
			validateExpression(argument, scope);

		if (argument is MemberAccessExpressionSyntax memberAccess
			&& getExpressionType(memberAccess, scope) is null)
		{
			context.Diagnostics.Report(currentFileContext, memberAccess.Span,
				$"The name {memberAccess.MemberName} does not exist in the current context. Cannot evaluate `nameof`.",
				DiagnosticIds.NameofInvalidSymbolError);
		}
		else if (argument is IdentifierExpressionSyntax id
			&& scope.Lookup(id.Name) is not VariableSymbol
			&& context.ResolveType(id.Name) is null)
		{
			context.Diagnostics.Report(currentFileContext, id.Span,
				$"The name {id.Name} does not exist in the current context. Cannot evaluate `nameof`.",
				DiagnosticIds.NameofInvalidSymbolError);
		}
	}

	/// <summary>
	/// Validates that a <c>typeof</c> expression names a known semantic type.
	/// </summary>
	public void ValidateTypeof(TypeofExpressionSyntax typeofExpression)
	{
		if (context.ResolveType(typeofExpression.TypeName) is null)
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, typeofExpression.Span,
				$"Type {typeofExpression.TypeName} could not be found. Cannot evaluate `typeof`.",
				DiagnosticIds.TypeofInvalidTypeError);
		}
	}

	/// <summary>
	/// Returns the statically foldable name represented by a <c>nameof</c> argument.
	/// </summary>
	private static string? GetNameofFoldedName(ExpressionSyntax expression) => expression switch
	{
		IdentifierExpressionSyntax id => id.Name,
		MemberAccessExpressionSyntax member => member.MemberName,
		_ => null,
	};

	/// <summary>
	/// Validates a static <c>nameof</c> member path against a semantic aggregate or enum type.
	/// </summary>
	private static bool TryValidateStaticNameof(TypeSymbol type, MemberAccessExpressionSyntax member)
	{
		var segments = new List<string>();
		ExpressionSyntax current = member;
		while (current is MemberAccessExpressionSyntax nestedMember)
		{
			segments.Insert(0, nestedMember.MemberName);
			current = nestedMember.Expression;
		}

		if (current is not IdentifierExpressionSyntax)
			return false;

		var currentType = type;
		foreach (var segment in segments)
		{
			currentType = currentType switch
			{
				StructTypeSymbol structType => structType.FindField(segment)?.Type,
				UnionTypeSymbol unionType => unionType.FindField(segment)?.Type,
				EnumTypeSymbol enumType => enumType.FindVariant(segment) is null ? null : TypeSymbol.Int,
				_ => null,
			};
			if (currentType is null)
				return false;
		}

		return true;
	}
}
