using Cvolo.Tests.Core;

namespace Cvolo.Tests;

public sealed class VisibilityTests : CompilerTestBase
{
	[Fact]
	public void CrossFile_PrivateField_Access_Is_Rejected()
	{
		var (exitCode, stdout, stderr) = RunCompiler("Visibility/AccessControl");

		Assert.Equal(1, exitCode);
		Assert.Contains("Compile Error CVL1030", stderr);
		Assert.Contains("Member 'Hidden' on type 'Secret' is inaccessible due to its visibility level.", stderr);
	}

	[Fact]
	public void CrossFile_PrivateField_LiteralInit_Is_Rejected()
	{
		var (exitCode, stdout, stderr) = RunCompiler("Visibility/AccessControl");

		Assert.Equal(1, exitCode);
		Assert.Contains("Cannot initialize private field 'Hidden' using an external struct literal. Use an authorized constructor within the type's defining package module boundary.", stderr);
	}

	[Fact]
	public void LegacyVisibility_Flag_Restores_Version_020_Behavior()
	{
		var (exitCode, stdout, stderr) = RunCompiler("Visibility/AccessControl", "--legacy-visibility");

		Assert.Equal(0, exitCode);
		Assert.DoesNotContain("CVL103", stderr);
	}

	[Fact]
	public void Unbound_PrivateRefField_Mutation_Is_Rejected()
	{
		var (exitCode, stdout, stderr) = RunCompiler("Visibility/UnboundSandbox");

		Assert.Equal(1, exitCode);
		Assert.Contains("Compile Error CVL1035", stderr);
		Assert.Contains("Structural mutation of refvar field 'Next' is blocked because it is not visible to this compilation scope.", stderr);
	}

	[Fact]
	public void PrivateVariant_SwitchBinding_Is_Rejected()
	{
		var (exitCode, stdout, stderr) = RunCompiler("Visibility/PrivateVariant");

		Assert.Equal(1, exitCode);
		Assert.Contains("Compile Error CVL1034", stderr);
		Assert.Contains("Pattern matching binding failed for type 'UResult'. Payload variant field 'HiddenCode' is obscured by visibility constraints.", stderr);
	}

	[Theory]
	[InlineData("Visibility/ExtensionWiden.cvl",
		"Compile Error CVL1031",
		"Element 'GetX' cannot declare a wider visibility modifier than its enclosing extension block visibility level (Internal).")]
	[InlineData("Visibility/PublicExtern.cvl",
		"Compile Error CVL1033",
		"Global 'extern' declarations cannot be marked public. Wrap foreign symbols in a safe, standard public Cvolo routine to expose them across package boundaries.")]
	[InlineData("Visibility/PublicGlobal.cvl",
		"Compile Error CVL1036",
		"Shared multi-word container 'Buffer' cannot be exposed publicly without synchronization. Potential 16-byte register tearing and Type Confusion detected. Wrap the global in a 'Lock' or 'Mutex'.")]
	[InlineData("Visibility/GenericLeak.cvl",
		"Compile Error CVL1038",
		"The visibility of generic type instantiation 'Box<Secret>' exceeds the visibility of its type argument 'Secret'. Upgrade the argument visibility or restrict the parent declaration.")]
	public void Invalid_Visibility_Declarations_Are_Rejected(string caseFile, string idText, string message)
	{
		var (exitCode, stdout, stderr) = RunCompiler(caseFile);

		Assert.Equal(1, exitCode);
		Assert.Contains(idText, stderr);
		Assert.Contains(message, stderr);
	}

	[Fact]
	public void Visibility_Modifiers_Compile_And_Run_In_Single_File()
	{
		var fileName = "Visibility/Positive.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary("Positive", "Visibility");
		Assert.Equal(0, runCode);
	}
}