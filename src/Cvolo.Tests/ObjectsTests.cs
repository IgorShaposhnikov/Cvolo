using Cvolo.Tests.Core;

namespace Cvolo.Tests;

public sealed class ObjectsTests : CompilerTestBase
{
	[Theory]
	[InlineData("StructInit", "10,20")]
	[InlineData("StructNested", "15,20")]
	[InlineData("ExtensionSuccess", "Point Coords: X = 15, Y = 25")]
	[InlineData("ConstructorBasic", "A=21, B=42")]
	[InlineData("ConstructorRaii", "Opening logs.txt\nOpening temp.txt\nwork\nClosing handle: 42\nClosing handle: 42")]
	[InlineData("ConstructorNamespaceVar", "window=42")]
	public void Structs(string caseName, string expected)
	{
		var fileName = $"Objects/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary(caseName, "Objects");
		Assert.Equal(0, runCode);
		Assert.Contains(expected.Replace("\r\n", "\n").Trim(), runStdout.Replace("\r\n", "\n").Trim());
	}

	[Theory]
	[InlineData("ExtensionMutabilityFail", "No overload of function 'p.Move' matches")]
	[InlineData("ConstructorDefensiveFail", "Defensive initialization: constructor 'Pair' does not initialize field 'B'")]
	[InlineData("DestructorNameMismatchFail", "Destructor name '~WrongName' does not match extended type 'Pair'")]
	[InlineData("AssociatedFunctionThroughValueFail", "Associated function `MemBlock.AlignUp` must be called through the type.")]
	[InlineData("InstanceMethodThroughTypeFail", "'IsAligned' is an instance method of 'MemBlock' and must be called on a value, not through the type.")]
	[InlineData("AssociatedFieldAccessFail", "Undefined variable 'Size'")]
	[InlineData("AssociatedFunctionWithReceiverFail", "cannot declare an instance receiver")]
	[InlineData("LeadingDotOutsideExtensionFail", "Associated function declarations are only allowed inside an extension block.")]
	[InlineData("AssociatedFunctionInProtocolFail", "cannot be declared in an extension block on a protocol")]
	public void Extension_Methods_Rejections(string caseName, string expectedError)
	{
		var fileName = $"Objects/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);

		Assert.Equal(1, exitCode);
		Assert.Contains(expectedError, stderr);
	}

	[Theory]
	[InlineData("AssociatedFunctionSuccess", "aligned=48\nok=1\nrepeat=48\ngeneric=4")]
	[InlineData("AssociatedOverloadSeparation", "value=12\ntype=11")]
	[InlineData("GenericAssociatedFunctionSuccess", "value=42\nscaled=8")]
	public void Associated_Functions_Succeed(string caseName, string expected)
	{
		var fileName = $"Objects/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary(caseName, "Objects");
		Assert.Equal(0, runCode);
		Assert.Contains(expected.Replace("\r\n", "\n").Trim(), runStdout.Replace("\r\n", "\n").Trim());
	}
}
