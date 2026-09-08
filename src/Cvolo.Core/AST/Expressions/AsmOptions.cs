using Cvolo.Core.AST.Base;

namespace Cvolo.Core.AST.Expressions;

[Flags]
public enum AsmOptions
{
	None = 0,
	Volatile = 1 << 0,
	AlignStack = 1 << 1,
	Intel = 1 << 2,
}
