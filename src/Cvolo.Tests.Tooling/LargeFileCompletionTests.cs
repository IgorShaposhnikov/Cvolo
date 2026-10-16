using System.Diagnostics;
using System.Text;
using Cvolo.Compiler.Tooling;
using Cvolo.Compiler.Tooling.Completion;

namespace Cvolo.Tests.Tooling;

/// <summary>
/// Field completion inside an aggregate literal must stay usable on real generated
/// translation units larger than the automatic-completion threshold (100,000 UTF-16
/// code units). This exercises the explicit-request path used by the editor when the
/// automatic resource guard skips a document.
/// </summary>
public sealed class LargeFileCompletionTests
{
	private static (string Source, int Position) LargeFile()
	{
		var builder = new StringBuilder();
		builder.AppendLine("struct CharacterBody { float X; float Y; float Z; }");
		builder.AppendLine("struct Window { CharacterBody Body; float Yaw; float Pitch; int Width; int Height; }");
		for (var i = 0; builder.Length < 145_000; i++)
		{
			builder.AppendLine($"int Generated{i}(int a, int b, int c) {{ return a + b + c + {i}; }}");
		}

		const string tail = "int main()\n{\n    Window w = Window\n    {\n        \n    };\n    return w.Width;\n}\n";
		var source = builder.ToString() + tail;
		var position = source.IndexOf("        \n", StringComparison.Ordinal) + 8;
		return (source, position);
	}

	[Fact]
	public void ExplicitFieldCompletion_OnAFileLargerThanTheAutomaticThreshold_ReturnsTheAggregateFields()
	{
		var (source, position) = LargeFile();
		Assert.True(source.Length > 100_000, "Fixture must exceed the automatic completion threshold.");

		using var fixture = TempProject.Create(("Main.cvl", source));
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		var document = project.InitialSnapshot.GetDocument(project.GetDocumentId("Main.cvl"));

		var stopwatch = Stopwatch.StartNew();
		var result = document.GetCompletions(position);
		stopwatch.Stop();

		foreach (var field in new[] { "Body", "Yaw", "Pitch", "Width", "Height" })
		{
			Assert.Contains(result.Candidates, c => c.Label == field && c.Kind == CompletionKind.StructField);
		}

		Assert.DoesNotContain(result.Candidates, c => c.Kind == CompletionKind.Function);
		Assert.True(stopwatch.ElapsedMilliseconds < 5_000, $"Completion took {stopwatch.ElapsedMilliseconds}ms.");
	}
}
