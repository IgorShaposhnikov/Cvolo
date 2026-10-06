using Cvolo.Analysis.Resolution;
using Cvolo.Analysis.Symbols;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Expressions;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Analysis.Formatting;

/// <summary>
/// The single canonical formatting pipeline. It classifies a call shape as one of the recognized
/// formatting contexts and binds a <see cref="ResolvedFormattingOperation"/> (or a
/// <see cref="ResolvedFormatterInterpolationOperation"/> for interpolated formatter writes) using
/// one precedence rule set shared by every consumer. Code generation consumes only the already-bound
/// operations kept in <see cref="BindingContext"/>.
/// </summary>
internal sealed class FormattingResolver(
	BindingContext context,
	OverloadResolver overloads,
	Func<string, bool> isNamespaceName,
	Action<ExpressionSyntax, SymbolTable> validate,
	Func<ExpressionSyntax, SymbolTable, TypeSymbol?> getType)
{
	private static readonly HashSet<string> PrimitiveNames =
	[
		"int", "uint", "long", "ulong", "short", "ushort", "byte", "sbyte",
		"nint", "nuint", "float", "double", "bool", "char",
	];

	/// <summary>
	/// Recognizes whether a single-argument call is one of the canonical formatting contexts:
	/// <c>formatter.Write(value)</c> on a canonical Base <c>Formatter</c> value, or
	/// <c>Console.Write(value)</c>/<c>Console.WriteLine(value)</c> against the console backend.
	/// </summary>
	public bool TryClassifyContext(CallExpressionSyntax call, SymbolTable scope, out FormattingContextKind kind)
	{
		kind = default;
		if (call.Arguments.Count != 1)
			return false;

		var functionName = call.FunctionName;
		var lastDot = LastTopLevelDotIndex(functionName);
		if (lastDot <= 0)
			return false;

		var memberName = functionName[(lastDot + 1)..];
		if (memberName is not ("Write" or "WriteLine"))
			return false;

		var receiver = functionName[..lastDot];

		// (i) A value receiver whose unwrapped type is the canonical Base Formatter struct selects the
		// formatter-interpolation/format-context dispatch, regardless of the source spelling used.
		if (overloads.TrySplitDottedReceiver(functionName, scope, out var isValue, out var receiverType, out var splitMember)
			&& isValue
			&& splitMember == memberName)
		{
			var unwrapped = receiverType is PointerTypeSymbol pointer ? pointer.ReferencedType : receiverType;
			if (memberName == "Write" && unwrapped is StructTypeSymbol { Name: "Formatter" })
			{
				kind = FormattingContextKind.FormatterWrite;
				return true;
			}
		}

		// (ii) The console backend namespace. 'Console' only counts when it resolves as the
		// System.Console namespace through an active using, preserving the existing "unknown type
		// 'Console'" diagnostic when no console namespace is imported.
		if (receiver is "Console" or "System.Console" && isNamespaceName(receiver))
		{
			kind = memberName == "WriteLine" ? FormattingContextKind.ConsoleWriteLine : FormattingContextKind.ConsoleWrite;
			return true;
		}

		return false;
	}

	/// <summary>
	/// Binds the §7.1 formatter-interpolation operation for <c>formatter.Write($"...")</c>. Each
	/// expression segment is validated and bound through the same <see cref="TryBind"/> pipeline used
	/// for direct formatting; a non-formattable segment is reported with the dedicated diagnostic and
	/// the whole operation is rejected. The interpolation is never materialized as a string.
	/// </summary>
	public bool TryBindFormatterInterpolation(CallExpressionSyntax call, SymbolTable scope, out ResolvedFormatterInterpolationOperation operation)
	{
		operation = null!;
		if (call.Arguments.Count != 1
			|| call.Arguments[0] is not InterpolatedStringExpressionSyntax interpolated
			|| interpolated.Segments.Count == 0)
		{
			return false;
		}

		var segments = new List<FormatterInterpolationSegment>(interpolated.Segments.Count);
		foreach (var segment in interpolated.Segments)
		{
			if (!segment.IsExpression)
			{
				segments.Add(new FormatterInterpolationSegment(false, segment.Text, null, null));
				continue;
			}

			if (segment.Expression is not { } expression)
			{
				ReportNotFormattable(interpolated.Span, "interpolation segment", null);
				return false;
			}

			validate(expression, scope);
			var expressionType = getType(expression, scope);
			if (expressionType is null)
			{
				ReportNotFormattable(expression.Span, "interpolation segment", null);
				return false;
			}

			var valueOperation = TryBind(expressionType, scope);
			if (valueOperation is null)
			{
				ReportNotFormattable(expression.Span, expressionType.Name, null);
				return false;
			}

			segments.Add(new FormatterInterpolationSegment(true, segment.Text, expression, valueOperation));
		}

		operation = new ResolvedFormatterInterpolationOperation { Segments = segments };
		return true;
	}

	/// <summary>
	/// Binds the canonical formatting operation for one value type. Precedence is frozen:
	/// explicit <see cref="IFormattable"/> conformance, then explicit <see cref="IStringable"/>
	/// conformance, then the canonical <c>Option</c>/<c>Result</c> composite, then primitive/string
	/// exact handling. <see langword="null"/> means the type is not formattable.
	/// </summary>
	public ResolvedFormattingOperation? TryBind(TypeSymbol valueType, SymbolTable scope)
	{
		// (1) Explicit user IFormattable conformance takes precedence over everything below it.
		var formatTarget = FindConformanceMember(valueType, scope, "IFormattable", "Format", IsFormatSignature);
		if (formatTarget is not null)
		{
			return Present(valueType, FormattingOperationKind.IFormattable, formatTarget);
		}

		// (2) Explicit user IStringable conformance.
		var toStringTarget = FindConformanceMember(valueType, scope, "IStringable", "ToString", IsToStringSignature);
		if (toStringTarget is not null)
		{
			return Present(valueType, FormattingOperationKind.IStringable, toStringTarget);
		}

		// (3) Canonical Option<T> composite.
		if (valueType is UnionTypeSymbol optionUnion
			&& (optionUnion.Name == "Option" || optionUnion.Name.StartsWith("Option<", StringComparison.Ordinal))
			&& optionUnion.PayloadVariant is not null
			&& optionUnion.NoneVariant is not null)
		{
			var payloadOperation = TryBind(optionUnion.PayloadVariant.Type, scope);
			if (payloadOperation is null)
				return null;

			return new ResolvedFormattingOperation
			{
				ValueType = valueType,
				Kind = FormattingOperationKind.Option,
				Payload = payloadOperation,
				OpenText = "Some(",
				CloseText = ")",
				AlternateText = "None",
				Context = FormattingContextKind.FormatterWrite,
			};
		}

		// (4) Canonical Result<T, E> composite.
		if (valueType is UnionTypeSymbol resultUnion
			&& (resultUnion.Name == "Result" || resultUnion.Name.StartsWith("Result<", StringComparison.Ordinal))
			&& resultUnion.FindField("Ok") is { } okVariant
			&& resultUnion.FindField("Err") is { } errVariant)
		{
			var successOperation = TryBind(okVariant.Type, scope);
			var errorOperation = TryBind(errVariant.Type, scope);
			if (successOperation is null || errorOperation is null)
				return null;

			return new ResolvedFormattingOperation
			{
				ValueType = valueType,
				Kind = FormattingOperationKind.Result,
				SuccessPayload = successOperation,
				ErrorPayload = errorOperation,
				OpenText = "Ok(",
				AlternateOpenText = "Err(",
				CloseText = ")",
				Context = FormattingContextKind.FormatterWrite,
			};
		}

		// (5) Primitive/string exact handling.
		if (PrimitiveNames.Contains(valueType.Name))
			return Present(valueType, FormattingOperationKind.Primitive, null);

		if (valueType.Name == "string")
			return Present(valueType, FormattingOperationKind.String, null);

		return null;
	}

	/// <summary>
	/// Reports the dedicated 'not formattable' diagnostic.
	/// </summary>
	public void ReportTypeIsNotFormattable(CallExpressionSyntax call, TypeSymbol valueType, TextSpan span)
	{
		ReportNotFormattable(span, valueType.Name, call.FunctionName);
	}

	private static ResolvedFormattingOperation Present(TypeSymbol valueType, FormattingOperationKind kind, FunctionSymbol? target)
	{
		return new()
		{
			ValueType = valueType,
			Kind = kind,
			TargetMethod = target,
			Context = FormattingContextKind.FormatterWrite,
		};
	}

	private void ReportNotFormattable(TextSpan span, string typeName, string? callee)
	{
		var fileContext = context.CurrentUnit is not null
			&& context.FileContexts.TryGetValue(context.CurrentUnit, out var named)
				? named
				: context.FileContexts.Values.FirstOrDefault();
		if (fileContext is null)
			return;

		var callNote = callee is null ? "" : $" in '{callee}'";
		context.Diagnostics.Report(
			fileContext,
			span,
			$"Type '{typeName}' is not formattable{callNote}; it must conform to IFormattable or IStringable or be a supported primitive, string, Option, or Result.",
			DiagnosticIds.TypeIsNotFormattable);
	}

	/// <summary>
	/// Finds the concrete conformance member for a value type that conforms to the named interface,
	/// matching the frozen member signature (<c>Format(refvar Formatter)</c> /
	/// <c>ToString()</c>). Generic instantiations fall back to the uninstantiated base name so
	/// explicit <c>Option&lt;T&gt;</c>/<c>Result&lt;T,E&gt;</c> conformance stays reachable.
	/// </summary>
	private FunctionSymbol? FindConformanceMember(
		TypeSymbol valueType,
		SymbolTable scope,
		string interfaceName,
		string memberName,
		Func<FunctionSymbol, bool> signatureMatches)
	{
		_ = scope;
		if (!ConformedInterfaces(valueType).Contains(interfaceName))
			return null;

		foreach (var (_, function) in context.GetExtensionMethodCandidates(valueType, context.CurrentUnit, memberName))
		{
			if (signatureMatches(function))
				return function;
		}

		var baseName = BaseTypeName(valueType.Name);
		if (baseName == valueType.Name)
			return null;

		foreach (var (_, function) in context.GetExtensionMethodCandidates(new TypeSymbol(baseName), context.CurrentUnit, memberName))
		{
			if (signatureMatches(function))
				return function;
		}

		return null;
	}

	/// <summary>
	/// <c>void Format(refvar Formatter formatter)</c>.
	/// </summary>
	private static bool IsFormatSignature(FunctionSymbol function)
		=> function.Parameters.Count == 2
			&& function.Parameters[0].Name == "this"
			&& function.Parameters[1].Type is PointerTypeSymbol formatter
			&& formatter.ReferencedType.Name == "Formatter";

	/// <summary>
	/// <c>string ToString()</c> (extension 'this' parameter only).
	/// </summary>
	private static bool IsToStringSignature(FunctionSymbol function)
		=> function.Parameters.Count == 1 && function.Parameters[0].Name == "this";

	/// <summary>
	/// Interfaces a type explicitly conforms to, considering instantiated (<c>Option&lt;int&gt;</c>)
	/// and base (<c>Option</c>) conformance table keys.
	/// </summary>
	private IReadOnlyCollection<string> ConformedInterfaces(TypeSymbol type)
	{
		if (context.Conformance.TryGetValue(type.Name, out var direct))
			return direct;

		var baseName = BaseTypeName(type.Name);
		if (baseName != type.Name && context.Conformance.TryGetValue(baseName, out var viaBase))
			return viaBase;

		return [];
	}

	private static string BaseTypeName(string name)
	{
		var typeArgumentStart = name.IndexOf('<', StringComparison.Ordinal);
		return typeArgumentStart > 0 ? name[..typeArgumentStart] : name;
	}

	/// <summary>
	/// Index of the last top-level '.', skipping dots inside generic brackets.
	/// </summary>
	private static int LastTopLevelDotIndex(string name)
	{
		var bracketDepth = 0;
		for (var i = name.Length - 1; i >= 0; i--)
		{
			switch (name[i])
			{
				case '>' or ']':
					bracketDepth++;
					break;
				case '<' or '[':
					if (bracketDepth > 0)
						bracketDepth--;
					break;
				case '.' when bracketDepth == 0:
					return i;
			}
		}

		return -1;
	}
}
