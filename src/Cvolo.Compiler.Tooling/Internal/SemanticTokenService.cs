using Cvolo.Analysis.Completion;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using Cvolo.Core.AST.Expressions;

namespace Cvolo.Compiler.Tooling.Internal;

/// <summary>
/// Computes semantic tokens for one document from the compiler's own binding: every declaration
/// name and resolvable identifier/member/call occurrence is classified with the LSP-4 resolver, so
/// the result is semantic (never lexical) and snapshot-pinned. Unresolved occurrences are omitted.
/// </summary>
internal static class SemanticTokenService
{
	internal static IReadOnlyList<SemanticTokenInfo> Compute(ProjectSnapshot snapshot, DocumentSnapshot document)
	{
		var analysis = snapshot.GetAnalysis();
		if (analysis.BinderContext is null
			|| !analysis.UnitsByDocument.TryGetValue(document.Id, out var unit)
			|| unit is null)
		{
			return [];
		}

		var source = document.Text.ToString();
		var tokens = new List<SemanticTokenInfo>();
		var seen = new HashSet<(int Start, int Length)>();

		lock (analysis.BinderContext)
		{
			foreach (var node in Descendants(unit))
			{
				if (TryAddDirectToken(node, source, tokens, seen))
					continue;

				foreach (var position in CandidatePositions(node, source))
				{
					if (position < 0)
						continue;

					var resolved = CompletionQuery.ResolveSymbol(analysis.BinderContext, unit, position);
					if (resolved is null)
						continue;

					var span = new TextSpan(resolved.SubjectSpan.Start, resolved.SubjectSpan.Length);
					if (span.Length <= 0 || !seen.Add((span.Start, span.Length)))
						continue;

					tokens.Add(new SemanticTokenInfo(span, NavigationIndex.MapKind(resolved.Kind), ModifiersFor(node, source, span)));
				}
			}
		}

		return [.. tokens.OrderBy(token => token.Span.Start).ThenBy(token => token.Span.Length)];
	}

	private static bool TryAddDirectToken(SyntaxNode node, string source, List<SemanticTokenInfo> tokens, HashSet<(int Start, int Length)> seen)
	{
		switch (node)
		{
			case NullLiteralExpressionSyntax nullLiteral:
				AddDirectToken(tokens, seen, nullLiteral.Span, ToolingSymbolKind.Keyword);
				return true;
			case IdentifierExpressionSyntax identifier when identifier.Name == "this":
				AddDirectToken(tokens, seen, identifier.Span, ToolingSymbolKind.Keyword);
				return true;
			case FunctionDeclarationSyntax function when function.Receiver != ReceiverContract.None:
				TryAddReceiverToken(function, source, tokens, seen);
				return false;
			case BinaryExpressionSyntax binary:
				TryAddOperatorToken(binary, source, tokens, seen);
				return false;
		}

		return false;
	}

	private static void TryAddReceiverToken(FunctionDeclarationSyntax function, string source, List<SemanticTokenInfo> tokens, HashSet<(int Start, int Length)> seen)
	{
		if (function.Body is not { } body)
			return;

		var start = function.Span.Start;
		var limit = body.Span.Start;
		if (start < 0 || limit > source.Length || start > limit)
			return;

		var index = source.IndexOf("this", start, limit - start, StringComparison.Ordinal);
		if (index < 0)
			return;

		AddDirectToken(tokens, seen, new Cvolo.Core.Diagnostics.TextSpan(index, "this".Length), ToolingSymbolKind.Keyword);
	}

	private static void TryAddOperatorToken(BinaryExpressionSyntax binary, string source, List<SemanticTokenInfo> tokens, HashSet<(int Start, int Length)> seen)
	{
		var start = binary.Left.Span.End;
		var end = binary.Right.Span.Start;
		if (binary.Operator.Length == 0 || start < 0 || end > source.Length || start > end)
			return;

		var index = source.IndexOf(binary.Operator, start, end - start, StringComparison.Ordinal);
		if (index < 0)
			return;

		AddDirectToken(tokens, seen, new Cvolo.Core.Diagnostics.TextSpan(index, binary.Operator.Length), ToolingSymbolKind.Operator);
	}

	private static void AddDirectToken(List<SemanticTokenInfo> tokens, HashSet<(int Start, int Length)> seen, Cvolo.Core.Diagnostics.TextSpan span, ToolingSymbolKind kind)
	{
		if (span.Start < 0 || span.Length <= 0)
			return;

		if (!seen.Add((span.Start, span.Length)))
			return;

		tokens.Add(new SemanticTokenInfo(new TextSpan(span.Start, span.Length), kind, SemanticTokenModifiers.None));
	}

	private static SemanticTokenModifiers ModifiersFor(SyntaxNode node, string source, TextSpan span)
	{
		var modifiers = IsDeclarationName(node, source, span) ? SemanticTokenModifiers.Declaration : SemanticTokenModifiers.None;

		switch (node)
		{
			case VariableDeclarationSyntax variable when !variable.IsMutable:
				modifiers |= SemanticTokenModifiers.Readonly;
				break;
			case GlobalVariableDeclarationSyntax global:
				modifiers |= SemanticTokenModifiers.Static;
				if (!global.IsMutable)
					modifiers |= SemanticTokenModifiers.Readonly;
				break;
		}

		return modifiers;
	}

	private static bool IsDeclarationName(SyntaxNode node, string source, TextSpan span)
	{
		var name = node switch
		{
			FunctionDeclarationSyntax function => function.Name,
			ConstructorDeclarationSyntax constructor => constructor.StructName,
			DestructorDeclarationSyntax destructor => destructor.StructName,
			StructDeclarationSyntax structDeclaration => structDeclaration.Name,
			UnionDeclarationSyntax unionDeclaration => unionDeclaration.Name,
			EnumDeclarationSyntax enumDeclaration => enumDeclaration.Name,
			InterfaceDeclarationSyntax interfaceDeclaration => interfaceDeclaration.Name,
			ProtocolDeclarationSyntax protocolDeclaration => protocolDeclaration.Name,
			DelegateDeclarationSyntax delegateDeclaration => delegateDeclaration.Name,
			TypeAliasDeclarationSyntax typeAlias => typeAlias.Name,
			VariableDeclarationSyntax variable => variable.Name,
			ParameterSyntax parameter => parameter.Name,
			GlobalVariableDeclarationSyntax global => global.Name,
			StructFieldSyntax structField => structField.Name,
			UnionFieldSyntax unionField => unionField.Name,
			EnumVariantDeclarationSyntax enumVariant => enumVariant.Name,
			_ => null,
		};

		return name is not null
			&& TryIndexOf(source, node.Span, name, out var position)
			&& span.Start == position
			&& span.Length == name.Length;
	}

	private static IEnumerable<int> CandidatePositions(SyntaxNode node, string source)
	{
		switch (node)
		{
			case IdentifierExpressionSyntax identifier:
				yield return identifier.Span.Start;
				break;
			case MemberAccessExpressionSyntax memberAccess:
				yield return memberAccess.Span.End - memberAccess.MemberName.Length;
				break;
			case CallExpressionSyntax call:
				yield return call.Span.Start + call.FunctionName.Length - Leaf(call.FunctionName).Length;
				break;
			case FunctionDeclarationSyntax function:
				yield return function.NameSpan.Start;
				break;
			case ConstructorDeclarationSyntax constructor:
				yield return constructor.NameSpan.Start;
				break;
			case VariableDeclarationSyntax variable:
				if (variable.Type is not null && TryIndexOf(source, variable.Span, variable.Type, out var variableType))
					yield return variableType;
				if (TryIndexOf(source, variable.Span, variable.Name, out var variableName))
					yield return variableName;
				break;
			case ParameterSyntax parameter:
				if (TryIndexOf(source, parameter.Span, parameter.Type, out var parameterType))
					yield return parameterType;
				if (TryIndexOf(source, parameter.Span, parameter.Name, out var parameterName))
					yield return parameterName;
				break;
			case GlobalVariableDeclarationSyntax global:
				if (TryIndexOf(source, global.Span, global.Type, out var globalType))
					yield return globalType;
				if (TryIndexOf(source, global.Span, global.Name, out var globalName))
					yield return globalName;
				break;
			case StructDeclarationSyntax structDeclaration:
				if (TryIndexOf(source, structDeclaration.Span, structDeclaration.Name, out var structName))
					yield return structName;
				break;
			case UnionDeclarationSyntax unionDeclaration:
				if (TryIndexOf(source, unionDeclaration.Span, unionDeclaration.Name, out var unionName))
					yield return unionName;
				break;
			case EnumDeclarationSyntax enumDeclaration:
				if (TryIndexOf(source, enumDeclaration.Span, enumDeclaration.Name, out var enumName))
					yield return enumName;
				break;
			case InterfaceDeclarationSyntax interfaceDeclaration:
				if (TryIndexOf(source, interfaceDeclaration.Span, interfaceDeclaration.Name, out var interfaceName))
					yield return interfaceName;
				break;
			case ProtocolDeclarationSyntax protocolDeclaration:
				if (TryIndexOf(source, protocolDeclaration.Span, protocolDeclaration.Name, out var protocolName))
					yield return protocolName;
				break;
			case DelegateDeclarationSyntax delegateDeclaration:
				if (TryIndexOf(source, delegateDeclaration.Span, delegateDeclaration.Name, out var delegateName))
					yield return delegateName;
				break;
			case TypeAliasDeclarationSyntax typeAlias:
				if (TryIndexOf(source, typeAlias.Span, typeAlias.Name, out var aliasName))
					yield return aliasName;
				break;
			case StructFieldSyntax structField:
				if (TryIndexOf(source, structField.Span, structField.Type, out var structFieldType))
					yield return structFieldType;
				if (TryIndexOf(source, structField.Span, structField.Name, out var structFieldName))
					yield return structFieldName;
				break;
			case UnionFieldSyntax unionField:
				if (TryIndexOf(source, unionField.Span, unionField.Type, out var unionFieldType))
					yield return unionFieldType;
				if (TryIndexOf(source, unionField.Span, unionField.Name, out var unionFieldName))
					yield return unionFieldName;
				break;
			case EnumVariantDeclarationSyntax enumVariant:
				if (TryIndexOf(source, enumVariant.Span, enumVariant.Name, out var enumVariantName))
					yield return enumVariantName;
				break;
		}
	}

	private static bool TryIndexOf(string source, Cvolo.Core.Diagnostics.TextSpan span, string name, out int position)
	{
		if (name.Length == 0 || span.Start < 0 || span.End > source.Length)
		{
			position = 0;
			return false;
		}

		var index = source.IndexOf(name, span.Start, StringComparison.Ordinal);
		if (index < 0 || index >= span.End)
		{
			position = 0;
			return false;
		}

		position = index;
		return true;
	}

	private static string Leaf(string name)
	{
		var dot = name.LastIndexOf('.');
		return dot < 0 ? name : name[(dot + 1)..];
	}

	private static IEnumerable<SyntaxNode> Descendants(SyntaxNode root)
	{
		yield return root;
		foreach (var child in root.GetChildren())
		{
			foreach (var node in Descendants(child))
				yield return node;
		}
	}
}
