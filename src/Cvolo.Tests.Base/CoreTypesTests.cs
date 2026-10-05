using Cvolo.Tests.Shared;

namespace Cvolo.Tests.Base;

/// <summary>
/// End-to-end coverage for Base SDK value types that are available without importing System.
/// Every successful case is built with <c>--freestanding</c> so the test cannot pass by accidentally
/// resolving a System module.
/// </summary>
public sealed class CoreTypesTests : CompilerTestBase
{
	[Theory]
	[InlineData("Option/Constructor.cvl", "Constructor")]
	[InlineData("Option/NoneSwitch.cvl", "NoneSwitch")]
	[InlineData("Result/OkAndErr.cvl", "OkAndErr")]
	[InlineData("Type/TypeIdentity.cvl", "TypeIdentity")]
	public void BaseCoreType_RuntimeContract(string fixture, string binaryName)
	{
		var (exitCode, stdout, stderr) = RunCompiler(fixture, "--freestanding");
		AssertCompilationSucceeded(exitCode, stdout, stderr, fixture);

		var folder = Path.GetDirectoryName(fixture)!.Replace('\\', '/');
		var (runCode, _) = ExecuteBinary(binaryName, folder);
		Assert.Equal(0, runCode);
	}

	[Fact]
	public void Option_WithMoveOnlyPayload_RemainsMoveOnly()
	{
		const string fixture = "Option/MoveOnlyPayloadFail.cvl";
		var (exitCode, _, stderr) = RunCompiler(fixture, "--freestanding");

		Assert.Equal(1, exitCode);
		Assert.Contains("Use of moved variable 'first'", stderr);
	}
}
