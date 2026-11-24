using Cvolo.Tests.Core;

namespace Cvolo.Tests;

public sealed class UnionsTests : CompilerTestBase
{
	[Theory]
	[InlineData("UnionDecl", "Integer: 42")]
	[InlineData("UnionFieldAccess", "Ok: 42")]
	[InlineData("UnionExtension", "Some: 100, IsSome: 1\nEmpty option created.")]
	[InlineData("OptionSwitchPromotion", "Some: 99")]
	[InlineData("NullOption", "Safe Option null initialized successfully.\nRaw pointer is null as expected.")]
	/// <summary>
	/// Runs union fixtures that must compile and produce the expected output.
	/// </summary>
	[InlineData("ResultConstructorTag", "0")]
	// A direct variant write retags the union, so reading that payload afterwards is legal.
	[InlineData("VariantSelectionWriteSuccess", "2")]
	// Contextual '(Variant: value)' initializers must select and emit the same tag as explicit literals.
	[InlineData("ContextualUnionInitSuccess", "42\nHello\nNone\n5,10")]
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
	[InlineData("UnionSwitchBorrowLockFail", "Cannot reassign 'opt' while a field borrow is still active")] // Fixed "stil" to "still active"
	[InlineData("UnionSwitchExhaustiveFail", "Switch statement is not exhaustive. Missing case for variant 'B'")]
	// A literal initializer fixes the active variant; the other payload must not be readable.
	[InlineData("InactiveVariantReadFail", "Cannot read variant 'B' of union 'Choice': its active variant is 'A'.")]
	// A parameter has no proven active variant, so any payload read must be rejected.
	[InlineData("UnknownVariantReadFail", "Cannot read variant 'A' of union 'Choice': the active variant is not known at this point.")]
	// Nested writes are guarded by the same active-variant proof as direct reads.
	[InlineData("NestedInactiveWriteFail", "Cannot read variant 'B' of union 'Box': its active variant is 'A'.")]
	// Compound assignment reads the target before writing it.
	[InlineData("InactiveVariantCompoundAssignFail", "Cannot read variant 'B' of union 'Choice': its active variant is 'A'.")]
	// Taking a mutable reference exposes an otherwise inactive payload.
	[InlineData("InactiveVariantRefFail", "Cannot read variant 'B' of union 'Choice': its active variant is 'A'.")]
	// Contextual union initializers reuse the explicit literal validation rules.
	[InlineData("ContextualUnionUnknownVariantFail", "Union 'Choice' does not contain variant 'Missing'")]
	[InlineData("ContextualUnionPayloadTypeFail", "Cannot initialize field 'Number' of type 'int' with value of type 'string'")]
	[InlineData("ContextualUnionMultipleVariantsFail", "Union initialization of 'Choice' must specify exactly one variant.")]
	// A parenthesized initializer with no expected type cannot be bound to a union.
	[InlineData("ContextualUnionNoExpectedTypeFail", "Contextual initializer '(Member: value)' requires an expected struct or union type.")]
	public void Semantic_Rejections(string caseName, string expectedError)
	{
		var fileName = $"Unions/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);

		Assert.Equal(1, exitCode);
		Assert.Contains(expectedError, stderr);
	}

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
