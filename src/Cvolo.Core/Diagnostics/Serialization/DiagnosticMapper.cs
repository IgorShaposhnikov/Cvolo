namespace Cvolo.Core.Diagnostics.Serialization;

/// <summary>
/// Projects the compiler's <see cref="Diagnostic"/> model into a stable,
/// language-server-friendly shape. Kept in Core so that any consumer
/// (CLI, LSP server, IDE plugin, CI report) shares one canonical mapping.
/// </summary>
public static class DiagnosticMapper
{
	/// <summary>
	/// Maps a diagnostic to its serializable entry. Positions are converted
	/// from the compiler's 1-based (Line, Col) to LSP's 0-based convention.
	/// </summary>
	public static DiagnosticEntry ToEntry(Diagnostic diag, string fallbackFile)
	{
		var ctx = diag.Context;

		int sl = 0, sc = 0, el = 0, ec = 0;
		if (ctx is not null)
		{
			var (a, b) = ctx.GetCoordinates(diag.Span.Start);
			var (c, d) = ctx.GetCoordinates(diag.Span.End);
			sl = Math.Max(0, a - 1);
			sc = Math.Max(0, b - 1);
			el = Math.Max(0, c - 1);
			ec = Math.Max(0, d - 1);
		}

		return new DiagnosticEntry
		{
			Id = diag.Id,
			Severity = MapSeverity(diag.Severity),
			Message = diag.Message,
			File = NormalizePath(ctx?.FilePath ?? fallbackFile),
			Range = new DiagnosticRange
			{
				Start = new DiagnosticPosition { Line = sl, Character = sc },
				End = new DiagnosticPosition { Line = el, Character = ec }
			}
		};
	}

	/// <summary>
	/// Builds an entry for failures that have no source-backed <see cref="Diagnostic"/>
	/// (project load errors, driver-level failures, entry-point missing, etc.).
	/// </summary>
	public static DiagnosticEntry Simple(string? id, string severity, string message, string file)
	{
		return new()
		{
			Id = id,
			Severity = severity,
			Message = message,
			File = NormalizePath(file),
			Range = new DiagnosticRange()
		};
	}

	private static string MapSeverity(DiagnosticSeverity severity) => severity switch
	{
		DiagnosticSeverity.Error => "error",
		DiagnosticSeverity.Warning => "warning",
		_ => "error"
	};

	/// <summary>
	/// Windows filesystem paths use backslashes; LSP clients (notably
	/// efm-langserver, which is implemented in Go) match diagnostics against
	/// open documents using forward slashes. JSON escaping turns every
	/// `\` into `\\`, which the editor-side matcher then fails to reconcile
	/// with its own path representation — the diagnostic is silently dropped.
	/// Normalizing here keeps every consumer of this envelope (CLI, cvolo lsp,
	/// editor plugins) on one canonical convention.
	/// </summary>
	private static string NormalizePath(string path)
		=> string.IsNullOrEmpty(path) ? path : new Uri(path).AbsolutePath;
}
