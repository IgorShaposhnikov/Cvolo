using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Expressions;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Core.AST.Declarations;

public sealed class AttributeSyntax(TextSpan span, string name, IReadOnlyList<ExpressionSyntax> arguments, IReadOnlyList<string?>? argumentNames = null) : SyntaxNode(span)
{
	public override SyntaxKind Kind => SyntaxKind.Attribute;

	public string Name { get; } = name;
	public IReadOnlyList<ExpressionSyntax> Arguments { get; } = arguments;

	/// <summary>Parallel to <see cref="Arguments"/>; null for positional args, the identifier for named args (e.g. `win:` in [LibraryImport]).</summary>
	public IReadOnlyList<string?> ArgumentNames { get; } = argumentNames ?? new string?[arguments.Count];

	public override IEnumerable<SyntaxNode> GetChildren() => Arguments;
}
