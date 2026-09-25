using Cvolo.Analysis.Completion;
using Cvolo.Analysis.Symbols;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using Cvolo.Core.AST.Expressions;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Analysis.Semantics;

/// <summary>
/// Resolves the semantic symbol at a source position using the compiler's own binding state:
/// the position-scoped local/parameter map, <see cref="BindingContext.ResolvedCalls"/>,
/// <see cref="BindingContext.ResolveGlobalReference"/>, <see cref="BindingContext.ResolveType"/>
/// and <see cref="ExpressionTypeResolver"/>. Declarations are located through
/// <see cref="DeclarationIndex"/>, never by matching raw text.
/// </summary>
internal static class SymbolResolver
{
	internal static ResolvedSymbol? Resolve(
		BindingContext context,
		CompilationUnitSyntax unit,
		int position,
		Dictionary<string, ScopedVariable> visible)
	{
		var source = unit.Context.Source;
		var index = DeclarationIndex.For(context);

		var node = NodeAt(unit, position);
		if (node is null)
			return null;

		ResolvedSymbol? result;
		if (FindAttributeAt(unit, position) is { } attribute)
			result = ResolveAttribute(context, index, source, attribute, position);
		else if (node is MemberAccessExpressionSyntax memberAccess && position >= memberAccess.Expression.Span.End)
			result = ResolveMember(context, visible, index, source, memberAccess);
		else if (node is CallExpressionSyntax call && IsOnCallName(source, call, position))
			result = ResolveCall(context, index, source, call);
		else if (node is IdentifierExpressionSyntax identifier)
			result = ResolveIdentifier(context, visible, index, source, identifier)
				?? ResolveImplicitExtensionField(context, unit, index, identifier);
		else if (node is StructInitializationExpressionSyntax structInitialization)
			result = ResolveTypeReference(context, index, source, structInitialization.Span, structInitialization.StructTypeName, position);
		else
			result = ResolveDeclarationName(context, unit, index, source, node, position);

		return result is null
			? null
			: result with { Documentation = ExtractDocumentation(DeclaringSource(context, result.Declaration, source), result.Declaration.Span.Start) };
	}

	/// <summary>
	/// Returns the source text of the compilation unit that owns <paramref name="declaration"/>,
	/// falling back to the requesting unit's source when the owner cannot be found. Documentation
	/// must be read from the declaring file, not the file the request originated in.
	/// </summary>
	private static string DeclaringSource(BindingContext context, SyntaxNode declaration, string fallback)
	{
		foreach (var unit in context.FileContexts.Keys)
		{
			if (ContainsNode(unit, declaration))
				return unit.Context.Source;
		}

		return fallback;
	}

	private static bool ContainsNode(SyntaxNode root, SyntaxNode target)
	{
		if (ReferenceEquals(root, target))
			return true;

		foreach (var child in root.GetChildren())
		{
			if (ContainsNode(child, target))
				return true;
		}

		return false;
	}

	/// <summary>
	/// Collects the contiguous documentation-comment block immediately preceding a declaration.
	/// Only <c>///</c> lines are documentation; an ordinary <c>//</c> comment breaks attachment.
	/// </summary>
	private static string? ExtractDocumentation(string source, int declarationStart)
	{
		if (declarationStart <= 0 || declarationStart > source.Length)
			return null;

		var lines = source[..declarationStart].Replace("\r\n", "\n").Split('\n');
		var collected = new List<string>();
		for (var i = lines.Length - 2; i >= 0; i--)
		{
			var line = lines[i].Trim();
			if (line.Length == 0 || !line.StartsWith("///", StringComparison.Ordinal))
				break;

			var text = line[3..].Trim();
			text = text.Replace("<summary>", string.Empty, StringComparison.Ordinal)
				.Replace("</summary>", string.Empty, StringComparison.Ordinal)
				.Trim();

			collected.Insert(0, text);
		}

		while (collected.Count > 0 && collected[0].Length == 0)
			collected.RemoveAt(0);
		while (collected.Count > 0 && collected[^1].Length == 0)
			collected.RemoveAt(collected.Count - 1);

		return collected.Count == 0 ? null : string.Join('\n', collected);
	}

	internal static string Describe(SyntaxNode declaration, string fallback)
		=> Display(declaration, fallback);

	private static SyntaxNode? NodeAt(CompilationUnitSyntax unit, int position)
	{
		SyntaxNode? current = unit;
		while (current is not null)
		{
			SyntaxNode? next = null;
			foreach (var child in current.GetChildren())
			{
				if (StrictContains(child.Span, position))
				{
					next = child;
					break;
				}
			}

			if (next is null)
				return current;

			current = next;
		}

		return null;
	}

	private static ResolvedSymbol? ResolveIdentifier(
		BindingContext context,
		Dictionary<string, ScopedVariable> visible,
		DeclarationIndex index,
		string source,
		IdentifierExpressionSyntax identifier)
	{
		if (visible.TryGetValue(identifier.Name, out var scoped) && scoped.Declaration is not null)
		{
			// Completion adds implicit extension fields to the visible value scope so chained
			// member completion can infer their receiver type. They are still fields, not
			// locals, so leave them to the dedicated extension-field resolver below.
			if (scoped.Declaration is ExtensionDeclarationSyntax && identifier.Name != "this")
				return null;

			var kind = scoped.Origin == OriginKind.Parameter ? ResolvedSymbolKind.Parameter : ResolvedSymbolKind.Local;
			return new ResolvedSymbol(kind, identifier.Name, null, identifier.Span, scoped.Declaration, Display(scoped.Declaration, scoped.Type?.Name ?? identifier.Name));
		}

		var global = context.ResolveGlobalReference(identifier.Name, out _);
		if (global is not null)
		{
			var declaration = FindGlobalDeclaration(context, global);
			if (declaration is not null)
				return new ResolvedSymbol(ResolvedSymbolKind.Global, identifier.Name, null, identifier.Span, declaration, Display(declaration, global.Type.Name));
		}

		var type = context.ResolveType(context.NormalizeGenericName(identifier.Name));
		if (type is not null && index.FindType(type.Name) is { } typeDeclaration)
			return new ResolvedSymbol(KindOf(type), identifier.Name, null, identifier.Span, typeDeclaration, Display(typeDeclaration, type.Name));

		return null;
	}

	/// <summary>
	/// Resolves a bare identifier that names a field of the type an enclosing extension method
	/// extends — the compiler's flat/implicit receiver access (for example <c>Start</c> inside
	/// <c>extension Range { ... Start ... }</c>). Only consulted after locals, parameters,
	/// globals and named types have failed to claim the name.
	/// </summary>
	private static ResolvedSymbol? ResolveImplicitExtensionField(
		BindingContext context,
		CompilationUnitSyntax unit,
		DeclarationIndex index,
		IdentifierExpressionSyntax identifier)
	{
		foreach (var member in Members(unit))
		{
			if (member is not ExtensionDeclarationSyntax extension || !EnclosesExtensionMember(extension, identifier.Span.Start))
				continue;

			var type = context.ResolveType(context.NormalizeGenericName(extension.ExtendedTypeName));
			if (type is null || index.FindType(type.Name) is not { } typeDeclaration)
				return null;

			return typeDeclaration switch
			{
				StructDeclarationSyntax structDeclaration when structDeclaration.Fields.FirstOrDefault(f => f.Name == identifier.Name) is { } structField
					=> new ResolvedSymbol(ResolvedSymbolKind.Field, identifier.Name, Leaf(type.Name), identifier.Span, structField, $"{structField.Type} {structField.Name}"),
				UnionDeclarationSyntax unionDeclaration when unionDeclaration.Fields.FirstOrDefault(f => f.Name == identifier.Name) is { } unionField
					=> new ResolvedSymbol(ResolvedSymbolKind.Field, identifier.Name, Leaf(type.Name), identifier.Span, unionField, $"{unionField.Type} {unionField.Name}"),
				_ => null,
			};
		}

		return null;
	}

	private static bool EnclosesExtensionMember(ExtensionDeclarationSyntax extension, int position)
	{
		foreach (var method in extension.Methods)
		{
			if (method.Body is not null && Contains(method.Body.Span, position))
				return true;
		}

		foreach (var constructor in extension.Constructors)
		{
			if (Contains(constructor.Body.Span, position))
				return true;
		}

		foreach (var destructor in extension.Destructors)
		{
			if (Contains(destructor.Body.Span, position))
				return true;
		}

		return false;
	}

	/// <summary>
	/// Finds the attribute (if any) whose span contains <paramref name="position"/>. Attributes are
	/// not part of <see cref="SyntaxNode.GetChildren"/>, so they are located by walking declaration
	/// nodes directly.
	/// </summary>
	private static AttributeSyntax? FindAttributeAt(SyntaxNode node, int position)
	{
		foreach (var attribute in AttributesOf(node))
		{
			if (Contains(attribute.Span, position))
				return attribute;
		}

		foreach (var child in node.GetChildren())
		{
			if (FindAttributeAt(child, position) is { } found)
				return found;
		}

		return null;
	}

	private static IReadOnlyList<AttributeSyntax> AttributesOf(SyntaxNode node) => node switch
	{
		FunctionDeclarationSyntax function => function.Attributes,
		ConstructorDeclarationSyntax constructor => constructor.Attributes,
		DestructorDeclarationSyntax destructor => destructor.Attributes,
		StructDeclarationSyntax structDeclaration => structDeclaration.Attributes,
		UnionDeclarationSyntax unionDeclaration => unionDeclaration.Attributes,
		EnumDeclarationSyntax enumDeclaration => enumDeclaration.Attributes,
		InterfaceDeclarationSyntax interfaceDeclaration => interfaceDeclaration.Attributes,
		ProtocolDeclarationSyntax protocolDeclaration => protocolDeclaration.Attributes,
		TypeAliasDeclarationSyntax typeAlias => typeAlias.Attributes,
		ParameterSyntax parameter => parameter.Attributes,
		_ => [],
	};

	/// <summary>
	/// Resolves an attribute name to the marker type it denotes. Cvolo normalizes an optional
	/// <c>Attribute</c> suffix away (so <c>[Error]</c> and <c>[ErrorAttribute]</c> are the same
	/// attribute), so both spellings are tried as type names.
	/// </summary>
	private static ResolvedSymbol? ResolveAttribute(BindingContext context, DeclarationIndex index, string source, AttributeSyntax attribute, int position)
	{
		var span = NameSpan(source, attribute.Span, attribute.Name, fromEnd: false);
		if (position < span.Start || position > span.End)
			return null;

		var name = attribute.Name;
		var leaf = Leaf(name);
		var candidates = name.EndsWith("Attribute", StringComparison.Ordinal)
			? new[] { name }
			: new[] { name + "Attribute", name };

		foreach (var candidate in candidates)
		{
			var type = context.ResolveType(context.NormalizeGenericName(candidate));
			if (type is not null && index.FindType(type.Name) is { } declaration)
				return new ResolvedSymbol(KindOf(type), leaf, null, span, declaration, Display(declaration, type.Name));

			// Attributes are compiler-global: fall back to any namespace when no active using
			// imports the marker type's namespace.
			if (index.FindTypeByLeafName(Leaf(candidate)) is { } fallback)
				return new ResolvedSymbol(KindOfDeclaration(fallback), leaf, null, span, fallback, Display(fallback, Leaf(candidate)));
		}

		return null;
	}

	private static ResolvedSymbolKind KindOfDeclaration(SyntaxNode node) => node switch
	{
		StructDeclarationSyntax => ResolvedSymbolKind.Struct,
		UnionDeclarationSyntax => ResolvedSymbolKind.Union,
		EnumDeclarationSyntax => ResolvedSymbolKind.Enum,
		InterfaceDeclarationSyntax => ResolvedSymbolKind.Interface,
		ProtocolDeclarationSyntax => ResolvedSymbolKind.Protocol,
		TypeAliasDeclarationSyntax => ResolvedSymbolKind.TypeAlias,
		_ => ResolvedSymbolKind.OtherType,
	};

	private static ResolvedSymbol? ResolveCall(BindingContext context, DeclarationIndex index, string source, CallExpressionSyntax call)
	{
		if (!context.ResolvedCalls.TryGetValue(call, out var function))
			return null;

		var declaration = index.FindFunction(function.Name);
		if (declaration is null)
			return null;

		var name = Leaf(call.FunctionName);
		var subject = NameSpan(source, call.Span, name, fromEnd: false);
		var isExtension = function.Parameters.Count > 0 && function.Parameters[0].Name == "this";
		var kind = declaration switch
		{
			ConstructorDeclarationSyntax => ResolvedSymbolKind.Constructor,
			DestructorDeclarationSyntax => ResolvedSymbolKind.Destructor,
			_ when isExtension => ResolvedSymbolKind.ExtensionMethod,
			_ => ResolvedSymbolKind.Function,
		};

		return new ResolvedSymbol(kind, name, null, subject, declaration, Display(declaration, function.ReturnType.Name));
	}

	private static ResolvedSymbol? ResolveMember(
		BindingContext context,
		Dictionary<string, ScopedVariable> visible,
		DeclarationIndex index,
		string source,
		MemberAccessExpressionSyntax memberAccess)
	{
		var memberName = memberAccess.MemberName;
		var subject = NameSpan(source, memberAccess.Span, memberName, fromEnd: true);

		var dotted = ExpressionTypeResolver.GetDottedName(memberAccess.Expression);
		if (dotted is not null)
		{
			if (context.ResolveType(context.NormalizeGenericName(dotted)) is EnumTypeSymbol dottedEnum &&
				dottedEnum.FindVariant(memberName) is not null)
			{
				if (ResolveEnumVariant(index, dottedEnum, memberName, subject) is { } dottedVariant)
					return dottedVariant;
			}

			var separator = dotted.LastIndexOf('.');
			if (separator > 0 && context.ResolveQualifiedGlobal(dotted[..separator], memberName) is { } qualifiedGlobal)
			{
				var declaration = FindGlobalDeclaration(context, qualifiedGlobal);
				if (declaration is not null)
					return new ResolvedSymbol(ResolvedSymbolKind.Global, memberName, null, subject, declaration, Display(declaration, qualifiedGlobal.Type.Name));
			}
		}

		var receiverType = ExpressionTypeResolver.Resolve(context, visible, memberAccess.Expression);
		if (receiverType is PointerTypeSymbol pointer)
			receiverType = pointer.ReferencedType;

		switch (receiverType)
		{
			case EnumTypeSymbol enumType when enumType.FindVariant(memberName) is not null:
				return ResolveEnumVariant(index, enumType, memberName, subject);

			case StructTypeSymbol structType when structType.FindField(memberName) is not null:
				if (index.FindType(structType.Name) is StructDeclarationSyntax structDeclaration &&
					structDeclaration.Fields.FirstOrDefault(f => f.Name == memberName) is { } field)
					return new ResolvedSymbol(ResolvedSymbolKind.Field, memberName, Leaf(structType.Name), subject, field, $"{field.Type} {field.Name}");
				break;

			case UnionTypeSymbol unionType when unionType.FindField(memberName) is not null:
				if (index.FindType(unionType.Name) is UnionDeclarationSyntax unionDeclaration &&
					unionDeclaration.Fields.FirstOrDefault(f => f.Name == memberName) is { } unionField)
					return new ResolvedSymbol(ResolvedSymbolKind.Field, memberName, Leaf(unionType.Name), subject, unionField, $"{unionField.Type} {unionField.Name}");
				break;
		}

		return null;
	}

	private static ResolvedSymbol? ResolveEnumVariant(DeclarationIndex index, EnumTypeSymbol enumType, string memberName, TextSpan subject)
	{
		if (index.FindType(enumType.Name) is EnumDeclarationSyntax enumDeclaration &&
			enumDeclaration.Variants.FirstOrDefault(v => v.Name == memberName) is { } variant)
			return new ResolvedSymbol(ResolvedSymbolKind.EnumMember, memberName, Leaf(enumType.Name), subject, variant, variant.Name);

		return null;
	}

	private static ResolvedSymbol? ResolveDeclarationName(BindingContext context, CompilationUnitSyntax unit, DeclarationIndex index, string source, SyntaxNode node, int position)
	{
		switch (node)
		{
			case FunctionDeclarationSyntax function:
				return ResolveTypeReference(context, index, source, function.ReturnTypeSpan, function.ReturnType, position)
					?? OnName(function.NameSpan, function.Name, position, IsExtensionMethod(unit, function) ? ResolvedSymbolKind.ExtensionMethod : ResolvedSymbolKind.Function, function, Display(function, function.ReturnType));
			case ConstructorDeclarationSyntax constructor:
				return OnName(constructor.NameSpan, constructor.StructName, position, ResolvedSymbolKind.Constructor, constructor, Display(constructor, constructor.StructName), constructor.StructName);
			case DestructorDeclarationSyntax destructor:
				return OnName(NameSpan(source, destructor.Span, destructor.StructName, fromEnd: false), destructor.StructName, position, ResolvedSymbolKind.Destructor, destructor, Display(destructor, destructor.StructName), destructor.StructName);
			case ExtensionDeclarationSyntax extension:
				return ResolveConformance(context, index, source, extension, position)
					?? ResolveTypeReference(context, index, source, extension.NameSpan, extension.ExtendedTypeName, position)
					?? OnName(NameSpan(source, extension.Span, Leaf(extension.ExtendedTypeName), fromEnd: false), Leaf(extension.ExtendedTypeName), position, ResolvedSymbolKind.ExtensionMethod, extension, Display(extension, extension.ExtendedTypeName));
			case StructDeclarationSyntax structDeclaration:
				return OnName(NameSpan(source, structDeclaration.Span, structDeclaration.Name, fromEnd: false), structDeclaration.Name, position, ResolvedSymbolKind.Struct, structDeclaration, Display(structDeclaration, structDeclaration.Name));
			case UnionDeclarationSyntax unionDeclaration:
				return OnName(NameSpan(source, unionDeclaration.Span, unionDeclaration.Name, fromEnd: false), unionDeclaration.Name, position, ResolvedSymbolKind.Union, unionDeclaration, Display(unionDeclaration, unionDeclaration.Name));
			case EnumDeclarationSyntax enumDeclaration:
				return OnName(NameSpan(source, enumDeclaration.Span, enumDeclaration.Name, fromEnd: false), enumDeclaration.Name, position, ResolvedSymbolKind.Enum, enumDeclaration, Display(enumDeclaration, enumDeclaration.Name));
			case InterfaceDeclarationSyntax interfaceDeclaration:
				return OnName(NameSpan(source, interfaceDeclaration.Span, interfaceDeclaration.Name, fromEnd: false), interfaceDeclaration.Name, position, ResolvedSymbolKind.Interface, interfaceDeclaration, Display(interfaceDeclaration, interfaceDeclaration.Name));
			case InterfaceMethodDeclarationSyntax interfaceMember:
				return ResolveTypeReference(context, index, source, interfaceMember.Span, interfaceMember.ReturnType, position)
					?? OnName(NameSpan(source, interfaceMember.Span, interfaceMember.Name, fromEnd: false), interfaceMember.Name, position, ResolvedSymbolKind.Method, interfaceMember, $"{interfaceMember.ReturnType} {interfaceMember.Name}({ParameterList(interfaceMember.Parameters)})");
			case ProtocolDeclarationSyntax protocolDeclaration:
				return OnName(NameSpan(source, protocolDeclaration.Span, protocolDeclaration.Name, fromEnd: false), protocolDeclaration.Name, position, ResolvedSymbolKind.Protocol, protocolDeclaration, Display(protocolDeclaration, protocolDeclaration.Name));
			case DelegateDeclarationSyntax delegateDeclaration:
				return ResolveTypeReference(context, index, source, delegateDeclaration.ReturnTypeSpan, delegateDeclaration.ReturnType, position)
					?? OnName(NameSpan(source, delegateDeclaration.Span, delegateDeclaration.Name, fromEnd: false), delegateDeclaration.Name, position, ResolvedSymbolKind.Delegate, delegateDeclaration, Display(delegateDeclaration, delegateDeclaration.Name));
			case ProtocolMethodDeclarationSyntax protocolMember:
				return ResolveTypeReference(context, index, source, protocolMember.Span, protocolMember.ReturnType, position)
					?? OnName(NameSpan(source, protocolMember.Span, protocolMember.Name, fromEnd: false), protocolMember.Name, position, ResolvedSymbolKind.Method, protocolMember, $"{protocolMember.ReturnType} {protocolMember.Name}({ParameterList(protocolMember.Parameters)})");
			case TypeAliasDeclarationSyntax typeAlias:
				return OnName(NameSpan(source, typeAlias.Span, typeAlias.Name, fromEnd: false), typeAlias.Name, position, ResolvedSymbolKind.TypeAlias, typeAlias, Display(typeAlias, typeAlias.Name));
			case GlobalVariableDeclarationSyntax global:
				return ResolveTypeReference(context, index, source, global.Span, global.Type, position)
					?? OnName(NameSpan(source, global.Span, global.Name, fromEnd: false), global.Name, position, ResolvedSymbolKind.Global, global, Display(global, global.Name));
			case VariableDeclarationSyntax variable:
				return ResolveTypeReference(context, index, source, variable.Span, variable.Type, position)
					?? OnName(NameSpan(source, variable.Span, variable.Name, fromEnd: false), variable.Name, position, ResolvedSymbolKind.Local, variable, Display(variable, variable.Name));
			case ParameterSyntax parameter:
				return ResolveTypeReference(context, index, source, parameter.Span, parameter.Type, position)
					?? OnName(NameSpan(source, parameter.Span, parameter.Name, fromEnd: false), parameter.Name, position, ResolvedSymbolKind.Parameter, parameter, Display(parameter, parameter.Name));
			case StructFieldSyntax structField:
				return ResolveTypeReference(context, index, source, structField.Span, structField.Type, position)
					?? OnName(NameSpan(source, structField.Span, structField.Name, fromEnd: false), structField.Name, position, ResolvedSymbolKind.Field, structField, Display(structField, structField.Name));
			case UnionFieldSyntax unionField:
				return ResolveTypeReference(context, index, source, unionField.Span, unionField.Type, position)
					?? OnName(NameSpan(source, unionField.Span, unionField.Name, fromEnd: false), unionField.Name, position, ResolvedSymbolKind.Field, unionField, Display(unionField, unionField.Name));
			case EnumVariantDeclarationSyntax enumVariant:
				return OnName(NameSpan(source, enumVariant.Span, enumVariant.Name, fromEnd: false), enumVariant.Name, position, ResolvedSymbolKind.EnumMember, enumVariant, Display(enumVariant, enumVariant.Name));
			default:
				return null;
		}
	}

	private static ResolvedSymbol? ResolveConformance(BindingContext context, DeclarationIndex index, string source, ExtensionDeclarationSyntax extension, int position)
	{
		if (extension.ConformsTo is null)
			return null;

		var leaf = LeafType(extension.ConformsTo);
		if (leaf.Length == 0)
			return null;

		var span = NameSpan(source, extension.ConformsToSpan, leaf, fromEnd: true);
		if (position < span.Start || position > span.End)
			return null;

		var type = context.ResolveType(context.NormalizeGenericName(extension.ConformsTo));
		if (type is null || index.FindType(type.Name) is not { } declaration)
			return null;

		return new ResolvedSymbol(KindOf(type), leaf, null, span, declaration, Display(declaration, type.Name));
	}

	private static ResolvedSymbol? ResolveTypeReference(BindingContext context, DeclarationIndex index, string source, TextSpan range, string? typeName, int position)
	{
		if (string.IsNullOrWhiteSpace(typeName))
			return null;

		var leaf = LeafType(typeName);
		if (leaf.Length == 0)
			return null;

		var span = NameSpan(source, range, leaf, fromEnd: false);
		if (position < span.Start || position > span.End)
			return null;

		var type = context.ResolveType(context.NormalizeGenericName(typeName));
		if (type is null || index.FindType(type.Name) is not { } declaration)
			return null;

		return new ResolvedSymbol(KindOf(type), leaf, null, span, declaration, Display(declaration, type.Name));
	}

	private static bool IsExtensionMethod(CompilationUnitSyntax unit, FunctionDeclarationSyntax function)
	{
		foreach (var member in Members(unit))
		{
			if (member is ExtensionDeclarationSyntax extension && extension.Methods.Contains(function))
				return true;
		}

		return false;
	}

	private static IEnumerable<SyntaxNode> Members(CompilationUnitSyntax unit)
		=> unit.NamespaceDeclaration is { } ns ? ns.Members : unit.Members;

	private static string LeafType(string typeName)
	{
		var text = typeName.Trim();
		if (text.StartsWith("refvar ", StringComparison.Ordinal))
			text = text[7..];
		else if (text.StartsWith("ref ", StringComparison.Ordinal))
			text = text[4..];

		var generic = text.IndexOf('<');
		if (generic >= 0)
			text = text[..generic];

		var dot = text.LastIndexOf('.');
		return dot >= 0 ? text[(dot + 1)..] : text;
	}

	private static ResolvedSymbol? OnName(TextSpan span, string name, int position, ResolvedSymbolKind kind, SyntaxNode declaration, string display, string? owner = null)
	{
		if (position < span.Start || position > span.End)
			return null;

		return new ResolvedSymbol(kind, name, owner, span, declaration, display);
	}

	private static bool IsOnCallName(string source, CallExpressionSyntax call, int position)
	{
		var span = NameSpan(source, call.Span, Leaf(call.FunctionName), fromEnd: false);
		return position >= span.Start && position <= span.End;
	}

	private static GlobalVariableDeclarationSyntax? FindGlobalDeclaration(BindingContext context, VariableSymbol symbol)
	{
		foreach (var (node, candidate) in context.GlobalVariables)
		{
			if (ReferenceEquals(candidate, symbol))
				return node;
		}

		return null;
	}

	private static ResolvedSymbolKind KindOf(TypeSymbol type) => type switch
	{
		EnumTypeSymbol => ResolvedSymbolKind.Enum,
		StructTypeSymbol => ResolvedSymbolKind.Struct,
		UnionTypeSymbol => ResolvedSymbolKind.Union,
		InterfaceTypeSymbol => ResolvedSymbolKind.Interface,
		ProtocolTypeSymbol => ResolvedSymbolKind.Protocol,
		DelegateTypeSymbol => ResolvedSymbolKind.Delegate,
		_ => ResolvedSymbolKind.OtherType,
	};

	private static string Display(SyntaxNode node, string fallback) => node switch
	{
		VariableDeclarationSyntax variable => $"{variable.Type ?? "var"} {variable.Name}",
		ParameterSyntax parameter => $"{parameter.Type} {parameter.Name}",
		GlobalVariableDeclarationSyntax global => global.IsForeign
			? $"extern \"{global.CallingConvention ?? "C"}\" global {(global.IsMutable ? "var " : string.Empty)}{global.Type} {global.Name}"
			: $"{global.Type} {global.Name}",
		FunctionDeclarationSyntax function => $"{function.ReturnType} {function.Name}{Generic(function.GenericParameters)}({ParameterList(function.Parameters)})",
		ConstructorDeclarationSyntax constructor => $"{constructor.StructName}({ParameterList(constructor.Parameters)})",
		DestructorDeclarationSyntax destructor => $"~{destructor.StructName}()",
		StructFieldSyntax structField => $"{structField.Type} {structField.Name}",
		UnionFieldSyntax unionField => $"{unionField.Type} {unionField.Name}",
		EnumVariantDeclarationSyntax enumVariant => enumVariant.Name,
		StructDeclarationSyntax structDeclaration => $"struct {structDeclaration.Name}{Generic(structDeclaration.GenericParameters)}",
		UnionDeclarationSyntax unionDeclaration => $"{(unionDeclaration.IsUnsafe ? "unsafe " : string.Empty)}union {unionDeclaration.Name}{Generic(unionDeclaration.GenericParameters)}",
		EnumDeclarationSyntax enumDeclaration => $"enum {enumDeclaration.Name}",
		InterfaceDeclarationSyntax interfaceDeclaration => $"interface {interfaceDeclaration.Name}{Generic(interfaceDeclaration.GenericParameters)}",
		ProtocolDeclarationSyntax protocolDeclaration => $"protocol {protocolDeclaration.Name}{Generic(protocolDeclaration.GenericParameters)}",
		TypeAliasDeclarationSyntax typeAlias => $"alias {typeAlias.Name}{Generic(typeAlias.GenericParameters)} = {typeAlias.Type}",
		ExtensionDeclarationSyntax extension => $"extension {extension.ExtendedTypeName}",
		DelegateDeclarationSyntax delegateDeclaration => $"{(delegateDeclaration.IsNative ? $"unsafe \"{delegateDeclaration.CallingConvention ?? "C"}\" " : string.Empty)}delegate {delegateDeclaration.ReturnType} {delegateDeclaration.Name}{Generic(delegateDeclaration.GenericParameters)}({ParameterList(delegateDeclaration.Parameters)})",
		_ => fallback,
	};

	private static string ParameterList(IReadOnlyList<ParameterSyntax> parameters)
		=> string.Join(", ", parameters.Select(p => $"{p.Type} {p.Name}"));

	private static string Generic(IReadOnlyList<string> parameters)
		=> parameters.Count == 0 ? "" : $"<{string.Join(", ", parameters)}>";

	private static TextSpan NameSpan(string source, TextSpan range, string name, bool fromEnd)
	{
		var start = Math.Clamp(range.Start, 0, source.Length);
		var end = Math.Clamp(range.End, start, source.Length);
		if (end <= start || name.Length == 0)
			return new TextSpan(start, 0);

		var slice = source[start..end];
		var index = fromEnd ? slice.LastIndexOf(name, StringComparison.Ordinal) : slice.IndexOf(name, StringComparison.Ordinal);
		return index < 0 ? new TextSpan(start, Math.Min(name.Length, end - start)) : new TextSpan(start + index, name.Length);
	}

	private static string Leaf(string name)
	{
		var dot = name.LastIndexOf('.');
		return dot < 0 ? name : name[(dot + 1)..];
	}

	private static bool Contains(TextSpan span, int position) => span.Start <= position && position <= span.End;

	private static bool StrictContains(TextSpan span, int position) => span.Start <= position && position < span.End;
}