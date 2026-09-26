using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cvolo.Projects;
using Cvolo.Tests.Core;
using Xunit;

namespace Cvolo.Tests;

public sealed class Inc9LibrarySelectionTests : CompilerTestBase
{
	private const string CoreOption = "Core/System/Option.cvl";
	private const string StdConsole = "Std/System/Console.cvl";
	private const string StdMathInt = "Std/System/Math/Int.cvl";
	private const string StdMathDouble = "Std/System/Math/Double.cvl";

	private static string CasePath(string testCase) =>
		Path.Combine(AppContext.BaseDirectory, TestCasesDirectory, testCase);

	private static bool HasLibrary(IReadOnlyList<string> sources, string relativePath)
	{
		var suffix = relativePath.Replace('/', Path.DirectorySeparatorChar);
		return sources.Any(source => source.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
	}

	private static IReadOnlyList<string> ResolveSources(string testCase, bool includeStandardLibrary = true)
	{
		var project = CompilationProject.Load(CasePath(testCase), AppContext.BaseDirectory, includeStandardLibrary: includeStandardLibrary);
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
			if (Directory.Exists(candidate) && Directory.Exists(Path.Combine(candidate, "Core")))
			{
				return candidate;
			}

			directory = directory.Parent;
		}

		throw new InvalidOperationException("Could not locate the repository libraries root.");
	}

	[Fact]
	public void NoImportProject_SelectsCoreAndExcludesHostedStd()
	{
		var sources = ResolveSources("Inc9/NoImport.cvl");

		Assert.True(HasLibrary(sources, CoreOption), "Core Option.cvl must always be selected.");
		Assert.False(HasLibrary(sources, StdConsole), "Console.cvl must not be selected without an import.");
		Assert.False(HasLibrary(sources, StdMathInt), "Math sources must not be selected without an import.");

		AssertCheckSucceeds("Inc9/NoImport.cvl");
	}

	[Fact]
	public void OptionalSyntax_ResolvesFromCoreWithoutUsing()
	{
		var sources = ResolveSources("Inc9/LocalOptional.cvl");
		Assert.True(HasLibrary(sources, CoreOption));

		AssertCheckSucceeds("Inc9/LocalOptional.cvl");
	}

	[Fact]
	public void Typeof_ResolvesSystemTypeFromCoreWithoutUsing()
	{
		var sources = ResolveSources("Inc9/TypeofCore.cvl");
		Assert.True(HasLibrary(sources, "Core/System/Type.cvl"));

		AssertCheckSucceeds("Inc9/TypeofCore.cvl");
	}

	[Fact]
	public void IntrinsicAttribute_IsAcceptedWithoutUsing()
	{
		AssertCheckSucceeds("Inc9/AttributeCore.cvl");
	}

	[Fact]
	public void UsingSystem_SelectsConsoleAndExcludesUnrelatedStd()
	{
		var sources = ResolveSources("Inc9/UsingSystemConsole.cvl");

		Assert.True(HasLibrary(sources, CoreOption));
		Assert.True(HasLibrary(sources, StdConsole));
		Assert.False(HasLibrary(sources, StdMathInt));
		Assert.False(HasLibrary(sources, StdMathDouble));
	}

	[Fact]
	public void NestedStdImport_SelectsRequiredModuleOnly()
	{
		var sources = ResolveSources("Inc9/UsingSystemMathInt.cvl");

		Assert.True(HasLibrary(sources, StdMathInt));
		Assert.False(HasLibrary(sources, StdMathDouble));
	}

	[Fact]
	public void FullyQualifiedStdReference_SelectsRequiredModule()
	{
		var sources = ResolveSources("Inc9/FullyQualifiedMath.cvl");

		Assert.True(HasLibrary(sources, StdMathInt));
		Assert.True(HasLibrary(sources, "Std/System/Math/Math.cvl"));
	}

	[Fact]
	public void NoStdlib_KeepsCoreAndDisablesHostedStd()
	{
		var consoleSources = ResolveSources("Inc9/UsingSystemConsole.cvl", includeStandardLibrary: false);
		Assert.True(HasLibrary(consoleSources, CoreOption));
		Assert.False(HasLibrary(consoleSources, StdConsole));

		var optionalSources = ResolveSources("Inc9/LocalOptional.cvl", includeStandardLibrary: false);
		Assert.True(HasLibrary(optionalSources, CoreOption));
	}

	[Fact]
	public void LibraryClosure_IsDeterministicAndDuplicateFree()
	{
		var first = ResolveSources("Inc9/UsingSystemMathInt.cvl");
		var second = ResolveSources("Inc9/UsingSystemMathInt.cvl");

		Assert.Equal(first, second);

		var distinct = first.Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).Count();
		Assert.Equal(first.Count, distinct);
	}

	[Fact]
	public void StandardLibraryWorkspace_DoesNotDuplicatePaths()
	{
		var constants = Path.Combine(FindRepositoryLibraries(), "Std", "System", "Math", "Constants.cvl");
		var project = CompilationProject.Load(constants, AppContext.BaseDirectory);
		var sources = project.SourceFiles;

		Assert.True(HasLibrary(sources, CoreOption));

		var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
		Assert.Equal(sources.Count, sources.Distinct(comparer).Count());
		Assert.Single(sources.Where(source => string.Equals(source, constants, StringComparison.OrdinalIgnoreCase)));
	}

	[Fact]
	public void LocalLibraryRoot_IsAuthoritativeOverBundledRoot()
	{
		var root = Path.Combine(Path.GetTempPath(), "cvolo-inc9-" + Guid.NewGuid().ToString("N"));
		var localLibraries = Path.Combine(root, "libraries");
		var coreDirectory = Path.Combine(localLibraries, "Core", "System");
		var stdDirectory = Path.Combine(localLibraries, "Std", "System");
		Directory.CreateDirectory(coreDirectory);
		Directory.CreateDirectory(stdDirectory);

		try
		{
			File.WriteAllText(
				Path.Combine(coreDirectory, "Option.cvl"),
				"namespace System;\n\nunion Option<T>\n{\n\tvoid None;\n\tT Some;\n}\n");

			var console = Path.Combine(stdDirectory, "Console.cvl");
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
}
