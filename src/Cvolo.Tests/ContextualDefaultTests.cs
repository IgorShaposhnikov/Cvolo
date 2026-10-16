using Cvolo.Analysis.Passes.Validation;
using Cvolo.Analysis.Symbols.Base;
using Cvolo.Analysis.Symbols.Structs;
using Cvolo.Core.AST.Base;
using Cvolo.Tests.Core;

namespace Cvolo.Tests;

public sealed class ContextualDefaultTests : CompilerTestBase
{
	[Theory]
	[InlineData("DefaultStructFieldSafe", "0 0")]
	[InlineData("DefaultEmbeddedPtrAccept", "5")]
	[InlineData("DefaultBigStruct", "0 0")]
	[InlineData("DefaultOptionRefNpo", "none")]
	[InlineData("DefaultArrayEmbedded", "ok")]
	[InlineData("DefaultEnumAccept", "red")]
	[InlineData("GlobalDefaultStruct", "0 0")]
	[InlineData("BareFieldDefaults", "0")]
	[InlineData("GlobalBareFieldDefaults", "0")]
	[InlineData("DefaultArrayOfTaggedOption", "NNN 0")]
	[InlineData("DefaultNestedStructOption", "none 0")]
	[InlineData("DefaultEmptyArrayInit", "NN")]
	[InlineData("GenericDefaultEligible", "0 0")]
	[InlineData("GenericDefaultOption", "none")]
	public void Execution_Success(string caseName, string expected)
	{
		var fileName = $"ContextualDefault/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary(caseName, "ContextualDefault");
		Assert.Equal(0, runCode);
		Assert.Contains(expected.Replace("\r\n", "\n").Trim(), runStdout.Replace("\r\n", "\n").Trim());
	}

	[Theory]
	[InlineData("DefaultVoidFail", "CVL4194")]
	[InlineData("DefaultRefFail", "CVL4195")]
	[InlineData("DefaultRawPtrStandaloneFail", "CVL4198")]
	[InlineData("DefaultUnsafeUnionFail", "CVL4196")]
	[InlineData("DefaultStringFail", "CVL4196")]
	[InlineData("UserUnionDefaultFail", "CVL4196")]
	[InlineData("RawArrayDefaultFail", "CVL4196")]
	[InlineData("NonOptionUnionDefaultFail", "CVL4196")]
	[InlineData("UnboundRawDefaultFail", "CVL4198")]
	[InlineData("ErrorUnionReferenceDefaultFail", "CVL4196")]
	[InlineData("ErrorUnionRawPayloadDefaultFail", "CVL4196")]
	[InlineData("UnionFieldRawPointerDefaultFail", "CVL4198")]
	[InlineData("GenericDefaultIneligibleFail", "CVL4196")]
	[InlineData("DefaultReturnStringFail", "CVL4196")]
	[InlineData("DefaultReturnRawPtrFail", "CVL4198")]
	[InlineData("DefaultArgumentStringFail", "CVL4196")]
	[InlineData("DefaultOptionalVoidFail", "CVL4019")]
	[InlineData("DefaultMissingFieldFail", "CVL4081")]
	[InlineData("DefaultNoneToRawPtrFail", "CVL4188")]
	public void Semantic_Rejections(string caseName, string expectedError)
	{
		var fileName = $"ContextualDefault/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);

		Assert.Equal(1, exitCode);
		Assert.Contains(expectedError, stderr);
	}

	[Fact]
	public void MonomorphizationLifecycle_DistinguishesStates()
	{
		// only a Ready specialization is usable; missing/Pending/Rejected are not.
		var context = new Cvolo.Analysis.BindingContext();
		Assert.False(context.IsMonomorphizationReady("Make<Point>"));

		context.MonomorphizationStates["Make<Point>"] = Cvolo.Analysis.MonomorphizationState.Pending;
		Assert.False(context.IsMonomorphizationReady("Make<Point>"));

		context.MonomorphizationStates["Make<Point>"] = Cvolo.Analysis.MonomorphizationState.Rejected;
		Assert.False(context.IsMonomorphizationReady("Make<Point>"));

		context.MonomorphizationStates["Make<Point>"] = Cvolo.Analysis.MonomorphizationState.Ready;
		Assert.True(context.IsMonomorphizationReady("Make<Point>"));
	}

	[Fact]
	public void GenericErrorUnion_PreservesLegacyIntegerDefault()
	{
		// Match the supported non-generic integer error-union case after substitution.
		const string fileName = "ContextualDefault/GenericErrorUnionDefault.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);
	}

	[Fact]
	public void OptionSpellingWithoutCanonicalIdentity_DoesNotAuthorizeDefault()
	{
		// Internal seam: source-level duplicate-type checks would mask this identity test.
		// A freshly fabricated symbol has no canonical declaration or import authority.
		var impostor = new UnionTypeSymbol("Option<int>",
		[
			new UnionFieldSymbol("None", TypeSymbol.Void, true),
			new UnionFieldSymbol("Some", TypeSymbol.Int, false)
		]);
		var plan = new DefaultabilityAnalyzer().GetDefaultPlan(
			impostor, GeneralFormationContext.Instance, SafetyTier.Safe);

		Assert.False(plan.IsEligible);
	}
}
