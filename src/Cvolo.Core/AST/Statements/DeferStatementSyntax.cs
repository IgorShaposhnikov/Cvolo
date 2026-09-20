using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Core.AST.Statements;

public sealed class DeferStatementSyntax(
	TextSpan span,
	SyntaxNode body,
	string? label = null,
	bool lexicalCapture = false,
	bool boundaryAware = false) : SyntaxNode(span)
{
	public override SyntaxKind Kind => SyntaxKind.DeferStatement;

	// Non-null for a targeted defer (`defer label { ... };`): the body is anchored to the
	// nearest enclosing block labeled `label` rather than the immediately enclosing block.
	// Null anchors to the immediately enclosing block.
	public string? TargetLabel { get; } = label;

	public SyntaxNode Body { get; } = body;

	/// <summary>
	/// Compiler-internal flag (spec §4.4 <c>CaptureMode = Lexical</c>). Set only by
	/// <see cref="Rewriters.TryCatchRewriter"/> on the synthesized <c>finally</c> defer:
	/// no value-capture temporaries are generated at registration time; the body observes
	/// the live state of enclosing bindings when it actually executes.
	/// </summary>
	public bool IsLexicalCapture { get; } = lexicalCapture;

	/// <summary>
	/// Compiler-internal flag (spec §4.4 <c>ScopeExitMode = BoundaryAware</c>). Set only on
	/// the synthesized <c>finally</c> defer: on an exit edge crossing nested scopes the body
	/// runs only after every inner scope's cleanup has completed.
	/// </summary>
	public bool IsBoundaryAware { get; } = boundaryAware;

	public override IEnumerable<SyntaxNode> GetChildren() => [Body];
}
