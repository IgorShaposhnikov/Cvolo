using Cvolo.Analysis;
using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Emitter.LLVM;

public interface IEmitter
{
	string Emit(IReadOnlyList<CompilationUnitSyntax> units, CompilationContext context, BindingContext bindingContext);
}
