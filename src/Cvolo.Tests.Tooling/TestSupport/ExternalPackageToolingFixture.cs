using System.Text.Json;
using Cvolo.Compiler.Tooling;
using Cvolo.Core.AST.Base;
using Cvolo.Packaging;
using Cvolo.Projects;

namespace Cvolo.Tests.Tooling;

/// <summary>
/// Builds a tooling snapshot whose external declarations come from the same PackageApiMetadata
/// rehydration path used for installed .cvlib dependencies, without touching the machine package cache.
/// </summary>
internal static class ExternalPackageToolingFixture
{
	internal static (ProjectSnapshot Snapshot, DocumentSnapshot Document) Create(
		string consumerSource,
		PackageApiMetadata metadata,
		string packageId = "NativeApi",
		string version = "1.0.0",
		IReadOnlyList<PackageSourceDocument>? packageSources = null)
	{
		var session = Guid.NewGuid();
		var projectId = new ProjectId(session, 0);
		var documentId = new DocumentId(session, 0);
		var document = new DocumentSnapshot(
			documentId,
			Path.Combine(Path.GetTempPath(), $"cvolo-tooling-{Guid.NewGuid():N}.cvl"),
			SourceText.From(consumerSource),
			null);

		// Exercise the serialized package boundary before tooling sees any declarations. This keeps
		// the fixture honest: hover/completion/signature help consume reconstructed metadata, not
		// syntax objects shared with the producer side of the test.
		var roundTripped = JsonSerializer.Deserialize<PackageApiMetadata>(metadata.Serialize())
			?? throw new InvalidOperationException("Package API metadata did not deserialize.");
		var externalUnits = roundTripped.CreateCompilationUnits(packageId, version)
			.Select(unit => new ExternalSemanticUnit(unit, ExternalSemanticUnitKind.ExternalPackageApi, packageId, version))
			.ToArray();
		var snapshot = ProjectSnapshot.CreateOwned(
			projectId,
			new Dictionary<DocumentId, DocumentSnapshot> { [documentId] = document },
			externalUnits,
			packageSources);
		return (snapshot, snapshot.GetDocument(documentId));
	}

	internal static PackageApiMetadata NativeSurface() => new()
	{
		Units =
		[
			new PackageApiUnit
			{
				Namespace = "NativeApi",
				Unions =
				[
					new PackageApiUnion
					{
						Name = "Payload",
						IsUnsafe = true,
						Fields = [new PackageApiUnionField("int", "Value", Visibility.Public)]
					}
				],
				Delegates =
				[
					new PackageApiDelegate
					{
						Name = "Callback",
						ReturnType = "int",
						Parameters = [new PackageApiParameter("int", "value")],
						IsNative = true,
						CallingConvention = "C"
					}
				],
				Globals =
				[
					new PackageApiGlobal
					{
						Name = "Counter",
						Type = "int",
						IsMutable = true,
						IsForeign = true,
						CallingConvention = "system",
						ImportName = "native_counter",
						LibraryName = "native",
						WinPath = "native.lib",
						LinuxPath = "./libnative.so",
						MacPath = "./libnative.dylib"
					}
				]
			}
		],
		NativeLibraries =
		[
			new Cvolo.Analysis.Symbols.FFI.NativeLibraryInfo(
				"native", "native.lib", "./libnative.so", "./libnative.dylib")
		]
	};
}
