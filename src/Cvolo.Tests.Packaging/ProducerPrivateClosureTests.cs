using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Cvolo.Core.AST.Declarations;
using Cvolo.Core.Packages;
using Cvolo.Packaging;

namespace Cvolo.Tests.Packaging;

/// <summary>
/// A source-backed generic template is monomorphized in the consuming compilation, so its body must
/// be able to bind and emit every declaration it depends on. ABI metadata only carries public types
/// and free functions, so non-public helper declarations that a template body may reference have to
/// travel through Sector 5 as well. These tests pin that closure without needing a native toolchain:
/// they build the archive directly and inspect what the template source rehydrates.
/// </summary>
public sealed class ProducerPrivateClosureTests : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "cvolo-pkg-private-" + Guid.NewGuid().ToString("N"));

	public ProducerPrivateClosureTests()
	{
		Directory.CreateDirectory(_root);
	}

	public void Dispose()
	{
		try
		{
			Directory.Delete(_root, recursive: true);
		}
		catch (IOException)
		{
		}
	}

	[Fact]
	public void TemplateSource_BundlesNonPublicDeclarationsNeededByGenericBodies()
	{
		var archive = BuildArchive("""
namespace Lib;

public T Identity<T>(T value) {
    return value;
}

enum InternalKind {
    Zero,
    One
}

int Helper() {
    return 0;
}

public struct Box {
    public int N;
}

extension Box {
    int Offset() {
        return 2;
    }
}
""");

		var unit = Assert.Single(PackageTemplateSource.Read(archive, "Lib", "1.0.0"));
		var members = unit.NamespaceDeclaration!.Members;

		Assert.Contains(members, member => member is FunctionDeclarationSyntax { Name: "Identity" });
		Assert.Contains(members, member => member is FunctionDeclarationSyntax { Name: "Helper" });
		Assert.Contains(members, member => member is EnumDeclarationSyntax { Name: "InternalKind" });
		Assert.Contains(members, member => member is ExtensionDeclarationSyntax { ExtendedTypeName: "Box" });
	}

	[Fact]
	public void TemplateSource_DoesNotRebundleStructsAlreadyCarriedByApiMetadata()
	{
		var archive = BuildArchive("""
namespace Lib;

public T Identity<T>(T value) {
    return value;
}

public struct Box {
    public int N;
}
""");

		var unit = Assert.Single(PackageTemplateSource.Read(archive, "Lib", "1.0.0"));
		var members = unit.NamespaceDeclaration!.Members;

		// Box is public and non-generic: it belongs to Sector 1 API metadata. Bundling it here as
		// well would give the consumer two definitions of the same type.
		Assert.DoesNotContain(members, member => member is StructDeclarationSyntax);
		Assert.Contains(members, member => member is FunctionDeclarationSyntax { Name: "Identity" });
	}

	private CvlArchive BuildArchive(string librarySource)
	{
		var libraryDirectory = Path.Combine(_root, "lib-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(libraryDirectory);
		var sourcePath = Path.Combine(libraryDirectory, "Lib.cvl");
		File.WriteAllText(sourcePath, librarySource);
		var bundle = PackageSourceBundle.Build([sourcePath], libraryDirectory);

		var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".cvlib");
		var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(new
		{
			Format = "cvlib.slice-manifest.v1",
			PackageId = "Lib",
			Version = "1.0.0",
			Dependencies = Array.Empty<object>(),
			Slices = Array.Empty<object>(),
		});
		var metadata = "{}"u8.ToArray();
		var sector1 = new byte[4 + manifestBytes.Length + metadata.Length];
		BinaryPrimitives.WriteUInt32LittleEndian(sector1, (uint)manifestBytes.Length);
		manifestBytes.CopyTo(sector1, 4);
		metadata.CopyTo(sector1, 4 + manifestBytes.Length);

		var writer = new CvlArchiveWriter();
		writer.SetSector(1, sector1);
		writer.SetSourceBuffer(bundle);
		writer.WriteUnsigned(path);

		return CvlArchiveReader.Read(path, allowUnsigned: true);
	}
}
