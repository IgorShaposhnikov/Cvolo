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
	private readonly Dictionary<string, (string OkTypeExpr, string ErrorTypeExpr, List<string> GenericParameters)> _resultUnions = new(StringComparer.Ordinal);
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
					if (HasResultAttribute(un.Attributes))
						IndexResultUnion(un);
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
		if (!TryParseGenericType(returnType, out var baseName, out var arguments))
			return;

		if (!_resultUnions.TryGetValue(baseName, out var shape))
			return;

		var okType = Substitute(shape.OkTypeExpr, shape.GenericParameters, arguments);
		var errorType = Substitute(shape.ErrorTypeExpr, shape.GenericParameters, arguments);
		if (okType.Length > 0 && errorType.Length > 0)
			_resultTypes[name] = (okType, errorType);
	}

	private void IndexResultUnion(UnionDeclarationSyntax union)
	{
		UnionFieldSyntax? ok = null;
		UnionFieldSyntax? error = null;
		var variantCount = 0;
		foreach (var field in union.Fields)
		{
			if (field.IsVoidVariant && field.Name != "Ok")
				continue;

			variantCount++;
			if (field.Name == "Ok")
				ok = field;
			else if (field.Name == "Err")
				error = field;
		}

		if (ok is null || error is null || variantCount != 2)
			return;

		_resultUnions[union.Name] = (ok.Type, error.Type, union.GenericParameters.ToList());
	}

	private static bool HasResultAttribute(IReadOnlyList<AttributeSyntax> attributes) =>
		attributes.Any(a => NormalizeAttributeName(a.Name) == "Result");

	private static string NormalizeAttributeName(string name)
	{
		var dot = name.LastIndexOf('.');
		var leaf = dot >= 0 ? name[(dot + 1)..] : name;
		return leaf.EndsWith("Attribute", StringComparison.Ordinal) ? leaf[..^"Attribute".Length] : leaf;
	}

	private static bool TryParseGenericType(string text, out string baseName, out List<string> arguments)
	{
		baseName = "";
		arguments = [];
		text = text.Trim();
		if (text.Length == 0)
			return false;

		var open = text.IndexOf('<');
		if (open < 0)
		{
			baseName = text;
			return true;
		}

		if (!text.EndsWith('>'))
			return false;

		baseName = text[..open].Trim();
		if (baseName.Length == 0)
			return false;

		var inner = text[(open + 1)..^1];
		var depth = 0;
		var start = 0;
		for (var i = 0; i < inner.Length; i++)
		{
			switch (inner[i])
			{
				case '<':
					depth++;
					break;
				case '>':
					depth--;
					break;
				case ',' when depth == 0:
					arguments.Add(inner[start..i].Trim());
					start = i + 1;
					break;
			}
		}

		arguments.Add(inner[start..].Trim());
		return arguments.All(a => a.Length > 0);
	}

	private static string Substitute(string expression, IReadOnlyList<string> parameters, IReadOnlyList<string> arguments)
	{
		if (parameters.Count == 0 || parameters.Count != arguments.Count)
			return expression;

		var builder = new System.Text.StringBuilder(expression.Length);
		var i = 0;
		while (i < expression.Length)
		{
			var c = expression[i];
			if (char.IsLetter(c) || c == '_')
			{
				var start = i;
				while (i < expression.Length && (char.IsLetterOrDigit(expression[i]) || expression[i] == '_'))
					i++;

				var identifier = expression[start..i];
				var replaced = false;
				for (var p = 0; p < parameters.Count; p++)
				{
					if (identifier == parameters[p])
					{
						builder.Append(arguments[p]);
						replaced = true;
						break;
					}
				}

				if (!replaced)
					builder.Append(identifier);
			}
			else
			{
				builder.Append(c);
				i++;
			}
		}

		return builder.ToString();
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
