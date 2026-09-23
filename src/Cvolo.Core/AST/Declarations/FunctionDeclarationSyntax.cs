using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Statements;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Core.AST.Declarations;

public sealed class FunctionDeclarationSyntax(
	TextSpan span,
	string returnType,
	string name,
	IReadOnlyList<string> genericParameters,
	IReadOnlyList<ParameterSyntax> parameters,
	BlockStatementSyntax body,
	IReadOnlyList<AttributeSyntax>? attributes = null,
	SafetyTier? modifier = null,
	ReceiverContract receiver = ReceiverContract.None,
	Visibility? visibility = null,
	TextSpan? nameSpan = null,
	TextSpan? returnTypeSpan = null,
	string? callingConvention = null) : SyntaxNode(span)
{
	public override SyntaxKind Kind => SyntaxKind.FunctionDeclaration;

	public string ReturnType { get; } = returnType;
	public string Name { get; } = name;

	/// <summary>
	/// Span of just the declared name token, for diagnostics that should highlight the name
	/// rather than the whole declaration (falls back to <see cref="SyntaxNode.Span"/>).
	/// </summary>
	public TextSpan NameSpan { get; } = nameSpan ?? span;

	/// <summary>
	/// Span of just the return-type clause, for diagnostics that should highlight the type
	/// rather than the whole declaration (falls back to <see cref="SyntaxNode.Span"/>).
	/// </summary>
	public TextSpan ReturnTypeSpan { get; } = returnTypeSpan ?? span;
	public IReadOnlyList<string> GenericParameters { get; } = genericParameters;
	public IReadOnlyList<ParameterSyntax> Parameters { get; } = parameters;
	public BlockStatementSyntax Body { get; } = body;
	public bool HasBody => Body is not null;
	public IReadOnlyList<AttributeSyntax> Attributes { get; } = attributes ?? [];
	public SafetyTier? Modifier { get; } = modifier;
	public ReceiverContract Receiver { get; } = receiver;
	public Visibility Visibility { get; } = visibility ?? Visibility.Internal;
	public Visibility? SyntacticVisibility { get; } = visibility;

	/// <summary>
	/// The declared calling convention ("C" or "system") for a native-ABI function written as
	/// 'unsafe "C" R F(...) { ... }'. Null for ordinary Cvolo functions.
	/// </summary>
	public string? CallingConvention { get; } = callingConvention;

	public override IEnumerable<SyntaxNode> GetChildren()
	{
		foreach (var p in Parameters)
			yield return p;
		if (Body is not null)
			yield return Body;
	}
}
