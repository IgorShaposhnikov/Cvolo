using Cvolo.Tests.Core;

namespace Cvolo.Tests;

public sealed class CollectionsTests : CompilerTestBase
{
	[Theory]
	[InlineData("ArrayInit", "10,20,30")]
	[InlineData("SliceLength", "5")]
	[InlineData("ArrayTypeInference", "100,200,300")]
	[InlineData("ArrayReplication", "Item 0: 42, Item 99: 42\nPoint 2: X = 10, Y = 20, Color R = 255")]
	public void ArraysAndSlices(string caseName, string expected)
	{
		var fileName = $"Collections/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary(caseName, "Collections");
		Assert.Equal(0, runCode);

		// Normalize line endings to avoid cross-platform CRLF (\r\n) vs LF (\n) match failures
		Assert.Contains(expected.Replace("\r\n", "\n").Trim(), runStdout.Replace("\r\n", "\n").Trim());
	}

	[Fact]
	public void ArrayBounds_Should_Panic_At_Runtime()
	{
		var fileName = "Collections/ArrayBoundsPanic.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary("ArrayBoundsPanic", "Collections");

		// Expected: Binary exits with non-zero code due to exit(1) in the panic block
		Assert.NotEqual(0, runCode);
		Assert.Contains("Runtime Error", runStdout);
		Assert.Contains("Index was outside the bounds", runStdout);
	}
}
