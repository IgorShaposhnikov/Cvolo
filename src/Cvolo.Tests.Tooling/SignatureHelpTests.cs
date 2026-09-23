using Cvolo.Compiler.Tooling;

namespace Cvolo.Tests.Tooling;

public sealed class SignatureHelpTests
{
	private static int At(string source, string needle, int delta = 0) =>
		source.IndexOf(needle, StringComparison.Ordinal) + delta;

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
		Assert.Equal(1, help.ActiveParameter);
		var signature = Assert.Single(help.Signatures);
		Assert.Equal("int Add(int left, int right)", signature.Label);
		Assert.Collection(
			signature.Parameters,
			parameter => Assert.Equal("int left", parameter.Label),
			parameter => Assert.Equal("int right", parameter.Label));
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
		Assert.Equal(0, help.ActiveParameter);
		Assert.Equal("int value", Assert.Single(signature.Parameters).Label);
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
