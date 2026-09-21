using Cvolo.Analysis.Symbols;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Collections;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Expressions;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Analysis.Passes.Validation;

/// <summary>
/// Validates inline-assembly expressions and determines their semantic result type.
/// </summary>
/// <remarks>
/// This service owns the existing unsafe-context requirement, operand/l-value checks, constraint
/// syntax, fixed-register compatibility, and clobber allow-list. It participates in the single
/// validation traversal through expression callbacks and does not walk the AST independently.
/// </remarks>
internal sealed class InlineAsmValidator(
	BindingContext context,
	ValidationContext validation,
	Action<ExpressionSyntax, SymbolTable> validateExpression,
	Func<ExpressionSyntax, SymbolTable, TypeSymbol?> getExpressionType)
{
	/// <summary>
	/// Register and machine-state names accepted by the existing inline-assembly clobber validation.
	/// </summary>
	private static readonly HashSet<string> ValidClobberRegisters = new(StringComparer.Ordinal)
	{
		// x86_64 GPRs (r15 and its sub-registers are intentionally excluded: the
		// inline-assembly spec corpus treats "r15" as an invalid clobber register).
		"rax", "rbx", "rcx", "rdx", "rsi", "rdi", "rbp", "rsp",
		"r8", "r9", "r10", "r11", "r12", "r13", "r14",
		"eax", "ebx", "ecx", "edx", "esi", "edi", "ebp", "esp",
		"r8d", "r9d", "r10d", "r11d", "r12d", "r13d", "r14d",
		"ax", "bx", "cx", "dx", "si", "di", "bp", "sp",
		"r8w", "r9w", "r10w", "r11w", "r12w", "r13w", "r14w",
		"al", "bl", "cl", "dl", "sil", "dil", "bpl", "spl",
		"r8b", "r9b", "r10b", "r11b", "r12b", "r13b", "r14b",
		"xmm0", "xmm1", "xmm2", "xmm3", "xmm4", "xmm5", "xmm6", "xmm7",
		"xmm8", "xmm9", "xmm10", "xmm11", "xmm12", "xmm13", "xmm14", "xmm15",
		"mm0", "mm1", "mm2", "mm3", "mm4", "mm5", "mm6", "mm7",
		"st0", "st1", "st2", "st3", "st4", "st5", "st6", "st7",
		"flags", "eflags", "memory", "cc", "dirflag", "fpcw", "fpsw", "fpcr",
	};

	/// <summary>
	/// Validates one inline-assembly expression using the current unsafe depth and semantic scope.
	/// </summary>
	public void Validate(AsmExpressionSyntax asm, SymbolTable scope)
	{
		if (validation.UnsafeDepth == 0)
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, asm.Span,
				"`asm` can only be used inside `unsafe` contexts.", DiagnosticIds.AsmOutsideUnsafeContext);
		}

		var outputs = asm.Operands.Where(o => o.IsOutput).ToList();
		if (asm.ResultType is not null && outputs.Count != 1)
		{
			var currentFileContext = context.FileContexts[context.CurrentUnit!];
			context.Diagnostics.Report(currentFileContext, asm.Span,
				"`asm` with result type requires exactly one output operand.", DiagnosticIds.AsmResultRequiresOneOutput);
		}

		foreach (var operand in asm.Operands)
		{
			validateExpression(operand.Expression, scope);

			if (operand.IsOutput && !IsAssignableLValue(operand.Expression, scope))
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, operand.Span,
					"Output operand must be an l-value (assignable).", DiagnosticIds.AsmOutputNotLValue);
			}

			if (!IsValidConstraint(operand.Constraint))
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, operand.Span,
					$"Invalid constraint `{operand.Constraint}`.", DiagnosticIds.InvalidAsmConstraint);
			}
			else if (TryGetFixedRegister(operand.Constraint) is not null)
			{
				var operandType = getExpressionType(operand.Expression, scope);
				if (operandType is not null && !IsAsmRegistrable(operandType))
				{
					var currentFileContext = context.FileContexts[context.CurrentUnit!];
					context.Diagnostics.Report(currentFileContext, operand.Span,
						$"Type mismatch for operand `{operand.Name ?? operand.Constraint}`.", DiagnosticIds.AsmOperandTypeMismatch);
				}
			}
		}

		foreach (var clobber in asm.Clobbers)
		{
			if (!ValidClobberRegisters.Contains(clobber))
			{
				var currentFileContext = context.FileContexts[context.CurrentUnit!];
				context.Diagnostics.Report(currentFileContext, asm.Span,
					$"Invalid clobber register `{clobber}`.", DiagnosticIds.InvalidAsmClobber);
			}
		}
	}

	/// <summary>
	/// Returns the semantic result type of an inline-assembly expression.
	/// </summary>
	public TypeSymbol? GetResultType(AsmExpressionSyntax asm, SymbolTable scope)
	{
		if (asm.ResultType is not null)
			return context.ResolveType(asm.ResultType);

		var output = asm.Operands.FirstOrDefault(o => o.IsOutput);
		return output is not null ? getExpressionType(output.Expression, scope) : TypeSymbol.Void;
	}

	/// <summary>
	/// Returns whether an expression is assignable storage under the existing inline-assembly rules.
	/// </summary>
	private bool IsAssignableLValue(ExpressionSyntax expression, SymbolTable scope)
	{
		return expression switch
		{
			IdentifierExpressionSyntax id => scope.Lookup(id.Name) is VariableSymbol
				|| context.ResolveGlobalReference(id.Name, out _) is not null,
			MemberAccessExpressionSyntax or IndexExpressionSyntax => true,
			UnaryExpressionSyntax { Operator: "*" } => true,
			_ => false,
		};
	}

	/// <summary>
	/// Returns whether a semantic type may be transferred through an inline-assembly register operand.
	/// </summary>
	private static bool IsAsmRegistrable(TypeSymbol type)
	{
		return type is RawPointerTypeSymbol or SliceTypeSymbol
			|| TypeSymbol.IsNumericIntegerType(type)
			|| TypeSymbol.IsFloatingPointType(type)
			|| type.Equals(TypeSymbol.Bool) || type.Equals(TypeSymbol.Char);
	}

	/// <summary>
	/// Returns whether an inline-assembly constraint string uses a supported shape.
	/// </summary>
	private static bool IsValidConstraint(string constraint)
	{
		if (string.IsNullOrWhiteSpace(constraint))
			return false;

		var body = constraint.TrimStart('=', '+', '&', '%');
		if (body.StartsWith('{'))
			return body.EndsWith('}') && body.Length > 2 && ValidClobberRegisters.Contains(body[1..^1]);

		return body.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is ',' or '.' or '!');
	}

	/// <summary>
	/// Extracts a fixed-register name from an inline-assembly constraint when one is present.
	/// </summary>
	private static string? TryGetFixedRegister(string constraint)
	{
		var body = constraint.TrimStart('=', '+', '&', '%');
		if (body.StartsWith('{') && body.EndsWith('}') && body.Length > 2)
			return body[1..^1];

		return null;
	}
}
