using Cvolo.Analysis.Symbols;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Compiler.Tooling.SignatureHelp;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Expressions;

namespace Cvolo.Compiler.Tooling.Internal;

/// <summary>
/// Computes compiler-backed signature help for ordinary calls and nominal delegate invocations.
/// The resolved function/delegate selected by semantic analysis is used directly, so overload and
/// native-delegate identity stay aligned with compilation semantics.
/// </summary>
internal static class SignatureHelpService
{
	internal static SignatureHelpResult? Compute(ProjectSnapshot snapshot, DocumentSnapshot document, int position)
	{
		var analysis = snapshot.GetAnalysis();
		if (analysis.BinderContext is not { } context
			|| !analysis.UnitsByDocument.TryGetValue(document.Id, out var unit)
			|| unit is null)
		{
			return null;
		}

		var call = FindInnermostCall(unit, position);
		if (call is null)
			return null;

		lock (context)
		{
			if (context.ResolvedCalls.TryGetValue(call, out var function))
				return Build(function, call, document.Text.ToString(), position);

			if (context.ResolvedDelegateCalls.TryGetValue(call, out var delegateType))
				return Build(delegateType, call, document.Text.ToString(), position);
		}

		return null;
	}

	private static SignatureHelpResult Build(FunctionSymbol function, CallExpressionSyntax call, string source, int position)
	{
		var parameters = function.Parameters
			.Where(parameter => parameter.Name != "this")
			.Select(parameter => new SignatureHelpParameter($"{parameter.Type.Name} {parameter.Name}"))
			.ToArray();
		var label = $"{function.ReturnType.Name} {SourceName(function)}({string.Join(", ", parameters.Select(p => p.Label))})";
		return new SignatureHelpResult([new SignatureHelpItem(label, parameters)], 0, ActiveParameter(call, source, position, parameters.Length));
	}

	private static SignatureHelpResult Build(DelegateTypeSymbol delegateType, CallExpressionSyntax call, string source, int position)
	{
		var parameters = delegateType.Parameters
			.Select(parameter => new SignatureHelpParameter($"{parameter.Type.Name} {parameter.Name}"))
			.ToArray();
		var nativePrefix = delegateType.IsNative
			? $"unsafe \"{delegateType.CallingConvention ?? "C"}\" "
			: string.Empty;
		var label = $"{nativePrefix}delegate {delegateType.ReturnType.Name} {Leaf(delegateType.Name)}({string.Join(", ", parameters.Select(p => p.Label))})";
		return new SignatureHelpResult([new SignatureHelpItem(label, parameters)], 0, ActiveParameter(call, source, position, parameters.Length));
	}

	private static int ActiveParameter(CallExpressionSyntax call, string source, int position, int parameterCount)
	{
		if (parameterCount == 0)
			return 0;

		var start = Math.Clamp(call.ArgumentListSpan.Start + 1, 0, source.Length);
		var end = Math.Clamp(position, start, Math.Min(call.ArgumentListSpan.End, source.Length));
		var depth = 0;
		var active = 0;
		var inString = false;
		var inChar = false;
		var escaped = false;

		for (var i = start; i < end; i++)
		{
			var ch = source[i];
			if (escaped)
			{
				escaped = false;
				continue;
			}

			if ((inString || inChar) && ch == '\\')
			{
				escaped = true;
				continue;
			}

			if (!inChar && ch == '"')
			{
				inString = !inString;
				continue;
			}
			if (!inString && ch == '\'')
			{
				inChar = !inChar;
				continue;
			}
			if (inString || inChar)
				continue;

			switch (ch)
			{
				case '(':
				case '[':
				case '{':
					depth++;
					break;
				case ')':
				case ']':
				case '}':
					if (depth > 0) depth--;
					break;
				case ',' when depth == 0:
					active++;
					break;
			}
		}

		return Math.Min(active, parameterCount - 1);
	}

	private static CallExpressionSyntax? FindInnermostCall(SyntaxNode node, int position)
	{
		CallExpressionSyntax? best = null;
		foreach (var candidate in Descendants(node).OfType<CallExpressionSyntax>())
		{
			var span = candidate.ArgumentListSpan;
			if (position < span.Start || position > span.End)
				continue;
			if (best is null || span.Length < best.ArgumentListSpan.Length)
				best = candidate;
		}
		return best;
	}

	private static IEnumerable<SyntaxNode> Descendants(SyntaxNode root)
	{
		yield return root;
		foreach (var child in root.GetChildren())
		{
			foreach (var descendant in Descendants(child))
				yield return descendant;
		}
	}

	private static string Leaf(string name)
	{
		var dot = name.LastIndexOf('.');
		return dot < 0 ? name : name[(dot + 1)..];
	}

	private static string SourceName(FunctionSymbol function)
	{
		var name = Leaf(function.Name);
		var suffix = string.Concat(function.Parameters
			.Where(parameter => parameter.Name != "this")
			.Select(parameter => "_" + parameter.Type.Name.Replace('.', '_')));

		return suffix.Length > 0 && name.EndsWith(suffix, StringComparison.Ordinal)
			? name[..^suffix.Length]
			: name;
	}
}
