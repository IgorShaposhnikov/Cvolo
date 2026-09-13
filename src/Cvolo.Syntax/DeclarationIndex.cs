using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;

namespace Cvolo.Syntax;

/// <summary>
/// A lightweight, pre-binding cross-file declaration index built from every parsed
/// compilation unit (user sources plus the standard library). The <see cref="Rewriters.TryCatchRewriter"/>
/// uses it to decide which calls are <c>Result</c>-producing steps, which error type each
/// step can emit, which pattern types are declared here and how, and which declared types
/// carry the <c>[Error]</c> attribute.
/// </summary>
public sealed class DeclarationIndex
{
	private readonly Dictionary<string, (string OkType, string ErrorType)> _resultTypes = new(StringComparer.Ordinal);
	private readonly Dictionary<string, string> _typeKinds = new(StringComparer.Ordinal);
	private readonly HashSet<string> _errorAttributeTypes = new(StringComparer.Ordinal);

	public static DeclarationIndex Build(IEnumerable<CompilationUnitSyntax> asts)
	{
		var index = new DeclarationIndex();
		foreach (var ast in asts)
			index.ScanUnit(ast);
		return index;
	}

	private void ScanUnit(CompilationUnitSyntax unit)
	{
		ScanMembers(unit.Members);
		if (unit.NamespaceDeclaration is { } ns)
			ScanMembers(ns.Members);
	}

	private void ScanMembers(IReadOnlyList<SyntaxNode> members)
	{
		foreach (var member in members)
		{
			switch (member)
			{
				case FunctionDeclarationSyntax fn:
					IndexFunction(fn.Name, fn.ReturnType);
					break;
				case EnumDeclarationSyntax en:
					_typeKinds[en.Name] = "enum";
					if (HasErrorAttribute(en.Attributes))
						_errorAttributeTypes.Add(en.Name);
					break;
				case StructDeclarationSyntax st:
					_typeKinds[st.Name] = "struct";
					if (HasErrorAttribute(st.Attributes))
						_errorAttributeTypes.Add(st.Name);
					break;
				case UnionDeclarationSyntax un:
					_typeKinds[un.Name] = "union";
					if (HasErrorAttribute(un.Attributes))
						_errorAttributeTypes.Add(un.Name);
					break;
				case InterfaceDeclarationSyntax ifce:
					_typeKinds[ifce.Name] = "interface";
					break;
				case ProtocolDeclarationSyntax pr:
					_typeKinds[pr.Name] = "protocol";
					break;
				case NamespaceDeclarationSyntax nested:
					ScanMembers(nested.Members);
					break;
			}
		}
	}

	private void IndexFunction(string name, string returnType)
	{
		if (TryParseResultReturnType(returnType, out var okType, out var errorType))
		{
			_resultTypes[name] = (okType, errorType);
		}
	}

	/// <summary>
	/// Parses a <c>Result&lt;T, E&gt;</c> return-type string into its two type arguments.
	/// Generics nested inside the <c>T</c> slot (e.g. <c>Result&lt;List&lt;int&gt;, E&gt;</c>)
	/// are skipped via angle-bracket depth tracking; the separator comma is the one at depth 0.
	/// </summary>
	public static bool TryParseResultReturnType(string returnType, out string okType, out string errorType)
	{
		okType = "";
		errorType = "";
		if (!returnType.StartsWith("Result<", StringComparison.Ordinal))
			return false;

		var depth = 0;
		var start = "Result<".Length;
		for (var i = start; i < returnType.Length; i++)
		{
			switch (returnType[i])
			{
				case '<':
					depth++;
					break;
				case '>':
					if (depth == 0)
						return false; // outer close reached without a top-level comma
					depth--;
					break;
				case ',' when depth == 0:
					okType = returnType[start..i].Trim();
					var rest = returnType[(i + 1)..].Trim();
					if (rest.EndsWith('>'))
						rest = rest[..^1].Trim();
					errorType = rest;
					return okType.Length > 0 && errorType.Length > 0;
			}
		}

		return false;
	}

	private static bool HasErrorAttribute(IReadOnlyList<AttributeSyntax> attributes) =>
		attributes.Any(a => a.Name == "Error" || a.Name.EndsWith(".Error", StringComparison.Ordinal));

	public (string OkType, string ErrorType)? ResultTypes(string functionName) =>
		_resultTypes.TryGetValue(functionName, out var types) ? types : null;

	public string? ResultErrorType(string functionName) =>
		ResultTypes(functionName)?.ErrorType;

	public string? ResultOkType(string functionName) =>
		ResultTypes(functionName)?.OkType;

	public bool TryGetTypeKind(string typeName, out string kind) =>
		_typeKinds.TryGetValue(typeName, out kind!);

	public bool HasErrorAttribute(string typeName) => _errorAttributeTypes.Contains(typeName);
}
