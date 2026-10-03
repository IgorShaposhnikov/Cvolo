using Cvolo.Tests.Core;

namespace Cvolo.Tests;

/// <summary>
/// Focused regressions for CVL1001 ("'[UnsafeBody]' attribute has no effect ..."), which must
/// consume the same unsafe-operation classification as semantic validation. These tests pin the
/// canonical-usage behavior required by the UnsafeBody-CVL1001 increment.
/// </summary>
public sealed class UnsafeBodyEffectivenessTests : CompilerTestBase
{
	private const string Directory = "Attributes/UnsafeBodyEffectiveness";
	private const string Message = "'[UnsafeBody]' attribute has no effect because function contains no unsafe operations.";

	private static int CountCvl1001(string stderr) => stderr.Split("CVL1001").Length - 1;

	[Fact]
	public void NativeDelegateInvocation_UnderUnsafeBody_DoesNotWarn()
	{
		var fileName = $"{Directory}/NativeDelegateInvokeUnsafeBody.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);

		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);
		Assert.DoesNotContain(Message, stderr);
	}

	[Fact]
	public void NativeDelegateInvocation_OutsideUnsafeContext_ReportsUnsafeDiagnostic_NotCvl1001()
	{
		var fileName = $"{Directory}/NativeDelegateInvokeSafe.cvl";
		var (exitCode, _, stderr) = RunCompiler(fileName);

		Assert.NotEqual(0, exitCode);
		Assert.Contains("Invoking native delegate", stderr);
		Assert.Contains("requires an unsafe context", stderr);
		Assert.DoesNotContain(Message, stderr);
	}

	[Fact]
	public void RawUnsafeCall_UnderUnsafeBody_DoesNotWarn()
	{
		var fileName = $"{Directory}/RawUnsafeCallUnsafeBody.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);

		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);
		Assert.DoesNotContain(Message, stderr);
	}

	[Fact]
	public void ExplicitUnsafeBlock_UnderUnsafeBody_CountsAsUnsafeUsage()
	{
		var fileName = $"{Directory}/ExplicitUnsafeBlockUnsafeBody.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);

		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);
		Assert.DoesNotContain(Message, stderr);
	}

	[Fact]
	public void EncapsulatedUnsafeBody_OnlyUnaffectedCallerWarns()
	{
		var fileName = $"{Directory}/EncapsulatedUnsafeBody.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);

		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);
		Assert.Contains(Message, stderr);
		Assert.Equal(1, CountCvl1001(stderr));
	}

	[Fact]
	public void BlockingSemanticError_DoesNotProduceMisleadingCvl1001()
	{
		var fileName = $"{Directory}/IncompleteAnalysisUnsafeBody.cvl";
		var (exitCode, _, stderr) = RunCompiler(fileName);

		Assert.NotEqual(0, exitCode);
		Assert.DoesNotContain(Message, stderr);
	}

	[Fact]
	public void NestedLambdaUsage_DoesNotCountForEnclosingFunction()
	{
		var fileName = $"{Directory}/NestedLambdaUnsafeBody.cvl";
		var (exitCode, _, stderr) = RunCompiler(fileName);

		Assert.Equal(0, exitCode);
		Assert.Contains(Message, stderr);
		Assert.Equal(1, CountCvl1001(stderr));
	}

	[Fact]
	public void SuppressWarningAttribute_SuppressesCvl1001_AndStillRuns()
	{
		var fileName = $"{Directory}/SuppressCvl1001.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);

		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);
		Assert.DoesNotContain(Message, stderr);

		var (runCode, runStdout) = ExecuteBinary("SuppressCvl1001", Directory);
		Assert.Equal(0, runCode);
		Assert.Contains("Answer: 42", runStdout);
	}
}
