using Cvolo.Core.AST;
using Cvolo.Core.AST.Base;
using Cvolo.Core.AST.Declarations;
using Cvolo.Core.Diagnostics;
using Cvolo.Syntax.Antlr;
using Cvolo.Tests.Core;

namespace Cvolo.Tests;

public sealed class OperatorOverloadTests : CompilerTestBase
{
	[Theory]
	[InlineData("BinaryArithmetic", "sum=4,7\ndiff=2,3\nscaled=10,20\nnegated=-1,-2\neq=0\nneq=1\nlt=1")]
	[InlineData("Bitwise", "and=8\nor=14\nxor=6\nleft=48\nright=3\ncomplement=4294967283\nempty=0")]
	[InlineData("CompoundAssignment", "afterAdd=10\nafterSub=5")]
	public void Operator_Overloads_Succeed(string caseName, string expected)
	{
		var fileName = $"OperatorOverloads/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);

		var (runCode, runStdout) = ExecuteBinary(caseName, "OperatorOverloads");
		Assert.Equal(0, runCode);
		Assert.Contains(expected.Replace("\r\n", "\n").Trim(), runStdout.Replace("\r\n", "\n").Trim());
	}

	[Theory]
	[InlineData("AssignmentNotOverloadableFail", "Assignment operator '=' cannot be overloaded")]
	[InlineData("InvalidOwnerFail", "'Helpers' is not an operand of operator '+'")]
	[InlineData("ArityMismatchFail", "must declare one operand (unary) or two operands (binary)")]
	[InlineData("MissingOperatorFail", "No '/' operator overload declared on 'Vec2' accepts operand type(s) Vec2, Vec2.")]
	public void Operator_Overloads_Rejections(string caseName, string expectedError)
	{
		var fileName = $"OperatorOverloads/{caseName}.cvl";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);

		Assert.Equal(1, exitCode);
		Assert.Contains(expectedError, stderr);
	}

	[Fact]
	public void Operator_Owner_May_Be_Declared_In_Another_File_Of_The_Same_Project()
	{
		var (exitCode, stdout, stderr) = RunCompilerCheck("OperatorOverloads/CrossFileOwner");

		AssertCompilationSucceeded(exitCode, stdout, stderr, "OperatorOverloads/CrossFileOwner");
	}

	[Fact]
	public void OperatorDeclaration_ParsesAsAnAssociatedOperatorNode()
	{
		const string source = """
			namespace Test;
			public struct Vec2 { public int X; public int Y; }
			public extension Vec2 {
			    public Vec2 operator +(Vec2 left, Vec2 right) { return left; }
			    public Vec2 operator -(Vec2 value) { return value; }
			}
			""";

		var parser = new AntlrSyntaxParser();
		var unit = parser.Parse(new CompilationContext(source, "operator-shape.cvl"));

		Assert.NotNull(unit);
		Assert.False(parser.Diagnostics.HasErrors, parser.Diagnostics.ToString());
		var extension = FirstExtension(unit!);
		Assert.Equal(2, extension.Operators.Count);

		var binary = extension.Operators[0];
		Assert.Equal("+", binary.Operator);
		Assert.Equal("op_add", binary.OperatorToken);
		Assert.False(binary.IsUnary);
		Assert.Equal(2, binary.Parameters.Count);
		Assert.True(binary.HasBody);
		Assert.Equal(FunctionBindingKind.Associated, binary.BindingKind);
		Assert.True(binary.IsAssociated);
		Assert.Equal(binary.OperatorSpan, binary.NameSpan);
		Assert.Equal("+", source[binary.OperatorSpan.Start..binary.OperatorSpan.End]);
		Assert.Equal("Vec2 operator +(Vec2 left, Vec2 right)", SymbolDisplay(binary));

		// A single operand selects the prefix form of '-', and the lexer must not split '>>'
		// style tokens apart in a way that changes the declared spelling.
		Assert.Equal("op_neg", extension.Operators[1].OperatorToken);
		Assert.True(extension.Operators[1].IsUnary);
		Assert.Equal(1, extension.Operators[1].Parameters.Count);
	}

	[Fact]
	public void OperatorDeclaration_PrintsBackAsSource()
	{
		const string source = """
			namespace Test;
			public struct Vec2 { public int X; public int Y; }
			public extension Vec2 {
			    public Vec2 operator +(Vec2 left, Vec2 right) { return left; }
			}
			""";

		var parser = new AntlrSyntaxParser();
		var unit = parser.Parse(new CompilationContext(source, "operator-print.cvl"));

		Assert.NotNull(unit);
		var printed = CvoloSourcePrinter.Print(unit!);
		Assert.Contains("public Vec2 operator +(Vec2 left, Vec2 right)", printed);
		Assert.DoesNotContain("Cvolo.Core.AST", printed, StringComparison.Ordinal);
	}

	[Fact]
	public void OperatorDeclaration_RejectsAnInstanceReceiver()
	{
		const string source = """
			namespace Test;
			public struct Vec2 { public int X; public int Y; }
			public extension Vec2 {
			    public Vec2 operator +(refvar this, Vec2 right) { return right; }
			}
			""";

		var parser = new AntlrSyntaxParser();
		var unit = parser.Parse(new CompilationContext(source, "operator-receiver.cvl"));

		Assert.NotNull(unit);
		Assert.True(parser.Diagnostics.HasErrors);
		Assert.Contains(
			parser.Diagnostics.Diagnostics,
			diagnostic => diagnostic.Id == DiagnosticIds.OperatorWithReceiver);

		var extension = FirstExtension(unit!);
		Assert.True(Assert.Single(extension.Operators).HasSyntaxError);
	}

	private static ExtensionDeclarationSyntax FirstExtension(CompilationUnitSyntax unit)
	{
		var members = unit.NamespaceDeclaration?.Members ?? unit.Members;
		return Assert.Single(members.OfType<ExtensionDeclarationSyntax>());
	}

	private static string SymbolDisplay(OperatorDeclarationSyntax op) =>
		$"{op.ReturnType} operator {op.Operator}({string.Join(", ", op.Parameters.Select(p => $"{p.Type} {p.Name}"))})";
}
