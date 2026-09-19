using System;
using System.Collections.Generic;
using Cvolo.Analysis.Symbols;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Collections;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Expressions;

namespace Cvolo.Analysis.Completion;

/// <summary>
/// Resolves the receiver type of an expression at a completion cursor.
/// Every lookup delegates to compiler-owned authorities (BindingContext.VariableSymbols,
/// BindingContext.ResolvedCalls, BindingContext.ResolveType, ResolveGlobalReference,
/// ResolveQualifiedGlobal, StructTypeSymbol.FindField, UnionTypeSymbol.FindField,
/// EnumTypeSymbol.FindVariant). The handful of rules that remain here (enum metadata members,
/// the slice/array 'Length' pseudo-member, pointer unwrapping, named-argument receiver typing)
/// duplicate no compiler state: they are the pure, side-effect-free subset of the compiler's own
/// expression typing, cited below. The compiler has NO standalone typing primitive to call
/// because its type inference is validation-interleaved — it reports diagnostics into the shared
/// DiagnosticBag while typing (e.g. ValidationPass.CheckMemberAccessExpression fully re-validates
/// the receiver subexpression, which cannot see position-scoped locals) and its scope tables are
/// transient pass-traversal state. So completion orchestrates the existing symbol lookups rather
/// than re-running validation.
/// Citations: ValidationPass.cs:1914-1954 GetExpressionType, :1419-1521 CheckMemberAccessExpression,
/// :1438-1462 enum metadata + 'Length' rules, :1751-1768 CheckArrayInitialization,
/// :3317-3326 CheckArrayReplication, :1839-1862 CheckTernaryExpression, :1956-1964
/// GetFlagsBinaryType, :1967-1973 GetAsmExpressionType, :4149-4191 GetUnaryExpressionType;
/// BindingContext.cs:615-619 ResolveEnumSizeTerm (Min/Max/Count), :315-316 dynamic slice resolution.
///
/// Fallback audit: TypeSymbol.Int coercions are kept ONLY where the compiler itself coerces an
/// unresolved operand the same way: borrow :1934, array-literal first element :1756,
/// array-replication value :3319, sizeof :1931. Fabricated coercions were REMOVED: heap-array
/// allocation no longer falls back to Int (the compiler asserts its element type, :1937;
/// unresolved => null), unary '&' now yields null when its operand is unresolved (:4154), and
/// ternary typing returns the THEN-branch type only (:1861), dropping the else-branch fallback.
/// </summary>
internal static class ExpressionTypeResolver
{
	/// <summary>Resolves the type of an expression using the completion-visible symbol map.</summary>
	internal static TypeSymbol? Resolve(BindingContext context, Dictionary<string, ScopedVariable> visible, ExpressionSyntax? expression)
	{
		return expression switch
		{
			IdentifierExpressionSyntax identifier =>
				(visible.TryGetValue(identifier.Name, out var local) ? local.Type : null)
				?? context.ResolveGlobalReference(identifier.Name, out _)?.Type,
			IntegerLiteralExpressionSyntax integer => integer.LiteralType switch
			{
				"uint" => TypeSymbol.UInt,
				"long" => TypeSymbol.Long,
				"ulong" => TypeSymbol.ULong,
				_ => integer.Value <= int.MaxValue ? TypeSymbol.Int : TypeSymbol.Long,
			},
			DoubleLiteralExpressionSyntax doubleLiteral => doubleLiteral.IsFloat ? TypeSymbol.Float : TypeSymbol.Double,
			BooleanLiteralExpressionSyntax => TypeSymbol.Bool,
			NullLiteralExpressionSyntax => TypeSymbol.Null,
			StringLiteralExpressionSyntax => TypeSymbol.String,
			CharacterLiteralExpressionSyntax => TypeSymbol.Char,
			CallExpressionSyntax call => call.FunctionName == "sizeof"
				? TypeSymbol.Int
				: context.ResolvedCalls.TryGetValue(call, out var resolved) ? resolved.ReturnType : null,
			MemberAccessExpressionSyntax memberAccess => ResolveMember(context, visible, memberAccess),
			BorrowExpressionSyntax borrow => new PointerTypeSymbol(Resolve(context, visible, borrow.Expression) ?? TypeSymbol.Int, borrow.IsMutable),
			StructInitializationExpressionSyntax structInit => ResolveTypeOrNull(context, structInit.StructTypeName),
			HeapAllocationExpressionSyntax heapAllocation => Resolve(context, visible, heapAllocation.Expression),
			HeapArrayAllocationExpressionSyntax arrayAllocation => ResolveTypeOrNull(context, arrayAllocation.ElementTypeName) is { } element
				? new SliceTypeSymbol(element)
				: null,
			IndexExpressionSyntax index => (Resolve(context, visible, index.Left) as ArrayTypeSymbol)?.ElementType,
			ArrayInitializationExpressionSyntax arrayInit => arrayInit.Elements.Count == 0
				? null
				: new ArrayTypeSymbol(Resolve(context, visible, arrayInit.Elements[0]) ?? TypeSymbol.Int, arrayInit.Elements.Count),
			ArrayReplicationExpressionSyntax arrayReplication => ResolveArrayReplication(context, visible, arrayReplication),
			ParenthesizedStructInitializerExpressionSyntax parenthesized => parenthesized.ResolvedStructTypeName is not null
				? ResolveTypeOrNull(context, parenthesized.ResolvedStructTypeName)
				: null,
			TernaryExpressionSyntax ternary => Resolve(context, visible, ternary.ThenExpression),
			VoidLiteralExpressionSyntax => TypeSymbol.Void,
			DefaultExpressionSyntax defaultExpression => defaultExpression.TypeName is not null ? ResolveTypeOrNull(context, defaultExpression.TypeName) : null,
			UnaryExpressionSyntax unary => ResolveUnary(context, visible, unary),
			AsmExpressionSyntax asm => asm.ResultType is not null ? ResolveTypeOrNull(context, asm.ResultType) : TypeSymbol.Void,
			NameofExpressionSyntax => TypeSymbol.String,
			TypeofExpressionSyntax => ResolveTypeOrNull(context, "System.Type"),
			IsPatternExpressionSyntax => TypeSymbol.Bool,
			BinaryExpressionSyntax binary => ResolveBinary(context, visible, binary),
			_ => null,
		};
	}

	/// <summary>
	/// Returns the dotted identifier text of an expression, or null when it is not a (qualified) name.
	/// </summary>
	internal static string? GetDottedName(ExpressionSyntax? expression)
	{
		return expression switch
		{
			IdentifierExpressionSyntax identifier => identifier.Name,
			MemberAccessExpressionSyntax memberAccess => GetDottedName(memberAccess.Expression) is { } receiver ? receiver + "." + memberAccess.MemberName : null,
			_ => null,
		};
	}

	private static TypeSymbol? ResolveUnary(BindingContext context, Dictionary<string, ScopedVariable> visible, UnaryExpressionSyntax unary)
	{
		var @operator = unary.Operator;
		if (@operator == "&")
		{
			var operandType = Resolve(context, visible, unary.Operand);
			return operandType is not null ? new RawPointerTypeSymbol(operandType) : null;
		}

		if (@operator == "*")
			return Resolve(context, visible, unary.Operand) switch
			{
				RawPointerTypeSymbol raw => raw.ElementType,
				PointerTypeSymbol pointer => pointer.ReferencedType,
				_ => null,
			};
		if (@operator.Length >= 3 && @operator.StartsWith("(", StringComparison.Ordinal) && @operator.EndsWith(")", StringComparison.Ordinal))
			return ResolveTypeOrNull(context, @operator[1..^1]);
		return Resolve(context, visible, unary.Operand);
	}

	private static TypeSymbol? ResolveArrayReplication(BindingContext context, Dictionary<string, ScopedVariable> visible, ArrayReplicationExpressionSyntax arrayReplication)
	{
		var valueType = Resolve(context, visible, arrayReplication.Value) ?? TypeSymbol.Int;
		return arrayReplication.Count is IntegerLiteralExpressionSyntax countLiteral
			? new ArrayTypeSymbol(valueType, unchecked((int)countLiteral.Value))
			: new ArrayTypeSymbol(valueType, 0);
	}

	private static TypeSymbol? ResolveBinary(BindingContext context, Dictionary<string, ScopedVariable> visible, BinaryExpressionSyntax binary)
	{
		if (binary.Operator is "|" or "&" or "^")
		{
			var left = Resolve(context, visible, binary.Left);
			return left is EnumTypeSymbol
				? left
				: Resolve(context, visible, binary.Right) is EnumTypeSymbol right ? right : null;
		}

		if (binary.Operator == "+" && IsConstantStringExpression(binary.Left) && IsConstantStringExpression(binary.Right))
			return TypeSymbol.String;
		return null;
	}

	private static bool IsConstantStringExpression(ExpressionSyntax? expression)
	{
		return expression switch
		{
			StringLiteralExpressionSyntax => true,
			BinaryExpressionSyntax binary => binary.Operator == "+" && IsConstantStringExpression(binary.Left) && IsConstantStringExpression(binary.Right),
			_ => false,
		};
	}

	private static TypeSymbol? ResolveMember(BindingContext context, Dictionary<string, ScopedVariable> visible, MemberAccessExpressionSyntax memberAccess)
	{
		if (TryResolveNamespaceGlobal(context, memberAccess, out var nsGlobal) && nsGlobal is not null)
			return nsGlobal;

		var memberName = memberAccess.MemberName;
		if (GetDottedName(memberAccess.Expression) is { } dotted && context.ResolveType(dotted) is EnumTypeSymbol enumType)
		{
			if (enumType.FindVariant(memberName) is not null)
				return enumType;
			return memberName switch
			{
				"Min" or "Max" or "Count" => TypeSymbol.Int,
				"Values" => new SliceTypeSymbol(enumType),
				_ => null,
			};
		}

		var leftType = Resolve(context, visible, memberAccess.Expression);
		if (leftType is PointerTypeSymbol pointer)
			leftType = pointer.ReferencedType;
		if (leftType?.Name.EndsWith("[]", StringComparison.Ordinal) == true && memberName == "Length")
			return TypeSymbol.Int;
		return leftType switch
		{
			UnionTypeSymbol union => union.FindField(memberName)?.Type,
			StructTypeSymbol structType => structType.FindField(memberName)?.Type,
			_ => null,
		};
	}

	private static bool TryResolveNamespaceGlobal(BindingContext context, MemberAccessExpressionSyntax memberAccess, out TypeSymbol? type)
	{
		type = null;
		var dotted = GetDottedName(memberAccess.Expression);
		if (dotted is null || dotted.IndexOf('.') < 0)
			return false;

		var separator = dotted.LastIndexOf('.');
		var namespacePath = dotted[..separator];
		var leaf = dotted[(separator + 1)..];
		type = context.ResolveQualifiedGlobal(namespacePath, leaf)?.Type;
		return true;
	}

	private static TypeSymbol? ResolveTypeOrNull(BindingContext context, string? typeName)
	{
		if (string.IsNullOrWhiteSpace(typeName))
			return null;
		return context.ResolveType(context.NormalizeGenericName(typeName));
	}
}

/// <summary>
/// A completion-scoped local or parameter binding: name, origin, and resolved type.
/// </summary>
internal readonly record struct ScopedVariable(string Name, OriginKind Origin, TypeSymbol? Type);
