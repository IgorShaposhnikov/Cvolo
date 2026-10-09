using System.Numerics;
using Cvolo.Analysis;
using Cvolo.Analysis.Semantics;
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
				var isSdkGlobal = globalSymbol.DeclaringUnit is not null
					&& BindingContext.FileContexts.TryGetValue(globalSymbol.DeclaringUnit, out var globalContext)
					&& globalContext.Origin is Cvolo.Core.Diagnostics.SourceOrigin.BaseSdk or Cvolo.Core.Diagnostics.SourceOrigin.SystemSdk;
				var defineHere = !importedPackageGlobal
					&& (_definedGlobalNames is null || _definedGlobalNames.Contains(qualifiedName));

				global.IsGlobalConstant = !globalSymbol.IsMutable;
				if (defineHere)
				{
					global.Linkage = isSdkGlobal || (_definedGlobalNames is not null && globalSymbol.Visibility == Visibility.Public)
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

		if (TypeSymbol.IsFloatingPointType(typeSymbol))
		{
			if (NumericConversionSemantics.TryGetFloatingConstant(initializer, typeSymbol, BindingContext, out var floatingConstant))
				return LLVMValueRef.CreateConstReal(llvmType, floatingConstant);

			if (TryEvaluateConstant(initializer, out var isDoubleResult, out var doubleResult, out var integerResult))
				return LLVMValueRef.CreateConstReal(llvmType, isDoubleResult ? doubleResult : integerResult);

			return LLVMValueRef.CreateConstNull(llvmType);
		}

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
			case UnaryExpressionSyntax { Operator: "+" } unary:
				return BuildInitializer(typeSymbol, unary.Operand, llvmType);
			case UnaryExpressionSyntax cast when NumericConversionSemantics.TryParseCastOperator(cast.Operator, out _):
				if (NumericConversionSemantics.TryEvaluateIntegerConstant(cast, BindingContext, out var castInteger, out var castSource)
					&& castSource is not null && TypeSymbol.IsNumericIntegerType(castSource))
				{
					return LLVMValueRef.CreateConstInt(llvmType, ToBitPattern(castInteger));
				}

				if (TryEvaluateConstant(cast, out var castIsDouble, out var castDouble, out var castLong))
				{
					return llvmType.Kind is LLVMTypeKind.LLVMDoubleTypeKind or LLVMTypeKind.LLVMFloatTypeKind
						? LLVMValueRef.CreateConstReal(llvmType, castIsDouble ? castDouble : castLong)
						: LLVMValueRef.CreateConstInt(llvmType, unchecked((ulong)castLong));
				}

				return LLVMValueRef.CreateConstNull(llvmType);
			case StructInitializationExpressionSyntax structInitializer when typeSymbol is StructTypeSymbol structType
				&& _codegen.LlvmStructTypes.TryGetValue(structType.Name, out var namedStruct):
				{
					var fieldValues = new List<LLVMValueRef>();
					foreach (var field in structType.Fields)
					{
						var memberInitializer = structInitializer.Initializers.FirstOrDefault(member => member.MemberName == field.Name);
						var fieldType = _codegen.Types.Lower(field.Type);
						fieldValues.Add(memberInitializer is not null
							? BuildInitializer(field.Type, memberInitializer.Expression, fieldType)
							: LLVMValueRef.CreateConstNull(fieldType));
					}

					return LLVMValueRef.CreateConstNamedStruct(namedStruct, [.. fieldValues]);
				}
			case BinaryExpressionSyntax binary when binary.Operator is "+" or "-" or "*" or "/" or "%":
				if (TryEvaluateConstant(binary, out var isDoubleResult, out var doubleResult, out var integerResult))
				{
					var isFloatType = llvmType.Kind is LLVMTypeKind.LLVMDoubleTypeKind or LLVMTypeKind.LLVMFloatTypeKind;
					return isFloatType
						? LLVMValueRef.CreateConstReal(llvmType, isDoubleResult ? doubleResult : integerResult)
						: LLVMValueRef.CreateConstInt(llvmType, unchecked((ulong)integerResult));
				}

				return LLVMValueRef.CreateConstNull(llvmType);
			// Integer and floating equality/relational comparisons fold like their runtime lowering:
			// signedness is taken from the canonical common semantic type and float `!=` keeps the
			// runtime's ordered semantics (NaN compares unequal to everything, including itself).
			case BinaryExpressionSyntax binary when binary.Operator is "==" or "!=" or "<" or "<=" or ">" or ">=":
				if (TryEvaluateComparison(binary, out var comparisonResult))
					return LLVMValueRef.CreateConstInt(LLVMTypeRef.Int1, comparisonResult ? 1UL : 0UL);

				return LLVMValueRef.CreateConstNull(llvmType);
			case SizeofExpressionSyntax or AlignofExpressionSyntax or OffsetofExpressionSyntax:
				return LLVMValueRef.CreateConstInt(llvmType, unchecked((ulong)LayoutConstantOf(initializer)));
			default:
				return LLVMValueRef.CreateConstNull(llvmType);
		}
	}

	private long LayoutConstantOf(ExpressionSyntax expression)
		=> BindingContext.ResolvedLayoutConstants.TryGetValue(expression, out var value) ? value : BindingContext.EvaluateLayoutConstant(expression);

	/// <summary>Reduces a folded integer constant to its 64-bit two's-complement bit pattern.</summary>
	private static ulong ToBitPattern(BigInteger value)
	{
		var mask = (BigInteger.One << 64) - 1;
		return (ulong)(value & mask);
	}

	/// <summary>
	/// Returns whether an initializer node is one of the literal forms accepted directly inside a
	/// compile-time struct initializer.
	/// </summary>
	private static bool IsSimpleConstant(ExpressionSyntax expression)
		=> expression is IntegerLiteralExpressionSyntax
			or DoubleLiteralExpressionSyntax
			or BooleanLiteralExpressionSyntax
			or CharacterLiteralExpressionSyntax
			or SizeofExpressionSyntax
			or AlignofExpressionSyntax
			or OffsetofExpressionSyntax;

	/// <summary>
	/// Recursively evaluates the existing global-constant arithmetic subset, preserving integer
	/// division-by-zero rejection and IEEE floating-point division behavior.
	/// </summary>
	private bool TryEvaluateConstant(
		ExpressionSyntax expression,
		out bool isDouble,
		out double doubleValue,
		out long integerValue)
	{
		switch (expression)
		{
			case SizeofExpressionSyntax or AlignofExpressionSyntax or OffsetofExpressionSyntax:
				isDouble = false;
				integerValue = LayoutConstantOf(expression);
				doubleValue = integerValue;
				return true;
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
			case UnaryExpressionSyntax { Operator: "+" } unary:
				return TryEvaluateConstant(unary.Operand, out isDouble, out doubleValue, out integerValue);
			case UnaryExpressionSyntax cast when NumericConversionSemantics.TryParseCastOperator(cast.Operator, out var castName)
				&& BindingContext.ResolveType(castName) is { } castType:
				if (NumericConversionSemantics.TryEvaluateIntegerConstant(cast, BindingContext, out var castValue, out _))
				{
					isDouble = TypeSymbol.IsFloatingPointType(castType);
					integerValue = unchecked((long)ToBitPattern(castValue));
					doubleValue = (double)castValue;
					return true;
				}

				if (TryEvaluateConstant(cast.Operand, out var operandIsDouble, out var operandDouble, out var operandInteger))
				{
					if (TypeSymbol.IsFloatingPointType(castType))
					{
						isDouble = true;
						doubleValue = castType.Equals(TypeSymbol.Float) ? (float)operandDouble : operandDouble;
						integerValue = 0;
					}
					else
					{
						isDouble = false;
						integerValue = operandInteger;
						doubleValue = operandInteger;
					}

					return true;
				}

				isDouble = false;
				doubleValue = 0;
				integerValue = 0;
				return false;
			case BinaryExpressionSyntax binary:
				if (!BindingContext.ResolvedBinaryExpressions.TryGetValue(binary, out var binding)
					|| !binding.IsSuccess
					|| binding.BuiltinOperatorKind is not (BuiltinBinaryOperatorKind.IntegerArithmetic or BuiltinBinaryOperatorKind.FloatingArithmetic))
				{
					isDouble = false;
					doubleValue = 0;
					integerValue = 0;
					return false;
				}

				if (!TryEvaluateConstant(binary.Left, out var leftIsDouble, out var leftDouble, out var leftInteger)
					|| !TryEvaluateConstant(binary.Right, out var rightIsDouble, out var rightDouble, out var rightInteger))
				{
					isDouble = false;
					doubleValue = 0;
					integerValue = 0;
					return false;
				}

				isDouble = binding.BuiltinOperatorKind == BuiltinBinaryOperatorKind.FloatingArithmetic;
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
						"%" => left % right,
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

					// §8.2.1: post-overflow signed normalization. Truncate the folded result to the
					// resolved integer width and reinterpret under its signedness so the emitted
					// constant matches runtime wrapping arithmetic (e.g. 2147483647 + 1 is
					// -2147483648, not +2147483648).
					if (binding.OperationType is { } intOperationType
						&& TypeSymbol.IsNumericIntegerType(intOperationType))
					{
						integerValue = unchecked((long)ToBitPattern(
							NumericConversionSemantics.TruncateIntegerToType(integerValue, intOperationType)));
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

	/// <summary>
	/// Folds a constant equality/relational comparison exactly like the runtime icmp/fcmp lowering:
	/// each operand is first truncated to its source width, then widened to the canonical common
	/// type using the source's signedness, then compared with the common type's signed/unsigned
	/// ordering. Float comparisons mirror <see cref="ExpressionEmitter"/>'s ordered predicates.
	/// </summary>
	private bool TryEvaluateComparison(BinaryExpressionSyntax binary, out bool result)
	{
		result = false;

		if (!BindingContext.ResolvedBinaryExpressions.TryGetValue(binary, out var binding) || !binding.IsSuccess
			|| binding.LeftConversion is null || binding.RightConversion is null)
			return false;

		if (binding.BuiltinOperatorKind == BuiltinBinaryOperatorKind.IntegerComparison
			&& binding.OperationType is { } integerType
			&& TypeSymbol.IsNumericIntegerType(integerType))
		{
			if (!TryEvaluateConstant(binary.Left, out _, out _, out var leftInteger)
				|| !TryEvaluateConstant(binary.Right, out _, out _, out var rightInteger))
				return false;

			var commonWidth = TypeSymbol.IntegerBitWidth(integerType);
			var useUnsigned = binding.IsUnsignedIntegerComparison;
			var leftValue = CoerceToCommonWidth((ulong)leftInteger, binding.LeftConversion.ValueType, commonWidth, useUnsigned);
			var rightValue = CoerceToCommonWidth((ulong)rightInteger, binding.RightConversion.ValueType, commonWidth, useUnsigned);

			result = binary.Operator switch
			{
				"==" => leftValue == rightValue,
				"!=" => leftValue != rightValue,
				"<" when useUnsigned => leftValue < rightValue,
				">" when useUnsigned => leftValue > rightValue,
				"<=" when useUnsigned => leftValue <= rightValue,
				">=" when useUnsigned => leftValue >= rightValue,
				"<" => (long)leftValue < (long)rightValue,
				">" => (long)leftValue > (long)rightValue,
				"<=" => (long)leftValue <= (long)rightValue,
				">=" => (long)leftValue >= (long)rightValue,
				_ => false,
			};
			return true;
		}

		if (binding.BuiltinOperatorKind == BuiltinBinaryOperatorKind.FloatingComparison)
		{
			if (!TryEvaluateConstant(binary.Left, out _, out var leftDouble, out _)
				|| !TryEvaluateConstant(binary.Right, out _, out var rightDouble, out _))
				return false;

			var isNaN = double.IsNaN(leftDouble) || double.IsNaN(rightDouble);
			result = binary.Operator switch
			{
				"==" => leftDouble == rightDouble,
				"!=" => !isNaN && leftDouble != rightDouble,
				"<" => leftDouble < rightDouble,
				">" => leftDouble > rightDouble,
				"<=" => leftDouble <= rightDouble,
				">=" => leftDouble >= rightDouble,
				_ => false,
			};
			return true;
		}

		return false;
	}

	/// <summary>
	/// Reduces a folded 64-bit host pattern to the canonical common width using the source's own
	/// width and signedness. For unsigned comparisons the result is the zero-extended common-width
	/// pattern (compared as <see cref="ulong"/>). For signed comparisons the result is sign-extended
	/// to 64 bits so casting to <see cref="long"/> always yields the true common-type value,
	/// including widths below 64 where the pattern is re-sign-extended at the common width.
	/// </summary>
	private static ulong CoerceToCommonWidth(ulong raw, TypeSymbol sourceType, int commonWidth, bool useUnsigned)
	{
		var sourceWidth = TypeSymbol.IntegerBitWidth(sourceType);
		var sourceMask = sourceWidth == 64 ? ulong.MaxValue : (1UL << sourceWidth) - 1;
		var truncated = raw & sourceMask;

		if (!useUnsigned)
		{
			var sourceSigned = TypeSymbol.IsSignedIntegerType(sourceType);
			var signExtended = sourceSigned && (truncated & (1UL << (sourceWidth - 1))) != 0
				? truncated | ~sourceMask
				: truncated;

			if (commonWidth == 64)
				return signExtended;

			var commonMask = (1UL << commonWidth) - 1;
			var atCommon = signExtended & commonMask;
			return (atCommon & (1UL << (commonWidth - 1))) != 0
				? atCommon | ~commonMask
				: atCommon;
		}

		return commonWidth >= 64 ? truncated : truncated & ((1UL << commonWidth) - 1);
	}
}
