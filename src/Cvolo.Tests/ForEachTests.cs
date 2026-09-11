using Cvolo.Tests.Core;

namespace Cvolo.Tests;

public sealed class ForEachTests : CompilerTestBase
{
	[Theory]
	[InlineData("ArrayBasic", "10,20,30,40,50,")]
	[InlineData("SliceBasic", "7,8,9,10,")]
	[InlineData("SliceParam", "26")]
	[InlineData("UserDefinedBasic", "0,1,2,3,")]
	[InlineData("VarMutableItem", "10,20,30,40,50,")]
	[InlineData("ExplicitItemType", "0,1,2,")]
	[InlineData("BreakContinue", "1,2,4,")]
	[InlineData("EnumeratorBreakContinue", "1,2,4,")]
	[InlineData("EnumeratorDefer", "get-enum;1,dtor;done")]
	[InlineData("LabeledForeach", "10:102020:102030:102040:1020")]
	[InlineData("NestedForeach", "13,14,23,24,")]
	[InlineData("RefVarArray", "10,20,30,40,50,")]
	[InlineData("ValRefCurrent", "10,20,30,")]
	[InlineData("VarRefCurrent", "1000,2000,3000,|10,20,30")]
	[InlineData("RefVarEnumerator", "100,200,300")]
	[InlineData("AmbiguousOverloadsOk", "0123")]
	public void ForEachBehavior(string caseName, string expected)
	{
		var fileName = $"ForEach/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary(caseName, "ForEach");
		Assert.Equal(0, runCode);
		Assert.Equal(expected, runStdout);
	}

	[Theory]
	[InlineData("NoGetEnumeratorFail", "CVL1080", "'GetEnumerator()' method is missing")]
	[InlineData("InvalidEnumeratorFail", "CVL1081", "missing a 'bool MoveNext()' method signature")]
	[InlineData("MissingCurrentFail", "CVL1082", "missing a 'Current' property or method getter")]
	[InlineData("MoveNextNotBoolFail", "CVL1083", "must return a logical 'bool' type scalar")]
	[InlineData("ReadOnlyItemFail", "CVL1084", "read-only and cannot be reassigned inside the execution block")]
	[InlineData("TypeMismatchFail", "CVL1085", "Explicit loop item type 'double' does not match the iterator's underlying 'Current' yield type 'int'")]
	[InlineData("RefVarByValueFail", "CVL1086", "returns by value, yielding no reference address")]
	[InlineData("RefVarReadOnlyRefFail", "CVL1087", "returns a read-only 'ref T'")]
	[InlineData("RefVarEscapeGlobalFail", "CVL1088", "Escape Boundary Violation")]
	[InlineData("RefVarReturnEscapeFail", "CVL1088", "Escape Boundary Violation")]
	[InlineData("CVL1090Visibility/", "CVL1090", "method is inaccessible due to its protection level")]
	public void ForEachRejections(string caseName, string expectedId, string expectedMessage)
	{
		var path = caseName.Contains('/') ? $"ForEach/{caseName.TrimEnd('/')}" : $"ForEach/{caseName}.cvl";
		var (exitCode, _, stderr) = RunCompiler(path);
		Assert.Equal(1, exitCode);
		Assert.Contains(expectedId, stderr);
		Assert.Contains(expectedMessage, stderr);
	}

	[Theory]
	[InlineData("RefVarExplicitTypeFail", "cannot be combined with an explicit item type")]
	[InlineData("ImmutableBorrowCallFail", "mutating method calls are not allowed")]
	[InlineData("ImmutableBorrowAssignFail", "structural mutation is not allowed")]
	public void ForEachMessageOnlyRejections(string caseName, string expectedMessage)
	{
		// These cases emit an error without a stable diagnostic id: the parser-level
		// refvar+explicit-type rejection and the Section 4.D immutable borrow contract
		// (the spec deliberately assigns no id). Assert the message only.
		var (exitCode, _, stderr) = RunCompiler($"ForEach/{caseName}.cvl");
		Assert.Equal(1, exitCode);
		Assert.Contains(expectedMessage, stderr);
	}
}
