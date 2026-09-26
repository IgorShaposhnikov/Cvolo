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
		var sources = ResolveSources("Inc9/NoImport.cvl");

		Assert.True(HasLibrary(sources, BaseOption), "Base Option.cvl must always be selected.");
		Assert.False(HasLibrary(sources, SystemConsole), "Console.cvl must not be selected without an import.");
		Assert.False(HasLibrary(sources, SystemMathInt), "Math sources must not be selected without an import.");

		AssertCheckSucceeds("Inc9/NoImport.cvl");
	}

	[Fact]
	public void OptionalSyntax_ResolvesFromBaseWithoutUsing()
	{
		var sources = ResolveSources("Inc9/LocalOptional.cvl");
		Assert.True(HasLibrary(sources, BaseOption));

		AssertCheckSucceeds("Inc9/LocalOptional.cvl");
	}

	[Fact]
	public void Typeof_ResolvesTypeFromBaseWithoutUsing()
	{
		var sources = ResolveSources("Inc9/TypeofCore.cvl");
		Assert.True(HasLibrary(sources, BaseType));

		AssertCheckSucceeds("Inc9/TypeofCore.cvl");
	}

	[Fact]
	public void IntrinsicAttribute_IsAcceptedWithoutUsing()
	{
		AssertCheckSucceeds("Inc9/AttributeCore.cvl");
	}

	[Fact]
	public void UsingSystem_SelectsConsoleAndExcludesUnrelatedSystem()
	{
		var sources = ResolveSources("Inc9/UsingSystemConsole.cvl");

		Assert.True(HasLibrary(sources, BaseOption));
		Assert.True(HasLibrary(sources, SystemSystem));
		Assert.True(HasLibrary(sources, SystemConsole));
		Assert.False(HasLibrary(sources, SystemMathInt));
		Assert.False(HasLibrary(sources, SystemMathDouble));
	}

	[Fact]
	public void NestedSystemImport_SelectsRequiredModuleOnly()
	{
		var sources = ResolveSources("Inc9/UsingSystemMathInt.cvl");

		Assert.True(HasLibrary(sources, SystemMathInt));
		Assert.False(HasLibrary(sources, SystemMathDouble));
	}

	[Fact]
	public void FullyQualifiedSystemReference_SelectsRequiredModule()
	{
		var sources = ResolveSources("Inc9/FullyQualifiedMath.cvl");

		Assert.True(HasLibrary(sources, SystemMathInt));
	}

	[Fact]
	public void Freestanding_KeepsBaseAndDisablesSystem()
	{
		var consoleSources = ResolveSources("Inc9/UsingSystemConsole.cvl", includeSystem: false);
		Assert.True(HasLibrary(consoleSources, BaseOption));
		Assert.False(HasLibrary(consoleSources, SystemConsole));

		var optionalSources = ResolveSources("Inc9/LocalOptional.cvl", includeSystem: false);
		Assert.True(HasLibrary(optionalSources, BaseOption));
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
		var root = Path.Combine(Path.GetTempPath(), "cvolo-inc9-" + Guid.NewGuid().ToString("N"));
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
	public void Freestanding_SystemImport_IsRejected()
	{
		var (exitCode, _, stderr) = RunCompilerCheck("Inc9/FreestandingSystemUse.cvl", "--freestanding");
		Assert.Equal(1, exitCode);
		Assert.Contains("CVL1097", stderr);
	}

	[Fact]
	public void Freestanding_WithoutSystemImport_Succeeds()
	{
		var (exitCode, stdout, stderr) = RunCompilerCheck("Inc9/NoImport.cvl", "--freestanding");
		AssertCompilationSucceeded(exitCode, stdout, stderr, "Inc9/NoImport.cvl");
	}

	[Fact]
	public void CustomResultUnion_WorksWithTryCatch()
	{
		AssertCheckSucceeds("Inc9/ResultShapeCustom.cvl");
	}

	[Fact]
	public void ResultUnion_MissingErr_IsRejected()
	{
		var (exitCode, _, stderr) = RunCompilerCheck("Inc9/ResultShapeMissingErr.cvl");
		Assert.Equal(1, exitCode);
		Assert.Contains("CVL1092", stderr);
	}
}
