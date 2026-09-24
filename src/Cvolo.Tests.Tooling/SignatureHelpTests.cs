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
