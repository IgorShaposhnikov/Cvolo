using Cvolo.Tests.Core;

namespace Cvolo.Tests;

public sealed class UnionsTests : CompilerTestBase
{
	/// <summary>
	/// Runs union fixtures that must compile and produce the expected output.
	/// </summary>
	[Theory]
	[InlineData("UnionDecl", "Integer: 42")]
	[InlineData("UnionFieldAccess", "Ok: 42")]
	[InlineData("UnionExtension", "Some: 100, IsSome: 1\nEmpty option created.")]
	[InlineData("OptionSwitchPromotion", "Some: 99")]
	[InlineData("NullOption", "Safe Option null initialized successfully.\nRaw pointer is null as expected.")]
	[InlineData("ResultConstructorTag", "0")]
	[InlineData("VariantSelectionWriteSuccess", "2")]
	[InlineData("ContextualUnionInitSuccess", "42\nHello\nNone\n5,10")]
	[InlineData("ActiveVariantFlowSuccess", "1\ntwo\n3\n4\n4\n4")]
	[InlineData("ContextualGenericUnionInitSuccess", "42\n7")]
	[InlineData("ContextualOwnedUnionInitSuccess", "some=7\nDone\nDropping: 7")]
	public void Execution_Success(string caseName, string expected)
	{
		var fileName = $"Unions/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary(caseName, "Unions");
		Assert.Equal(0, runCode);
		Assert.Contains(expected.Replace("\r\n", "\n").Trim(), runStdout.Replace("\r\n", "\n").Trim());
	}

	/// <summary>
	/// Checks exact stdout and exit code for tagged-union initializer payloads at -O0 and -O3.
	/// </summary>
	[Theory]
	[InlineData("UnionEqualsIntZero", "0")]
	[InlineData("UnionColonIntZero", "0")]
	[InlineData("UnionEqualsSingleString", "100")]
	[InlineData("UnionColonSingleString", "100")]
	[InlineData("ContextualUnionLocalDeclSuccess", "6")]
	public void UnionInitializerPayload_ExactOutput(string caseName, string expected)
	{
		foreach (var optimization in new[] { "-O0", "-O3" })
		{
			var fileName = $"Unions/{caseName}.cvl";
			var (exitCode, stdout, stderr) = RunCompiler(fileName, optimization);
			AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

			var (runCode, runStdout) = ExecuteBinary(caseName, "Unions");
			Assert.Equal(0, runCode);
			Assert.Equal(expected, runStdout.Replace("\r\n", "\n").Trim());
		}
	}

	/// <summary>
	/// Checks that the Result constructor tag is correct when optimization is disabled.
	/// </summary>
	[Fact]
	public void ResultConstructor_SetsTheActiveVariantTag_WithoutOptimization()
	{
		const string caseName = "ResultConstructorTag";
		var fileName = $"Unions/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName, "-O0");
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary(caseName, "Unions");
		Assert.Equal(0, runCode);
		Assert.Equal("0", runStdout.Replace("\r\n", "\n").Trim());
	}

	/// <summary>
	/// Runs standard-library Option fixtures that must compile and produce the expected output.
	/// </summary>
	[Theory]
	[InlineData("OptionConstructor", "Some: 42")]
	public void StdLib_Option_Success(string caseName, string expected)
	{
		var fileName = $"StandardLibrary/Option/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary(caseName, "StandardLibrary/Option");
		Assert.Equal(0, runCode);
		Assert.Contains(expected.Replace("\r\n", "\n").Trim(), runStdout.Replace("\r\n", "\n").Trim());
	}

	/// <summary>
	/// Verifies that invalid union usage is rejected with the expected diagnostic.
	/// </summary>
	[Theory]
	[InlineData("UnionDuplicateFieldFail", "Duplicate field 'Active'")]
	[InlineData("UnionFieldAccessFail", "does not contain variant 'Missing'")]
	[InlineData("UnionSwitchBorrowLockFail", "Cannot reassign 'opt' while a field borrow is still active")]
	[InlineData("UnionSwitchExhaustiveFail", "Switch statement is not exhaustive. Missing case for variant 'B'")]
	[InlineData("InactiveVariantReadFail", "Cannot read variant 'B' of union 'Choice': its active variant is 'A'.")]
	[InlineData("UnknownVariantReadFail", "Cannot read variant 'A' of union 'Choice': the active variant is not known at this point.")]
	[InlineData("NestedInactiveWriteFail", "Cannot read variant 'B' of union 'Box': its active variant is 'A'.")]
	[InlineData("InactiveVariantCompoundAssignFail", "Cannot read variant 'B' of union 'Choice': its active variant is 'A'.")]
	[InlineData("InactiveVariantRefFail", "Cannot read variant 'B' of union 'Choice': its active variant is 'A'.")]
	[InlineData("UnknownVariantAfterJoinFail", "Cannot read variant 'A' of union 'Flow': the active variant is not known at this point.")]
	[InlineData("UnknownVariantAfterCallFail", "Cannot read variant 'A' of union 'Flow': the active variant is not known at this point.")]
	[InlineData("GenericFunctionInactiveVariantReadFail", "Cannot read variant 'A' of union 'Flow': the active variant is not known at this point.")]
	[InlineData("LambdaInactiveVariantReadFail", "Cannot read variant 'A' of union 'Flow': its active variant is 'B'.")]
	[InlineData("ContextualUnionUnknownVariantFail", "Union 'Choice' does not contain variant 'Missing'")]
	[InlineData("ContextualUnionPayloadTypeFail", "Cannot initialize field 'Number' of type 'int' with value of type 'string'")]
	[InlineData("ContextualUnionMultipleVariantsFail", "Union initialization of 'Choice' must specify exactly one variant.")]
	[InlineData("ContextualUnionNoExpectedTypeFail", "Contextual initializer '(Member: value)' requires an expected struct or union type.")]
	public void Semantic_Rejections(string caseName, string expectedError)
	{
		var fileName = $"Unions/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);

		Assert.Equal(1, exitCode);
		Assert.Contains(expectedError, stderr);
	}

	/// <summary>
	/// Verifies that invalid standard-library Option usage is rejected with the expected diagnostic.
	/// </summary>
	[Theory]
	[InlineData("OptionMoveIsMove", "Use of moved variable 'opt1'")]
	public void StdLib_Option_Rejections(string caseName, string expectedError)
	{
		var fileName = $"StandardLibrary/Option/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);

		Assert.Equal(1, exitCode);
		Assert.Contains(expectedError, stderr);
	}
}
