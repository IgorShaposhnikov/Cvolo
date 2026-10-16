using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Cvolo.Compiler.Tooling;
using Cvolo.Core.AST.Base;
using Cvolo.Core.Packages;
using Cvolo.Packaging;

namespace Cvolo.Tests.Tooling;

/// <summary>
/// an imported aggregate's defaultability is judged from the producer's
/// certificate rather than by guessing at the rehydrated declaration. These cases drive the
/// live workspace/analysis path (the same universe the compiler binds) so Tooling and the
/// command-line compiler agree on the verdict.
/// </summary>
public sealed class ImportedDefaultabilityToolingTests
{
	[Fact]
	public void ImportedEligibleStructDefault_ProducesNoDefaultDiagnostic()
	{
		var root = CreateRoot();
		var libraries = Path.Combine(root, "libs");
		Directory.CreateDirectory(libraries);
		try
		{
			WriteLibrary(
				Path.Combine(libraries, "Lib.cvlib"),
				new PackageApiStruct
				{
					Name = "Point",
					Fields =
					[
						new PackageApiStructField("int", "X", Visibility.Public),
						new PackageApiStructField("int", "Y", Visibility.Public),
					],
					Defaultability = new PackageApiDefaultability { State = PackageApiDefaultability.Eligible },
				});

			const string source = "using Lib;\nint main() { Point p = default(Point); return p.X; }\n";
			File.WriteAllText(Path.Combine(root, "Main.cvl"), source);

			var diagnostics = Analyze(root, "Main.cvl");

			Assert.DoesNotContain(diagnostics, d => d.Id is "CVL4196" or "CVL4197");
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[Fact]
	public void ImportedUncertifiedStructDefault_ReportsUnverified()
	{
		var root = CreateRoot();
		var libraries = Path.Combine(root, "libs");
		Directory.CreateDirectory(libraries);
		try
		{
			WriteLibrary(
				Path.Combine(libraries, "Lib.cvlib"),
				new PackageApiStruct
				{
					Name = "Point",
					Fields =
					[
						new PackageApiStructField("int", "X", Visibility.Public),
						new PackageApiStructField("int", "Y", Visibility.Public),
					],
					// No certificate: a consumer must fail closed rather than assume eligibility.
					Defaultability = null,
				});

			const string source = "using Lib;\nint main() { Point p = default(Point); return p.X; }\n";
			File.WriteAllText(Path.Combine(root, "Main.cvl"), source);

			var diagnostics = Analyze(root, "Main.cvl");

			Assert.Contains(diagnostics, d => d.Id == "CVL4197");
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[Fact]
	public void ImportedIneligibleStructDefault_ReportsProducerCause()
	{
		var root = CreateRoot();
		var libraries = Path.Combine(root, "libs");
		Directory.CreateDirectory(libraries);
		try
		{
			WriteLibrary(
				Path.Combine(libraries, "Lib.cvlib"),
				new PackageApiStruct
				{
					Name = "Handle",
					Fields = [new PackageApiStructField("int", "Value", Visibility.Public)],
					Defaultability = new PackageApiDefaultability
					{
						State = PackageApiDefaultability.Ineligible,
						Reason = "type declares a destructor",
					},
				});

			const string source = "using Lib;\nint main() { Handle h = default(Handle); return h.Value; }\n";
			File.WriteAllText(Path.Combine(root, "Main.cvl"), source);

			var diagnostics = Analyze(root, "Main.cvl");

			var diagnostic = Assert.Single(diagnostics, d => d.Id == "CVL4196");
			Assert.Contains("destructor", diagnostic.Message, StringComparison.Ordinal);
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[Fact]
	public void ImportedStructWithStaleCertificateFingerprint_ReportsUnverified()
	{
		var root = CreateRoot();
		var libraries = Path.Combine(root, "libs");
		Directory.CreateDirectory(libraries);
		try
		{
			WriteLibrary(
				Path.Combine(libraries, "Lib.cvlib"),
				new PackageApiStruct
				{
					Name = "Point",
					Fields =
					[
						new PackageApiStructField("int", "X", Visibility.Public),
						new PackageApiStructField("int", "Y", Visibility.Public),
					],
					// Eligible, but the fingerprint does not match the declaration the consumer
					// actually rehydrated: the proof belongs to a different layout, so it cannot
					// be trusted and must fail closed.
					Defaultability = new PackageApiDefaultability
					{
						State = PackageApiDefaultability.Eligible,
						Fingerprint = "0000000000000000",
					},
				});

			const string source = "using Lib;\nint main() { Point p = default(Point); return p.X; }\n";
			File.WriteAllText(Path.Combine(root, "Main.cvl"), source);

			var diagnostics = Analyze(root, "Main.cvl");

			Assert.Contains(diagnostics, d => d.Id == "CVL4197");
			Assert.DoesNotContain(diagnostics, d => d.Id == "CVL4196");
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[Fact]
	public void ImportedIneligibleStructDefault_PointsAtTheDefaultExpression()
	{
		var root = CreateRoot();
		var libraries = Path.Combine(root, "libs");
		Directory.CreateDirectory(libraries);
		try
		{
			WriteLibrary(
				Path.Combine(libraries, "Lib.cvlib"),
				new PackageApiStruct
				{
					Name = "Handle",
					Fields = [new PackageApiStructField("int", "Value", Visibility.Public)],
					Defaultability = new PackageApiDefaultability
					{
						State = PackageApiDefaultability.Ineligible,
						Reason = "type declares a destructor",
					},
				});

			const string source = "using Lib;\nint main() { Handle h = default(Handle); return h.Value; }\n";
			File.WriteAllText(Path.Combine(root, "Main.cvl"), source);

			var diagnostics = Analyze(root, "Main.cvl");

			var diagnostic = Assert.Single(diagnostics, d => d.Id == "CVL4196");
			var span = diagnostic.Location.Span;
			Assert.Equal("default(Handle)", source.Substring(span.Start, span.Length));
		}
		finally
		{
			Directory.Delete(root, recursive: true);
		}
	}

	private static IReadOnlyList<Diagnostic> Analyze(string root, string documentName)
	{
		var project = CvoloWorkspace.Create().OpenProject(root, ["libs"]);
		var document = project.InitialSnapshot.GetDocument(project.GetDocumentId(documentName));
		return document.GetDiagnostics();
	}

	private static void WriteLibrary(string path, PackageApiStruct structSurface)
	{
		var manifestJson = JsonSerializer.Serialize(new
		{
			Format = "cvlib.slice-manifest.v1",
			PackageId = "Lib",
			Version = "1.0.0",
			Dependencies = Array.Empty<object>(),
			Slices = Array.Empty<object>(),
		});
		var manifestBytes = Encoding.UTF8.GetBytes(manifestJson);
		var api = new PackageApiMetadata
		{
			Units =
			[
				new PackageApiUnit
				{
					Namespace = "Lib",
					Structs = [structSurface],
				},
			],
		}.Serialize();

		var sector1 = new byte[4 + manifestBytes.Length + api.Length];
		BinaryPrimitives.WriteUInt32LittleEndian(sector1, (uint)manifestBytes.Length);
		manifestBytes.CopyTo(sector1, 4);
		api.CopyTo(sector1, 4 + manifestBytes.Length);

		var writer = new CvlArchiveWriter();
		writer.SetSector(1, sector1);
		writer.WriteUnsigned(path);
	}

	private static string CreateRoot()
	{
		var root = Path.Combine(Path.GetTempPath(), "cvolo-imported-default-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		return root;
	}
}
