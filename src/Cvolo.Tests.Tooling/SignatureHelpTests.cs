using Cvolo.Compiler.Tooling;
using Cvolo.Compiler.Tooling.SignatureHelp;

namespace Cvolo.Tests.Tooling;

public sealed class SignatureHelpTests
{
	private static int At(string source, string needle, int delta = 0) =>
		source.IndexOf(needle, StringComparison.Ordinal) + delta;

	private static string LabelText(SignatureCandidateInfo signature, SignatureParameterInfo parameter) =>
		signature.Label.Substring(parameter.LabelSpan.Start, parameter.LabelSpan.Length);

	[Fact]
	public void OrdinaryFunctionCall_ReportsResolvedSignatureAndActiveParameter()
	{
		const string source = "int Add(int left, int right) { return left + right; }\nint main() { return Add(1, 2); }\n";
		using var fixture = TempProject.Create(("Main.cvl", source));
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		var document = project.InitialSnapshot.GetDocument(project.GetDocumentId("Main.cvl"));

		var position = At(source, "1, 2", "1, ".Length);
		var help = document.GetSignatureHelp(position);

		Assert.NotNull(help);
		Assert.Equal(0, help!.ActiveSignature);
		var signature = Assert.Single(help.Signatures);
		Assert.Equal("int Add(int left, int right)", signature.Label);
		Assert.Equal(1, signature.ActiveParameter);
		Assert.Collection(
			signature.Parameters,
			parameter =>
			{
				Assert.Equal("int left", LabelText(signature, parameter));
				Assert.InRange(parameter.LabelSpan.Start, 0, signature.Label.Length);
				Assert.InRange(parameter.LabelSpan.Length, 1, signature.Label.Length);
			},
			parameter => Assert.Equal("int right", LabelText(signature, parameter)));
	}

	[Fact]
	public void WrongArityCall_StillShowsAvailableSignature()
	{
		const string source = "void Vertex2f(float x, float y) {}\n"
			+ "int Main() { Vertex2f(); return 0; }\n";
		using var fixture = TempProject.Create(("Main.cvl", source));
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		var document = project.InitialSnapshot.GetDocument(project.GetDocumentId("Main.cvl"));
		Assert.Contains(document.GetDiagnostics(), d => d.Id == "CVL1078");
		var position = source.LastIndexOf("Vertex2f(", StringComparison.Ordinal) + "Vertex2f(".Length;
		var help = document.GetSignatureHelp(position);
		Assert.NotNull(help);
		Assert.Contains(help!.Signatures, signature => signature.Label.Contains("Vertex2f(float x, float y)", StringComparison.Ordinal));
	}

	[Theory]
	[InlineData("Add(|)")]
	[InlineData("Add(|")]
	public void SignatureHelp_AfterOpeningParenthesis_RemainsAvailableDuringTyping(string markedCall)
	{
		var marker = markedCall.IndexOf('|');
		var invocation = markedCall.Remove(marker, 1);
		var source = "int Add(int left, int right) { return left + right; }\n"
			+ "int Main() { " + invocation + "; return 0; }\n";
		var callOffset = source.LastIndexOf(invocation, StringComparison.Ordinal);
		var position = callOffset + marker;
		using var fixture = TempProject.Create(("Main.cvl", source));
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		var document = project.InitialSnapshot.GetDocument(project.GetDocumentId("Main.cvl"));

		var help = document.GetSignatureHelp(position);
		Assert.NotNull(help);
		Assert.Contains(help!.Signatures, signature =>
			signature.Label == "int Add(int left, int right)"
			&& signature.ActiveParameter == 0);
	}

	[Theory]
	[InlineData("Begin(|)\n    Begin(2);")]
	[InlineData("Begin(|)\n    while (false) {}")]
	public void AutoClosedCallMissingSemicolon_StillShowsParameters(string markedStatements)
	{
		var marker = markedStatements.IndexOf('|');
		var statements = markedStatements.Remove(marker, 1);
		var source = "void Begin(int mode) {}\nint Main() {\n    " + statements + "\n    return 0;\n}\n";
		var callStart = source.IndexOf("    Begin(", StringComparison.Ordinal);
		var position = callStart + 4 + marker;
		using var fixture = TempProject.Create(("Main.cvl", source));
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		var document = project.InitialSnapshot.GetDocument(project.GetDocumentId("Main.cvl"));

		var help = document.GetSignatureHelp(position);
		Assert.NotNull(help);
		Assert.Contains(help!.Signatures, signature =>
			signature.Label == "void Begin(int mode)"
			&& signature.ActiveParameter == 0);
	}

	[Theory]
	[InlineData("Clear();")]
	[InlineData("Clear()\\n    Begin(1);")]
	public void ImportedFunctionWithoutMatchingArguments_StillOffersSignatureHelp(string markedStatements)
	{
		var statements = markedStatements.Replace("\\n", "\n", StringComparison.Ordinal);
		var mainSource = "using OpenGL;\\n"
			+ "int Main() {\\n"
			+ "    " + statements + "\\n"
			+ "    return 0;\\n"
			+ "}\\n";
		mainSource = mainSource.Replace("\\n", "\n", StringComparison.Ordinal);
		const string librarySource =
			"namespace OpenGL;\n"
			+ "public void Clear(int mask) {}\n";
		const string beginSource = "void Begin(int mode) {}\n";
		using var fixture = TempProject.Create(
			("Main.cvl", mainSource),
			("OpenGL.cvl", librarySource),
			("Begin.cvl", beginSource));
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		var document = project.InitialSnapshot.GetDocument(project.GetDocumentId("Main.cvl"));
		var callOffset = mainSource.IndexOf("    Clear(", StringComparison.Ordinal) + "    Clear(".Length;
		var help = document.GetSignatureHelp(callOffset);

		Assert.NotNull(help);
		Assert.Contains(help!.Signatures, signature =>
			signature.Label == "void Clear(int mask)" && signature.ActiveParameter == 0);
	}

	[Fact]
	public void NonCallableKeywordOpener_StillHasNoSignatureHelp()
	{
		const string source = "int Main() { if () {} return 0; }\n";
		using var fixture = TempProject.Create(("Main.cvl", source));
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		var document = project.InitialSnapshot.GetDocument(project.GetDocumentId("Main.cvl"));
		var position = source.IndexOf("if (", StringComparison.Ordinal) + 4;
		Assert.Null(document.GetSignatureHelp(position));
	}

	[Fact]
	public void OverloadedFunctionCall_ListsAllOverloadsInDeclarationOrder()
	{
		const string source =
			"int Add(int left, int right) { return left + right; }\n" +
			"int Add(int a, int b, int c) { return a + b + c; }\n" +
			"int main() { return Add(1, 2); }\n";
		using var fixture = TempProject.Create(("Main.cvl", source));
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		var document = project.InitialSnapshot.GetDocument(project.GetDocumentId("Main.cvl"));

		var help = document.GetSignatureHelp(At(source, "1, 2"));

		Assert.NotNull(help);
		Assert.Equal(2, help!.Signatures.Count);
		Assert.Equal("int Add(int left, int right)", help.Signatures[0].Label);
		Assert.Equal("int Add(int a, int b, int c)", help.Signatures[1].Label);
		Assert.Equal(0, help.ActiveSignature);
		Assert.Equal(0, help.Signatures[0].ActiveParameter);
		Assert.Null(help.Signatures[1].ActiveParameter);
	}

	[Fact]
	public void OverloadWithZeroParameters_ActiveParameterIsNull()
	{
		const string source = "int Ping() { return 0; }\nint main() { return Ping(); }\n";
		using var fixture = TempProject.Create(("Main.cvl", source));
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		var document = project.InitialSnapshot.GetDocument(project.GetDocumentId("Main.cvl"));

		var help = document.GetSignatureHelp(At(source, "Ping();", 5));

		Assert.NotNull(help);
		var signature = Assert.Single(help!.Signatures);
		Assert.Equal("int Ping()", signature.Label);
		Assert.Empty(signature.Parameters);
		Assert.Null(signature.ActiveParameter);
	}

	[Fact]
	public void NestedCall_ReportsInnermostCallable()
	{
		const string source =
			"int Max(int left, int right) { return 0; }\n" +
			"int Min(int left, int right) { return 0; }\n" +
			"int main() { return Max(1, Min(|)); }\n";
		var position = source.IndexOf("|", StringComparison.Ordinal);
		var sourceText = source.Remove(position, 1);

		using var fixture = TempProject.Create(("Main.cvl", sourceText));
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		var document = project.InitialSnapshot.GetDocument(project.GetDocumentId("Main.cvl"));

		var help = document.GetSignatureHelp(position);

		Assert.NotNull(help);
		var signature = Assert.Single(help!.Signatures);
		Assert.Equal("int Min(int left, int right)", signature.Label);
		Assert.Equal(0, help.ActiveSignature);
		Assert.Equal(0, signature.ActiveParameter);
	}

	[Fact]
	public void NativeDelegateCall_UsesNominalDelegateSignatureFromPackageMetadata()
	{
		const string source = "using NativeApi;\nint Invoke(Callback callback) { unsafe { return callback(41); } }\n";
		var (_, document) = ExternalPackageToolingFixture.Create(source, ExternalPackageToolingFixture.NativeSurface());
		var position = At(source, "41", 1);

		var help = document.GetSignatureHelp(position);

		Assert.NotNull(help);
		var signature = Assert.Single(help!.Signatures);
		Assert.Equal("unsafe \"C\" delegate int Callback(int value)", signature.Label);
		Assert.Equal(0, signature.ActiveParameter);
		Assert.Equal("int value", LabelText(signature, Assert.Single(signature.Parameters)));
	}

	[Fact]
	public void InvalidPositionAndNonCallFollowPublicContract()
	{
		const string source = "int main() { return 0; }\n";
		using var fixture = TempProject.Create(("Main.cvl", source));
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		var document = project.InitialSnapshot.GetDocument(project.GetDocumentId("Main.cvl"));

		Assert.Null(document.GetSignatureHelp(source.IndexOf("return", StringComparison.Ordinal)));
		Assert.Throws<ArgumentOutOfRangeException>(() => document.GetSignatureHelp(-1));
		Assert.Throws<ArgumentOutOfRangeException>(() => document.GetSignatureHelp(source.Length + 1));
	}
}
