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
	private const string BindingFixtures = Category + "/Binding";

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

	private static (string File, string Fixture) Unit(string scenario, string file) =>
		(file, $"{BindingFixtures}/{scenario}/{file}");

	/// <summary>
	/// Binds the given source units in the exact order supplied. Because the caller owns the unit
	/// list, the same sources can be bound ascending, descending or shuffled to prove that no
	/// semantic result depends on enumeration order.
	/// </summary>
	private static BindResult BindInOrder(params (string File, string Fixture)[] files)
	{
		var binder = new Binder();
		var units = files
			.Select(file => Parse(ReadTestCaseSource(file.Fixture), file.File, binder))
			.ToList();
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
		var consumer = Unit("ForwardStructField", "Consumer.cvl");
		var producer = Unit("ForwardStructField", "Producer.cvl");
		AssertClean(BindInOrder(producerFirst ? new[] { producer, consumer } : new[] { consumer, producer }));
	}

	[Fact]
	public void StructField_MutualForwardReference_ThroughPointers_Resolves()
	{
		var leftNode = Unit("MutualPointer", "LeftNode.cvl");
		var rightNode = Unit("MutualPointer", "RightNode.cvl");
		AssertClean(BindInOrder(leftNode, rightNode));
		AssertClean(BindInOrder(rightNode, leftNode));
	}

	[Fact]
	public void StructField_ForwardReference_QualifiedAcrossNamespaces_Resolves()
	{
		var consumer = Unit("QualifiedAcrossNamespaces", "Consumer.cvl");
		var producer = Unit("QualifiedAcrossNamespaces", "Producer.cvl");
		AssertClean(BindInOrder(consumer, producer));
		AssertClean(BindInOrder(producer, consumer));
	}

	[Fact]
	public void StructField_ForwardReference_ImportedNamespace_Resolves()
	{
		var consumer = Unit("ImportedNamespace", "Consumer.cvl");
		var producer = Unit("ImportedNamespace", "Producer.cvl");

		AssertClean(BindInOrder(consumer, producer));
	}

	[Fact]
	public void StructField_ForwardReference_ThroughNamespaceAlias_Resolves()
	{
		var consumer = Unit("NamespaceAlias", "Consumer.cvl");
		var producer = Unit("NamespaceAlias", "Producer.cvl");

		AssertClean(BindInOrder(consumer, producer));
	}

	[Fact]
	public void DelegateParameter_ForwardReference_ResolvesAcrossFiles()
	{
		var delegateUnit = Unit("DelegateParameter", "Delegate.cvl");
		var structUnit = Unit("DelegateParameter", "Struct.cvl");
		AssertClean(BindInOrder(delegateUnit, structUnit));
		AssertClean(BindInOrder(structUnit, delegateUnit));
	}

	[Fact]
	public void DelegateReturnType_ForwardReference_ResolvesAcrossFiles()
	{
		var delegateUnit = Unit("DelegateReturn", "Delegate.cvl");
		var structUnit = Unit("DelegateReturn", "Struct.cvl");
		AssertClean(BindInOrder(delegateUnit, structUnit));
		AssertClean(BindInOrder(structUnit, delegateUnit));
	}

	[Fact]
	public void UnionField_ForwardReference_ResolvesAcrossFiles()
	{
		var unionUnit = Unit("UnionField", "Union.cvl");
		var structUnit = Unit("UnionField", "Struct.cvl");
		AssertClean(BindInOrder(unionUnit, structUnit));
		AssertClean(BindInOrder(structUnit, unionUnit));
	}

	[Fact]
	public void GenericStruct_ForwardReference_Instantiation_Resolves()
	{
		var holderUnit = Unit("GenericStruct", "Holder.cvl");
		var boxUnit = Unit("GenericStruct", "Box.cvl");
		AssertClean(BindInOrder(holderUnit, boxUnit));
		AssertClean(BindInOrder(boxUnit, holderUnit));
	}

	[Fact]
	public void TypeAlias_ForwardReference_ResolvesAcrossFiles()
	{
		var aliasUnit = Unit("TypeAlias", "Alias.cvl");
		var targetUnit = Unit("TypeAlias", "NativeHandle.cvl");
		var consumerUnit = Unit("TypeAlias", "Consumer.cvl");
		AssertClean(BindInOrder(aliasUnit, targetUnit, consumerUnit));
		AssertClean(BindInOrder(consumerUnit, targetUnit, aliasUnit));
	}

	// ---- Order invariance: reversed orders produce identical semantic results ----

	[Fact]
	public void ForwardReference_DiagnosticsIdenticalInReversedOrder()
	{
		var consumer = Unit("DiagnosticsIdentical", "Consumer.cvl");
		var producer = Unit("DiagnosticsIdentical", "Producer.cvl");
		var forward = BindInOrder(consumer, producer);
		var reverse = BindInOrder(producer, consumer);
		Assert.Equal(forward.Text, reverse.Text);
		AssertClean(forward);
	}

	// ---- Negatives preserved: unknown types, duplicates, cycles ----

	[Fact]
	public void UnknownType_StillProducesCVL4135_InEitherOrder()
	{
		var source = Unit("UnknownType", "UnknownFieldType.cvl");
		Assert.True(BindInOrder(source).Contains("CVL4135"));
		Assert.True(BindInOrder(("z.cvl", source.Fixture)).Contains("CVL4135"));
	}

	[Fact]
	public void DuplicateDeclaration_FailsInBothFileOrders_WithoutSilentlyReplacing()
	{
		var firstDeclaration = Unit("DuplicateStruct", "FirstDeclaration.cvl");
		var secondDeclaration = Unit("DuplicateStruct", "SecondDeclaration.cvl");
		var forward = BindInOrder(firstDeclaration, secondDeclaration);
		var reverse = BindInOrder(secondDeclaration, firstDeclaration);
		Assert.True(forward.Contains("CVL4054"));
		Assert.True(reverse.Contains("CVL4054"));
		Assert.Contains("Duplicate type definition 'State'", forward.Text);
		Assert.Contains("Duplicate type definition 'State'", reverse.Text);
	}

	[Fact]
	public void DuplicateDelegateDeclaration_FailsInBothFileOrders()
	{
		var firstDeclaration = Unit("DuplicateDelegate", "FirstDeclaration.cvl");
		var secondDeclaration = Unit("DuplicateDelegate", "SecondDeclaration.cvl");
		Assert.True(BindInOrder(firstDeclaration, secondDeclaration).Contains("CVL4054"));
		Assert.True(BindInOrder(secondDeclaration, firstDeclaration).Contains("CVL4054"));
	}

	[Fact]
	public void DuplicateDeclaration_AcrossTypeKinds_Fails()
	{
		var structDeclaration = Unit("DuplicateTypeKinds", "StructDeclaration.cvl");
		var unionDeclaration = Unit("DuplicateTypeKinds", "UnionDeclaration.cvl");
		Assert.True(BindInOrder(structDeclaration, unionDeclaration).Contains("CVL4054"));
		Assert.True(BindInOrder(unionDeclaration, structDeclaration).Contains("CVL4054"));
	}

	[Fact]
	public void ByValueLayoutCycle_IsRejected_WithoutRecursion()
	{
		var firstAggregate = Unit("ByValueCycle", "FirstAggregate.cvl");
		var secondAggregate = Unit("ByValueCycle", "SecondAggregate.cvl");
		var forward = BindInOrder(firstAggregate, secondAggregate);
		var reverse = BindInOrder(secondAggregate, firstAggregate);
		Assert.True(forward.Contains("CVL4181"));
		Assert.True(reverse.Contains("CVL4181"));
		Assert.Contains("contains itself by value", forward.Text);
	}

	[Fact]
	public void ByValueSelfReference_IsRejected_WithoutRecursion()
	{
		var result = BindInOrder(Unit("ByValueSelf", "SelfReferentialStruct.cvl"));
		Assert.True(result.Contains("CVL4181"));
		Assert.False(result.Contains("CVL4135"), result.Text);
	}

	[Fact]
	public void ByValueCycleThroughArray_IsRejected_WithoutRecursion()
	{
		var source = Unit("ByValueArray", "ArrayCycleStruct.cvl");

		Assert.True(BindInOrder(source).Contains("CVL4181"));
	}

	[Fact]
	public void AliasCycle_StillProducesCVL1201_InEitherOrder()
	{
		var firstAlias = Unit("AliasCycle", "FirstAlias.cvl");
		var secondAlias = Unit("AliasCycle", "SecondAlias.cvl");
		Assert.True(BindInOrder(firstAlias, secondAlias).Contains("CVL1201"));
		Assert.True(BindInOrder(secondAlias, firstAlias).Contains("CVL1201"));
	}

	// ---- Predeclaration must not broaden visibility / import / ambiguity rules ----

	[Fact]
	public void FieldType_WithoutUsingDirective_StaysUnresolved_NoImportLeakage()
	{
		var consumer = Unit("ImportLeakage", "Consumer.cvl");
		var producer = Unit("ImportLeakage", "HiddenNamespaceType.cvl");

		// The consumer never imports Graphics, so predeclaring the producer's identity project-wide
		// must not make the bare name visible there.
		Assert.True(BindInOrder(consumer, producer).Contains("CVL4135"));
	}

	[Fact]
	public void DeferredResolution_UsesOriginatingUnitNamespace_NotPreviousUnit()
	{
		// The producer is processed first and its namespace must not leak into the consumer's
		// deferred field resolution.
		var producer = Unit("OriginNamespace", "Producer.cvl");
		var consumer = Unit("OriginNamespace", "Consumer.cvl");
		AssertClean(BindInOrder(producer, consumer));
		AssertClean(BindInOrder(consumer, producer));
	}

	[Fact]
	public void DeferredResolution_UsesOriginatingUnitUsings()
	{
		// The consumer's `using` must be the one that applies to its own deferred field resolution,
		// regardless of which unit the declaration pass happened to process last.
		var other = Unit("OriginUsings", "UnrelatedNamespace.cvl");
		var producer = Unit("OriginUsings", "ImportedType.cvl");
		var consumer = Unit("OriginUsings", "Consumer.cvl");
		AssertClean(BindInOrder(other, producer, consumer));
		AssertClean(BindInOrder(consumer, producer, other));
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
