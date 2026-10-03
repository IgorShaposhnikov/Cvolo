using Cvolo.Analysis;
using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;
using Cvolo.Syntax.Antlr;
using Cvolo.Tests.Core;

namespace Cvolo.Tests;

/// <summary>
/// Feature: Cross-File Type Declaration Order (INC 0.0.21). Semantic results must not depend on
/// <c>CompilationUnitSyntax</c> enumeration order. Every named type identity (struct, union, enum,
/// delegate, interface, protocol, generic template, alias) is predeclared across all units before
/// any field or signature is resolved, and deferred resolution runs under the originating unit's
/// lexical context. Duplicate declarations are order-independent, unknown types still produce
/// CVL4135, and by-value layout cycles are rejected (CVL4181) without recursion crashes.
/// </summary>
public sealed class CrossFileTypeDeclarationOrderTests : CompilerTestBase
{
	private const string Category = "DeclarationOrder";

	private readonly record struct BindResult(bool HasErrors, string Text)
	{
		public bool Contains(string id) => Text.Contains(id, StringComparison.Ordinal);
	}

	private static CompilationUnitSyntax Parse(string source, string file, Binder binder)
	{
		var context = new CompilationContext(source, file);
		var ast = new AntlrSyntaxParser().Parse(context);
		Assert.NotNull(ast);
		binder.Context.FileContexts[ast!] = context;
		return ast!;
	}

	/// <summary>
	/// Binds the given source units in the exact order supplied. Because the caller owns the unit
	/// list, the same sources can be bound ascending, descending or shuffled to prove that no
	/// semantic result depends on enumeration order.
	/// </summary>
	private static BindResult BindInOrder(params (string File, string Source)[] files)
	{
		var binder = new Binder();
		var units = files.Select(file => Parse(file.Source, file.File, binder)).ToList();
		binder.Bind(units);

		var text = string.Join("\n", binder.Diagnostics.Diagnostics.Select(d => $"{d.Id}: {d.Message}"));
		return new BindResult(binder.Diagnostics.HasErrors, text);
	}

	private static void AssertClean(BindResult result)
	{
		Assert.False(result.HasErrors, $"Expected a clean bind but got:{Environment.NewLine}{result.Text}");
	}

	// ---- Forward references resolve regardless of file order ----

	[Theory]
	[InlineData(false)] // consumer enumerated first
	[InlineData(true)]  // producer enumerated first
	public void StructField_ForwardReference_ResolvesInEitherFileOrder(bool producerFirst)
	{
		const string consumer = "namespace Fwd; public struct A { public B Value; } int main() { A a = A { Value: B { X: 1 } }; return a.Value.X; }";
		const string producer = "namespace Fwd; public struct B { public int X; }";

		var files = producerFirst
			? new[] { ("b.cvl", producer), ("a.cvl", consumer) }
			: new[] { ("a.cvl", consumer), ("b.cvl", producer) };

		AssertClean(BindInOrder(files));
	}

	[Fact]
	public void StructField_MutualForwardReference_ThroughPointers_Resolves()
	{
		const string a = "namespace Fwd; public struct A { public B* Other; } int main() { return 0; }";
		const string b = "namespace Fwd; public struct B { public A* Other; }";

		AssertClean(BindInOrder(("a.cvl", a), ("b.cvl", b)));
		AssertClean(BindInOrder(("b.cvl", b), ("a.cvl", a)));
	}

	[Fact]
	public void StructField_ForwardReference_QualifiedAcrossNamespaces_Resolves()
	{
		const string consumer = "namespace Consumer; public struct Renderer { public Graphics.ShaderProgram Shader; } int main() { return 0; }";
		const string producer = "namespace Graphics; public struct ShaderProgram { public int Id; }";

		AssertClean(BindInOrder(("consumer.cvl", consumer), ("producer.cvl", producer)));
		AssertClean(BindInOrder(("producer.cvl", producer), ("consumer.cvl", consumer)));
	}

	[Fact]
	public void StructField_ForwardReference_ImportedNamespace_Resolves()
	{
		const string consumer = "namespace Consumer; using Graphics; public struct Renderer { public ShaderProgram Shader; } int main() { return 0; }";
		const string producer = "namespace Graphics; public struct ShaderProgram { public int Id; }";

		AssertClean(BindInOrder(("consumer.cvl", consumer), ("producer.cvl", producer)));
	}

	[Fact]
	public void StructField_ForwardReference_ThroughNamespaceAlias_Resolves()
	{
		const string consumer = "namespace Consumer; using S = Graphics.Shaders; public struct Renderer { public S.ShaderProgram Shader; } int main() { return 0; }";
		const string producer = "namespace Graphics.Shaders; public struct ShaderProgram { public int Id; }";

		AssertClean(BindInOrder(("consumer.cvl", consumer), ("producer.cvl", producer)));
	}

	[Fact]
	public void DelegateParameter_ForwardReference_ResolvesAcrossFiles()
	{
		const string delegateUnit = "namespace Fwd; public delegate void Handler(B value); int main() { return 0; }";
		const string structUnit = "namespace Fwd; public struct B { public int X; }";

		AssertClean(BindInOrder(("d.cvl", delegateUnit), ("s.cvl", structUnit)));
		AssertClean(BindInOrder(("s.cvl", structUnit), ("d.cvl", delegateUnit)));
	}

	[Fact]
	public void DelegateReturnType_ForwardReference_ResolvesAcrossFiles()
	{
		const string delegateUnit = "namespace Fwd; public delegate B Factory(); int main() { return 0; }";
		const string structUnit = "namespace Fwd; public struct B { public int X; }";

		AssertClean(BindInOrder(("d.cvl", delegateUnit), ("s.cvl", structUnit)));
		AssertClean(BindInOrder(("s.cvl", structUnit), ("d.cvl", delegateUnit)));
	}

	[Fact]
	public void UnionField_ForwardReference_ResolvesAcrossFiles()
	{
		const string unionUnit = "namespace Fwd; public union U { public B Payload; public int Tag; } int main() { return 0; }";
		const string structUnit = "namespace Fwd; public struct B { public int X; }";

		AssertClean(BindInOrder(("u.cvl", unionUnit), ("s.cvl", structUnit)));
		AssertClean(BindInOrder(("s.cvl", structUnit), ("u.cvl", unionUnit)));
	}

	[Fact]
	public void GenericStruct_ForwardReference_Instantiation_Resolves()
	{
		const string consumer = "namespace Fwd; public struct Holder { public Box<int> Value; } int main() { return 0; }";
		const string producer = "namespace Fwd; public struct Box<T> { public T Value; }";

		AssertClean(BindInOrder(("h.cvl", consumer), ("b.cvl", producer)));
		AssertClean(BindInOrder(("b.cvl", producer), ("h.cvl", consumer)));
	}

	[Fact]
	public void TypeAlias_ForwardReference_ResolvesAcrossFiles()
	{
		const string aliasUnit = "namespace Fwd; alias Handle = NativeHandle;";
		const string structUnit = "namespace Fwd; public struct NativeHandle { public int Value; }";
		const string consumer = "namespace Fwd; public struct State { public Handle Value; } int main() { return 0; }";

		AssertClean(BindInOrder(("a.cvl", aliasUnit), ("n.cvl", structUnit), ("s.cvl", consumer)));
		AssertClean(BindInOrder(("s.cvl", consumer), ("n.cvl", structUnit), ("a.cvl", aliasUnit)));
	}

	// ---- Order invariance: reversed orders produce identical semantic results ----

	[Fact]
	public void ForwardReference_DiagnosticsIdenticalInReversedOrder()
	{
		const string a = "namespace Fwd; public struct A { public B Value; } int main() { return 0; }";
		const string b = "namespace Fwd; public struct B { public int X; }";

		var forward = BindInOrder(("a.cvl", a), ("b.cvl", b));
		var reverse = BindInOrder(("b.cvl", b), ("a.cvl", a));

		Assert.Equal(forward.Text, reverse.Text);
		AssertClean(forward);
	}

	// ---- Negatives preserved: unknown types, duplicates, cycles ----

	[Fact]
	public void UnknownType_StillProducesCVL4135_InEitherOrder()
	{
		const string a = "namespace Fwd; public struct A { public NoSuchType Value; } int main() { return 0; }";

		Assert.True(BindInOrder(("a.cvl", a)).Contains("CVL4135"));
		Assert.True(BindInOrder(("z.cvl", a)).Contains("CVL4135"));
	}

	[Fact]
	public void DuplicateDeclaration_FailsInBothFileOrders_WithoutSilentlyReplacing()
	{
		const string a = "namespace Fwd; public struct State { public int X; } int main() { State s = State { X: 1 }; return s.X; }";
		const string b = "namespace Fwd; public struct State { public long Y; }";

		var forward = BindInOrder(("a.cvl", a), ("b.cvl", b));
		var reverse = BindInOrder(("b.cvl", b), ("a.cvl", a));

		Assert.True(forward.Contains("CVL4054"));
		Assert.True(reverse.Contains("CVL4054"));
		Assert.Contains("Duplicate type definition 'State'", forward.Text);
		Assert.Contains("Duplicate type definition 'State'", reverse.Text);
	}

	[Fact]
	public void DuplicateDelegateDeclaration_FailsInBothFileOrders()
	{
		const string a = "namespace Fwd; public delegate void Handler(int value); int main() { return 0; }";
		const string b = "namespace Fwd; public delegate void Handler(long value);";

		Assert.True(BindInOrder(("a.cvl", a), ("b.cvl", b)).Contains("CVL4054"));
		Assert.True(BindInOrder(("b.cvl", b), ("a.cvl", a)).Contains("CVL4054"));
	}

	[Fact]
	public void DuplicateDeclaration_AcrossTypeKinds_Fails()
	{
		const string a = "namespace Fwd; public struct Thing { public int X; } int main() { return 0; }";
		const string b = "namespace Fwd; public union Thing { public int Y; }";

		Assert.True(BindInOrder(("a.cvl", a), ("b.cvl", b)).Contains("CVL4054"));
		Assert.True(BindInOrder(("b.cvl", b), ("a.cvl", a)).Contains("CVL4054"));
	}

	[Fact]
	public void ByValueLayoutCycle_IsRejected_WithoutRecursion()
	{
		const string a = "namespace Fwd; public struct A { public B Value; } int main() { return 0; }";
		const string b = "namespace Fwd; public struct B { public A Value; }";

		var forward = BindInOrder(("a.cvl", a), ("b.cvl", b));
		var reverse = BindInOrder(("b.cvl", b), ("a.cvl", a));

		Assert.True(forward.Contains("CVL4181"));
		Assert.True(reverse.Contains("CVL4181"));
		Assert.Contains("contains itself by value", forward.Text);
	}

	[Fact]
	public void ByValueSelfReference_IsRejected_WithoutRecursion()
	{
		var result = BindInOrder(("a.cvl", "namespace Fwd; public struct A { public A Value; } int main() { return 0; }"));

		Assert.True(result.Contains("CVL4181"));
		Assert.False(result.Contains("CVL4135"), result.Text);
	}

	[Fact]
	public void ByValueCycleThroughArray_IsRejected_WithoutRecursion()
	{
		const string a = "namespace Fwd; public struct A { public A[1] Items; } int main() { return 0; }";

		Assert.True(BindInOrder(("a.cvl", a)).Contains("CVL4181"));
	}

	[Fact]
	public void AliasCycle_StillProducesCVL1201_InEitherOrder()
	{
		const string a = "namespace Fwd; alias X = Y;";
		const string b = "namespace Fwd; alias Y = X; int main() { return 0; }";

		Assert.True(BindInOrder(("a.cvl", a), ("b.cvl", b)).Contains("CVL1201"));
		Assert.True(BindInOrder(("b.cvl", b), ("a.cvl", a)).Contains("CVL1201"));
	}

	// ---- Predeclaration must not broaden visibility / import / ambiguity rules ----

	[Fact]
	public void FieldType_WithoutUsingDirective_StaysUnresolved_NoImportLeakage()
	{
		const string consumer = "namespace Consumer; public struct Renderer { public ShaderProgram Shader; } int main() { return 0; }";
		const string producer = "namespace Graphics; public struct ShaderProgram { public int Id; }";

		// The consumer never imports Graphics, so predeclaring the producer's identity project-wide
		// must not make the bare name visible there.
		Assert.True(BindInOrder(("consumer.cvl", consumer), ("producer.cvl", producer)).Contains("CVL4135"));
	}

	[Fact]
	public void DeferredResolution_UsesOriginatingUnitNamespace_NotPreviousUnit()
	{
		// The producer is processed first and its namespace must not leak into the consumer's
		// deferred field resolution.
		const string producer = "namespace Alpha; public struct Token { public int Id; }";
		const string consumer = "namespace Beta; public struct Holder { public Alpha.Token Token; } int main() { return 0; }";

		AssertClean(BindInOrder(("producer.cvl", producer), ("consumer.cvl", consumer)));
		AssertClean(BindInOrder(("consumer.cvl", consumer), ("producer.cvl", producer)));
	}

	[Fact]
	public void DeferredResolution_UsesOriginatingUnitUsings()
	{
		// The consumer's `using` must be the one that applies to its own deferred field resolution,
		// regardless of which unit the declaration pass happened to process last.
		const string other = "namespace Other; using Graphics; public struct Unrelated { public int Id; }";
		const string producer = "namespace Graphics; public struct ShaderProgram { public int Id; }";
		const string consumer = "namespace Consumer; using Graphics; public struct Renderer { public ShaderProgram Shader; } int main() { return 0; }";

		AssertClean(BindInOrder(("other.cvl", other), ("producer.cvl", producer), ("consumer.cvl", consumer)));
		AssertClean(BindInOrder(("consumer.cvl", consumer), ("producer.cvl", producer), ("other.cvl", other)));
	}

	// ---- End-to-end compile-and-run ----

	[Fact]
	public void ForwardReference_CompilesAndRuns_CrossFileStruct()
	{
		var fileName = $"{Category}/ForwardStruct";
		var (exitCode, stdout, stderr) = RunCompiler(fileName);
		AssertCompilationSucceeded(exitCode, stdout, stderr, fileName);
		Assert.DoesNotContain("CVL4135", stderr);

		var (runCode, runStdout) = ExecuteBinary("ForwardStruct", $"{Category}/ForwardStruct");
		Assert.Equal(0, runCode);
		Assert.Contains("Total: 7", runStdout);
	}
}
