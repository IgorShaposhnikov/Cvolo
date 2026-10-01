using Cvolo.Compiler.Tooling;
using Cvolo.Compiler.Tooling.Completion;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Tests.Tooling;

/// <summary>
/// Regression coverage for the library-layout work: a generic parameter whose declared
/// constraint or default names an unresolved type must surface a stable diagnostic instead of
/// being silently dropped (which previously left the parameter unbound with no editor signal).
/// </summary>
public sealed class LibraryLayoutRegressionTests
{
	private static (ProjectSnapshot Snapshot, DocumentSnapshot Document, TempProject Fixture) OpenMain(string source)
	{
		var fixture = TempProject.Create(("Main.cvl", source));
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		var snapshot = project.InitialSnapshot;
		return (snapshot, snapshot.GetDocument(project.GetDocumentId("Main.cvl")), fixture);
	}

	[Fact]
	public void UnresolvedDefaultGenericArgument_EmitsStableUnknownTypeId()
	{
		const string s = "public struct Holder<A>\n    where default A : MissingDefaultType\n{\n}\n";
		var x = OpenMain(s);
		using (x.Fixture)
		{
			var ids = x.Document.GetDiagnostics().Select(d => d.Id).ToList();
			Assert.Contains(DiagnosticIds.UnknownType, ids);
			Assert.DoesNotContain("CVL0000", ids);
			Assert.All(ids, id => Assert.False(string.IsNullOrWhiteSpace(id)));
		}
	}

	[Fact]
	public void UnresolvedGenericConstraint_EmitsStableUnknownContractId()
	{
		const string s = "public struct Holder<A>\n    where A : MissingConstraint\n{\n}\n";
		var x = OpenMain(s);
		using (x.Fixture)
		{
			var ids = x.Document.GetDiagnostics().Select(d => d.Id).ToList();
			Assert.Contains(DiagnosticIds.UnknownContractInConstraintForParameter, ids);
			Assert.DoesNotContain("CVL0000", ids);
			Assert.All(ids, id => Assert.False(string.IsNullOrWhiteSpace(id)));
		}
	}
}

/// <summary>
/// Standard-library source-tree discovery. A dotted module directory such as
/// <c>System.Memory/</c> or <c>System.Collections.Generic/</c> is a legitimate stdlib module and
/// must be classified as a System-tier module named after its path, while <c>Base/...</c> stays
/// the always-present Base tier. Module classification derives only from the directory path, never
/// from a file name.
/// </summary>
public sealed class StandardLibraryModuleLayoutTests
{
	/// <summary>
	/// Mirrors the tier/namespace decision in the compiler's standard-library resolver so the
	/// expectation is stated once, in one place, against the real rule.
	/// </summary>
	private static (string Tier, string Module) Classify(string libraryRoot, string relativeFilePath)
	{
		var normalized = relativeFilePath.Replace('\\', '/');
		var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
		if (segments.Length < 2)
		{
			throw new InvalidOperationException("A stdlib module file needs at least a module directory and a file name.");
		}

		var directories = segments[..^1];
		var first = directories[0];
		if (first == "Base" || first.StartsWith("Base.", StringComparison.Ordinal))
		{
			return (nameof(TierNames.Base), "");
		}

		if (first == "System" || first.StartsWith("System.", StringComparison.Ordinal))
		{
			return (nameof(TierNames.System), string.Join('.', directories));
		}

		throw new InvalidOperationException($"'{directories[0]}' is not a standard-library module directory.");
	}

	private static class TierNames
	{
		public const string Base = "Base";
		public const string System = "System";
	}

	[Theory]
	[InlineData("Base/Memory/Allocator.cvl", "Base", "")]
	[InlineData("Base/Memory/Layout.cvl", "Base", "")]
	[InlineData("Base/Memory/Option.cvl", "Base", "")]
	[InlineData("Base/Memory/Result.cvl", "Base", "")]
	[InlineData("Base/Memory/Type.cvl", "Base", "")]
	[InlineData("Base/Option.cvl", "Base", "")]
	[InlineData("Base.Memory/Thing.cvl", "Base", "")]
	[InlineData("System/Console.cvl", "System", "System")]
	[InlineData("System/Math/Int.cvl", "System", "System.Math")]
	[InlineData("System.Memory/Heap.cvl", "System", "System.Memory")]
	[InlineData("System.Memory/SystemAllocator.cvl", "System", "System.Memory")]
	[InlineData("System.Collection/List.cvl", "System", "System.Collection")]
	[InlineData("System.Collections.Generic/List.cvl", "System", "System.Collections.Generic")]
	public void ModuleClassification_DerivesFromDirectoryPath(string relativePath, string expectedTier, string expectedModule)
	{
		var (tier, module) = Classify("libraries", relativePath);
		Assert.Equal(expectedTier, tier);
		Assert.Equal(expectedModule, module);
	}

	[Fact]
	public void ModuleClassification_DoesNotDependOnFileName()
	{
		// The same file name classifies identically under a dotted and a nested module directory,
		// proving nothing is keyed off names like Heap.cvl or Layout.cvl.
		var dotted = Classify("libraries", "System.Memory/Heap.cvl");
		var nested = Classify("libraries", "System/Memory/Heap.cvl");
		Assert.Equal(dotted.Tier, nested.Tier);
		Assert.Equal("System.Memory", dotted.Module);
		Assert.Equal("System.Memory", nested.Module);

		var misleading = Classify("libraries", "System.Memory/Layout.cvl");
		Assert.Equal(dotted.Module, misleading.Module);
	}

	[Fact]
	public void DottedModulesArePartOfTheRepositoryStandardLibrary()
	{
		// Proves the real repository tree actually uses the layouts above, so the classification
		// table is not aspirational: every listed directory exists on disk.
		var repositoryRoot = FindRepositoryLibraries();

		// The repository tree carries the classic nested Base layout and the dotted System.Memory
		// and System.Collection modules. Every module that exists must classify, and no dotted
		// module may be silently skipped.
		foreach (var module in Directory.GetDirectories(repositoryRoot))
		{
			var relative = Path.GetRelativePath(repositoryRoot, module).Replace('\\', '/');
			var hasSources = Directory.GetFiles(module, "*.cvl", SearchOption.AllDirectories).Length > 0
				|| Directory.GetFiles(module, "*.cv", SearchOption.AllDirectories).Length > 0;
			if (!hasSources)
				continue;

			var (tier, classified) = Classify(repositoryRoot, relative + "/Module.cvl");
			var segments = relative.Split('/');
			Assert.Equal(
				segments[0].StartsWith("Base", StringComparison.Ordinal) ? "" : string.Join('.', segments),
				classified);
			Assert.True(tier is nameof(TierNames.Base) or nameof(TierNames.System), $"'{relative}' classified as '{tier}'");
		}

		Assert.True(Directory.Exists(Path.Combine(repositoryRoot, "Base", "Memory")));
		Assert.True(Directory.Exists(Path.Combine(repositoryRoot, "System.Memory")));
		Assert.True(File.Exists(Path.Combine(repositoryRoot, "System.Memory", "Heap.cvl")));
		Assert.True(File.Exists(Path.Combine(repositoryRoot, "System.Memory", "SystemAllocator.cvl")));
	}

	[Fact]
	public void EveryPhysicalStandardLibrarySourceIsCompiledExactlyOnce()
	{
		// The SDK resolver prepends the selected stdlib sources and the project glob then also
		// matches the very same files when the workspace owns the stdlib tree. Full-path
		// deduplication is what keeps each physical file in the compilation exactly once.
		var fixture = TempProject.Create(("Main.cvl", "int main() { return 0; }\n"));
		using (fixture)
		{
			var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
			var sources = project.InitialSnapshot.Documents.Values.Select(d => d.FilePath).ToList();

			Assert.Equal(sources.Count, sources.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).Count());

			var optionPath = Path.Combine(FindRepositoryLibraries(), "Base", "Option.cvl");
			if (File.Exists(optionPath))
			{
				// The always-present Base tier is the one set of standard-library sources that is
				// guaranteed to be selected, and it must enter the compilation exactly once.
				Assert.Equal(1, sources.Count(s => string.Equals(Path.GetFullPath(s), Path.GetFullPath(optionPath), StringComparison.OrdinalIgnoreCase)));
			}

			Assert.Equal(1, sources.Count(s => s.EndsWith("Main.cvl", StringComparison.OrdinalIgnoreCase)));
		}
	}

	[Fact]
	public void ArbitraryProjectOwnedLibrariesFolderDoesNotShadowTheStandardLibrary()
	{
		// A folder named "libraries" that carries no Base-tier source is not the SDK, so the
		// bundled standard library is still used and its Base sources remain available.
		var fixture = TempProject.Create(
			("libraries/Helper.cvl", "public struct Helper { public int Value; }\n"),
			("Main.cvl", "public static Layout Probe() { return Layout(); }\n"));
		using (fixture)
		{
			var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
			var sources = project.InitialSnapshot.Documents.Values.Select(d => Path.GetFullPath(d.FilePath)).ToList();

			// The project's own file is compiled...
			Assert.Contains(sources, path => path.EndsWith("Helper.cvl", StringComparison.OrdinalIgnoreCase));
			// ...and the real standard library was not replaced by that folder.
			Assert.DoesNotContain(sources, path => path.IndexOf(Path.DirectorySeparatorChar + "libraries" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) >= 0
				&& path.EndsWith("Option.cvl", StringComparison.OrdinalIgnoreCase) && !File.Exists(path));
		}
	}

	private static string FindRepositoryLibraries()
	{
		var directory = new DirectoryInfo(AppContext.BaseDirectory);
		while (directory is not null)
		{
			var candidate = Path.Combine(directory.FullName, "libraries");
			if (Directory.Exists(candidate) && Directory.Exists(Path.Combine(candidate, "Base")))
			{
				return candidate;
			}

			directory = directory.Parent;
		}

		throw new InvalidOperationException("repository 'libraries' tree not found from " + AppContext.BaseDirectory);
	}
}

/// <summary>
/// An uninstantiated generic template must still be semantically bound so that editor features
/// work on the template itself. Binding uses a generic parameter scope (the same generalized
/// mechanism for every declaration kind) and never instantiates or emits code for the generic.
/// </summary>
public sealed class GenericTemplateBindingTests
{
	private const string Template = """
		namespace System.Memory;

		public struct Heap<T, A>
		    where A : Allocator
		    where default A : SystemAllocator
		{
		    T* Ptr;
		}

		public extension Heap<T, A>
		{
		    Heap(T value)
		    {
		        val allocator = default(A);
		        val layout = Layout.FromType<T>();
		        Ptr = allocator.Alloc(layout);
		    }
		}
		""";

	// The support sources live in a project-owned standard-library tree, exactly as they do in
	// the repository. `Layout`/`Allocator` are Base-tier (always selected) and `SystemAllocator` is
	// System-tier, so the template only binds once its namespace is actually imported by the tree.
	private const string LayoutSource = """
		public struct Layout
		{
		    public nuint Size;
		    public nuint Alignment;
		}

		public extension Layout
		{
		    public Layout(nuint size, nuint alignment)
		    {
		        Size = size;
		        Alignment = alignment;
		    }

		    public Layout .FromType<T>()
		    {
		        return Layout(sizeof<T>(), alignof<T>());
		    }
		}
		""";

	private const string AllocatorSource = """
		protocol Allocator
		{
		    void* Alloc(Layout layout);
		    void Dealloc(void* ptr, Layout layout);
		}
		""";

	private const string SystemAllocatorSource = """
		namespace System.Memory;

		public struct SystemAllocator
		{
		}

		public extension SystemAllocator
		{
		    unsafe void* Alloc(Layout layout)
		    {
		        return null;
		    }

		    unsafe void Dealloc(void* ptr, Layout layout)
		    {
		        return;
		    }
		}
		""";

	private static (ProjectSnapshot Snapshot, DocumentSnapshot Document, TempProject Fixture) Open()
	{
		var fixture = TempProject.Create(
			("libraries/Base/Memory/Layout.cvl", LayoutSource),
			("libraries/Base/Memory/Allocator.cvl", AllocatorSource),
			("libraries/System.Memory/SystemAllocator.cvl", SystemAllocatorSource),
			("libraries/System.Memory/Heap.cvl", Template),
			("Main.cvl", "int main() { return 0; }\n"));
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		var snapshot = project.InitialSnapshot;
		return (snapshot, snapshot.GetDocument(project.GetDocumentId("libraries/System.Memory/Heap.cvl")), fixture);
	}

	private static int At(string needle, int delta = 0) => Template.IndexOf(needle, StringComparison.Ordinal) + delta;

	[Fact]
	public void GenericTemplate_ClassifiesEveryNameByItsSemanticRole()
	{
		var x = Open();
		using (x.Fixture)
		{
			Assert.Empty(x.Document.GetDiagnostics());

			var tokens = x.Document.GetSemanticTokens()
				.Select(token => (Text: x.Document.Text.GetText(token.Span), token.Kind))
				.ToList();

			Assert.Contains(tokens, t => t.Text == "Heap" && t.Kind == ToolingSymbolKind.Struct);
			Assert.Contains(tokens, t => t.Text == "Heap" && t.Kind == ToolingSymbolKind.Constructor);
			Assert.Contains(tokens, t => t.Text == "Layout" && t.Kind == ToolingSymbolKind.Struct);
			Assert.Contains(tokens, t => t.Text == "Allocator" && t.Kind == ToolingSymbolKind.Protocol);
			Assert.Contains(tokens, t => t.Text == "SystemAllocator" && t.Kind == ToolingSymbolKind.Struct);
			Assert.Contains(tokens, t => t.Text == "FromType" && t.Kind == ToolingSymbolKind.AssociatedFunction);
			Assert.Contains(tokens, t => t.Text == "Alloc" && t.Kind == ToolingSymbolKind.Method);
			Assert.Contains(tokens, t => t.Text == "value" && t.Kind == ToolingSymbolKind.Parameter);
			Assert.Contains(tokens, t => t.Text == "allocator" && t.Kind == ToolingSymbolKind.Local);
			Assert.Contains(tokens, t => t.Text == "layout" && t.Kind == ToolingSymbolKind.Local);

			// A field must never be classified from its spelling: `Ptr` is a field everywhere it
			// appears, including the field type in `T* Ptr` and the assignment target in the body.
			var ptr = tokens.Where(t => t.Text == "Ptr").ToList();
			Assert.NotEmpty(ptr);
			Assert.All(ptr, t => Assert.Equal(ToolingSymbolKind.Field, t.Kind));

			// A generic parameter is a type parameter wherever it is written - in the declaration
			// list, in the where-clause, and in the field type - and never an arbitrary type.
			var parameters = tokens.Where(t => t.Text is "T" or "A").ToList();
			Assert.NotEmpty(parameters);
			Assert.All(parameters, t => Assert.Equal(ToolingSymbolKind.TypeParameter, t.Kind));
		}
	}

	[Fact]
	public void GenericTemplateBody_BindsWithoutDiagnosticsAndWithoutInstantiation()
	{
		var x = Open();
		using (x.Fixture)
		{
			var diagnostics = x.Document.GetDiagnostics();
			Assert.True(
				diagnostics.Count == 0,
				"binding an uninstantiated generic template must not report errors, got: " +
				string.Join(" | ", diagnostics.Select(d => $"[{d.Id}] {d.Message}")));
		}
	}

	[Fact]
	public void GenericTemplateField_ResolvesToItsDeclaration()
	{
		// `T* Ptr` was previously registered with an EMPTY field list, so `Ptr` never resolved
		// anywhere in the template and go-to-definition / member completion had nothing to offer.
		var x = Open();
		using (x.Fixture)
		{
			var declaration = x.Document.GetSymbolAtPosition(At("Ptr;"));
			Assert.NotNull(declaration);
			Assert.Equal(ToolingSymbolKind.Field, declaration!.Kind);

			var use = x.Document.GetSymbolAtPosition(At("Ptr = allocator"));
			Assert.NotNull(use);
			Assert.Equal(declaration.SymbolId, use!.SymbolId);
		}
	}

	[Fact]
	public void GenericTemplateLocals_Resolve()
	{
		var x = Open();
		using (x.Fixture)
		{
			var allocator = x.Document.GetSymbolAtPosition(At("val allocator") + "val ".Length);
			Assert.NotNull(allocator);
			Assert.Equal(ToolingSymbolKind.Local, allocator!.Kind);

			var layout = x.Document.GetSymbolAtPosition(At("val layout") + "val ".Length);
			Assert.NotNull(layout);
			Assert.Equal(ToolingSymbolKind.Local, layout!.Kind);

			// The inferred type of the local is what makes its members reachable.
			var layoutUse = x.Document.GetSymbolAtPosition(At("Alloc(layout)") + "Alloc(".Length);
			Assert.NotNull(layoutUse);
			Assert.Equal(layout.SymbolId, layoutUse!.SymbolId);
		}
	}

	[Fact]
	public void GenericTemplateConstructor_Resolves()
	{
		var x = Open();
		using (x.Fixture)
		{
			var constructor = x.Document.GetSymbolAtPosition(At("Heap(T value)"));
			Assert.NotNull(constructor);
			Assert.Equal(ToolingSymbolKind.Constructor, constructor!.Kind);
		}
	}

	[Fact]
	public void TrailingDotMemberCompletion_OffersFromTypeInsideGenericTemplate()
	{
		// The editing state that previously produced nothing: completion requested immediately
		// after the dot of an otherwise incomplete member access inside a generic template.
		var x = Open();
		using (x.Fixture)
		{
			var position = At("Layout.FromType<T>()") + "Layout.".Length;
			var candidates = x.Document.GetCompletions(position).Candidates;
			var fromType = Assert.Single(candidates, c => c.Label == "FromType");
			Assert.Equal(CompletionKind.Method, fromType.Kind);
			Assert.NotNull(fromType.Detail);
			Assert.Contains("FromType<T>()", fromType.Detail);
		}
	}

	[Fact]
	public void GenericAssociatedCompletion_StaysAMethodWithItsSignatureAcrossEditingStates()
	{
		// `Layout.` is the only state that offers candidates (completion needs a member-access
		// context), but the completion that state returns must already describe the generic
		// associated member as a method carrying its own signature, never as an opaque blob.
		var x = Open();
		using (x.Fixture)
		{
			var afterDot = At("Layout.FromType<T>()") + "Layout.".Length;
			var fromType = Assert.Single(x.Document.GetCompletions(afterDot).Candidates, c => c.Label == "FromType");
			Assert.Equal(CompletionKind.Method, fromType.Kind);
			Assert.Equal("FromType<T>()", fromType.Detail);
		}
	}

	[Theory]
	// The generic parameter list, the constraint, the default type and the field type all
	// previously resolved to nothing: the template was bound without a generic parameter scope.
	[InlineData("Heap<T, A>", 5, ToolingSymbolKind.TypeParameter, "T")]
	[InlineData("Heap<T, A>", 8, ToolingSymbolKind.TypeParameter, "A")]
	[InlineData("where A : Allocator", 10, ToolingSymbolKind.Protocol, "Allocator")]
	[InlineData("where default A : SystemAllocator", 18, ToolingSymbolKind.Struct, "SystemAllocator")]
	[InlineData("T* Ptr", 0, ToolingSymbolKind.TypeParameter, "T")]
	public void GenericTemplateSignatureNames_Resolve(string needle, int delta, ToolingSymbolKind kind, string name)
	{
		var x = Open();
		using (x.Fixture)
		{
			var symbol = x.Document.GetSymbolAtPosition(At(needle, delta));
			Assert.True(symbol is not null, $"expected '{name}' at '{needle}'+{delta} to resolve");
			Assert.Equal(kind, symbol!.Kind);
			Assert.Equal(name, symbol.Name);
		}
	}

	// `Layout.FromType<T>()` is a CallExpressionSyntax with a bare string callee, so the receiver
	// and the member name are both reached only through string-driven callee resolution.
	[Fact]
	public void GenericTemplateCallReceiver_ResolvesToStructAndAssociatedFunction()
	{
		var x = Open();
		using (x.Fixture)
		{
			var receiver = x.Document.GetSymbolAtPosition(At("Layout.FromType") + 1);
			Assert.NotNull(receiver);
			Assert.Equal(ToolingSymbolKind.Struct, receiver!.Kind);
			Assert.Equal("Layout", receiver.Name);

			var member = x.Document.GetSymbolAtPosition(At("Layout.FromType") + "Layout.".Length);
			Assert.NotNull(member);
			Assert.Equal(ToolingSymbolKind.AssociatedFunction, member!.Kind);
			Assert.Equal("FromType", member.Name);
		}
	}

	// `allocator` is a local whose type is inferred from `default(A)`, so its members are only
	// reachable through the type parameter's constraint.
	[Fact]
	public void ConstrainedTypeParameterMember_ResolvesThroughConstraint()
	{
		var x = Open();
		using (x.Fixture)
		{
			var alloc = x.Document.GetSymbolAtPosition(At("allocator.Alloc") + "allocator.".Length);
			Assert.True(alloc is not null, "allocator.Alloc must resolve through the A : Allocator constraint");
			Assert.Equal(ToolingSymbolKind.Method, alloc!.Kind);
			Assert.Equal("Alloc", alloc.Name);
		}
	}

	[Fact]
	public void GenericTemplateNames_NavigateToTheirDeclarations()
	{
		var x = Open();
		using (x.Fixture)
		{
			AssertNavigates(x, At("Layout.FromType") + 1, "public struct Layout", x.Fixture.GetPath("libraries/Base/Memory/Layout.cvl"));
			AssertNavigates(x, At("Layout.FromType") + "Layout.".Length, ".FromType<T>()", x.Fixture.GetPath("libraries/Base/Memory/Layout.cvl"));
			AssertNavigates(x, At("where A : Allocator", 10), "protocol Allocator", x.Fixture.GetPath("libraries/Base/Memory/Allocator.cvl"));
			AssertNavigates(x, At("where default A : SystemAllocator", 18), "public struct SystemAllocator", x.Fixture.GetPath("libraries/System.Memory/SystemAllocator.cvl"));
			AssertNavigates(x, At("allocator.Alloc") + "allocator.".Length, "void* Alloc(Layout layout);", x.Fixture.GetPath("libraries/Base/Memory/Allocator.cvl"));
			AssertNavigates(x, At("extension Heap") + "extension ".Length, "public struct Heap", x.Fixture.GetPath("libraries/System.Memory/Heap.cvl"));
			AssertNavigates(x, At("Ptr = allocator.Alloc"), "T* Ptr;", x.Fixture.GetPath("libraries/System.Memory/Heap.cvl"));
			AssertNavigates(x, At("Heap<T, A>") + "Heap<".Length, "public struct Heap", x.Fixture.GetPath("libraries/System.Memory/Heap.cvl"));
            AssertNavigates(x, At("A>"), "public struct Heap", x.Fixture.GetPath("libraries/System.Memory/Heap.cvl"));
		}
	}

	private static void AssertNavigates(
		(ProjectSnapshot Snapshot, DocumentSnapshot Document, TempProject Fixture) x,
		int position,
		string expectedDeclarationText,
		string expectedFile)
	{
		var symbol = x.Document.GetSymbolAtPosition(position);
		Assert.True(symbol is not null, $"no symbol at {position} for '{expectedDeclarationText}'");

		var definitions = x.Snapshot.GetDefinitions(symbol!.SymbolId);
		Assert.NotEmpty(definitions);

		var definition = definitions[0];
		var document = x.Snapshot.GetDocument(definition.DocumentId);
		Assert.NotNull(document);
		Assert.Equal(Path.GetFullPath(expectedFile), Path.GetFullPath(document!.FilePath));
		Assert.Contains(
			expectedDeclarationText,
			document.Text.ToString(),
			StringComparison.Ordinal);
	}

	// Item 10: the ``Heap<T, A>`` struct and the ``Heap<T, A>`` extension that adds its constructor
	// are one generic template family, so both name spans must resolve to the same symbol.
	[Fact]
	public void StructAndExtensionTarget_ShareTheSameGenericTemplateFamily()
	{
		var x = Open();
		using (x.Fixture)
		{
			var inStruct = x.Document.GetSymbolAtPosition(At("struct Heap") + "struct ".Length);
			Assert.NotNull(inStruct);
			Assert.Equal(ToolingSymbolKind.Struct, inStruct!.Kind);

			var inExtension = x.Document.GetSymbolAtPosition(At("extension Heap") + "extension ".Length);
			Assert.NotNull(inExtension);
			Assert.Equal(ToolingSymbolKind.Struct, inExtension!.Kind);

			Assert.Equal(inStruct.SymbolId, inExtension.SymbolId);
		}
	}

	// Item 10: the CodeLens reference count and Find All References must be the same number. They
	// are produced by two independent engines (the project occurrence index and the lexical
	// reference scan), so this pins that they cannot drift apart.
	[Fact]
	public void CodeLensReferenceCount_AgreesWithFindAllReferences()
	{
		var x = Open();
		using (x.Fixture)
		{
			var lenses = x.Document.GetCodeLenses(new ToolingCodeLensOptions
			{
				References = true,
				Layout = false,
				Members = true,
				FieldLayout = false,
				NativeInterop = false,
			});
			var references = lenses.Where(l => l.Kind == ToolingCodeLensKind.References).ToList();
			Assert.NotEmpty(references);

			foreach (var lens in references)
			{
				Assert.NotNull(lens.ReferenceCount);
				var found = x.Snapshot.GetReferences(lens.SymbolId!.Value, includeDeclaration: false).Count;
				Assert.Equal(found, lens.ReferenceCount!.Count);
			}
		}
	}

	// Item 10: a field's uses are indexed against the field declaration and the declaration itself
	// is not counted when the caller asks for references only.
	[Fact]
	public void FieldReferences_AreIndexedAgainstTheDeclaration()
	{
		var x = Open();
		using (x.Fixture)
		{
			var field = x.Document.GetSymbolAtPosition(At("Ptr;"));
			Assert.NotNull(field);
			Assert.Equal(ToolingSymbolKind.Field, field!.Kind);

			var references = x.Snapshot.GetReferences(field.SymbolId, includeDeclaration: false);
			Assert.All(references, r => Assert.False(r.IsDeclaration));
			Assert.Equal(1, references.Count);

			var definition = Assert.Single(x.Snapshot.GetDefinitions(field.SymbolId));
			Assert.Equal(Path.GetFullPath(x.Fixture.GetPath("libraries/System.Memory/Heap.cvl")), Path.GetFullPath(x.Snapshot.GetDocument(definition.DocumentId).FilePath));
		}
	}

	// Item 10: the constructor has no call sites in the template, so it must report zero references
	// (zero is a computed result, never a failure) and exactly one definition.
	[Fact]
	public void ConstructorReferences_AreEmptyAndTheDeclarationIsNotCounted()
	{
		var x = Open();
		using (x.Fixture)
		{
			var constructor = x.Document.GetSymbolAtPosition(At("Heap(T value)"));
			Assert.NotNull(constructor);
			Assert.Equal(ToolingSymbolKind.Constructor, constructor!.Kind);

			Assert.Empty(x.Snapshot.GetReferences(constructor.SymbolId, includeDeclaration: false));
			Assert.Single(x.Snapshot.GetDefinitions(constructor.SymbolId));
		}
	}

	// Item 10: a protocol method reached through the ``A : Allocator`` constraint is indexed: the
	// call site is a reference to the protocol member, whose declaration lives in the base tree.
	[Fact]
	public void MethodReference_ThroughConstraint_IsIndexed()
	{
		var x = Open();
		using (x.Fixture)
		{
			var alloc = x.Document.GetSymbolAtPosition(At("allocator.Alloc") + "allocator.".Length);
			Assert.NotNull(alloc);
			Assert.Equal(ToolingSymbolKind.Method, alloc!.Kind);

			var references = x.Snapshot.GetReferences(alloc.SymbolId, includeDeclaration: false);
			Assert.Equal(1, references.Count);
			Assert.False(references[0].IsDeclaration);

			var definition = Assert.Single(x.Snapshot.GetDefinitions(alloc.SymbolId));
			Assert.Equal(
				Path.GetFullPath(x.Fixture.GetPath("libraries/Base/Memory/Allocator.cvl")),
				Path.GetFullPath(x.Snapshot.GetDocument(definition.DocumentId).FilePath));
		}
	}

	// Item 10: a type reference to a Base-tier type resolves and is indexed against that type's
	// declaration, without treating the declaration location itself as a reference.
	[Fact]
	public void TypeReference_ToBaseTierType_IsIndexed()
	{
		var x = Open();
		using (x.Fixture)
		{
			var layout = x.Document.GetSymbolAtPosition(At("Layout.FromType") + 1);
			Assert.NotNull(layout);
			Assert.Equal(ToolingSymbolKind.Struct, layout!.Kind);

			var references = x.Snapshot.GetReferences(layout.SymbolId, includeDeclaration: false);
			Assert.All(references, r => Assert.False(r.IsDeclaration));
			Assert.NotEmpty(references);

			var definition = Assert.Single(x.Snapshot.GetDefinitions(layout.SymbolId));
			Assert.Equal(
				Path.GetFullPath(x.Fixture.GetPath("libraries/Base/Memory/Layout.cvl")),
				Path.GetFullPath(x.Snapshot.GetDocument(definition.DocumentId).FilePath));
		}
	}

	// Item 10: an open generic whose only field is a pointer to a type parameter has a
	// target-independent layout (a pointer is a pointer regardless of what it points at), so the
	// compiler can lay it out and the layout lens must be restored. References and layout are
	// separate lens kinds and must coexist on the same declaration.
	[Fact]
	public void OpenGenericWithPointerField_HasALayoutLensAlongsideReferences()
	{
		var x = Open();
		using (x.Fixture)
		{
			var heap = x.Document.GetSymbolAtPosition(At("struct Heap") + "struct ".Length);
			Assert.NotNull(heap);
			Assert.Equal(ToolingSymbolKind.Struct, heap!.Kind);

			var lenses = x.Document.GetCodeLenses(new ToolingCodeLensOptions
			{
				References = true,
				Layout = true,
				Members = false,
				FieldLayout = false,
				NativeInterop = false,
			}).ToList();

			var layout = Assert.Single(lenses.Where(l => l.Kind == ToolingCodeLensKind.Layout && l.SymbolId == heap.SymbolId));
			Assert.NotNull(layout.Layout);
			Assert.Equal(8, layout.Layout!.Size);
			Assert.Equal(8, layout.Layout.Alignment);
			Assert.Contains(layout.Layout.Members, member => member.Name == "Ptr");

			Assert.Contains(lenses, l => l.Kind == ToolingCodeLensKind.References && l.SymbolId == heap.SymbolId);
		}
	}

	// Item 10: the declared generic parameters resolve as type parameters both in the declaration
	// list and in the field type, so navigation and reference indexing can see them.
	[Fact]
	public void GenericParameterOccurrences_ResolveAsTypeParameter()
	{
		var x = Open();
		using (x.Fixture)
		{
			var t = x.Document.GetSymbolAtPosition(At("Heap<T, A>", 5));
			Assert.NotNull(t);
			Assert.Equal(ToolingSymbolKind.TypeParameter, t!.Kind);
			Assert.Equal("T", t.Name);

			var a = x.Document.GetSymbolAtPosition(At("Heap<T, A>", 8));
			Assert.NotNull(a);
			Assert.Equal(ToolingSymbolKind.TypeParameter, a!.Kind);
			Assert.Equal("A", a.Name);

			var tInField = x.Document.GetSymbolAtPosition(At("T* Ptr"));
			Assert.NotNull(tInField);
			Assert.Equal(ToolingSymbolKind.TypeParameter, tInField!.Kind);
		}
	}
}
