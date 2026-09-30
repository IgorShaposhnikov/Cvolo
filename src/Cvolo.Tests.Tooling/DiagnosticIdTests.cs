using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cvolo.Compiler.Tooling;
using Cvolo.Packaging;

namespace Cvolo.Tests.Tooling;

/// <summary>
/// Guards the diagnostic-id contract: every diagnostic the analysis pipeline
/// emits must carry an explicit, stable catalog id. The placeholder
/// <c>CVL0000</c> must never appear in emitted diagnostics or production code.
/// </summary>
public sealed class DiagnosticIdTests
{
	private const string PlaceholderId = "CVL0000";

	private static DocumentSnapshot Open(string source)
	{
		using var fixture = TempProject.Create(("Main.cvl", source));
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		var documentId = project.GetDocumentId("Main.cvl");
		return project.InitialSnapshot.GetDocument(documentId);
	}

	private const string SemanticErrors =
		"namespace Smoke;\n\n" +
		"public struct Point\n{\n    public int x;\n    public int y;\n}\n\n" +
		"public struct Broken\n{\n    public int ok;\n    public TotallyUnknownType bad;\n}\n\n" +
		"int Compute()\n{\n    int uninit;\n    return uninit;\n}\n";

	[Fact]
	public void EmittedDiagnostics_NeverUseThePlaceholderId()
	{
		var document = Open(SemanticErrors);

		var diagnostics = document.GetDiagnostics();
		Assert.NotEmpty(diagnostics);
		Assert.DoesNotContain(diagnostics, d => d.Id == PlaceholderId);
	}

	[Fact]
	public void EmittedDiagnostics_AlwaysCarryANonEmptyId()
	{
		var document = Open(SemanticErrors);

		Assert.All(document.GetDiagnostics(), d => Assert.False(string.IsNullOrWhiteSpace(d.Id)));
	}

	[Fact]
	public void TheSameDiagnosticKind_AtDifferentLocations_SharesOneId()
	{
		const string source =
			"namespace Smoke;\n\n" +
			"public struct A\n{\n    public NopeField first;\n}\n\n" +
			"public struct B\n{\n    public NopeField second;\n}\n";

		var document = Open(source);

		var unknownType = document.GetDiagnostics()
			.Where(d => d.Message.Contains("Unknown type", StringComparison.Ordinal))
			.ToList();

		Assert.Equal(2, unknownType.Count);
		var id = unknownType[0].Id;
		Assert.False(string.IsNullOrWhiteSpace(id));
		Assert.All(unknownType, d => Assert.Equal(id, d.Id));
	}

	[Fact]
	public void SyntaxError_UnexpectedToken_GetsItsOwnStableId()
	{
		// A stray top-level '}' is extraneous input -> "unexpected token".
		var document = Open("int main()\n{\n}\n}\n");

		var diagnostics = document.GetDiagnostics();
		Assert.NotEmpty(diagnostics);
		Assert.DoesNotContain(diagnostics, d => d.Id == PlaceholderId);
		Assert.Contains(diagnostics, d => d.Id == "CVL4173");
	}

	[Fact]
	public void SyntaxError_MissingToken_GetsItsOwnStableId()
	{
		// A missing statement terminator -> "missing ';'".
		var document = Open("int main()\n{\n    int x = 0\n    return 0;\n}\n");

		var diagnostics = document.GetDiagnostics();
		Assert.NotEmpty(diagnostics);
		Assert.DoesNotContain(diagnostics, d => d.Id == PlaceholderId);
		Assert.Contains(diagnostics, d => d.Id == "CVL4174");
	}

	[Fact]
	public void ProductionSources_DoNotContainThePlaceholderId()
	{
		var root = FindRepositoryRoot();
		Assert.False(root is null, "Could not locate the repository root (src/Cvolo.slnx).");

		var offenders = new List<string>();
		foreach (var file in Directory.EnumerateFiles(Path.Combine(root!, "src"), "*.cs", SearchOption.AllDirectories))
		{
			var relative = Path.GetRelativePath(root!, file);
			if (relative.Contains("\\bin\\", StringComparison.Ordinal) ||
				relative.Contains("\\obj\\", StringComparison.Ordinal))
			{
				continue;
			}

			// Test projects may reference the placeholder to prove it is never emitted.
			if (relative.Contains("Cvolo.Tests", StringComparison.Ordinal))
				continue;

			if (File.ReadAllText(file).Contains(PlaceholderId, StringComparison.Ordinal))
				offenders.Add(relative);
		}

		Assert.Empty(offenders);
	}

	private static string? FindRepositoryRoot()
	{
		var directory = new DirectoryInfo(AppContext.BaseDirectory);
		while (directory is not null)
		{
			if (File.Exists(Path.Combine(directory.FullName, "src", "Cvolo.slnx")))
				return directory.FullName;
			directory = directory.Parent;
		}

		return null;
	}
}
