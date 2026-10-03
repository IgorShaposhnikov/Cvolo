using Cvolo.Tests.Core;

namespace Cvolo.Tests.Generics;

/// <summary>
/// Regression coverage for binding open generic extension templates before any concrete
/// specialization exists. The extension declaration's own type parameters must be in scope
/// while its member signatures and bodies are validated.
/// </summary>
public sealed class GenericExtensionTemplateTests : CompilerTestBase
{
	[Fact]
	public void OpenAssociatedFunction_BindsWithExtensionGenericScope()
	{
		const string fileName = "Generics/Templates/OpenAssociatedExtensionScope.cvl";
		var (exitCode, stdout, stderr) = RunCompilerCheck(fileName);

		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);
		Assert.DoesNotContain("Undefined variable 'value'", stderr);
		Assert.DoesNotContain("No overload of function 'Result'", stderr);
	}
}
