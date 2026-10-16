using Cvolo.Analysis;
using Cvolo.Analysis.Completion;
using Cvolo.Analysis.Semantics;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using Cvolo.Core.AST.Directives;
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

				// A non-dotted call name is not resolved by the symbol resolver (a call has
				// no callee child node), so a name that denotes a type would otherwise fall
				// back to the lexical function colour. Resolve it as a type here.
				if (node is CallExpressionSyntax calleeTypeCall)
					TryAddCalleeTypeToken(analysis.BinderContext, unit, calleeTypeCall, source, tokens, seen);

				// A namespace or namespace-alias receiver in a qualified reference resolves to no
				// value/type symbol, so it would otherwise lose its scope colour. Classify the
				// receiver name directly when it names a declared namespace or alias.
				if (node is MemberAccessExpressionSyntax memberAccess)
					TryAddNamespaceReceiverToken(analysis.BinderContext, unit, memberAccess.Expression, source, tokens, seen);
				else if (node is CallExpressionSyntax receiverCall)
					TryAddNamespaceReceiverToken(analysis.BinderContext, unit, receiverCall, source, tokens, seen);

				foreach (var position in CandidatePositions(node, source))
				{
					if (position < 0)
						continue;

					var resolved = CompletionQuery.ResolveSymbol(analysis.BinderContext, unit, position, includeDocumentationAndType: false);
					if (resolved is null)
						continue;

					var span = new TextSpan(resolved.SubjectSpan.Start, resolved.SubjectSpan.Length);
					if (span.Length <= 0 || !seen.Add((span.Start, span.Length)))
						continue;

					tokens.Add(new SemanticTokenInfo(span, NavigationIndex.MapKind(resolved.Kind), ModifiersFor(node, source, span, resolved)));
				}
			}
		}

		return [.. tokens.OrderBy(token => token.Span.Start).ThenBy(token => token.Span.Length)];
	}

	/// <summary>
	/// Classifies a non-dotted call name that denotes a type. A call node has no callee
	/// child, so the symbol resolver does not resolve <c>WP</c> in <c>WP()</c>; a type name
	/// would otherwise fall back to the lexical function colour.
	/// </summary>
	private static void TryAddCalleeTypeToken(
		BindingContext context,
		CompilationUnitSyntax unit,
		CallExpressionSyntax call,
		string source,
		List<SemanticTokenInfo> tokens,
		HashSet<(int Start, int Length)> seen)
	{
		if (call.FunctionName.Contains('.')
			|| !TryIndexOf(source, call.Span, call.FunctionName, out var nameStart))
		{
			return;
		}

		var type = context.ResolveType(context.GetMangledName(call.FunctionName, unit.NamespaceDeclaration?.Name))
			?? context.ResolveType(call.FunctionName);
		if (type is null)
		{
			return;
		}

		var span = new TextSpan(nameStart, call.FunctionName.Length);
		if (!seen.Add((span.Start, span.Length)))
		{
			return;
		}

		tokens.Add(new SemanticTokenInfo(span, TypeKind(type), SemanticTokenModifiers.None));
	}

	private static ToolingSymbolKind TypeKind(TypeSymbol type) => type switch
	{
		StructTypeSymbol => ToolingSymbolKind.Struct,
		UnionTypeSymbol => ToolingSymbolKind.Union,
		EnumTypeSymbol => ToolingSymbolKind.Enum,
		InterfaceTypeSymbol => ToolingSymbolKind.Interface,
		ProtocolTypeSymbol => ToolingSymbolKind.Protocol,
		DelegateTypeSymbol => ToolingSymbolKind.Delegate,
		TypeParameterSymbol => ToolingSymbolKind.TypeParameter,
		_ => ToolingSymbolKind.OtherType,
	};

	private static bool TryAddDirectToken(SyntaxNode node, string source, List<SemanticTokenInfo> tokens, HashSet<(int Start, int Length)> seen)
	{
		switch (node)
		{
			case BooleanLiteralExpressionSyntax booleanLiteral:
				AddDirectToken(tokens, seen, booleanLiteral.Span, ToolingSymbolKind.Keyword);
				return true;
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
			case CoalesceExpressionSyntax coalesce:
				TryAddCoalesceOperatorToken(coalesce, source, tokens, seen);
				return false;
			case OptionalChainExpressionSyntax optionalChain:
				TryAddOptionalChainOperatorToken(optionalChain, source, tokens, seen);
				return false;
			case OperatorDeclarationSyntax operatorDeclaration:
				// The declared operator token is the declaration's name; the `operator` keyword
				// itself stays unclassified so it renders as ordinary punctuation-adjacent text.
				AddDirectToken(tokens, seen, operatorDeclaration.OperatorSpan, ToolingSymbolKind.Operator);
				return false;
			case SizeofExpressionSyntax:
				AddDirectToken(tokens, seen, new Cvolo.Core.Diagnostics.TextSpan(node.Span.Start, "sizeof".Length), ToolingSymbolKind.Keyword);
				return false;
			case AlignofExpressionSyntax:
				AddDirectToken(tokens, seen, new Cvolo.Core.Diagnostics.TextSpan(node.Span.Start, "alignof".Length), ToolingSymbolKind.Keyword);
				return false;
			case OffsetofExpressionSyntax:
				AddDirectToken(tokens, seen, new Cvolo.Core.Diagnostics.TextSpan(node.Span.Start, "offsetof".Length), ToolingSymbolKind.Keyword);
				return false;
			case DefaultExpressionSyntax:
				// `default(T)` carries the default-value keyword; only the keyword itself is
				// classified, so the parenthesised type argument keeps its ordinary type token.
				AddDirectToken(tokens, seen, new Cvolo.Core.Diagnostics.TextSpan(node.Span.Start, "default".Length), ToolingSymbolKind.Keyword);
				return false;
			case FunctionDeclarationSyntax builtinFunction when builtinFunction.BuiltinSpan is { } builtinFunctionSpan:
				AddDirectToken(tokens, seen, builtinFunctionSpan, ToolingSymbolKind.Keyword);
				return false;
			case StructDeclarationSyntax builtinStruct when builtinStruct.BuiltinSpan is { } builtinStructSpan:
				AddDirectToken(tokens, seen, builtinStructSpan, ToolingSymbolKind.Keyword);
				return false;
			case UnionDeclarationSyntax builtinUnion when builtinUnion.BuiltinSpan is { } builtinUnionSpan:
				AddDirectToken(tokens, seen, builtinUnionSpan, ToolingSymbolKind.Keyword);
				return false;
			case EnumDeclarationSyntax builtinEnum when builtinEnum.BuiltinSpan is { } builtinEnumSpan:
				AddDirectToken(tokens, seen, builtinEnumSpan, ToolingSymbolKind.Keyword);
				return false;
			case ConstructorDeclarationSyntax builtinConstructor when builtinConstructor.BuiltinSpan is { } builtinConstructorSpan:
				AddDirectToken(tokens, seen, builtinConstructorSpan, ToolingSymbolKind.Keyword);
				return false;
			case DestructorDeclarationSyntax builtinDestructor when builtinDestructor.BuiltinSpan is { } builtinDestructorSpan:
				AddDirectToken(tokens, seen, builtinDestructorSpan, ToolingSymbolKind.Keyword);
				return false;
			case UsingDirectiveSyntax { AliasSpan: { } aliasSpan } namespaceAlias:
				// 'using Alias = Namespace.Path;' declares a compile-time namespace prefix, so the
				// alias name is the declaration and gets the alias token kind.
				AddDirectToken(tokens, seen, aliasSpan, ToolingSymbolKind.NamespaceAlias);
				return true;
		}

		return false;
	}

	/// <summary>
	/// Classifies the receiver of a qualified reference when the receiver names a declared
	/// namespace or a namespace alias. Such a receiver has no value/type symbol, so the
	/// resolver cannot classify it; a real symbol of the same spelling always wins.
	/// </summary>
	private static void TryAddNamespaceReceiverToken(
		BindingContext context,
		CompilationUnitSyntax unit,
		SyntaxNode node,
		string source,
		List<SemanticTokenInfo> tokens,
		HashSet<(int Start, int Length)> seen)
	{
		string? name;
		int spanStart;

		switch (node)
		{
			case IdentifierExpressionSyntax identifier:
				name = identifier.Name;
				spanStart = identifier.Span.Start;
				break;
			case CallExpressionSyntax call when call.FunctionName.Contains('.'):
				name = ReceiverName(call.FunctionName);
				if (name is null || !TryIndexOf(source, call.Span, name, out var receiverStart))
					return;
				spanStart = receiverStart;
				break;
			default:
				return;
		}

		if (string.IsNullOrEmpty(name) || spanStart < 0)
			return;

		// A local, parameter or field that shadows the spelling is a real symbol; keep its token.
		if (CompletionQuery.ResolveSymbol(context, unit, spanStart, includeDocumentationAndType: false) is not null)
			return;

		var span = new Cvolo.Core.Diagnostics.TextSpan(spanStart, name.Length);

		if (context.GetNamespaceAliases(unit).ContainsKey(name))
		{
			AddDirectToken(tokens, seen, span, ToolingSymbolKind.NamespaceAlias);
			return;
		}

		if (context.DeclaredNamespaces.Contains(name))
		{
			AddDirectToken(tokens, seen, span, ToolingSymbolKind.Namespace);
		}
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

	/// <summary>
	/// Classifies the fallback operator between the two operands. The operator is a dedicated token
	/// rather than a stored string, so its extent is located in the gap between the operands exactly
	/// as a binary operator's is.
	/// </summary>
	private static void TryAddCoalesceOperatorToken(CoalesceExpressionSyntax coalesce, string source, List<SemanticTokenInfo> tokens, HashSet<(int Start, int Length)> seen)
	{
		var start = coalesce.Left.Span.End;
		var end = coalesce.Right.Span.Start;
		if (start < 0 || end > source.Length || start > end)
			return;

		var index = source.IndexOf("??", start, end - start, StringComparison.Ordinal);
		if (index < 0)
			return;

		AddDirectToken(tokens, seen, new Cvolo.Core.Diagnostics.TextSpan(index, 2), ToolingSymbolKind.Operator);
	}

	/// <summary>
	/// Classifies the optional-chaining operator between the receiver and the member. `?` and `.` are
	/// separate tokens, but the operator is rendered as the contiguous `?.` glyph, so its extent is
	/// located in the gap between the receiver and the member rather than being stored on the node.
	/// </summary>
	private static void TryAddOptionalChainOperatorToken(OptionalChainExpressionSyntax optionalChain, string source, List<SemanticTokenInfo> tokens, HashSet<(int Start, int Length)> seen)
	{
		var start = optionalChain.Receiver.Span.End;
		var end = optionalChain.Span.End;
		if (start < 0 || end > source.Length || start > end)
			return;

		var index = source.IndexOf("?.", start, end - start, StringComparison.Ordinal);
		if (index < 0)
			return;

		AddDirectToken(tokens, seen, new Cvolo.Core.Diagnostics.TextSpan(index, 2), ToolingSymbolKind.Operator);
	}

	private static void AddDirectToken(List<SemanticTokenInfo> tokens, HashSet<(int Start, int Length)> seen, Cvolo.Core.Diagnostics.TextSpan span, ToolingSymbolKind kind)
	{
		if (span.Start < 0 || span.Length <= 0)
			return;

		if (!seen.Add((span.Start, span.Length)))
			return;

		tokens.Add(new SemanticTokenInfo(new TextSpan(span.Start, span.Length), kind, SemanticTokenModifiers.None));
	}

	private static SemanticTokenModifiers ModifiersFor(SyntaxNode node, string source, TextSpan span, ResolvedSymbol? resolved)
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

		// A constant is a compiler-proven immutable top-level global. Deriving this from the
		// resolved declaration (not the spelling, casing or an immutable local `val`) keeps the
		// classification identical at the declaration and every usage occurrence, including
		// usages reached through a namespace or a namespace alias.
		if (resolved?.Declaration is GlobalVariableDeclarationSyntax globalDeclaration)
		{
			modifiers |= SemanticTokenModifiers.Static;
			if (!globalDeclaration.IsMutable)
				modifiers |= SemanticTokenModifiers.Readonly | SemanticTokenModifiers.Constant;
		}

		// A true imported extern function: the declaration node itself proves the import
		// direction. An `expose extern` export, a native-ABI (`unsafe "C"`) Cvolo function with a
		// body, a delegate, a delegate-valued variable, a loader or a wrapper is a different node
		// kind and never reaches this marker.
		if (resolved?.Declaration is ExternDeclarationSyntax or ExternBlockFunctionSyntax)
			modifiers |= SemanticTokenModifiers.ExternFunction;

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
			ExternDeclarationSyntax externDeclaration => externDeclaration.Name,
			ExternBlockFunctionSyntax externFunction => externFunction.Name,
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
				if (ReceiverName(call.FunctionName) is { Length: > 0 } callReceiver
					&& TryIndexOf(source, call.Span, callReceiver, out var callReceiverPosition))
					yield return callReceiverPosition;
				break;
			case SizeofExpressionSyntax sizeofExpression:
				if (TryIndexOf(source, sizeofExpression.Span, sizeofExpression.TypeName, out var sizeofType))
					yield return sizeofType;
				break;
			case AlignofExpressionSyntax alignofExpression:
				if (TryIndexOf(source, alignofExpression.Span, alignofExpression.TypeName, out var alignofType))
					yield return alignofType;
				break;
			case OffsetofExpressionSyntax offsetofExpression:
				if (TryIndexOf(source, offsetofExpression.Span, offsetofExpression.TypeName, out var offsetofType))
					yield return offsetofType;
				foreach (var offsetofMember in offsetofExpression.Members)
					yield return offsetofMember.Span.Start;
				break;
			case FunctionDeclarationSyntax function:
				yield return function.NameSpan.Start;
				foreach (var genericPosition in GenericPositions(source, function.Span, function.Name, function.GenericParameters))
					yield return genericPosition;
				foreach (var bounds in function.GenericParameterConstraints.Values)
					foreach (var bound in bounds)
						if (TryIndexOf(source, function.Span, bound, out var boundPosition))
							yield return boundPosition;
				foreach (var defaultType in function.GenericParameterDefaults.Values)
					if (TryIndexOf(source, function.Span, defaultType, out var functionDefaultPosition))
						yield return functionDefaultPosition;
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
			case ExternDeclarationSyntax externDeclaration:
				if (TryIndexOf(source, externDeclaration.Span, externDeclaration.Name, out var externName))
					yield return externName;
				break;
			case ExternBlockFunctionSyntax externFunction:
				if (TryIndexOf(source, externFunction.Span, externFunction.Name, out var externFunctionName))
					yield return externFunctionName;
				break;
			case StructDeclarationSyntax structDeclaration:
				if (TryIndexOf(source, structDeclaration.Span, structDeclaration.Name, out var structName))
					yield return structName;
				foreach (var genericPosition in GenericPositions(source, structDeclaration.Span, structDeclaration.Name, structDeclaration.GenericParameters))
					yield return genericPosition;
				foreach (var bounds in structDeclaration.GenericParameterConstraints.Values)
					foreach (var bound in bounds)
						if (TryIndexOf(source, structDeclaration.Span, bound, out var boundPosition))
							yield return boundPosition;
				foreach (var defaultType in structDeclaration.GenericParameterDefaults.Values)
					if (TryIndexOf(source, structDeclaration.Span, defaultType, out var structDefaultPosition))
						yield return structDefaultPosition;
				break;
			case UnionDeclarationSyntax unionDeclaration:
				if (TryIndexOf(source, unionDeclaration.Span, unionDeclaration.Name, out var unionName))
					yield return unionName;
				foreach (var genericPosition in GenericPositions(source, unionDeclaration.Span, unionDeclaration.Name, unionDeclaration.GenericParameters))
					yield return genericPosition;
				foreach (var defaultType in unionDeclaration.GenericParameterDefaults.Values)
					if (TryIndexOf(source, unionDeclaration.Span, defaultType, out var unionDefaultPosition))
						yield return unionDefaultPosition;
				foreach (var bounds in unionDeclaration.GenericParameterConstraints.Values)
					foreach (var bound in bounds)
						if (TryIndexOf(source, unionDeclaration.Span, bound, out var unionBoundPosition))
							yield return unionBoundPosition;
				break;
			case EnumDeclarationSyntax enumDeclaration:
				if (TryIndexOf(source, enumDeclaration.Span, enumDeclaration.Name, out var enumName))
					yield return enumName;
				break;
			case InterfaceDeclarationSyntax interfaceDeclaration:
				if (TryIndexOf(source, interfaceDeclaration.Span, interfaceDeclaration.Name, out var interfaceName))
					yield return interfaceName;
				foreach (var genericPosition in GenericPositions(source, interfaceDeclaration.Span, interfaceDeclaration.Name, interfaceDeclaration.GenericParameters))
					yield return genericPosition;
				break;
			case ProtocolDeclarationSyntax protocolDeclaration:
				if (TryIndexOf(source, protocolDeclaration.Span, protocolDeclaration.Name, out var protocolName))
					yield return protocolName;
				foreach (var genericPosition in GenericPositions(source, protocolDeclaration.Span, protocolDeclaration.Name, protocolDeclaration.GenericParameters))
					yield return genericPosition;
				break;
			case DelegateDeclarationSyntax delegateDeclaration:
				if (TryIndexOf(source, delegateDeclaration.Span, delegateDeclaration.Name, out var delegateName))
					yield return delegateName;
				foreach (var genericPosition in GenericPositions(source, delegateDeclaration.Span, delegateDeclaration.Name, delegateDeclaration.GenericParameters))
					yield return genericPosition;
				break;
			case TypeAliasDeclarationSyntax typeAlias:
				if (TryIndexOf(source, typeAlias.Span, typeAlias.Name, out var aliasName))
					yield return aliasName;
				foreach (var genericPosition in GenericPositions(source, typeAlias.Span, typeAlias.Name, typeAlias.GenericParameters))
					yield return genericPosition;
				break;
			case ExtensionDeclarationSyntax extensionDeclaration:
				if (extensionDeclaration.ExtendedTypeName.Length > 0
					&& TryIndexOf(source, extensionDeclaration.Span, extensionDeclaration.ExtendedTypeName, out var extendedTypeName))
					yield return extendedTypeName;
				foreach (var genericPosition in GenericPositions(source, extensionDeclaration.Span, extensionDeclaration.ExtendedTypeName, extensionDeclaration.GenericParameters))
					yield return genericPosition;
				foreach (var defaultType in extensionDeclaration.GenericParameterDefaults.Values)
					if (TryIndexOf(source, extensionDeclaration.Span, defaultType, out var extensionDefaultPosition))
						yield return extensionDefaultPosition;
				foreach (var bounds in extensionDeclaration.GenericParameterConstraints.Values)
					foreach (var bound in bounds)
						if (TryIndexOf(source, extensionDeclaration.Span, bound, out var extensionBoundPosition))
							yield return extensionBoundPosition;
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

	private static string? ReceiverName(string functionName)
	{
		var dot = functionName.LastIndexOf('.');
		return dot <= 0 ? null : functionName[..dot];
	}

	/// <summary>
	/// Positions of the names in a declaration's generic-parameter list. The list is located by
	/// finding the opening angle bracket after the declaration name and then its matching close, so
	/// a parameter is never confused with a same-named identifier elsewhere in the declaration.
	/// </summary>
	private static IEnumerable<int> GenericPositions(string source, Cvolo.Core.Diagnostics.TextSpan span, string declarationName, IReadOnlyList<string> names)
	{
		if (names.Count == 0)
			yield break;

		var searchStart = span.Start;
		if (declarationName.Length > 0 && TryIndexOf(source, span, declarationName, out var namePosition))
			searchStart = namePosition + declarationName.Length;

		var open = source.IndexOf('<', searchStart);
		if (open < 0 || open >= span.End)
			yield break;

		var close = MatchingAngle(source, open, span.End);
		var listEnd = close < 0 ? span.End : close;

		foreach (var name in names)
		{
			if (name.Length == 0)
				continue;

			if (TryIndexOf(source, new Cvolo.Core.Diagnostics.TextSpan(open + 1, listEnd - open - 1), name, out var position))
				yield return position;
		}
	}

	private static int MatchingAngle(string source, int open, int limit)
	{
		var depth = 0;
		var end = Math.Min(limit, source.Length);
		for (var i = open; i < end; i++)
		{
			if (source[i] == '<')
			{
				depth++;
			}
			else if (source[i] == '>')
			{
				depth--;
				if (depth == 0)
					return i;
			}
		}

		return -1;
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
