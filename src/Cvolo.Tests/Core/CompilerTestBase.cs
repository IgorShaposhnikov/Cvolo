namespace Cvolo.Tests.Core;

/// <summary>
/// Backward-compatible namespace shim. The implementation lives in
/// <see cref="Cvolo.Tests.Shared.CompilerTestBase"/> so every test project uses the same
/// compiler/file/isolation infrastructure.
/// </summary>
public abstract class CompilerTestBase : Cvolo.Tests.Shared.CompilerTestBase
{
}
