using Cvolo.Analysis;
using Cvolo.Analysis.Symbols;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Collections;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Expressions;

namespace Cvolo.Emitter.LLVM.Codegen.Values;

/// <summary>
/// Resolves the semantic type produced by already-bound Cvolo expressions during LLVM emission.
/// </summary>
/// <remarks>
/// Semantic validation and overload resolution happen before code generation. This resolver mirrors
/// the code generator's existing post-binding type classification so emitters share one source of
/// truth instead of carrying independent expression-type callbacks. It does not decide whether a
/// conversion or operation is legal and it does not lower semantic types to LLVM types.
/// </remarks>
/// <remarks>
/// Creates an expression-type resolver over the module-level semantic state and the function
/// frame active at the point where a query is made.
/// </remarks>
/// <param name="codegen">Shared module and binding state for the current code-generation run.</param>
/// <param name="getFunction">Returns the function-local state active for the current query.</param>
internal sealed class ExpressionTypeResolver(CodegenContext codegen, Func<FunctionCodegenContext> getFunction)
{
	private BindingContext BindingContext => codegen.BindingContext ?? throw new InvalidOperationException("Expression type resolution requires an active binding context.");

	private FunctionCodegenContext Function => getFunction();

	/// <summary>
	/// Returns the semantic type produced by an expression using the same rules that were previously
	/// embedded in <c>CodeGenerator.GetExprType</c>.
	/// </summary>
	/// <param name="expression">Bound expression whose result type is required by an emitter.</param>
	/// <returns>The semantic result type used by LLVM emission.</returns>
	public TypeSymbol Resolve(ExpressionSyntax expression)
	{
		if (expression is BorrowExpressionSyntax borrow)
			return new PointerTypeSymbol(Resolve(borrow.Expression), borrow.IsMutable);

		if (expression is LambdaExpressionSyntax lambda
			&& BindingContext.ResolvedLambdas.TryGetValue(lambda, out var lambdaInfo))
		{
			return lambdaInfo.Delegate;
		}

		if (expression is UnaryExpressionSyntax nativeAddress
			&& BindingContext.ResolvedNativeFunctionAddresses.TryGetValue(nativeAddress, out var addressBinding))
		{
			return addressBinding.Delegate;
		}

		if (BindingContext.ResolvedFunctionConversions.TryGetValue(expression, out var convertedFunction))
		{
			return BuildGroupDelegateType(convertedFunction, expression is MemberAccessExpressionSyntax);
		}

		return expression switch
		{
			IntegerLiteralExpressionSyntax intLit => intLit.LiteralType switch
			{
				"uint" => TypeSymbol.UInt,
				"long" => TypeSymbol.Long,
				"ulong" => TypeSymbol.ULong,
				_ => intLit.Value <= (ulong)int.MaxValue ? TypeSymbol.Int : TypeSymbol.Long,
			},
			DoubleLiteralExpressionSyntax dblLit => dblLit.IsFloat ? TypeSymbol.Float : TypeSymbol.Double,
			BooleanLiteralExpressionSyntax => TypeSymbol.Bool,
			StringLiteralExpressionSyntax => TypeSymbol.String,
			CharacterLiteralExpressionSyntax => TypeSymbol.Char,
			IdentifierExpressionSyntax id => ResolveIdentifier(id),
			MemberAccessExpressionSyntax member => ResolveMemberAccess(member),
			IndexExpressionSyntax index => ResolveIndex(index),
			StructInitializationExpressionSyntax init => BindingContext.ResolveType(init.StructTypeName)!,
			UnaryExpressionSyntax unary => ResolveUnary(unary),
			AsmExpressionSyntax asm => ResolveAsm(asm),
			NameofExpressionSyntax => TypeSymbol.String,
			TypeofExpressionSyntax => BindingContext.ResolveType("System.Type") ?? TypeSymbol.String,
			TernaryExpressionSyntax ternary => Resolve(ternary.ThenExpression),
			CallExpressionSyntax call => ResolveCallReturn(call),
			BinaryExpressionSyntax binary => ResolveBinary(binary),
			HeapAllocationExpressionSyntax heap => Resolve(heap.Expression),
			HeapArrayAllocationExpressionSyntax heapArray =>
				new SliceTypeSymbol(BindingContext.ResolveType(heapArray.ElementTypeName)!),
			ArrayInitializationExpressionSyntax array =>
				new ArrayTypeSymbol(array.Elements.Count > 0 ? Resolve(array.Elements[0]) : TypeSymbol.Int, array.Elements.Count),
			_ => TypeSymbol.Int,
		};
	}

	/// <summary>
	/// Returns the semantic result type of an inline-assembly expression.
	/// </summary>
	/// <remarks>
	/// An explicit result type wins. Otherwise the first output operand determines the result type;
	/// an asm expression without either form produces <c>void</c>.
	/// </remarks>
	public TypeSymbol ResolveAsm(AsmExpressionSyntax asm)
	{
		if (asm.ResultType is not null && BindingContext.ResolveType(asm.ResultType) is { } resultType)
			return resultType;

		var output = asm.Operands.FirstOrDefault(operand => operand.IsOutput);
		return output is not null ? Resolve(output.Expression) : TypeSymbol.Void;
	}

	/// <summary>
	/// Determines whether a bound call resolves to a constructor for the requested semantic target
	/// type while preserving the existing namespace- and generic-name matching behavior.
	/// </summary>
	public bool IsConstructorCall(CallExpressionSyntax call, TypeSymbol targetType)
	{
		var targetTypeName = targetType.Name;
		if (targetTypeName.Contains('<'))
			targetTypeName = targetTypeName.Substring(0, targetTypeName.IndexOf('<'));

		var shortTargetTypeName = targetTypeName.Contains('.')
			? targetTypeName[(targetTypeName.LastIndexOf('.') + 1)..]
			: targetTypeName;

		var shortCallName = call.FunctionName.Contains('.')
			? call.FunctionName[(call.FunctionName.LastIndexOf('.') + 1)..]
			: call.FunctionName;

		if (!string.Equals(shortCallName, shortTargetTypeName, StringComparison.Ordinal))
			return false;

		var constructors = BindingContext.Constructors;
		if (!constructors.TryGetValue(targetType.Name, out var constructorList)
			&& !constructors.TryGetValue(targetTypeName, out constructorList)
			&& !constructors.TryGetValue(shortTargetTypeName, out constructorList))
		{
			return false;
		}

		return BindingContext.ResolvedCalls.TryGetValue(call, out var resolved)
			&& resolved.Parameters.Count > 0
			&& resolved.Parameters[0].Name == "this";
	}

	/// <summary>
	/// Builds the synthetic semantic delegate signature used for a free-function or bound-method
	/// method-group conversion.
	/// </summary>
	/// <param name="function">Resolved function represented by the method group.</param>
	/// <param name="isBound">Whether the first receiver parameter is captured in the delegate context.</param>
	public DelegateTypeSymbol BuildGroupDelegateType(FunctionSymbol function, bool isBound)
	{
		var start = isBound && function.Parameters.Count > 0 ? 1 : 0;
		var parameters = new List<ParameterSymbol>();
		for (var i = start; i < function.Parameters.Count; i++)
			parameters.Add(function.Parameters[i]);

		return new DelegateTypeSymbol(
			"$group." + function.Name,
			function.ReturnType,
			parameters,
			[], [], false, null);
	}

	/// <summary>
	/// Resolves an identifier from function-local type state, including implicit enum variants and
	/// implicit receiver-field access used inside extension bodies.
	/// </summary>
	private TypeSymbol ResolveIdentifier(IdentifierExpressionSyntax identifier)
	{
		if (Function.VariableTypes.TryGetValue(identifier.Name, out var type))
		{
			if (type is PointerTypeSymbol pointer && pointer.ReferencedType is EnumTypeSymbol enumType)
				return enumType;
			return type;
		}

		if (Function.VariableTypes.TryGetValue("this", out var thisType)
			&& thisType is PointerTypeSymbol thisPointer
			&& thisPointer.ReferencedType is EnumTypeSymbol enumSelf
			&& enumSelf.FindVariant(identifier.Name) is not null)
		{
			return enumSelf;
		}

		if (Function.VariableTypes.TryGetValue("this", out var receiverType)
			&& receiverType is PointerTypeSymbol receiverPointer
			&& receiverPointer.ReferencedType is StructTypeSymbol receiverStruct
			&& receiverStruct.FindField(identifier.Name) is { } field)
		{
			return field.Type;
		}

		return TypeSymbol.Int;
	}

	/// <summary>
	/// Resolves unary-expression result types, including raw-pointer dereference/address-of and the
	/// safe-zone integer-to-enum cast representation.
	/// </summary>
	private TypeSymbol ResolveUnary(UnaryExpressionSyntax unary)
	{
		if (unary.Operator == "*")
		{
			var operandType = Resolve(unary.Operand);
			return operandType is RawPointerTypeSymbol rawPointer ? rawPointer.ElementType : TypeSymbol.Int;
		}

		if (unary.Operator == "&")
			return new RawPointerTypeSymbol(Resolve(unary.Operand));

		if (unary.Operator.StartsWith("(") && unary.Operator.EndsWith(')'))
		{
			var typeName = unary.Operator[1..^1];
			var result = BindingContext.ResolveType(typeName)!;
			if (result is EnumTypeSymbol castEnum && Function.UnsafeDepth == 0)
			{
				var operandType = Resolve(unary.Operand);
				if (operandType is not EnumTypeSymbol && TypeSymbol.IsIntegerType(operandType))
					return BindingContext.ResolveType($"Option<{castEnum.Name}>") ?? result;
			}

			return result;
		}

		return Resolve(unary.Operand);
	}

	/// <summary>
	/// Resolves a call result from binder-owned call/delegate resolution and preserves the legacy
	/// mangled-name registry fallback for unresolved synthetic paths.
	/// </summary>
	private TypeSymbol ResolveCallReturn(CallExpressionSyntax call)
	{
		if (BindingContext.ResolvedDelegateCalls.TryGetValue(call, out var delegateCallType))
			return delegateCallType.ReturnType;

		if (BindingContext.ResolvedCalls.TryGetValue(call, out var resolvedFunction))
			return resolvedFunction.ReturnType;

		var mangledName = ResolveFunctionName(call.FunctionName, codegen.CurrentUnit!);
		if (call.TypeArguments.Count > 0)
			mangledName = $"{mangledName}<{string.Join(", ", call.TypeArguments)}>";

		return codegen.FunctionReturnTypes.TryGetValue(mangledName, out var type)
			? type
			: TypeSymbol.Int;
	}

	/// <summary>
	/// Resolves binary-expression result types using the existing string, comparison, floating-point,
	/// and integer-width promotion rules.
	/// </summary>
	private TypeSymbol ResolveBinary(BinaryExpressionSyntax binary)
	{
		if (binary.Operator == "+" && IsConstantStringTree(binary.Left) && IsConstantStringTree(binary.Right))
			return TypeSymbol.String;

		if (binary.Operator is "==" or "!=" or "<" or ">" or "<=" or ">=")
			return TypeSymbol.Bool;

		var leftType = Resolve(binary.Left);
		var rightType = Resolve(binary.Right);
		if (leftType.Equals(TypeSymbol.Double) || rightType.Equals(TypeSymbol.Double))
			return TypeSymbol.Double;

		if (TypeSymbol.IsIntegerType(leftType) && TypeSymbol.IsIntegerType(rightType))
		{
			var leftWidth = TypeSymbol.IntegerBitWidth(leftType);
			var rightWidth = TypeSymbol.IntegerBitWidth(rightType);
			if (rightWidth > leftWidth)
				return rightType;
		}

		return leftType;
	}

	/// <summary>
	/// Resolves member-access result types for enum metaprogramming, slices, structs, and unions.
	/// </summary>
	private TypeSymbol ResolveMemberAccess(MemberAccessExpressionSyntax member)
	{
		if (TryResolveEnumTypeReceiver(member) is { } enumType)
		{
			if (enumType.FindVariant(member.MemberName) is not null)
				return enumType;
			if (member.MemberName == "Values")
				return new SliceTypeSymbol(enumType);
			if (member.MemberName is "Min" or "Max" or "Count")
				return TypeSymbol.Int;
			return enumType;
		}

		var parentType = Resolve(member.Expression);
		if (parentType is PointerTypeSymbol pointer)
			parentType = pointer.ReferencedType;

		if (parentType is SliceTypeSymbol && member.MemberName == "Length")
			return TypeSymbol.Int;

		if (parentType is StructTypeSymbol structType && structType.FindField(member.MemberName) is { } structField)
			return structField.Type;

		if (parentType is UnionTypeSymbol unionType && unionType.FindField(member.MemberName) is { } unionField)
			return unionField.Type;

		return TypeSymbol.Int;
	}

	/// <summary>
	/// Resolves the element type produced by indexing an array or slice.
	/// </summary>
	private TypeSymbol ResolveIndex(IndexExpressionSyntax index)
	{
		var parentType = Resolve(index.Left);
		return parentType switch
		{
			ArrayTypeSymbol arrayType => arrayType.ElementType,
			SliceTypeSymbol sliceType => sliceType.ElementType,
			_ => TypeSymbol.Int,
		};
	}

	/// <summary>
	/// Resolves an enum type used syntactically as the receiver of a scoped variant or enum
	/// metaprogramming member.
	/// </summary>
	private EnumTypeSymbol? TryResolveEnumTypeReceiver(MemberAccessExpressionSyntax member)
	{
		var dottedName = GetDottedName(member.Expression);
		return dottedName is null ? null : BindingContext.ResolveType(dottedName) as EnumTypeSymbol;
	}

	/// <summary>
	/// Reconstructs a dotted identifier/member-access chain when the expression is purely a name.
	/// </summary>
	private static string? GetDottedName(ExpressionSyntax expression)
	{
		if (expression is IdentifierExpressionSyntax identifier)
			return identifier.Name;

		if (expression is MemberAccessExpressionSyntax member && GetDottedName(member.Expression) is { } baseName)
		{
			return $"{baseName}.{member.MemberName}";
		}

		return null;
	}

	/// <summary>
	/// Resolves a source function name against the active namespace and expanded using directives.
	/// </summary>
	private string ResolveFunctionName(string name, CompilationUnitSyntax activeUnit)
	{
		if (name is "main" or "Main")
			return "main";

		if (codegen.Globals.ContainsKey(name) || BindingContext.GenericFunctionTemplates.ContainsKey(name))
			return name;

		var currentNamespace = activeUnit.NamespaceDeclaration?.Name;
		var localMangled = string.IsNullOrEmpty(currentNamespace) ? name : $"{currentNamespace}.{name}";
		if (codegen.Globals.ContainsKey(localMangled) || BindingContext.GenericFunctionTemplates.ContainsKey(localMangled))
			return localMangled;

		foreach (var importedNamespace in BindingContext.GetActiveUsings(activeUnit))
		{
			var candidate = $"{importedNamespace}.{name}";
			if (codegen.Globals.ContainsKey(candidate) || BindingContext.GenericFunctionTemplates.ContainsKey(candidate))
				return candidate;
		}

		return name;
	}

	/// <summary>
	/// Returns whether an expression is a compile-time tree consisting only of string literals
	/// joined by the binary <c>+</c> operator.
	/// </summary>
	private static bool IsConstantStringTree(ExpressionSyntax expression)
	{
		return expression switch
		{
			StringLiteralExpressionSyntax => true,
			BinaryExpressionSyntax binary when binary.Operator == "+" =>
				IsConstantStringTree(binary.Left) && IsConstantStringTree(binary.Right),
			_ => false,
		};
	}
}
