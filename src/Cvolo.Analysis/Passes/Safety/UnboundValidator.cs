using Cvolo.Analysis.Symbols;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Analysis.VisibilityChecks;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using Cvolo.Core.AST.Expressions;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Analysis.Passes.Safety;

/// <summary>
/// Owns safety rules specific to the <c>unbound</c> sandbox: structural reference-field
/// mutation, visibility preservation, and prevention of local-reference escapes to storage
/// that outlives the sandbox.
/// </summary>
internal sealed class UnboundValidator(
	BindingContext context,
	Func<ExpressionSyntax, string?> getBaseIdentifierName,
	Func<SafetyTier> getCurrentTier,
	Func<bool> isInsideUnbound)
{
	private readonly HashSet<string> _localRefs = [];
	private readonly Func<ExpressionSyntax, string?> _getBaseIdentifierName = getBaseIdentifierName;
	private readonly Func<SafetyTier> _getCurrentTier = getCurrentTier;
	private readonly Func<bool> _isInsideUnbound = isInsideUnbound;

	/// <summary>Clears per-function unbound-reference tracking.</summary>
	public void Reset() => _localRefs.Clear();

	/// <summary>
	/// Records a local <c>ref</c>/<c>refvar</c> declaration created while an unbound frame is
	/// active. The explicit unbound-stack predicate preserves tracking through nested unsafe blocks.
	/// </summary>
	public void TrackLocalReferenceDeclaration(VariableDeclarationSyntax declaration)
	{
		if (declaration.Type is not null
			&& declaration.Type.StartsWith("ref")
			&& _isInsideUnbound())
		{
			_localRefs.Add(declaration.Name);
		}
	}

	/// <summary>
	/// Enforces CVL1035 when a reference field is traversed from an unbound sandbox. Unbound
	/// suspends borrow exclusivity, but it does not suspend ordinary visibility restrictions.
	/// </summary>
	public void ValidateMemberAccess(MemberAccessExpressionSyntax member, SymbolTable scope)
	{
		ReportReferenceFieldVisibilityLeak(member.Expression, member.MemberName, member.Span, scope);
	}

	/// <summary>
	/// Enforces CVL1008 for assignments that would move a reference created inside an unbound
	/// sandbox into global storage.
	/// </summary>
	public void ValidateGlobalAssignmentEscape(BinaryExpressionSyntax assignment, VariableSymbol destination, SymbolTable scope)
	{
		if (!_isInsideUnbound() || !destination.IsGlobal || !IsLocalUnboundReference(assignment.Right, scope))
			return;

		context.Diagnostics.Report(context.CurrentUnit!.Context, assignment.Span,
			$"Reference cannot escape unbound scope: cannot assign local reference to global variable '{_getBaseIdentifierName(assignment.Left) ?? destination.Name}'");
	}

	/// <summary>
	/// Enforces structural reference-field mutation isolation, unbound visibility preservation,
	/// and CVL1008 escape prevention for stores through member access.
	/// </summary>
	public void ValidateReferenceFieldAssignment(BinaryExpressionSyntax assignment, SymbolTable scope)
	{
		if (assignment.Operator != "=" || assignment.Left is not MemberAccessExpressionSyntax member)
			return;

		// Structural Field-Mutation Isolation (§2 Rule 7): safe code must not directly write
		// to a struct's ref/refvar fields. Preserve the original CurrentTier != Unbound check,
		// including its behavior for an unsafe block nested inside an unbound frame.
		if (_getCurrentTier() != SafetyTier.Unbound
			&& GetReferenceFieldName(member.Expression, member.MemberName, scope) is { } mutableReferenceField)
		{
			var baseName = _getBaseIdentifierName(member.Expression) ?? "?";
			context.Diagnostics.Report(context.CurrentUnit!.Context, assignment.Span,
				$"Cannot assign to reference field '{mutableReferenceField}' of variable '{baseName}' in safe code. Use an 'unbound' block or function to modify structural reference fields.");
		}

		// CVL1035 is intentionally checked here as well as during member traversal, preserving
		// the previous validation sequence for assignment left-hand sides.
		if (_isInsideUnbound())
			ReportReferenceFieldVisibilityLeak(member.Expression, member.MemberName, assignment.Span, scope);

		// CVL1008: a local reference created inside unbound cannot escape into a reference field
		// of a parameter or global object that outlives the sandbox.
		if (_isInsideUnbound()
			&& IsLocalUnboundReference(assignment.Right, scope)
			&& IsExternalEscapeBase(member.Expression, scope)
			&& GetReferenceFieldName(member.Expression, member.MemberName, scope) is { } referenceField)
		{
			var baseName = _getBaseIdentifierName(member.Expression) ?? "?";
			context.Diagnostics.Report(context.CurrentUnit!.Context, assignment.Span,
				$"Reference cannot escape unbound scope: cannot assign local reference to reference field '{referenceField}' of non-local variable '{baseName}'");
		}
	}

	/// <summary>
	/// Returns whether an expression resolves to a local ref/refvar binding declared while the
	/// current function was inside an unbound scope.
	/// </summary>
	private bool IsLocalUnboundReference(ExpressionSyntax expression, SymbolTable scope)
	{
		var name = _getBaseIdentifierName(expression);
		if (name is null || !_localRefs.Contains(name))
			return false;

		return scope.Lookup(name) is VariableSymbol { Type: PointerTypeSymbol };
	}

	/// <summary>
	/// Returns whether a member-access base is a parameter or global whose storage may outlive the
	/// current unbound scope.
	/// </summary>
	private bool IsExternalEscapeBase(ExpressionSyntax baseExpression, SymbolTable scope)
	{
		var name = _getBaseIdentifierName(baseExpression);
		if (name is null)
			return false;

		return scope.Lookup(name) is VariableSymbol symbol
			&& (symbol.IsGlobal || symbol.Origin == OriginKind.Parameter);
	}

	/// <summary>Returns the field name only when the resolved struct field is a ref/refvar field.</summary>
	private string? GetReferenceFieldName(ExpressionSyntax baseExpression, string fieldName, SymbolTable scope)
	{
		var (_, field) = ResolveStructField(baseExpression, fieldName, scope);
		return field is { Type: PointerTypeSymbol } ? field.Name : null;
	}

	/// <summary>Resolves a member-access base to its containing struct and requested field.</summary>
	private (StructTypeSymbol? Container, StructFieldSymbol? Field) ResolveStructField(
		ExpressionSyntax baseExpression,
		string fieldName,
		SymbolTable scope)
	{
		var name = _getBaseIdentifierName(baseExpression);
		if (name is null || scope.Lookup(name) is not VariableSymbol symbol)
			return (null, null);

		var structType = symbol.Type switch
		{
			StructTypeSymbol valueStruct => valueStruct,
			PointerTypeSymbol pointer when pointer.ReferencedType is StructTypeSymbol referencedStruct => referencedStruct,
			_ => null
		};

		return (structType, structType?.FindField(fieldName));
	}

	/// <summary>
	/// Reports CVL1035 when unbound code traverses or mutates an inaccessible ref/refvar field.
	/// Internal fields remain reachable within the module; ordinary visibility logic decides the
	/// cross-file private case.
	/// </summary>
	private void ReportReferenceFieldVisibilityLeak(
		ExpressionSyntax baseExpression,
		string fieldName,
		TextSpan span,
		SymbolTable scope)
	{
		if (context.LegacyVisibility || !_isInsideUnbound())
			return;

		var (container, field) = ResolveStructField(baseExpression, fieldName, scope);
		if (container is null || field is null || field.Type is not PointerTypeSymbol)
			return;
		if (field.Visibility == Visibility.Public)
			return;

		CompilationUnitSyntax? declaringUnit = null;
		if (context.SymbolUnits.TryGetValue(container.Name, out var unit))
			declaringUnit = unit;
		if (declaringUnit is null || VisibilityChecker.IsAccessible(field.Visibility, context.CurrentUnit, declaringUnit))
			return;

		context.Diagnostics.Report(context.CurrentUnit!.Context, span,
			$"The 'unbound' sandbox cannot suspend access restrictions. Structural mutation of refvar field '{field.Name}' is blocked because it is not visible to this compilation scope.",
			DiagnosticIds.UnboundVisibilityLeak);
	}
}
