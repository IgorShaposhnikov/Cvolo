namespace Cvolo.Tests.BinaryBinding;

public sealed class BinaryBindingRuntimeTests : BinaryBindingTestBase
{
    public static IEnumerable<object[]> RuntimeCases()
    {
        var cases = new (string Group, string Name)[]
        {
            ("Runtime", "DirectFloatArgument"),
            ("Runtime", "ContextEquivalence"),
            ("Runtime", "MixedNumeric"),
            ("Runtime", "GlobalConstant"),
            ("Builtins", "FlagsInline"),
            ("Builtins", "ShiftInline"),
            ("Builtins", "RawPointerArithmetic"),
            ("Builtins", "ShortCircuit"),
            ("Builtins", "StringComparison"),
            ("Builtins", "NativeDelegateNull"),
            ("Builtins", "ReferenceOperands"),
            ("Operators", "OperatorInline"),
            ("Traversal", "ForeachLowering"),
            ("Traversal", "IndexOperand"),
            ("Generics", "GenericSpecialization"),
        };

        foreach (var optimization in new[] { "-O0", "-O3" })
        {
            foreach (var (group, name) in cases)
                yield return new object[] { group, name, optimization };
        }
    }

    [Theory]
    [MemberData(nameof(RuntimeCases))]
    public void SuccessfulBinaryFixtures_AreStableAcrossOptimizationLevels(string group, string caseName, string optimization)
    {
        var fileName = Fixture(group, caseName);
        var (exitCode, stdout, stderr) = RunCompiler(fileName, optimization);
        AssertCompilationSucceeded(exitCode, stdout, stderr, $"{fileName} ({optimization})");

        var (runCode, _) = ExecuteBinary(caseName, $"{Root}/{group}");
        Assert.True(runCode == 0, $"{fileName} exited with {runCode} at {optimization}.");
    }

    [Theory]
    [InlineData("-O0")]
    [InlineData("-O3")]
    public void CrossFileOperator_RuntimeRegression(string optimization)
    {
        var project = FixtureDirectory("DeclarationTiming", "CrossFileTiming");
        var (exitCode, stdout, stderr) = RunCompiler(project, optimization);
        AssertCompilationSucceeded(exitCode, stdout, stderr, $"{project} ({optimization})");

        var (runCode, _) = ExecuteBinary("CrossFileTiming", project);
        Assert.Equal(0, runCode);
    }
}
