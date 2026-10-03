using Cvolo.Analysis;
using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;
using Cvolo.Syntax.Antlr;
using Cvolo.Tests.Core;

namespace Cvolo.Tests;

/// <summary>
/// Regressions for raw-pointer indexing (`ptr[index]`). The INC requires `T*` indexing to behave
/// as an r-value, an l-value and an addressable expression; to type as `T` rather than falling
/// back to `int`; to validate its index operand with the same canonical integer rule as
/// `ptr + index`; to require unsafe capability; and to evaluate its base and index exactly once.
/// Every runtime case is pinned at both -O0 and -O3.
/// </summary>
public sealed class RawPointerIndexTests : CompilerTestBase
{
	private const string Category = "RawPointerIndex";
	private const string UnsafeMessage = "Indexing a raw pointer requires an unsafe context.";
	private const string IntegerMessage = "Raw-pointer index must be an integer, but it is '{0}'.";

	[Theory]
	[InlineData("-O0")]
	[InlineData("-O3")]
	public void BasicIndex_ReadWriteAndAddressOf_CompilesAndRuns(string optimization)
	{
		AssertRunsAndPrints("BasicIndex", "IndexBasic OK", optimization);
	}

	[Theory]
	[InlineData("-O0")]
	[InlineData("-O3")]
	public void FloatIndex_YieldsFloat_NotInt(string optimization)
	{
		// Guards the emitter's `int` fallback for non-array/slice index bases.
		AssertRunsAndPrints("FloatIndex", "x=2.500000 v0=7.250000", optimization);
	}

	[Theory]
	[InlineData("-O0")]
	[InlineData("-O3")]
	public void AddressOfIndexedElement_PointsIntoTheArray(string optimization)
	{
		AssertRunsAndPrints("AddressOfIndex", "v1=55", optimization);
	}

	[Theory]
	[InlineData("-O0")]
	[InlineData("-O3")]
	public void SingleEvaluation_EvaluatesBaseAndIndexExactlyOnce(string optimization)
	{
		AssertRunsAndPrints("SingleEvaluation", "pc=1 ic=1", optimization);
	}

	[Theory]
	[InlineData("-O0")]
	[InlineData("-O3")]
	public void UnsafeBodyIndex_CompilesWithoutCvl1001(string optimization)
	{
		var caseName = "UnsafeBodyIndex";
		var fileName = $"{Category}/{caseName}";
		var (exitCode, stdout, stderr) = RunCompiler(fileName, optimization);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		// The attribute is effective: indexing records an unsafe usage for this body.
		Assert.DoesNotContain("has no effect", stderr);

		var (runCode, runStdout) = ExecuteBinary(caseName, $"{Category}/{caseName}");
		Assert.Equal(0, runCode);
		Assert.Contains("v=20", runStdout);
	}

	[Fact]
	public void FloatIndexOperand_IsRejectedBeforeCodegen()
	{
		var fileName = $"{Category}/NonIntegerIndex.cvl";
		var (exitCode, _, stderr) = RunCompiler(fileName);

		Assert.NotEqual(0, exitCode);
		Assert.Contains(DiagnosticIds.RawPointerIndexMustBeAnInteger, stderr);
		Assert.Contains(string.Format(IntegerMessage, "float"), stderr);
	}

	[Fact]
	public void StructIndexOperand_IsRejectedBeforeCodegen()
	{
		var fileName = $"{Category}/StructIndex.cvl";
		var (exitCode, _, stderr) = RunCompiler(fileName);

		Assert.NotEqual(0, exitCode);
		Assert.Contains(DiagnosticIds.RawPointerIndexMustBeAnInteger, stderr);
		Assert.Contains(string.Format(IntegerMessage, "Index"), stderr);
	}

	[Fact]
	public void IndexOutsideUnsafeContext_ReportsRawPointerIndexDiagnostic()
	{
		var fileName = $"{Category}/IndexOutsideUnsafe.cvl";
		var (exitCode, _, stderr) = RunCompiler(fileName);

		Assert.NotEqual(0, exitCode);
		Assert.Contains(DiagnosticIds.RawPointerIndexRequiresUnsafe, stderr);
		Assert.Contains(UnsafeMessage, stderr);
	}

	/// <summary>
	/// Focused diagnostic-id assertion for the unsafe-context rule, independent of the CLI. A
	/// global raw pointer keeps this case free of the raw-pointer-declaration diagnostic, so
	/// CVL4182 is the only reported problem.
	/// </summary>
	[Fact]
	public void RawPointerIndex_RequiresUnsafeCapability_DiagnosticIdIsReported()
	{
		var diagnostics = BindDiagnostics(
			"global var int* gptr;",
			"int Read() { return gptr[0]; }",
			"int Main() { return Read(); }");

		Assert.Contains(diagnostics, d => d.Id == DiagnosticIds.RawPointerIndexRequiresUnsafe);
		Assert.DoesNotContain(diagnostics, d => d.Id == DiagnosticIds.RawPointerIndexMustBeAnInteger);
	}

	/// <summary>
	/// The index operand is validated with the same canonical integer rule as raw-pointer
	/// arithmetic, so a `long` index must be accepted.
	/// </summary>
	[Fact]
	public void NonIntIntegerIndex_IsAccepted_LikeRawPointerArithmetic()
	{
		var diagnostics = BindDiagnostics(
			"""
			[UnsafeBody]
			int Read(int* ptr, long index)
			{
				return ptr[index];
			}
			""");

		Assert.DoesNotContain(diagnostics, d => d.Id == DiagnosticIds.RawPointerIndexMustBeAnInteger);
		Assert.DoesNotContain(diagnostics, d => d.Id == DiagnosticIds.RawPointerIndexRequiresUnsafe);
	}

	private static List<Diagnostic> BindDiagnostics(params string[] sources)
	{
		var binder = new Binder();
		var units = new List<CompilationUnitSyntax>();

		for (var i = 0; i < sources.Length; i++)
		{
			var context = new CompilationContext(sources[i], $"Unit{i}.cvl");
			var ast = new AntlrSyntaxParser().Parse(context);
			Assert.NotNull(ast);
			binder.Context.FileContexts[ast!] = context;
			units.Add(ast!);
		}

		binder.Bind(units);
		return binder.Diagnostics.Diagnostics.ToList();
	}

	private void AssertRunsAndPrints(string caseName, string expected, string optimization)
	{
		var folder = $"{Category}/{caseName}";
		var (exitCode, stdout, stderr) = RunCompiler(folder, optimization);
		AssertCompilationSucceeded(exitCode, stdout, stderr, folder);

		var (runCode, runStdout) = ExecuteBinary(caseName, folder);
		Assert.Equal(0, runCode);
		Assert.Contains(expected, runStdout.Replace("\r\n", "\n"));
	}
}