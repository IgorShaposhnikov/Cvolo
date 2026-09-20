using System.Runtime.CompilerServices;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;

namespace Cvolo.Analysis.Semantics;

/// <summary>
/// Per-binding-context index from a compiler-mangled symbol name (functions and named types) to
/// the declaration node that produced it. Names are re-derived with the binder's own mangling and
/// type-resolution primitives, so no occurrence is matched by raw text.
/// </summary>
internal sealed class DeclarationIndex
{
	private static readonly ConditionalWeakTable<BindingContext, DeclarationIndex> Cache = new();

	private readonly Dictionary<string, SyntaxNode> _functions = new(StringComparer.Ordinal);
	private readonly Dictionary<string, SyntaxNode> _types = new(StringComparer.Ordinal);

	private DeclarationIndex()
	{
	}

	public static DeclarationIndex For(BindingContext context)
		=> Cache.GetValue(context, Build);

	public SyntaxNode? FindFunction(string mangledName)
		=> Lookup(_functions, mangledName);

	public SyntaxNode? FindType(string mangledName)
		=> Lookup(_types, mangledName);

	/// <summary>
	/// Finds a named type by its unqualified leaf name regardless of namespace, used for
	/// compiler-global constructs (attributes) that do not depend on the active usings.
	/// </summary>
	public SyntaxNode? FindTypeByLeafName(string leafName)
	{
		foreach (var (name, node) in _types)
		{
			if (string.Equals(LeafName(name), leafName, StringComparison.Ordinal))
				return node;
		}

		return null;
	}

	private static string LeafName(string mangledName)
	{
		var generic = mangledName.IndexOf('<');
		var name = generic > 0 ? mangledName[..generic] : mangledName;
		var dot = name.LastIndexOf('.');
		return dot < 0 ? name : name[(dot + 1)..];
	}

	private static SyntaxNode? Lookup(Dictionary<string, SyntaxNode> map, string mangledName)
	{
		if (map.TryGetValue(mangledName, out var node))
			return node;

		var genericIndex = mangledName.IndexOf('<');
		if (genericIndex > 0 && map.TryGetValue(mangledName[..genericIndex], out var template))
			return template;

		return null;
	}

	private static DeclarationIndex Build(BindingContext context)
	{
		var index = new DeclarationIndex();
		foreach (var unit in context.FileContexts.Keys)
			index.VisitUnit(context, unit);
		return index;
	}

	private void VisitUnit(BindingContext context, CompilationUnitSyntax unit)
	{
		var previousUnit = context.CurrentUnit;
		var previousNamespace = context.CurrentNamespace;
		context.CurrentUnit = unit;
		context.CurrentNamespace = unit.NamespaceDeclaration?.Name;
		try
		{
			var ns = unit.NamespaceDeclaration?.Name;
			if (unit.NamespaceDeclaration is { } namespaceDeclaration)
			{
				foreach (var member in namespaceDeclaration.Members)
					Visit(context, member, ns);
			}

			foreach (var member in unit.Members)
				Visit(context, member, ns);
		}
		finally
		{
			context.CurrentUnit = previousUnit;
			context.CurrentNamespace = previousNamespace;
		}
	}

	private void Visit(BindingContext context, SyntaxNode node, string? ns)
	{
		switch (node)
		{
			case FunctionDeclarationSyntax function:
				RegisterFunction(context, function, ns);
				break;
			case ExtensionDeclarationSyntax extension:
				RegisterExtension(context, extension, ns);
				break;
			case ExternDeclarationSyntax externDeclaration:
				_functions[externDeclaration.Name] = externDeclaration;
				break;
			case ExternBlockSyntax externBlock:
				foreach (var function in externBlock.Functions)
					_functions[context.GetMangledName(function.Name, ns)] = function;
				break;
			case StructDeclarationSyntax structDeclaration:
				_types[context.GetMangledName(structDeclaration.Name, ns)] = structDeclaration;
				break;
			case UnionDeclarationSyntax unionDeclaration:
				_types[context.GetMangledName(unionDeclaration.Name, ns)] = unionDeclaration;
				break;
			case EnumDeclarationSyntax enumDeclaration:
				_types[context.GetMangledName(enumDeclaration.Name, ns)] = enumDeclaration;
				break;
			case InterfaceDeclarationSyntax interfaceDeclaration:
				_types[context.GetMangledName(interfaceDeclaration.Name, ns)] = interfaceDeclaration;
				break;
			case ProtocolDeclarationSyntax protocolDeclaration:
				_types[context.GetMangledName(protocolDeclaration.Name, ns)] = protocolDeclaration;
				break;
			case TypeAliasDeclarationSyntax typeAlias:
				_types[context.GetMangledName(typeAlias.Name, ns)] = typeAlias;
				break;
		}
	}

	private void RegisterFunction(BindingContext context, FunctionDeclarationSyntax function, string? ns)
	{
		var baseName = function.Name is "main" or "Main" ? "main" : context.GetMangledName(function.Name, ns);
		_functions[baseName] = function;

		if (TryResolveParameters(context, function.Parameters, out var parameterTypes))
			_functions[context.GetOverloadedMangledName(baseName, parameterTypes)] = function;
	}

	private void RegisterExtension(BindingContext context, ExtensionDeclarationSyntax extension, string? ns)
	{
		var extendedType = context.ResolveType(context.NormalizeGenericName(extension.ExtendedTypeName));

		foreach (var method in extension.Methods)
		{
			var baseName = context.GetMangledName($"{extension.ExtendedTypeName}.{method.Name}", ns);
			_functions[baseName] = method;

			if (extendedType is null || !TryResolveParameters(context, method.Parameters, out var parameterTypes))
				continue;

			var withReceiver = new List<TypeSymbol>(parameterTypes.Count + 1)
			{
				new PointerTypeSymbol(extendedType, isMutable: false),
			};
			withReceiver.AddRange(parameterTypes);
			_functions[context.GetOverloadedMangledName(baseName, withReceiver)] = method;
		}

		foreach (var constructor in extension.Constructors)
		{
			var baseName = context.GetMangledName(extension.ExtendedTypeName, ns);
			_functions[baseName] = constructor;

			if (TryResolveParameters(context, constructor.Parameters, out var parameterTypes))
				_functions[context.GetOverloadedMangledName(baseName, parameterTypes)] = constructor;
		}

		foreach (var destructor in extension.Destructors)
			_functions[context.GetMangledName($"{extension.ExtendedTypeName}.~{destructor.StructName}", ns)] = destructor;
	}

	private static bool TryResolveParameters(BindingContext context, IReadOnlyList<ParameterSyntax> parameters, out List<TypeSymbol> types)
	{
		types = new List<TypeSymbol>(parameters.Count);
		foreach (var parameter in parameters)
		{
			var type = context.ResolveType(context.NormalizeGenericName(parameter.Type));
			if (type is null)
				return false;

			types.Add(type);
		}

		return true;
	}
}
