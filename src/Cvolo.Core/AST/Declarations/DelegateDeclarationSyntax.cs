using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Core.AST.Declarations;

public sealed class DelegateDeclarationSyntax(
	TextSpan span,
	string returnType,
	string name,
	IReadOnlyList<string> genericParameters,
	IReadOnlyList<ParameterSyntax> parameters,
	Visibility? visibility = null,
	bool isNative = false,
	string? callingConvention = null,
	TextSpan? nameSpan = null,
	TextSpan? returnTypeSpan = null) : SyntaxNode(span)
{
	public override SyntaxKind Kind => SyntaxKind.DelegateDeclaration;

	public string ReturnType { get; } = returnType;
	public string Name { get; } = name;

	public TextSpan NameSpan { get; } = nameSpan ?? span;
	public TextSpan ReturnTypeSpan { get; } = returnTypeSpan ?? span;

	public IReadOnlyList<string> GenericParameters { get; } = genericParameters;
	public IReadOnlyList<ParameterSyntax> Parameters { get; } = parameters;

	public Visibility Visibility { get; } = visibility ?? Visibility.Internal;
	public Visibility? SyntacticVisibility { get; } = visibility;

	public bool IsNative { get; } = isNative;
	public string? CallingConvention { get; } = callingConvention;

	public override IEnumerable<SyntaxNode> GetChildren()
	{
		foreach (var p in Parameters)
			yield return p;
	}
}