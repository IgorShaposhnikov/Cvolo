using System.Text;
using Cvolo.Analysis;
using Cvolo.Analysis.Symbols;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Compiler.Tooling.SignatureHelp;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using Cvolo.Core.AST.Expressions;

namespace Cvolo.Compiler.Tooling.Internal;

/// <summary>
/// Computes compiler-backed signature help for ordinary calls and nominal delegate invocations.
/// The resolved function/delegate selected by semantic analysis is used directly, and the
/// resolved callable's overload group (when it is bound to an overloaded function name) is
/// presented in full, so overload and native-delegate identity stay aligned with compilation
/// semantics. Documentation stays out of the display label and is carried separately for the
/// signature and its parameters, matching ordinary signature-help presentation.
/// </summary>
internal static class SignatureHelpService
{
	internal static SignatureHelpInfo? Compute(ProjectSnapshot snapshot, DocumentSnapshot document, int position)
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

		var source = document.Text.ToString();

		lock (context)
		{
			if (context.ResolvedCalls.TryGetValue(call, out var function))
				return Build(function, context, call, source, position);

			if (context.ResolvedDelegateCalls.TryGetValue(call, out var delegateType))
				return Build(delegateType, call, source, position);

			return RecoverBySyntax(context, unit, call, source, position);
		}
	}

	/// <summary>
	/// Recovers signature help for incomplete/nested call sites that the binder did not retain in
	/// <see cref="BindingContext.ResolvedCalls"/>. The compiler's overload table remains the source
	/// of callable identity; the language-server layer never reconstructs overloads from text.
	/// </summary>
	private static SignatureHelpInfo? RecoverBySyntax(BindingContext context, CompilationUnitSyntax unit, CallExpressionSyntax call, string source, int position)
	{
		if (string.IsNullOrEmpty(call.FunctionName))
			return null;

		var namespaceName = unit.NamespaceDeclaration?.Name;
		var key = context.GetMangledName(call.FunctionName, namespaceName);
		if (!context.OverloadedFunctions.TryGetValue(key, out var group) || group.Count == 0)
			return null;

		var signatures = new SignatureCandidateInfo[group.Count];
		var exactArity = new List<int>();
		for (var index = 0; index < group.Count; index++)
		{
			signatures[index] = BuildStandardSignature(context, group[index]);
			var parameterCount = group[index].Parameters.Count(parameter => parameter.Name != "this");
			if (parameterCount == call.Arguments.Count)
				exactArity.Add(index);
		}

		var resolvedIndex = exactArity.Count == 1 ? exactArity[0] : 0;
		var activeCount = group[resolvedIndex].Parameters.Count(parameter => parameter.Name != "this");
		var activeParameter = activeCount == 0
			? null
			: (int?)ActiveParameter(call, source, position, activeCount);

		signatures[resolvedIndex] = signatures[resolvedIndex] with { ActiveParameter = activeParameter };
		return new SignatureHelpInfo(signatures, resolvedIndex);
	}

	private static SignatureHelpInfo Build(FunctionSymbol function, BindingContext context, CallExpressionSyntax call, string source, int position)
	{
		var (group, resolvedIndex) = ResolveOverloadGroup(context, function);
		var activeParameterCount = function.Parameters.Count(parameter => parameter.Name != "this");
		var activeParameter = activeParameterCount == 0
			? null
			: (int?)ActiveParameter(call, source, position, activeParameterCount);

		var signatures = new SignatureCandidateInfo[group.Count];
		for (var index = 0; index < group.Count; index++)
		{
			var signature = BuildStandardSignature(context, group[index]);
			signatures[index] = index == resolvedIndex
				? signature with { ActiveParameter = activeParameter }
				: signature;
		}

		return new SignatureHelpInfo(signatures, resolvedIndex);
	}

	private static SignatureHelpInfo Build(DelegateTypeSymbol delegateType, CallExpressionSyntax call, string source, int position)
	{
		var parameters = delegateType.Parameters
			.Select(parameter => $"{parameter.Type.Name} {parameter.Name}")
			.ToArray();
		var nativePrefix = delegateType.IsNative
			? $"unsafe \"{delegateType.CallingConvention ?? "C"}\" "
			: string.Empty;

		var builder = new StringBuilder(nativePrefix).Append("delegate ").Append(delegateType.ReturnType.Name)
			.Append(' ').Append(Leaf(delegateType.Name)).Append('(');
		var infos = new SignatureParameterInfo[parameters.Length];
		for (var index = 0; index < parameters.Length; index++)
		{
			if (index > 0)
				builder.Append(", ");
			var start = builder.Length;
			builder.Append(parameters[index]);
			infos[index] = new SignatureParameterInfo(new SignatureLabelSpan(start, parameters[index].Length));
		}

		builder.Append(')');
		var activeParameter = parameters.Length == 0
			? null
			: (int?)ActiveParameter(call, source, position, parameters.Length);

		return new SignatureHelpInfo(
			[new SignatureCandidateInfo(builder.ToString(), null, infos, activeParameter)],
			0);
	}

	private static SignatureCandidateInfo BuildStandardSignature(BindingContext context, FunctionSymbol function)
	{
		var parameters = function.Parameters
			.Where(parameter => parameter.Name != "this")
			.ToArray();

		var declaration = FindDeclaration(context, function);
		var documentation = declaration is { } found
			&& context.FileContexts.TryGetValue(found.Unit, out var fileContext)
			? ExtractDocumentation(fileContext.Source, found.Node.Span.Start)
			: null;

		IReadOnlyList<ParameterSyntax> syntaxParameters = declaration?.Node switch
		{
			FunctionDeclarationSyntax functionDeclaration => functionDeclaration.Parameters,
			ConstructorDeclarationSyntax constructorDeclaration => constructorDeclaration.Parameters,
			_ => [],
		};

		var builder = new StringBuilder(function.ReturnType.Name).Append(' ').Append(SourceName(context, function)).Append('(');
		var infos = new SignatureParameterInfo[parameters.Length];
		for (var index = 0; index < parameters.Length; index++)
		{
			if (index > 0)
				builder.Append(", ");

			var start = builder.Length;
			var text = $"{parameters[index].Type.Name} {parameters[index].Name}";
			builder.Append(text);

			string? parameterDocumentation = null;
			if (declaration is { } parameterOwner
				&& index < syntaxParameters.Count
				&& context.FileContexts.TryGetValue(parameterOwner.Unit, out var parameterFileContext))
			{
				parameterDocumentation = ExtractDocumentation(parameterFileContext.Source, syntaxParameters[index].Span.Start);
			}

			infos[index] = new SignatureParameterInfo(
				new SignatureLabelSpan(start, text.Length),
				parameterDocumentation);
		}

		builder.Append(')');
		return new SignatureCandidateInfo(builder.ToString(), documentation, infos, null);
	}

	/// <summary>
	/// Locates the source declaration corresponding to a bound function. This is semantic matching:
	/// the same binder mangling/signature rules are re-used, so documentation is never attached by a
	/// same-spelling textual search.
	/// </summary>
	private static (CompilationUnitSyntax Unit, SyntaxNode Node)? FindDeclaration(BindingContext context, FunctionSymbol function)
	{
		foreach (var unit in context.FileContexts.Keys)
		{
			var ns = unit.NamespaceDeclaration?.Name;
			if (unit.NamespaceDeclaration is { } namespaceDeclaration)
			{
				foreach (var member in namespaceDeclaration.Members)
				{
					if (MatchNode(context, member, ns, function) is { } node)
						return (unit, node);
				}
			}

			foreach (var member in unit.Members)
			{
				if (MatchNode(context, member, ns, function) is { } node)
					return (unit, node);
			}
		}

		return null;
	}

	private static SyntaxNode? MatchNode(BindingContext context, SyntaxNode node, string? ns, FunctionSymbol function)
	{
		switch (node)
		{
			case FunctionDeclarationSyntax func:
			{
				var baseName = func.Name is "main" or "Main" ? "main" : context.GetMangledName(func.Name, ns);
				return TryResolveParameters(context, func.Parameters) is { } types
					&& string.Equals(context.GetOverloadedMangledName(baseName, types), function.Name, StringComparison.Ordinal)
					? node
					: null;
			}
			case ExtensionDeclarationSyntax extension:
			{
				var extendedType = context.ResolveType(context.NormalizeGenericName(extension.ExtendedTypeName));
				foreach (var method in extension.Methods)
				{
					var baseName = context.GetMangledName($"{extension.ExtendedTypeName}.{method.Name}", ns);
					if (extendedType is not null && TryResolveParameters(context, method.Parameters) is { } types)
					{
						var withReceiver = new List<TypeSymbol>(types.Count + 1)
						{
							new PointerTypeSymbol(extendedType, isMutable: false),
						};
						withReceiver.AddRange(types);
						if (string.Equals(context.GetOverloadedMangledName(baseName, withReceiver), function.Name, StringComparison.Ordinal))
							return method;
					}
				}

				foreach (var constructor in extension.Constructors)
				{
					var baseName = context.GetMangledName(extension.ExtendedTypeName, ns);
					if (TryResolveParameters(context, constructor.Parameters) is { } types
						&& string.Equals(context.GetOverloadedMangledName(baseName, types), function.Name, StringComparison.Ordinal))
					{
						return constructor;
					}
				}

				return null;
			}
			default:
				return null;
		}
	}

	private static IReadOnlyList<TypeSymbol>? TryResolveParameters(BindingContext context, IReadOnlyList<ParameterSyntax> parameters)
	{
		var types = new List<TypeSymbol>(parameters.Count);
		foreach (var parameter in parameters)
		{
			var type = context.ResolveType(context.NormalizeGenericName(parameter.Type));
			if (type is null)
				return null;

			types.Add(type);
		}

		return types;
	}

	/// <summary>
	/// Reads only Cvolo documentation comments (<c>///</c>). Ordinary <c>//</c> comments are never
	/// surfaced as API documentation, including when they immediately precede a declaration.
	/// </summary>
	private static string? ExtractDocumentation(string source, int position)
	{
		if (position <= 0 || position > source.Length)
			return null;

		// AST declaration/parameter spans start at the first token rather than at indentation.
		// Normalize to the physical line start before scanning the immediately preceding /// block.
		var scan = position;
		while (scan > 0 && source[scan - 1] is not '\r' and not '\n')
			scan--;

		var lines = new Stack<string>();
		while (scan > 0)
		{
			var lineEnd = scan;
			while (lineEnd > 0 && source[lineEnd - 1] is '\r' or '\n')
				lineEnd--;

			var lineStart = lineEnd;
			while (lineStart > 0 && source[lineStart - 1] is not '\r' and not '\n')
				lineStart--;

			var line = source[lineStart..lineEnd].Trim();
			if (line.Length == 0 || !line.StartsWith("///", StringComparison.Ordinal))
				break;

			lines.Push(line[3..].Trim());
			scan = lineStart;
		}

		return lines.Count == 0 ? null : string.Join("\n", lines);
	}

	/// <summary>
	/// Re-derives the plain (un-overloaded) binder key of the resolved function and looks it up in the
	/// overload table. Extension methods and other callables whose names are otherwise not present in
	/// the table collapse to a single-signature result.
	/// </summary>
	private static (IReadOnlyList<FunctionSymbol> Group, int ResolvedIndex) ResolveOverloadGroup(BindingContext context, FunctionSymbol resolved)
	{
		if (KeyOf(context, resolved) is { } key && context.OverloadedFunctions.TryGetValue(key, out var group))
		{
			for (var index = 0; index < group.Count; index++)
			{
				if (ReferenceEquals(group[index], resolved))
					return (group, index);
			}
		}

		return ([resolved], 0);
	}

	private static string? KeyOf(BindingContext context, FunctionSymbol resolved)
	{
		var name = resolved.Name;
		var leaf = Leaf(name);
		if (leaf.Contains('.'))
			return null;

		var sourceName = SourceName(context, resolved);
		var dot = name.LastIndexOf('.');
		return dot < 0 ? sourceName : $"{name[..dot]}.{sourceName}";
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

	private static string SourceName(BindingContext context, FunctionSymbol function)
	{
		// Prefer the declaration spelling so receiver/type mangling never leaks into editor labels.
		if (FindDeclaration(context, function) is { Node: FunctionDeclarationSyntax declaration })
			return declaration.Name;

		if (FindDeclaration(context, function) is { Node: ConstructorDeclarationSyntax constructor })
			return constructor.StructName;

		var name = Leaf(function.Name);
		var nonThis = function.Parameters.Where(parameter => parameter.Name != "this").ToList();
		var suffix = string.Concat(nonThis.Select(parameter => "_" + MangleTypeName(parameter.Type.Name)));
		if (suffix.Length == 0)
			suffix = "_void";

		return name.EndsWith(suffix, StringComparison.Ordinal)
			? name[..^suffix.Length]
			: name;
	}

	private static string MangleTypeName(string name) =>
		name.Replace("<", "_")
			.Replace(">", "_")
			.Replace("[", "Arr")
			.Replace("]", "")
			.Replace(" ", "")
			.Replace(",", "_")
			.Replace(".", "_")
			.Replace("*", "Ptr");
}
