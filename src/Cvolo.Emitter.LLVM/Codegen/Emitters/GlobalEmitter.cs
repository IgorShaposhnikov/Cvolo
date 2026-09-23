using Cvolo.Analysis;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Expressions;
using LLVMSharp.Interop;

namespace Cvolo.Emitter.LLVM.Codegen.Emitters;

/// <summary>
/// Emits module-level Cvolo data-segment globals and their compile-time LLVM initializers.
/// </summary>
/// <remarks>
/// This emitter intentionally handles only global storage definitions/declarations. Function and
/// type predeclarations remain in <see cref="DeclarationEmitter"/>, while runtime expression
/// lowering remains in <see cref="ExpressionEmitter"/>.
/// </remarks>
internal sealed class GlobalEmitter
{
	private readonly CodegenContext _codegen;
	private readonly IReadOnlySet<string>? _definedGlobalNames;

	/// <summary>
	/// Creates a global emitter using the existing package-definition filter that decides whether a
	/// global receives storage in the current module or remains an external declaration.
	/// </summary>
	public GlobalEmitter(CodegenContext codegen, IReadOnlySet<string>? definedGlobalNames)
	{
		_codegen = codegen;
		_definedGlobalNames = definedGlobalNames;
	}

	private BindingContext BindingContext => _codegen.BindingContext
		?? throw new InvalidOperationException("Global emission requires an active binding context.");

	/// <summary>
	/// Emits every bound module global exactly once, preserving package/public linkage rules and the
	/// short-name candidate registry used by later expression and function emission.
	/// </summary>
	public void EmitGlobals()
	{
		foreach (var (globalNode, globalSymbol) in BindingContext.GlobalVariables)
		{
			var qualifiedName = globalSymbol.QualifiedGlobalName;
			if (_codegen.GlobalVariables.ContainsKey(qualifiedName))
				continue;

			// C _Bool has one-byte object storage even though the compiler keeps ordinary
			// Cvolo bool values in the internal i1 representation. Imported foreign bool
			// globals therefore use i8 storage and are bridged on loads/stores.
			var llvmType = globalSymbol.IsForeign && globalSymbol.Type.Equals(TypeSymbol.Bool)
				? LLVMTypeRef.Int8
				: _codegen.Types.Lower(globalSymbol.Type);
			var nativeName = globalSymbol.IsForeign ? globalSymbol.ImportName ?? globalSymbol.Name : qualifiedName;
			var global = _codegen.Module.AddGlobal(llvmType, nativeName);

			// Imported foreign globals are externally defined mutable C data: never const, never
			// defined or initialized here.
			if (globalSymbol.IsForeign)
			{
				global.IsGlobalConstant = false;
				global.Linkage = LLVMLinkage.LLVMExternalLinkage;
				_codegen.ForeignGlobalNames.Add(qualifiedName);
			}
			else
			{
				var importedPackageGlobal = globalSymbol.DeclaringUnit is not null
					&& BindingContext.ExternalPackageUnits.Contains(globalSymbol.DeclaringUnit);
				var defineHere = !importedPackageGlobal
					&& (_definedGlobalNames is null || _definedGlobalNames.Contains(qualifiedName));

				global.IsGlobalConstant = !globalSymbol.IsMutable;
				if (defineHere)
				{
					global.Linkage = _definedGlobalNames is not null && globalSymbol.Visibility == Visibility.Public
						? LLVMLinkage.LLVMExternalLinkage
						: LLVMLinkage.LLVMInternalLinkage;
					global.Initializer = BuildInitializer(globalSymbol.Type, globalNode.Initializer, llvmType);
				}
				else
				{
					global.Linkage = LLVMLinkage.LLVMExternalLinkage;
				}
			}

			_codegen.GlobalVariables[qualifiedName] = global;
			_codegen.GlobalVariableTypes[qualifiedName] = globalSymbol.Type;

			if (!_codegen.GlobalShortNames.TryGetValue(globalSymbol.Name, out var candidates))
				_codegen.GlobalShortNames[globalSymbol.Name] = candidates = [];
			candidates.Add(qualifiedName);
		}
	}

	/// <summary>
	/// Builds the existing restricted LLVM constant initializer for literals, unary negatives,
	/// constant arithmetic, and struct initializers whose explicitly supplied fields are constants.
	/// Unsupported nodes intentionally continue to lower to the zero/null constant.
	/// </summary>
	private LLVMValueRef BuildInitializer(TypeSymbol typeSymbol, ExpressionSyntax? initializer, LLVMTypeRef llvmType)
	{
		if (initializer is null)
			return LLVMValueRef.CreateConstNull(llvmType);

		switch (initializer)
		{
			case IntegerLiteralExpressionSyntax integerLiteral:
				return LLVMValueRef.CreateConstInt(llvmType, unchecked((ulong)integerLiteral.Value));
			case DoubleLiteralExpressionSyntax doubleLiteral:
				return LLVMValueRef.CreateConstReal(llvmType, doubleLiteral.Value);
			case BooleanLiteralExpressionSyntax booleanLiteral:
				return LLVMValueRef.CreateConstInt(LLVMTypeRef.Int1, booleanLiteral.Value ? 1UL : 0UL);
			case CharacterLiteralExpressionSyntax characterLiteral:
				return LLVMValueRef.CreateConstInt(LLVMTypeRef.Int8, characterLiteral.Value);
			case UnaryExpressionSyntax { Operator: "-" } unary:
				switch (unary.Operand)
				{
					case IntegerLiteralExpressionSyntax negativeInteger:
						return LLVMValueRef.CreateConstInt(llvmType, 0UL - negativeInteger.Value);
					case DoubleLiteralExpressionSyntax negativeDouble:
						return LLVMValueRef.CreateConstReal(llvmType, -negativeDouble.Value);
					default:
						return LLVMValueRef.CreateConstNull(llvmType);
				}
			case StructInitializationExpressionSyntax structInitializer when typeSymbol is StructTypeSymbol structType
				&& _codegen.LlvmStructTypes.TryGetValue(structType.Name, out var namedStruct):
				{
					var fieldValues = new List<LLVMValueRef>();
					foreach (var field in structType.Fields)
					{
						var memberInitializer = structInitializer.Initializers.FirstOrDefault(member => member.MemberName == field.Name);
						var fieldType = _codegen.Types.Lower(field.Type);
						fieldValues.Add(memberInitializer is not null && IsSimpleConstant(memberInitializer.Expression)
							? BuildInitializer(field.Type, memberInitializer.Expression, fieldType)
							: LLVMValueRef.CreateConstNull(fieldType));
					}

					return LLVMValueRef.CreateConstNamedStruct(namedStruct, [.. fieldValues]);
				}
			case BinaryExpressionSyntax binary when binary.Operator is "+" or "-" or "*" or "/":
				if (TryEvaluateConstant(binary, out var isDoubleResult, out var doubleResult, out var integerResult))
				{
					var isFloatType = llvmType.Kind is LLVMTypeKind.LLVMDoubleTypeKind or LLVMTypeKind.LLVMFloatTypeKind;
					return isFloatType
						? LLVMValueRef.CreateConstReal(llvmType, isDoubleResult ? doubleResult : integerResult)
						: LLVMValueRef.CreateConstInt(llvmType, unchecked((ulong)integerResult));
				}

				return LLVMValueRef.CreateConstNull(llvmType);
			default:
				return LLVMValueRef.CreateConstNull(llvmType);
		}
	}

	/// <summary>
	/// Returns whether an initializer node is one of the literal forms accepted directly inside a
	/// compile-time struct initializer.
	/// </summary>
	private static bool IsSimpleConstant(ExpressionSyntax expression)
		=> expression is IntegerLiteralExpressionSyntax
			or DoubleLiteralExpressionSyntax
			or BooleanLiteralExpressionSyntax
			or CharacterLiteralExpressionSyntax;

	/// <summary>
	/// Recursively evaluates the existing global-constant arithmetic subset, preserving integer
	/// division-by-zero rejection and IEEE floating-point division behavior.
	/// </summary>
	private static bool TryEvaluateConstant(
		ExpressionSyntax expression,
		out bool isDouble,
		out double doubleValue,
		out long integerValue)
	{
		switch (expression)
		{
			case IntegerLiteralExpressionSyntax integerLiteral:
				isDouble = false;
				doubleValue = integerLiteral.Value;
				integerValue = unchecked((long)integerLiteral.Value);
				return true;
			case DoubleLiteralExpressionSyntax doubleLiteral:
				isDouble = true;
				doubleValue = doubleLiteral.Value;
				integerValue = 0;
				return true;
			case BooleanLiteralExpressionSyntax booleanLiteral:
				isDouble = false;
				doubleValue = booleanLiteral.Value ? 1.0 : 0.0;
				integerValue = booleanLiteral.Value ? 1 : 0;
				return true;
			case CharacterLiteralExpressionSyntax characterLiteral:
				isDouble = false;
				doubleValue = characterLiteral.Value;
				integerValue = characterLiteral.Value;
				return true;
			case UnaryExpressionSyntax { Operator: "-" } unary:
				if (!TryEvaluateConstant(unary.Operand, out isDouble, out doubleValue, out integerValue))
					return false;
				doubleValue = -doubleValue;
				integerValue = -integerValue;
				return true;
			case BinaryExpressionSyntax binary:
				if (!TryEvaluateConstant(binary.Left, out var leftIsDouble, out var leftDouble, out var leftInteger)
					|| !TryEvaluateConstant(binary.Right, out var rightIsDouble, out var rightDouble, out var rightInteger))
				{
					isDouble = false;
					doubleValue = 0;
					integerValue = 0;
					return false;
				}

				isDouble = leftIsDouble || rightIsDouble;
				if (isDouble)
				{
					var left = leftIsDouble ? leftDouble : leftInteger;
					var right = rightIsDouble ? rightDouble : rightInteger;
					doubleValue = binary.Operator switch
					{
						"+" => left + right,
						"-" => left - right,
						"*" => left * right,
						"/" => left / right,
						_ => 0,
					};
					integerValue = 0;
				}
				else
				{
					switch (binary.Operator)
					{
						case "+": integerValue = leftInteger + rightInteger; break;
						case "-": integerValue = leftInteger - rightInteger; break;
						case "*": integerValue = leftInteger * rightInteger; break;
						case "/":
							if (rightInteger == 0)
							{
								isDouble = false;
								doubleValue = 0;
								integerValue = 0;
								return false;
							}

							integerValue = leftInteger / rightInteger;
							break;
						case "%":
							if (rightInteger == 0)
							{
								isDouble = false;
								doubleValue = 0;
								integerValue = 0;
								return false;
							}

							integerValue = leftInteger % rightInteger;
							break;
						default:
							isDouble = false;
							doubleValue = 0;
							integerValue = 0;
							return false;
					}

					doubleValue = integerValue;
				}

				return true;
			default:
				isDouble = false;
				doubleValue = 0;
				integerValue = 0;
				return false;
		}
	}
}
