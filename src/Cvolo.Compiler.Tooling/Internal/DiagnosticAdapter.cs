using Cvolo.Core.Diagnostics;

namespace Cvolo.Compiler.Tooling.Internal;

/// <summary>
/// Internal bridge converting compiler diagnostic bags into tooling <see cref="Diagnostic"/>
/// DTOs, resolving each diagnostic's source file to a <see cref="DocumentId"/>.
/// </summary>
internal static class DiagnosticAdapter
{
	/// <summary>
	/// Converts every diagnostic in <paramref name="bag"/>. Diagnostics whose source file has no
	/// matching <see cref="DocumentId"/> in <paramref name="docByFilePath"/> are omitted.
	/// </summary>
	public static IReadOnlyList<Diagnostic> ConvertDiagnostics(DiagnosticBag bag, Dictionary<string, DocumentId> docByFilePath)
	{
		var result = new List<Diagnostic>();

		foreach (var diag in bag.Diagnostics)
		{
			var converted = ConvertSingle(diag, docByFilePath);
			if (converted is not null)
				result.Add(converted);
		}

		return result;
	}

	private static Diagnostic? ConvertSingle(Core.Diagnostics.Diagnostic coreDiag, Dictionary<string, DocumentId> docByFilePath)
	{
		if (!docByFilePath.TryGetValue(coreDiag.Context.FilePath, out var docId))
			return null;

		var severity = MapSeverity(coreDiag.Severity);
		var span = new TextSpan(coreDiag.Span.Start, coreDiag.Span.Length);
		var location = new DiagnosticLocation(docId, span);
		var id = coreDiag.Id ?? "CVL0000";

		return new Diagnostic(
			severity,
			id,
			coreDiag.Message,
			location,
			[]);
	}

	private static DiagnosticSeverity MapSeverity(Core.Diagnostics.DiagnosticSeverity severity)
	{
		return severity switch
		{
			Core.Diagnostics.DiagnosticSeverity.Error => DiagnosticSeverity.Error,
			Core.Diagnostics.DiagnosticSeverity.Warning => DiagnosticSeverity.Warning,
			_ => DiagnosticSeverity.Error
		};
	}
}
