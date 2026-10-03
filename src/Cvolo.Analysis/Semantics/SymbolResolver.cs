using Cvolo.Analysis.Completion;
using Cvolo.Analysis.Symbols;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using Cvolo.Core.AST.Directives;
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
			result = ResolveMember(context, unit, visible, index, source, memberAccess);
		else if (node is CallExpressionSyntax call && IsOnCallTarget(source, call, position))
			result = ResolveCallee(context, unit, visible, index, source, call, position)
				?? ResolveCall(context, index, source, call);
		else if (node is IdentifierExpressionSyntax identifier)
			result = ResolveIdentifier(context, visible, index, source, identifier)
				?? ResolveImplicitExtensionField(context, unit, index, identifier)
				?? ResolveNamespaceAliasUse(context, unit, identifier);
		else if (node is StructInitializationExpressionSyntax structInitialization)
			result = ResolveTypeReference(context, index, source, structInitialization.Span, structInitialization.StructTypeName, position);
		else if (node is SizeofExpressionSyntax sizeofExpression)
			result = ResolveTypeReference(context, index, source, sizeofExpression.Span, sizeofExpression.TypeName, position);
		else if (node is AlignofExpressionSyntax alignofExpression)
			result = ResolveTypeReference(context, index, source, alignofExpression.Span, alignofExpression.TypeName, position);
		else if (node is OffsetofExpressionSyntax offsetofExpression)
			result = ResolveOffsetof(context, index, source, offsetofExpression, position);
		else
			result = ResolveDeclarationName(context, unit, index, source, node, position);

		return result is null
			? null
			: result with
			{
				Documentation = ExtractDocumentation(DeclaringSource(context, result.Declaration, source), result.Declaration.Span.Start),
				ResolvedType = TypeOf(context, visible, result.Declaration),
			};
	}

	/// <summary>
	/// The compiler-resolved type of the symbol's declaration, when it has one. Exposed through
	/// <see cref="ResolvedSymbol.ResolvedType"/> so a caller can lay a value out without ever
	/// re-deriving a type from a display string. Inference stays the compiler's: a <c>var</c>
	/// declaration is typed from its initializer by <see cref="ExpressionTypeResolver"/>, and a
	/// declaration whose type the compiler cannot resolve yields null rather than a guess.
	/// </summary>
	private static TypeSymbol? TypeOf(
		BindingContext context,
		Dictionary<string, ScopedVariable> visible,
		SyntaxNode declaration)
	{
		switch (declaration)
		{
			case VariableDeclarationSyntax variable:
				return variable.Type is { Length: > 0 } declared && declared != "var"
					? ResolveDeclaredType(context, declared)
					: ExpressionTypeResolver.Resolve(context, visible, variable.Initializer);
			case ParameterSyntax parameter:
				return ResolveDeclaredType(context, parameter.Type);
			case GlobalVariableDeclarationSyntax global:
				return ResolveDeclaredType(context, global.Type);
			case StructFieldSyntax field:
				return ResolveDeclaredType(context, field.Type);
			case UnionFieldSyntax unionField:
				return ResolveDeclaredType(context, unionField.Type);
			default:
				return null;
		}
	}

	/// <summary>
	/// Resolves a type spelled in source. A reference type keeps its <c>ref </c>/<c>refvar </c>
	/// prefix because <see cref="BindingContext.ResolveType"/> reads that prefix to build the
	/// pointer symbol; collapsing the space would leave an unknown name instead.
	/// </summary>
	private static TypeSymbol? ResolveDeclaredType(BindingContext context, string? typeText)
	{
		if (string.IsNullOrWhiteSpace(typeText))
			return null;

		var text = typeText.Trim();
		if (text.StartsWith("refvar ", StringComparison.Ordinal) || text.StartsWith("ref ", StringComparison.Ordinal))
			return context.ResolveType(text);

		return context.ResolveType(context.NormalizeGenericName(text));
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

		// A bare name is looked up through the compiler's own lookup path (current namespace, then
		// active usings, then the unqualified name), so a type declared in the global namespace
		// resolves from inside a namespace that does not import it.
		if (context.TryResolveTypeByLookupName(context.NormalizeGenericName(identifier.Name), null, out var lookedUp)
			&& index.FindType(lookedUp.Name) is { } lookedUpDeclaration)
		{
			return new ResolvedSymbol(KindOf(lookedUp), identifier.Name, null, identifier.Span, lookedUpDeclaration, Display(lookedUpDeclaration, lookedUp.Name));
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

	/// <summary>
	/// Resolves the position inside a dotted call's callee. A call keeps only its callee as a
	/// string (<c>CallExpressionSyntax.FunctionName</c>) — the callee is not one of its child nodes,
	/// so the receiver and the member name are split from that text. Inside an uninstantiated
	/// generic template body no call is overload-resolved, so resolving the callee by shape is the
	/// only way <c>Layout.FromType&lt;T&gt;()</c> stays navigable there.
	/// </summary>
	private static ResolvedSymbol? ResolveCallee(
		BindingContext context,
		CompilationUnitSyntax unit,
		Dictionary<string, ScopedVariable> visible,
		DeclarationIndex index,
		string source,
		CallExpressionSyntax call,
		int position)
	{
		var separator = call.FunctionName.LastIndexOf('.');
		if (separator <= 0 || separator == call.FunctionName.Length - 1)
			return null;

		var receiverName = call.FunctionName[..separator];
		var memberName = call.FunctionName[(separator + 1)..];
		var calleeStart = FindName(source, call.Span.Start, call.Span.End, call.FunctionName, fromEnd: false);
		if (calleeStart < 0)
			return null;

		var receiverSpan = new TextSpan(calleeStart, receiverName.Length);
		var memberSpan = new TextSpan(calleeStart + receiverName.Length + 1, memberName.Length);

		if (position >= receiverSpan.Start && position <= receiverSpan.End)
			return ResolveCalleeReceiver(context, unit, visible, index, source, receiverName, receiverSpan);

		return position >= memberSpan.Start && position <= memberSpan.End
			? ResolveCalleeMember(context, unit, visible, index, source, receiverName, memberName, memberSpan)
			: null;
	}

	/// <summary>
	/// Resolves a callee written on a type receiver. A member that declares its own type
	/// parameters is parked as a template on its owner instead of entering the associated overload
	/// table, so the template table is consulted first.
	/// </summary>
	private static ResolvedSymbol? ResolveCalleeMember(
		BindingContext context,
		CompilationUnitSyntax unit,
		Dictionary<string, ScopedVariable> visible,
		DeclarationIndex index,
		string source,
		string receiverName,
		string memberName,
		TextSpan subject)
	{
		if (context.TryResolveTypeByLookupName(context.NormalizeGenericName(receiverName), unit, out var ownerType))
		{
			if (ResolveAssociatedTemplate(context, unit, ownerType, memberName) is { } template)
				return template with { SubjectSpan = subject };

			return ownerType is EnumTypeSymbol enumType && enumType.FindVariant(memberName) is not null
				? ResolveEnumVariant(index, enumType, memberName, subject)
				: null;
		}

		// A receiver that is a value typed by one of the enclosing template's own type parameters is
		// typed only by the bound that parameter declares. Resolving through that bound is what makes
		// `allocator.Alloc` reachable while `A` is still an uninstantiated parameter.
		return visible.TryGetValue(receiverName, out var scoped) && scoped.Type is TypeParameterSymbol typeParameter
			? ResolveConstrainedMember(context, unit, index, source, typeParameter.Name, memberName, subject)
			: null;
	}

	/// <summary>
	/// Resolves a member written on a value whose type is a type parameter, through the bound that
	/// parameter declares. That bound is the whole of what the template promises the value can do,
	/// so its own declaration is where navigation lands.
	/// </summary>
	private static ResolvedSymbol? ResolveConstrainedMember(
		BindingContext context,
		CompilationUnitSyntax unit,
		DeclarationIndex index,
		string source,
		string typeParameterName,
		string memberName,
		TextSpan subject)
	{
		foreach (var member in Members(unit))
		{
			if (member is not StructDeclarationSyntax { GenericParameterConstraints: { } constraints } owner
				|| !constraints.TryGetValue(typeParameterName, out var bounds))
			{
				continue;
			}

			foreach (var bound in bounds)
			{
				if (!context.TryResolveTypeByLookupName(context.NormalizeGenericName(bound), unit, out var boundType))
					continue;

				if (boundType is ProtocolTypeSymbol protocol
					&& protocol.Members.FirstOrDefault(m => m.Name == memberName) is { } protocolMember)
				{
					return new ResolvedSymbol(ResolvedSymbolKind.Method, memberName, Leaf(boundType.Name), subject, protocolMember, $"{protocolMember.ReturnType} {protocolMember.Name}({ParameterList(protocolMember.Parameters)})");
				}

				if (boundType is not StructTypeSymbol structType)
					continue;

				if (structType.FindField(memberName) is not null
					&& index.FindType(structType.Name) is StructDeclarationSyntax fieldOwner
					&& fieldOwner.Fields.FirstOrDefault(f => f.Name == memberName) is { } field)
				{
					return new ResolvedSymbol(ResolvedSymbolKind.Field, memberName, Leaf(structType.Name), subject, field, $"{field.Type} {field.Name}");
				}

				if (ResolveAssociatedTemplate(context, unit, structType, memberName) is { } associated)
					return associated with { SubjectSpan = subject };
			}
		}

		return null;
	}

	/// <summary>
	/// Resolves a callee receiver written as a type name, selecting that type's declaration; a
	/// receiver that names a value instead is resolved through the ordinary identifier path.
	/// </summary>
	private static ResolvedSymbol? ResolveCalleeReceiver(
		BindingContext context,
		CompilationUnitSyntax unit,
		Dictionary<string, ScopedVariable> visible,
		DeclarationIndex index,
		string source,
		string receiverName,
		TextSpan subject)
	{
		if (context.TryResolveTypeByLookupName(context.NormalizeGenericName(receiverName), unit, out var receiverType)
			&& index.FindType(receiverType.Name) is { } receiverDeclaration)
		{
			return new ResolvedSymbol(KindOf(receiverType), Leaf(receiverName), null, subject, receiverDeclaration, Display(receiverDeclaration, receiverType.Name));
		}

		return visible.TryGetValue(receiverName, out var scoped) && scoped.Declaration is not null
			? new ResolvedSymbol(
				scoped.Origin == OriginKind.Parameter ? ResolvedSymbolKind.Parameter : ResolvedSymbolKind.Local,
				receiverName,
				null,
				subject,
				scoped.Declaration,
				Display(scoped.Declaration, scoped.Type?.Name ?? receiverName))
			: null;
	}

	private static ResolvedSymbol? ResolveCall(BindingContext context, DeclarationIndex index, string source, CallExpressionSyntax call)
	{
		if (!context.ResolvedCalls.TryGetValue(call, out var function))
			return null;

		var declaration = index.FindFunction(function.Name);
		if (declaration is null)
			return null;

		var name = Leaf(call.FunctionName);
		var subject = NameSpan(source, call.Span, name, fromEnd: false);
		// Only receiver-backed callables are instance methods; an associated function is called
		// through its type and must not be reported as an extension method of that type.
		var isExtension = function.IsInstanceExtension
			|| (function.CallableKind == CallableKind.Free && function.Parameters.Count > 0 && function.Parameters[0].Name == "this");

		if (declaration is ConstructorDeclarationSyntax constructor)
		{
			var structDeclaration = index.FindTypeByLeafName(Leaf(constructor.StructName));
			if (structDeclaration is not null)
				return new ResolvedSymbol(ResolvedSymbolKind.Struct, name, null, subject, structDeclaration, Display(structDeclaration, constructor.StructName));
		}

		var kind = declaration switch
		{
			DestructorDeclarationSyntax => ResolvedSymbolKind.Destructor,
			_ when function.CallableKind == CallableKind.Associated => ResolvedSymbolKind.AssociatedFunction,
			_ when isExtension => ResolvedSymbolKind.ExtensionMethod,
			_ => ResolvedSymbolKind.Function,
		};

		return new ResolvedSymbol(kind, name, null, subject, declaration, Display(declaration, function.ReturnType.Name));
	}

	private static ResolvedSymbol? ResolveMember(
		BindingContext context,
		CompilationUnitSyntax unit,
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

			// A receiver written as a type name selects that type's callables. A member that
			// declares its own type parameters is parked as a template on its owner instead of
			// entering the associated overload table, so the template table is consulted as well.
			// Without this, `Layout.FromType<T>()` — a callable the reader wrote and can complete —
			// resolves to nothing at all.
			if (context.TryResolveTypeByLookupName(dotted, null, out var ownerType)
				&& ResolveAssociatedTemplate(context, unit, ownerType, memberName) is { } template)
			{
				return template with { SubjectSpan = subject };
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

	/// <summary>
	/// Resolves a member written on a type receiver that is still a generic template on that owner
	/// (`Layout .FromType&lt;T&gt;()`). The call site supplies the type arguments at use time, so the
	/// declaration that navigation must land on is the template's own source declaration.
	/// </summary>
	private static ResolvedSymbol? ResolveAssociatedTemplate(
		BindingContext context,
		CompilationUnitSyntax unit,
		TypeSymbol ownerType,
		string memberName)
	{
		var previousUnit = context.CurrentUnit;
		var previousNamespace = context.CurrentNamespace;
		context.CurrentUnit = unit;
		context.CurrentNamespace = unit.NamespaceDeclaration?.Name;

		try
		{
			foreach (var template in context.GetGenericExtensionMethodTemplates(ownerType, unit, memberName))
			{
				if (!template.Method.IsAssociated)
					continue;

				return new ResolvedSymbol(
					ResolvedSymbolKind.AssociatedFunction,
					template.Method.Name,
					Leaf(ownerType.Name),
					template.Method.NameSpan,
					template.Method,
					Display(template.Method, template.Method.Name));
			}

			return null;
		}
		finally
		{
			context.CurrentUnit = previousUnit;
			context.CurrentNamespace = previousNamespace;
		}
	}

	private static ResolvedSymbol? ResolveOffsetof(
		BindingContext context,
		DeclarationIndex index,
		string source,
		OffsetofExpressionSyntax offsetofExpression,
		int position)
	{
		var typeNameStart = source.IndexOf(offsetofExpression.TypeName, offsetofExpression.Span.Start, offsetofExpression.Span.Length, StringComparison.Ordinal);
		if (typeNameStart >= 0 && position >= typeNameStart && position < typeNameStart + offsetofExpression.TypeName.Length)
		{
			return ResolveTypeReference(context, index, source, offsetofExpression.Span, offsetofExpression.TypeName, position);
		}

		var type = context.ResolveType(context.NormalizeGenericName(offsetofExpression.TypeName));
		if (type is null)
			return null;

		foreach (var member in offsetofExpression.Members)
		{
			var subject = member.Span;

			switch (type)
			{
				case StructTypeSymbol structType when structType.FindField(member.Name) is { } structField:
					if (index.FindType(structType.Name) is StructDeclarationSyntax structDeclaration &&
						structDeclaration.Fields.FirstOrDefault(f => f.Name == member.Name) is { } structFieldSyntax)
					{
						if (position >= subject.Start && position < subject.End)
							return new ResolvedSymbol(ResolvedSymbolKind.Field, member.Name, Leaf(structType.Name), subject, structFieldSyntax, $"{structFieldSyntax.Type} {structFieldSyntax.Name}");

						type = structField.Type;
						break;
					}

					return null;

				case UnionTypeSymbol unionType when unionType.FindField(member.Name) is { } unionField:
					if (index.FindType(unionType.Name) is UnionDeclarationSyntax unionDeclaration &&
						unionDeclaration.Fields.FirstOrDefault(f => f.Name == member.Name) is { } unionFieldSyntax)
					{
						if (position >= subject.Start && position < subject.End)
							return new ResolvedSymbol(ResolvedSymbolKind.Field, member.Name, Leaf(unionType.Name), subject, unionFieldSyntax, $"{unionFieldSyntax.Type} {unionFieldSyntax.Name}");

						type = unionField.Type;
						break;
					}

					return null;

				default:
					return null;
			}
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
				return ResolveGenericDeclaration(context, index, source, function, function.Span, function.GenericParameters, null, null, position)
					?? ResolveTypeReference(context, index, source, function.ReturnTypeSpan, function.ReturnType, position)
					?? OnName(function.NameSpan, function.Name, position, DeclarationKind(unit, function), function, Display(function, function.ReturnType));
			case ConstructorDeclarationSyntax constructor:
				return OnName(constructor.NameSpan, constructor.StructName, position, ResolvedSymbolKind.Constructor, constructor, Display(constructor, constructor.StructName), constructor.StructName);
			// An operator's name is its punctuation token, so the hover subject is the operator span
			// itself and the presentation is the `operator +(...)` form rather than an identifier.
			case OperatorDeclarationSyntax op:
				return ResolveTypeReference(context, index, source, op.ReturnTypeSpan, op.ReturnType, position)
					?? OnName(op.OperatorSpan, op.DisplayName, position, ResolvedSymbolKind.Operator, op, Display(op, op.ReturnType));
			case DestructorDeclarationSyntax destructor:
				return OnName(NameSpan(source, destructor.Span, destructor.StructName, fromEnd: false), destructor.StructName, position, ResolvedSymbolKind.Destructor, destructor, Display(destructor, destructor.StructName), destructor.StructName);
			case ExtensionDeclarationSyntax extension:
				return ResolveConformance(context, index, source, extension, position)
					?? ResolveGenericDeclaration(context, index, source, extension, extension.Span, extension.GenericParameters, null, extension.GenericParameterDefaults, position)
					?? ResolveTypeReference(context, index, source, extension.NameSpan, extension.ExtendedTypeName, position)
					?? OnName(NameSpan(source, extension.Span, Leaf(extension.ExtendedTypeName), fromEnd: false), Leaf(extension.ExtendedTypeName), position, ResolvedSymbolKind.ExtensionMethod, extension, Display(extension, extension.ExtendedTypeName));
			case StructDeclarationSyntax structDeclaration:
				return ResolveGenericDeclaration(context, index, source, structDeclaration, structDeclaration.Span, structDeclaration.GenericParameters, structDeclaration.GenericParameterConstraints, structDeclaration.GenericParameterDefaults, position)
					?? OnName(NameSpan(source, structDeclaration.Span, structDeclaration.Name, fromEnd: false), structDeclaration.Name, position, ResolvedSymbolKind.Struct, structDeclaration, Display(structDeclaration, structDeclaration.Name));
			case UnionDeclarationSyntax unionDeclaration:
				return ResolveGenericDeclaration(context, index, source, unionDeclaration, unionDeclaration.Span, unionDeclaration.GenericParameters, null, unionDeclaration.GenericParameterDefaults, position)
					?? OnName(NameSpan(source, unionDeclaration.Span, unionDeclaration.Name, fromEnd: false), unionDeclaration.Name, position, ResolvedSymbolKind.Union, unionDeclaration, Display(unionDeclaration, unionDeclaration.Name));
			case EnumDeclarationSyntax enumDeclaration:
				return OnName(NameSpan(source, enumDeclaration.Span, enumDeclaration.Name, fromEnd: false), enumDeclaration.Name, position, ResolvedSymbolKind.Enum, enumDeclaration, Display(enumDeclaration, enumDeclaration.Name));
			case InterfaceDeclarationSyntax interfaceDeclaration:
				return ResolveGenericDeclaration(context, index, source, interfaceDeclaration, interfaceDeclaration.Span, interfaceDeclaration.GenericParameters, null, null, position)
					?? OnName(NameSpan(source, interfaceDeclaration.Span, interfaceDeclaration.Name, fromEnd: false), interfaceDeclaration.Name, position, ResolvedSymbolKind.Interface, interfaceDeclaration, Display(interfaceDeclaration, interfaceDeclaration.Name));
			case InterfaceMethodDeclarationSyntax interfaceMember:
				return ResolveTypeReference(context, index, source, interfaceMember.Span, interfaceMember.ReturnType, position)
					?? OnName(NameSpan(source, interfaceMember.Span, interfaceMember.Name, fromEnd: false), interfaceMember.Name, position, ResolvedSymbolKind.Method, interfaceMember, $"{interfaceMember.ReturnType} {interfaceMember.Name}({ParameterList(interfaceMember.Parameters)})");
			case ProtocolDeclarationSyntax protocolDeclaration:
				return ResolveGenericDeclaration(context, index, source, protocolDeclaration, protocolDeclaration.Span, protocolDeclaration.GenericParameters, null, null, position)
					?? OnName(NameSpan(source, protocolDeclaration.Span, protocolDeclaration.Name, fromEnd: false), protocolDeclaration.Name, position, ResolvedSymbolKind.Protocol, protocolDeclaration, Display(protocolDeclaration, protocolDeclaration.Name));
			case DelegateDeclarationSyntax delegateDeclaration:
				return ResolveGenericDeclaration(context, index, source, delegateDeclaration, delegateDeclaration.Span, delegateDeclaration.GenericParameters, null, null, position)
					?? ResolveTypeReference(context, index, source, delegateDeclaration.ReturnTypeSpan, delegateDeclaration.ReturnType, position)
					?? OnName(NameSpan(source, delegateDeclaration.Span, delegateDeclaration.Name, fromEnd: false), delegateDeclaration.Name, position, ResolvedSymbolKind.Delegate, delegateDeclaration, Display(delegateDeclaration, delegateDeclaration.Name));
			case ProtocolMethodDeclarationSyntax protocolMember:
				return ResolveTypeReference(context, index, source, protocolMember.Span, protocolMember.ReturnType, position)
					?? OnName(NameSpan(source, protocolMember.Span, protocolMember.Name, fromEnd: false), protocolMember.Name, position, ResolvedSymbolKind.Method, protocolMember, $"{protocolMember.ReturnType} {protocolMember.Name}({ParameterList(protocolMember.Parameters)})");
			case TypeAliasDeclarationSyntax typeAlias:
				return OnName(NameSpan(source, typeAlias.Span, typeAlias.Name, fromEnd: false), typeAlias.Name, position, ResolvedSymbolKind.TypeAlias, typeAlias, Display(typeAlias, typeAlias.Name));
			case UsingDirectiveSyntax usingDirective when usingDirective.IsAlias:
				return ResolveNamespaceAliasDirective(source, usingDirective, position);
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
					?? ResolveDeclaredTypeParameter(context, unit, source, structField.Span, structField.Type, position)
					?? OnName(NameSpan(source, structField.Span, structField.Name, fromEnd: false), structField.Name, position, ResolvedSymbolKind.Field, structField, Display(structField, structField.Name));
			case UnionFieldSyntax unionField:
				return ResolveTypeReference(context, index, source, unionField.Span, unionField.Type, position)
					?? ResolveDeclaredTypeParameter(context, unit, source, unionField.Span, unionField.Type, position)
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

	/// <summary>
	/// Resolves a declared type that names one of the enclosing template's own type parameters
	/// (the <c>T*</c> field of <c>Heap&lt;T, A&gt;</c>). The parameter belongs to the generic
	/// declaration rather than to the field, so navigation lands on the declaration that owns it.
	/// </summary>
	private static ResolvedSymbol? ResolveDeclaredTypeParameter(
		BindingContext context,
		CompilationUnitSyntax unit,
		string source,
		TextSpan declaredSpan,
		string? writtenType,
		int position)
	{
		var leaf = LeafType(writtenType).TrimEnd('*', '[', ']');
		if (leaf.Length == 0 || !context.IsActiveGenericParameter(leaf))
			return null;

		var span = NameSpan(source, declaredSpan, leaf, fromEnd: false);
		if (position < span.Start || position > span.End)
			return null;

		foreach (var declaration in Members(unit))
		{
			if (StrictContains(declaration.Span, declaredSpan.Start) && DeclaresTypeParameter(declaration, leaf))
				return new ResolvedSymbol(ResolvedSymbolKind.TypeParameter, leaf, null, span, declaration, leaf);
		}

		return null;
	}

	private static bool DeclaresTypeParameter(SyntaxNode declaration, string name) => declaration switch
	{
		StructDeclarationSyntax structDeclaration => structDeclaration.GenericParameters.Contains(name),
		UnionDeclarationSyntax unionDeclaration => unionDeclaration.GenericParameters.Contains(name),
		ExtensionDeclarationSyntax extension => extension.GenericParameters.Contains(name),
		_ => false,
	};

	/// <summary>
	/// Resolves a type parameter written in a generic declaration's parameter list, and any bound or
	/// default type named alongside it. A generic declaration records those names as text rather
	/// than as nodes, so every occurrence is located inside the declaration's own span. Navigation
	/// lands on the declaration that owns the parameter, because that is the only place the
	/// parameter is defined — a template's parameter list has no node of its own to land on.
	/// </summary>
	private static ResolvedSymbol? ResolveGenericDeclaration(
		BindingContext context,
		DeclarationIndex index,
		string source,
		SyntaxNode declaration,
		TextSpan declarationSpan,
		IReadOnlyList<string> genericParameters,
		IReadOnlyDictionary<string, List<string>>? genericParameterConstraints,
		IReadOnlyDictionary<string, string>? genericParameterDefaults,
		int position)
	{
		if (genericParameters.Count > 0)
		{
			var open = source.IndexOf('<', Math.Clamp(declarationSpan.Start, 0, source.Length));
			if (open >= 0 && open < declarationSpan.End)
			{
				var close = MatchingAngle(source, open, declarationSpan.End);
				if (close > open)
				{
					var parameterList = new TextSpan(open, close - open);
					foreach (var parameter in genericParameters)
					{
						var span = NameSpan(source, parameterList, parameter, fromEnd: true);
						if (position >= span.Start && position <= span.End)
							return new ResolvedSymbol(ResolvedSymbolKind.TypeParameter, parameter, null, span, declaration, parameter);
					}
				}
			}
		}

		if (genericParameterConstraints is not null)
		{
			foreach (var bound in genericParameterConstraints.Values.SelectMany(names => names))
			{
				if (ResolveTypeReference(context, index, source, declarationSpan, bound, position) is { } boundSymbol)
					return boundSymbol;
			}
		}

		if (genericParameterDefaults is not null)
		{
			foreach (var fallback in genericParameterDefaults.Values)
			{
				if (ResolveTypeReference(context, index, source, declarationSpan, fallback, position) is { } defaultSymbol)
					return defaultSymbol;
			}
		}

		return null;
	}

	/// <summary>
	/// The index of the <c>&gt;</c> that closes the <c>&lt;</c> at <paramref name="open"/>, or -1
	/// when the list is never closed inside <paramref name="limit"/>. Nested lists are counted, so a
	/// parameter written as <c>Option&lt;T&gt;</c> does not end the outer list.
	/// </summary>
	private static int MatchingAngle(string source, int open, int limit)
	{
		var depth = 0;
		for (var index = open; index < Math.Min(limit, source.Length); index++)
		{
			if (source[index] == '<')
			{
				depth++;
			}
			else if (source[index] == '>' && --depth == 0)
			{
				return index;
			}
		}

		return -1;
	}

	/// <summary>
	/// Classifies a function declaration by where it lives. An associated function is an extension
	/// member that is called through its owner type, so it stays distinct from a receiver-backed
	/// extension method and from a free function.
	/// </summary>
	private static ResolvedSymbolKind DeclarationKind(CompilationUnitSyntax unit, FunctionDeclarationSyntax function)
	{
		foreach (var member in Members(unit))
		{
			if (member is not ExtensionDeclarationSyntax extension || !extension.Methods.Contains(function))
				continue;

			return function.IsAssociated
				? ResolvedSymbolKind.AssociatedFunction
				: ResolvedSymbolKind.ExtensionMethod;
		}

		return ResolvedSymbolKind.Function;
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

	/// <summary>
	/// Resolves the alias name inside <c>using Alias = Namespace.Path;</c>. Only the alias name is
	/// the binding: the target namespace keeps its own identity, so navigating or renaming the
	/// target text is never treated as an alias occurrence.
	/// </summary>
	private static ResolvedSymbol? ResolveNamespaceAliasDirective(string source, UsingDirectiveSyntax directive, int position)
	{
		if (directive.AliasSpan is not { } aliasSpan || position < aliasSpan.Start || position > aliasSpan.End)
			return null;

		return DescribeNamespaceAlias(directive);
	}

	/// <summary>
	/// Resolves a written use of a namespace alias prefix. Only reached after the local, parameter,
	/// and global lookups have failed, so a runtime value that shares the alias' spelling still wins.
	/// </summary>
	private static ResolvedSymbol? ResolveNamespaceAliasUse(BindingContext context, CompilationUnitSyntax unit, IdentifierExpressionSyntax identifier)
	{
		return context.GetNamespaceAliases(unit).TryGetValue(identifier.Name, out var directive)
			? DescribeNamespaceAlias(directive)
			: null;
	}

	private static ResolvedSymbol DescribeNamespaceAlias(UsingDirectiveSyntax directive)
	{
		var alias = directive.AliasName ?? directive.NamespaceName;
		var span = directive.AliasSpan ?? directive.Span;
		return new ResolvedSymbol(
			ResolvedSymbolKind.NamespaceAlias,
			alias,
			directive.NamespaceName,
			span,
			directive,
			$"namespace alias {alias} = {directive.NamespaceName}");
	}

	private static bool IsOnCallName(string source, CallExpressionSyntax call, int position)
	{
		var span = NameSpan(source, call.Span, Leaf(call.FunctionName), fromEnd: false);
		return position >= span.Start && position <= span.End;
	}

	/// <summary>
	/// Whether the position is on a call's callee at all — its member name or, for a dotted callee
	/// such as <c>Layout.FromType</c>, its receiver. The receiver is part of the callee the reader
	/// navigated to, so it must be claimable even though it is not the call's function name.
	/// </summary>
	private static bool IsOnCallTarget(string source, CallExpressionSyntax call, int position)
	{
		if (IsOnCallName(source, call, position))
			return true;

		var separator = call.FunctionName.LastIndexOf('.');
		if (separator <= 0)
			return false;

		var receiverName = call.FunctionName[..separator];
		var start = FindName(source, call.Span.Start, call.Span.End, receiverName, fromEnd: false);
		return start >= 0 && position >= start && position <= start + receiverName.Length;
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
		OperatorDeclarationSyntax op => $"{op.ReturnType} operator {op.Operator}({ParameterList(op.Parameters)})",
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

		var index = FindName(source, start, end, name, fromEnd);
		return index < 0 ? new TextSpan(start, Math.Min(name.Length, end - start)) : new TextSpan(index, name.Length);
	}

	/// <summary>
	/// Locates a whole-word occurrence of <paramref name="name"/> inside <c>[start, end)</c>. A name that
	/// only appears as part of a longer identifier - the <c>v</c> in the keyword <c>val</c>, the
	/// <c>Count</c> in <c>Counter</c> - does not name the symbol, so those occurrences are skipped.
	/// </summary>
	private static int FindName(string source, int start, int end, string name, bool fromEnd)
	{
		var limit = Math.Min(end, source.Length);
		var index = fromEnd
			? (limit > 0 ? source.LastIndexOf(name, limit - 1, StringComparison.Ordinal) : -1)
			: source.IndexOf(name, start, StringComparison.Ordinal);
		while (index >= start && index + name.Length <= limit)
		{
			var before = index > 0 ? source[index - 1] : ' ';
			var after = index + name.Length < source.Length ? source[index + name.Length] : ' ';
			if (!IsWordChar(before) && !IsWordChar(after))
				return index;

			index = fromEnd
				? (index > start ? source.LastIndexOf(name, index - 1, StringComparison.Ordinal) : -1)
				: source.IndexOf(name, index + 1, StringComparison.Ordinal);
		}

		return -1;
	}

	private static bool IsWordChar(char value) => char.IsLetterOrDigit(value) || value == '_';

	private static string Leaf(string name)
	{
		var dot = name.LastIndexOf('.');
		return dot < 0 ? name : name[(dot + 1)..];
	}

	private static bool Contains(TextSpan span, int position) => span.Start <= position && position <= span.End;

	private static bool StrictContains(TextSpan span, int position) => span.Start <= position && position < span.End;
}