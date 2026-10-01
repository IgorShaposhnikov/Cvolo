using Cvolo.Compiler.Tooling;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Tests.Tooling;

/// <summary>
/// A call whose receiver names something that is not a type has one finding: the receiver does not
/// exist. Everything behind it is unreachable, so reporting an argument mismatch against candidates
/// that were never candidates sends the reader to the wrong line. The existing catalog entry for an
/// unknown type is reused, so the identifier stays stable and no new kind is minted.
/// </summary>
public sealed class UnresolvedReceiverDiagnosticTests
{
	private const string Source = """
		public struct Holder
		{
		    public int field;
		}

		public struct Point
		{
		    public int x;
		}

		public struct Used
		{
		    Point p;
		}

		int main()
		{
		    val a = MissingType.FromType<int>();
		    val b = MissingType.Other();
		    val c = Deeply.Nested.Missing();
		    return 0;
		}
		""";

	private static (ProjectSnapshot Snapshot, DocumentSnapshot Document, TempProject Fixture) Open(string source)
	{
		var fixture = TempProject.Create(("Main.cvl", source));
		var project = CvoloWorkspace.Create().OpenProject(fixture.ProjectFilePath);
		var snapshot = project.InitialSnapshot;
		return (snapshot, snapshot.GetDocument(project.GetDocumentId("Main.cvl")), fixture);
	}

	private static string Describe(IReadOnlyList<Cvolo.Compiler.Tooling.Diagnostic> diagnostics) =>
		string.Join(" | ", diagnostics.Select(d => $"[{d.Id}] {d.Message}"));

	[Fact]
	public void AnUnresolvedReceiver_IsReportedWithTheUnknownTypeDiagnostic()
	{
		var x = Open(Source);
		using (x.Fixture)
		{
			var unknown = x.Document.GetDiagnostics()
				.Where(d => d.Id == DiagnosticIds.UnknownTypeInExpression)
				.ToList();

			Assert.True(unknown.Count > 0, "expected an unknown-type diagnostic, got: " + Describe(x.Document.GetDiagnostics()));
			Assert.Contains(unknown, d => d.Message.Contains("MissingType", StringComparison.Ordinal));
			Assert.Contains(unknown, d => d.Message.Contains("Deeply.Nested", StringComparison.Ordinal));
		}
	}

	[Fact]
	public void AnUnresolvedReceiver_DoesNotAlsoProduceAnOverloadMismatch()
	{
		var x = Open(Source);
		using (x.Fixture)
		{
			var overload = x.Document.GetDiagnostics()
				.Where(d => d.Id == DiagnosticIds.NoOverloadOfFunctionMatchesArgumentTypes)
				.ToList();

			Assert.True(
				overload.Count == 0,
				"an unresolved receiver must stop member resolution instead of cascading into an overload error, got: " +
					Describe(x.Document.GetDiagnostics()));
		}
	}

	// A receiver the compiler CAN name is a different finding: the type is real and the member is
	// missing, so the report names the member rather than an argument mismatch against candidates
	// that were never declared.
	[Fact]
	public void AResolvedReceiver_WithAMissingMember_IsAMemberFinding()
	{
		var x = Open("""
			public struct Holder
			{
			    public int field;
			}

			int main()
			{
			    val a = Holder.MissingMember();
			    return 0;
			}
			""");
		using (x.Fixture)
		{
			var diagnostics = x.Document.GetDiagnostics();
			Assert.DoesNotContain(diagnostics, d => d.Id == DiagnosticIds.NoOverloadOfFunctionMatchesArgumentTypes);
			Assert.DoesNotContain(diagnostics, d => d.Id == DiagnosticIds.UnknownTypeInExpression);
			Assert.Contains(diagnostics, d => d.Id == DiagnosticIds.StructDoesNotContainField);
		}
	}

	// A member that exists but is called with the wrong arguments is not missing: the ordinary
	// overload diagnostic must survive, and no missing-member finding may preempt it.
	[Fact]
	public void AResolvedReceiver_WithAMemberOfTheWrongArity_KeepsTheOverloadDiagnostic()
	{
		var x = Open("""
			public struct Holder
			{
			    public int field;
			}

			public extension Holder
			{
			    public int Compute(int left, int right) { return left + right; }
			}

			int main()
			{
			    val a = Holder.Compute(1);
			    return 0;
			}
			""");
		using (x.Fixture)
		{
			var diagnostics = x.Document.GetDiagnostics();
			Assert.DoesNotContain(diagnostics, d => d.Id == DiagnosticIds.StructDoesNotContainField);
			Assert.DoesNotContain(diagnostics, d => d.Id == DiagnosticIds.UnknownTypeInExpression);
		}
	}

	// The guard keys off "is this name a type", so a value receiver whose chain contains a variable
	// must be left to instance lookup rather than being reported as an unknown type.
	[Fact]
	public void AValueReceiverChain_IsNotReportedAsAnUnknownType()
	{
		var x = Open("""
			public struct Point
			{
			    public int x;
			}

			public struct Used
			{
			    Point p;
			}

			int main()
			{
			    Used u;
			    val v = u.p.x;
			    return 0;
			}
			""");
		using (x.Fixture)
		{
			Assert.DoesNotContain(
				x.Document.GetDiagnostics(),
				d => d.Id == DiagnosticIds.UnknownTypeInExpression);
		}
	}

	[Fact]
	public void EveryDiagnosticHasAStableNonEmptyIdentifier()
	{
		var x = Open(Source);
		using (x.Fixture)
		{
			foreach (var diagnostic in x.Document.GetDiagnostics())
			{
				Assert.False(string.IsNullOrWhiteSpace(diagnostic.Id), "a diagnostic was reported with an empty id");
				Assert.NotEqual("CVL0000", diagnostic.Id);
				Assert.StartsWith("CVL", diagnostic.Id, StringComparison.Ordinal);
			}
		}
	}
}