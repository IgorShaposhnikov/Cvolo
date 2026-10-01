using Cvolo.Compiler.Tooling;
using Xunit;

namespace Cvolo.Tests.Tooling;

public sealed class LocalStdlibValidationTests
{
	private const string LayoutSource =
		"public struct Layout { public nuint Size; public nuint Alignment; }\n" +
		"public extension Layout\n" +
		"{\n" +
		"    public Layout(nuint size, nuint alignment) { Size = size; Alignment = alignment; }\n" +
		"    public Layout .FromType<T>() { return Layout(sizeof<T>(), alignof<T>()); }\n" +
		"}\n";

	private const string AllocatorSource =
		"protocol Allocator { void* Alloc(Layout layout); void Dealloc(void* ptr, Layout layout); }\n";

	private const string SystemAllocatorSource =
		"namespace System.Memory;\n" +
		"extern void* malloc(int size);\n" +
		"extern void free(void* ptr);\n" +
		"public struct SystemAllocator { }\n" +
		"public extension SystemAllocator\n" +
		"{\n" +
		"    unsafe void* Alloc(Layout layout) { return malloc(1); }\n" +
		"    unsafe void Dealloc(void* ptr, Layout layout) { free(ptr); }\n" +
		"}\n";

	private static string Heap(string fromType) =>
		"namespace System.Memory;\n" +
		"public struct Heap<T, A> where A : Allocator where default A : SystemAllocator { T* Ptr; }\n" +
		"public extension Heap<T, A>\n" +
		"{\n" +
		"    Heap(T value)\n" +
		"    {\n" +
		"        val allocator = default(A);\n" +
		"        val layout = Layout." + fromType + ";\n" +
		"        Ptr = allocator.Alloc(layout);\n" +
		"    }\n" +
		"}\n";

	private static IReadOnlyList<Diagnostic> HeapDiagnostics(string fromType)
	{
		var fixture = TempProject.Create(
			("libraries/Base/Memory/Layout.cvl", LayoutSource),
			("libraries/Base/Memory/Allocator.cvl", AllocatorSource),
			("libraries/System.Memory/SystemAllocator.cvl", SystemAllocatorSource),
			("libraries/System.Memory/Heap.cvl", Heap(fromType)),
			("Main.cvl", "int main() { return 0; }\n"));
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		var document = project.InitialSnapshot.GetDocument(project.GetDocumentId("libraries/System.Memory/Heap.cvl"));
		return document.GetDiagnostics();
	}

	[Fact]
	public void InvalidGenericMemberCall_InALocalStdlibGenericExtensionBody_IsDiagnosed()
	{
		var diagnostics = HeapDiagnostics("FromType()");
		Assert.Contains(diagnostics, d => d.Id == "CVL4063");
		Assert.DoesNotContain(diagnostics, d => d.Id == "CVL0000");
	}

	[Fact]
	public void ValidGenericMemberCall_InALocalStdlibGenericExtensionBody_HasNoArityDiagnostic()
	{
		var diagnostics = HeapDiagnostics("FromType<T>()");
		Assert.DoesNotContain(diagnostics, d => d.Id == "CVL4063");
		Assert.DoesNotContain(diagnostics, d => d.Id == "CVL0000");
	}
}
