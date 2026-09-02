using Cvolo.Core.AST;
using Cvolo.Core.AST.Base;

namespace Cvolo.Analysis.Symbols.Base;

public abstract class Symbol(string name)
{
	public string Name { get; } = name;

	/// <summary>Effective visibility scope of this symbol under the Visibility &amp; Access Control spec.</summary>
	public Visibility Visibility { get; set; } = Visibility.Internal;

	/// <summary>The compilation unit that declared this symbol; the cross-file boundary for private access.</summary>
	public CompilationUnitSyntax? DeclaringUnit { get; set; }
}