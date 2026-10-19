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

		// A recovered parser node can report a synthetic/out-of-source span.
		// Do not invent replacement coordinates or poison this snapshot's
		// hover, signature help, completion and inlay-hint queries.
		var start = coreDiag.Span.Start;
		var length = coreDiag.Span.Length;
		var sourceLength = coreDiag.Context.Source.Length;
		if (start < 0 || length < 0 || start > sourceLength || length > sourceLength - start)
			return null;

		var severity = MapSeverity(coreDiag.Severity);
		var span = new TextSpan(start, length);
		var location = new DiagnosticLocation(docId, span);
		var id = coreDiag.Id;

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
