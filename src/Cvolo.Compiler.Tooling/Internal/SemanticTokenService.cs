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
				if (!TryCandidatePosition(node, source, out var position) || position < 0)
					continue;

				var resolved = CompletionQuery.ResolveSymbol(analysis.BinderContext, unit, position);
				if (resolved is null)
					continue;

				var span = new TextSpan(resolved.SubjectSpan.Start, resolved.SubjectSpan.Length);
				if (span.Length <= 0 || !seen.Add((span.Start, span.Length)))
					continue;

				tokens.Add(new SemanticTokenInfo(span, NavigationIndex.MapKind(resolved.Kind), ModifiersFor(node)));
			}
		}

		return [.. tokens.OrderBy(token => token.Span.Start).ThenBy(token => token.Span.Length)];
	}

	private static SemanticTokenModifiers ModifiersFor(SyntaxNode node)
	{
		var modifiers = IsDeclarationNode(node) ? SemanticTokenModifiers.Declaration : SemanticTokenModifiers.None;

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

	private static bool IsDeclarationNode(SyntaxNode node) => node is
		FunctionDeclarationSyntax or
		ConstructorDeclarationSyntax or
		DestructorDeclarationSyntax or
		StructDeclarationSyntax or
		UnionDeclarationSyntax or
		EnumDeclarationSyntax or
		InterfaceDeclarationSyntax or
		ProtocolDeclarationSyntax or
		DelegateDeclarationSyntax or
		TypeAliasDeclarationSyntax or
		VariableDeclarationSyntax or
		ParameterSyntax or
		GlobalVariableDeclarationSyntax or
		StructFieldSyntax or
		UnionFieldSyntax or
		EnumVariantDeclarationSyntax;

	private static bool TryCandidatePosition(SyntaxNode node, string source, out int position)
	{
		switch (node)
		{
			case IdentifierExpressionSyntax identifier:
				position = identifier.Span.Start;
				return true;
			case MemberAccessExpressionSyntax memberAccess:
				position = memberAccess.Span.End - memberAccess.MemberName.Length;
				return true;
			case CallExpressionSyntax call:
				position = call.Span.Start + call.FunctionName.Length - Leaf(call.FunctionName).Length;
				return true;
			case FunctionDeclarationSyntax function:
				position = function.NameSpan.Start;
				return true;
			case ConstructorDeclarationSyntax constructor:
				position = constructor.NameSpan.Start;
				return true;
			case VariableDeclarationSyntax variable:
				return TryIndexOf(source, variable.Span, variable.Name, out position);
			case ParameterSyntax parameter:
				return TryIndexOf(source, parameter.Span, parameter.Name, out position);
			case GlobalVariableDeclarationSyntax global:
				return TryIndexOf(source, global.Span, global.Name, out position);
			case StructDeclarationSyntax structDeclaration:
				return TryIndexOf(source, structDeclaration.Span, structDeclaration.Name, out position);
			case UnionDeclarationSyntax unionDeclaration:
				return TryIndexOf(source, unionDeclaration.Span, unionDeclaration.Name, out position);
			case EnumDeclarationSyntax enumDeclaration:
				return TryIndexOf(source, enumDeclaration.Span, enumDeclaration.Name, out position);
			case InterfaceDeclarationSyntax interfaceDeclaration:
				return TryIndexOf(source, interfaceDeclaration.Span, interfaceDeclaration.Name, out position);
			case ProtocolDeclarationSyntax protocolDeclaration:
				return TryIndexOf(source, protocolDeclaration.Span, protocolDeclaration.Name, out position);
			case DelegateDeclarationSyntax delegateDeclaration:
				return TryIndexOf(source, delegateDeclaration.Span, delegateDeclaration.Name, out position);
			case TypeAliasDeclarationSyntax typeAlias:
				return TryIndexOf(source, typeAlias.Span, typeAlias.Name, out position);
			case StructFieldSyntax structField:
				return TryIndexOf(source, structField.Span, structField.Name, out position);
			case UnionFieldSyntax unionField:
				return TryIndexOf(source, unionField.Span, unionField.Name, out position);
			case EnumVariantDeclarationSyntax enumVariant:
				return TryIndexOf(source, enumVariant.Span, enumVariant.Name, out position);
			default:
				position = 0;
				return false;
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
