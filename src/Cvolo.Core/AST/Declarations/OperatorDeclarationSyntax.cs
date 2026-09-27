using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Statements;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Core.AST.Declarations;

/// <summary>
/// An operator overload declared inside an extension block, e.g.
/// <c>public Vec2 operator +(Vec2 left, Vec2 right) { ... }</c>.
/// </summary>
/// <remarks>
/// Operators are receiverless associated callables that share the leading-dot registration path;
/// they need no leading dot because the 'operator' keyword is already unambiguous. They are
/// deliberately a separate node rather than a <see cref="FunctionDeclarationSyntax"/> flag: the
/// declared name is punctuation, the parameter list is mandatory and receiver-free, and tooling
/// (hover, completion, definition, references, rename, semantic tokens) has to present the
/// operator spelling rather than an identifier.
/// </remarks>
public sealed class OperatorDeclarationSyntax(
	TextSpan span,
	string returnType,
	string operatorSpelling,
	string operatorToken,
	bool isUnary,
	IReadOnlyList<ParameterSyntax> parameters,
	BlockStatementSyntax? body,
	IReadOnlyList<AttributeSyntax>? attributes = null,
	Visibility? visibility = null,
	TextSpan? operatorSpan = null,
	TextSpan? returnTypeSpan = null) : SyntaxNode(span)
{
	public override SyntaxKind Kind => SyntaxKind.OperatorDeclaration;

	public string ReturnType { get; } = returnType;

	/// <summary>The declared operator as written in source, e.g. '+', '==', '&lt;&lt;'.</summary>
	public string Operator { get; } = operatorSpelling;

	/// <summary>
	/// Stable mangled token for the declared operator ('op_add', 'op_lshift', ...). This is the
	/// name the operator registers and emits under; see <see cref="OperatorTokens"/>.
	/// </summary>
	public string OperatorToken { get; } = operatorToken;

	/// <summary>True for prefix-position operators ('+', '-', '!', '~'), which take one operand.</summary>
	public bool IsUnary { get; } = isUnary;

	public IReadOnlyList<ParameterSyntax> Parameters { get; } = parameters;
	public BlockStatementSyntax? Body { get; } = body;
	public bool HasBody => Body is not null;
	public IReadOnlyList<AttributeSyntax> Attributes { get; } = attributes ?? [];

	/// <summary>Baseline visibility inherited from the enclosing extension block; null means unspecified.</summary>
	public Visibility Visibility { get; } = visibility ?? Visibility.Internal;
	public Visibility? SyntacticVisibility { get; } = visibility;

	/// <summary>Span of just the operator token, for diagnostics and navigation that target the operator.</summary>
	public TextSpan OperatorSpan { get; } = operatorSpan ?? span;

	/// <summary>Span of just the operator token; the operator is this declaration's name.</summary>
	public TextSpan NameSpan => OperatorSpan;

	/// <summary>Span of just the return-type clause, for diagnostics that should highlight the type.</summary>
	public TextSpan ReturnTypeSpan { get; } = returnTypeSpan ?? span;

	/// <summary>
	/// Operators are always associated (receiverless) callables; there is no receiver-backed form.
	/// </summary>
	public FunctionBindingKind BindingKind => FunctionBindingKind.Associated;

	/// <inheritdoc />
	public bool IsAssociated => true;

	/// <summary>The registration name: the mangled operator token.</summary>
	public string Name => OperatorToken;

	/// <summary>Human-readable form used by tooling, e.g. 'operator +'.</summary>
	public string DisplayName => OperatorTokens.Display(OperatorToken);

	/// <summary>
	/// True when parsing already rejected this declaration (for example an instance receiver in the
	/// parameter list). Declaration registration skips such a declaration instead of reporting
	/// follow-on errors about a shape that is already known to be invalid.
	/// </summary>
	public bool HasSyntaxError { get; init; }

	public override IEnumerable<SyntaxNode> GetChildren()
	{
		foreach (var p in Parameters)
			yield return p;
		if (Body is not null)
			yield return Body;
	}

	/// <summary>
	/// Operators flow through binding, validation and emission as ordinary receiverless functions
	/// named by their mangled token, so all existing associated-callable machinery applies unchanged.
	/// The body reference is shared, keeping diagnostic spans accurate.
	/// </summary>
	public FunctionDeclarationSyntax ToFunctionDeclaration()
		=> new(
			Span,
			ReturnType,
			OperatorToken,
			[],
			Parameters,
			Body,
			Attributes,
			visibility: SyntacticVisibility,
			nameSpan: OperatorSpan,
			returnTypeSpan: ReturnTypeSpan,
			bindingKind: FunctionBindingKind.Associated);
}
