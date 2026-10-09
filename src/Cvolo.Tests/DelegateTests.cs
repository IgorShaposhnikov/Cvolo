using System;
using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;
using Cvolo.Projects;
using Cvolo.Syntax.Antlr;
using Cvolo.Tests.Core;
using Xunit;

namespace Cvolo.Tests;

public sealed class DelegateTests : CompilerTestBase
{
	private const string Category = "Delegates";

	[Theory]
	[InlineData("DelegatesBasic.cvl")]
	public void Parser_Delegates_Should_Parse(string caseName)
	{
		var assemblyDir = Path.GetDirectoryName(typeof(DelegateTests).Assembly.Location)!;
		var fullPath = Path.Combine(assemblyDir, "TestCases", $"{Category}/{caseName}");

		var project = CompilationProject.Load(fullPath);
		var parser = new AntlrSyntaxParser();

		foreach (var file in project.SourceFiles)
		{
			var sourceCode = File.ReadAllText(file);
			var context = new CompilationContext(sourceCode, file);
			var ast = parser.Parse(context);

			Assert.NotNull(ast);
			Assert.False(parser.Diagnostics.HasErrors, $"Expected parser to successfully parse '{caseName}'.");
		}
	}

	[Theory]
	[InlineData("DelegatesBasic", "11\n105\n17\n-8\n6\n")]
	public void Delegates_BasicFlow(string caseName, string expected)
	{
		var fileName = $"{Category}/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary(caseName, Category);
		Assert.Equal(0, runCode);
		Assert.Equal(expected.Replace("\r\n", "\n").Trim(), runStdout.Replace("\r\n", "\n").Trim());
	}

	// A bound-method delegate stores a context pointer to its receiver, which would need an
	// exclusive mutable borrow-lock for the delegate's whole live range. That contract is not
	// defined yet, so conversion must fail with CVL1322 whenever the selected method's effective
	// receiver is mutable. Add cases here when the receiver-contract rules change.
	[Theory]
	[InlineData("RefvarThisBoundMethodReject")]
	[InlineData("AutoInferredMutatingBoundMethodReject")]
	public void BoundMethodWithMutableReceiver_IsRejected(string caseName)
	{
		var fileName = $"{Category}/{caseName}.cvl";
		var (exitCode, _, stderr) = RunCompiler(fileName);
		Assert.NotEqual(0, exitCode);
		Assert.Contains("CVL1322", stderr);
	}

	// Negative control: the mutable-receiver rejection must not over-reach and break
	// non-mutating bound-method delegates (here a read-only `ref this` receiver).
	[Theory]
	[InlineData("ReadOnlyRefBoundMethod", "42")]
	public void ReadOnlyReceiverBoundMethod_StillCompilesAndRuns(string caseName, string expected)
	{
		var fileName = $"{Category}/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary(caseName, Category);
		Assert.Equal(0, runCode);
		Assert.Equal(expected.Replace("\r\n", "\n").Trim(), runStdout.Replace("\r\n", "\n").Trim());
	}
}
