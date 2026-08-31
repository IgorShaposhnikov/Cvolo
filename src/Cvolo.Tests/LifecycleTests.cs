using Cvolo.Tests.Core;

namespace Cvolo.Tests;

public sealed class LifecycleTests : CompilerTestBase
{
	[Theory]
	[InlineData("ReceiverVar", "40")]
	[InlineData("ReceiverRef", "30, 10")]
	[InlineData("StrictMutability", "100")]
	public void Execution(string caseName, string expected)
	{
		var fileName = $"Lifecycle/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary(caseName, "Lifecycle");
		Assert.Equal(0, runCode);
		Assert.Contains(expected, runStdout);
	}

	[Theory]
	[InlineData("ArrayDestructorLoop", "main start", "dtor 300", "dtor 200", "dtor 100")]
	[InlineData("ArrayDestructorLoopMainScope", "main end", "dtor 20", "dtor 10", null)]
	[InlineData("ArrayDestructorLoopOneElement", "in block", "dtor 7", null, null)]
	public void ArrayDestructorLoop(string caseName, string midOutput, string firstDtor, string? secondDtor, string? thirdDtor)
	{
		var fileName = $"Lifecycle/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary(caseName, "Lifecycle");
		Assert.Equal(0, runCode);

		// Elements must be destroyed in reverse index order (Length-1 .. 0), so each
		// destructor's output must appear after all later-indexed destructors.
		var nl = Environment.NewLine;
		var expectedSequence = string.Join(nl, new[] { midOutput, firstDtor, secondDtor, thirdDtor }
			.Where(s => s is not null)!);
		Assert.Contains(expectedSequence, runStdout);
	}

	[Theory]
	[InlineData("NestedFieldDrop", "in block", "dtor 42", null)]
	[InlineData("NestedFieldDropTransitive", "in block", "dtor 200", "dtor 100")]
	public void NestedFieldDrop(string caseName, string midOutput, string firstDtor, string? secondDtor)
	{
		var fileName = $"Lifecycle/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary(caseName, "Lifecycle");
		Assert.Equal(0, runCode);

		// A struct without its own destructor must still drop its transitively embedded
		// resource-move fields; array elements must be dropped deepest-first in reverse order.
		var nl = Environment.NewLine;
		var expectedSequence = string.Join(nl, new[] { midOutput, firstDtor, secondDtor }
			.Where(s => s is not null)!);
		Assert.Contains(expectedSequence, runStdout);
	}

	[Theory]
	[InlineData("DestructorDepthWithinLimit", true)]
	[InlineData("DestructorPointerSelfRef", false)]
	public void DestructorDepthLimit(string caseName, bool expectDtor)
	{
		var fileName = $"Lifecycle/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary(caseName, "Lifecycle");
		Assert.Equal(0, runCode);
		Assert.Contains("main end", runStdout);

		// A chain within the 1024 nesting cap must still drop its leaf; a pointer edge must not.
		if (expectDtor)
		{
			var inBlock = runStdout.IndexOf("in block");
			var mainEnd = runStdout.IndexOf("main end");
			Assert.True(inBlock >= 0 && mainEnd > inBlock);
			Assert.Contains("dtor", runStdout.Substring(inBlock, mainEnd - inBlock));
		}
	}

	[Fact]
	public void AutoInferWarning_Emits_CVL1011()
	{
		var fileName = "Lifecycle/AutoInferWarning.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);
		Assert.Contains("CVL1011", stderr);
		Assert.Contains("Auto-inference chose mutability", stderr);
	}

	[Fact]
	public void SuppressAutoInferWarning_Hides_CVL1011_AndCompiles()
	{
		var fileName = "Lifecycle/SuppressAutoInferWarning.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);
		Assert.DoesNotContain("CVL1011", stderr);

		var (runCode, runStdout) = ExecuteBinary("SuppressAutoInferWarning", "Lifecycle");
		Assert.Equal(0, runCode);
		Assert.Contains("100", runStdout);
	}

	[Theory]
	[InlineData("ReceiverVarOnValFail", "No overload of function 'p.Move' matches")]
	[InlineData("ReceiverRefMutatesFail", "declares read-only 'ref this' receiver but mutates field(s)")]
	[InlineData("StrictMutabilityFail", "must declare 'ref this' or 'refvar this' receiver in [StrictMutability] struct")]
	[InlineData("FreeFnReceiverFail", "Receiver parameter ('refvar this' / 'ref this') is only allowed on extension methods")]
	[InlineData("DestructorDepthExceeded", "Cyclic destructor nesting depth exceeded. Please use an arena allocator or manual cleanup.")]
	public void Rejections(string caseName, string expectedError)
	{
		var fileName = $"Lifecycle/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);

		Assert.Equal(1, exitCode);
		Assert.Contains(expectedError, stderr);
	}
}
