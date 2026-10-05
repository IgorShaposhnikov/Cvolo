using Cvolo.Projects;
using Cvolo.Tests.Core;

namespace Cvolo.Tests;

public sealed class StandardLibraryResolutionTests : CompilerTestBase
{
	private const string BaseOption = "Base/Option.cvl";
	private const string BaseType = "Base/Type.cvl";
	private const string SystemConsole = "System/Console.cvl";
	private const string SystemSystem = "System/System.cvl";
	private const string SystemMathInt = "System/Math/Int.cvl";
	private const string SystemMathDouble = "System/Math/Double.cvl";

	private static string CasePath(string testCase) =>
		Path.Combine(AppContext.BaseDirectory, TestCasesDirectory, testCase);

	private static bool HasLibrary(IReadOnlyList<string> sources, string relativePath)
	{
		var suffix = relativePath.Replace('/', Path.DirectorySeparatorChar);
		return sources.Any(source => source.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
	}

	private static IReadOnlyList<string> ResolveSources(string testCase, bool includeSystem = true)
	{
		var project = CompilationProject.Load(CasePath(testCase), AppContext.BaseDirectory, includeSystem: includeSystem);
		return project.SourceFiles;
	}

	private void AssertCheckSucceeds(string testCase)
	{
		var (exitCode, stdout, stderr) = RunCompilerCheck(testCase);
		AssertCompilationSucceeded(exitCode, stdout, stderr, testCase);
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

		throw new InvalidOperationException("Could not locate the repository libraries root.");
	}

	[Fact]
	public void NoImportProject_SelectsBaseAndExcludesSystem()
	{
		var sources = ResolveSources("StandardLibraryResolution/NoImport.cvl");

		Assert.True(HasLibrary(sources, BaseOption), "Base Option.cvl must always be selected.");
		Assert.False(HasLibrary(sources, SystemConsole), "Console.cvl must not be selected without an import.");
		Assert.False(HasLibrary(sources, SystemMathInt), "Math sources must not be selected without an import.");

		AssertCheckSucceeds("StandardLibraryResolution/NoImport.cvl");
	}

	[Fact]
	public void OptionalSyntax_ResolvesFromBaseWithoutUsing()
	{
		var sources = ResolveSources("StandardLibraryResolution/LocalOptional.cvl");
		Assert.True(HasLibrary(sources, BaseOption));

		AssertCheckSucceeds("StandardLibraryResolution/LocalOptional.cvl");
	}

	[Fact]
	public void Typeof_ResolvesTypeFromBaseWithoutUsing()
	{
		var sources = ResolveSources("StandardLibraryResolution/TypeofCore.cvl");
		Assert.True(HasLibrary(sources, BaseType));

		AssertCheckSucceeds("StandardLibraryResolution/TypeofCore.cvl");
	}

	[Fact]
	public void IntrinsicAttribute_IsAcceptedWithoutUsing()
	{
		AssertCheckSucceeds("StandardLibraryResolution/AttributeCore.cvl");
	}

	[Fact]
	public void UsingSystem_SelectsConsoleAndExcludesUnrelatedSystem()
	{
		var sources = ResolveSources("StandardLibraryResolution/UsingSystemConsole.cvl");

		Assert.True(HasLibrary(sources, BaseOption));
		Assert.True(HasLibrary(sources, SystemSystem));
		Assert.True(HasLibrary(sources, SystemConsole));
		Assert.False(HasLibrary(sources, SystemMathInt));
		Assert.False(HasLibrary(sources, SystemMathDouble));
	}

	[Fact]
	public void NestedSystemImport_SelectsRequiredModuleOnly()
	{
		var sources = ResolveSources("StandardLibraryResolution/UsingSystemMathInt.cvl");

		Assert.True(HasLibrary(sources, SystemMathInt));
		Assert.False(HasLibrary(sources, SystemMathDouble));
	}

	[Fact]
	public void FullyQualifiedSystemReference_SelectsRequiredModule()
	{
		var sources = ResolveSources("StandardLibraryResolution/FullyQualifiedMath.cvl");

		Assert.True(HasLibrary(sources, SystemMathInt));
	}

	[Fact]
	public void Freestanding_KeepsBaseAndDisablesSystem()
	{
		var consoleSources = ResolveSources("StandardLibraryResolution/UsingSystemConsole.cvl", includeSystem: false);
		Assert.True(HasLibrary(consoleSources, BaseOption));
		Assert.False(HasLibrary(consoleSources, SystemConsole));

		var optionalSources = ResolveSources("StandardLibraryResolution/LocalOptional.cvl", includeSystem: false);
		Assert.True(HasLibrary(optionalSources, BaseOption));
	}

	[Fact]
	public void LibraryClosure_IsDeterministicAndDuplicateFree()
	{
		var first = ResolveSources("StandardLibraryResolution/UsingSystemMathInt.cvl");
		var second = ResolveSources("StandardLibraryResolution/UsingSystemMathInt.cvl");

		Assert.Equal(first, second);

		var distinct = first.Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).Count();
		Assert.Equal(first.Count, distinct);
	}

	[Fact]
	public void BaseWorkspace_DoesNotDuplicatePaths()
	{
		var option = Path.Combine(FindRepositoryLibraries(), "Base", "Option.cvl");
		var project = CompilationProject.Load(option, AppContext.BaseDirectory);
		var sources = project.SourceFiles;

		var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
		Assert.Equal(sources.Count, sources.Distinct(comparer).Count());
		Assert.Single(sources.Where(source => string.Equals(source, option, StringComparison.OrdinalIgnoreCase)));
	}

	[Fact]
	public void LocalLibraryRoot_IsAuthoritativeOverBundledRoot()
	{
		var root = Path.Combine(Path.GetTempPath(), "cvolo-stdlib-resolution-" + Guid.NewGuid().ToString("N"));
		var localLibraries = Path.Combine(root, "libraries");
		var baseDirectory = Path.Combine(localLibraries, "Base");
		var systemDirectory = Path.Combine(localLibraries, "System");
		Directory.CreateDirectory(baseDirectory);
		Directory.CreateDirectory(systemDirectory);

		try
		{
			File.WriteAllText(
				Path.Combine(baseDirectory, "Option.cvl"),
				"union Option<T>\n{\n\tvoid None;\n\tT Some;\n}\n");

			var console = Path.Combine(systemDirectory, "Console.cvl");
			File.WriteAllText(console, "namespace System.Console;\n\npublic void WriteLine(string text)\n{\n}\n");

			var project = CompilationProject.Load(console, AppContext.BaseDirectory);
			var sources = project.SourceFiles;

			Assert.All(sources, source => Assert.StartsWith(localLibraries, source, StringComparison.OrdinalIgnoreCase));
		}
		finally
		{
			if (Directory.Exists(root))
				Directory.Delete(root, recursive: true);
		}
	}

	[Fact]
	public void Freestanding_WithoutSystemImport_Succeeds()
	{
		var (exitCode, stdout, stderr) = RunCompilerCheck("StandardLibraryResolution/NoImport.cvl", "--freestanding");
		AssertCompilationSucceeded(exitCode, stdout, stderr, "StandardLibraryResolution/NoImport.cvl");
	}

	[Fact]
	public void CustomResultUnion_WorksWithTryCatch()
	{
		AssertCheckSucceeds("StandardLibraryResolution/ResultShapeCustom.cvl");
	}

	[Fact]
	public void ResultUnion_MissingErr_IsRejected()
	{
		var (exitCode, _, stderr) = RunCompilerCheck("StandardLibraryResolution/ResultShapeMissingErr.cvl");
		Assert.Equal(1, exitCode);
		Assert.Contains("CVL1092", stderr);
	}
}
