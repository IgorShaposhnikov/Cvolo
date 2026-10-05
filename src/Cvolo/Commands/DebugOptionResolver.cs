using System.CommandLine;
using Cvolo.Core.Diagnostics;
using Cvolo.Packaging;

namespace Cvolo.Commands;

/// <summary>
/// Resolves the option combination that <c>--debug</c> / <c>-g</c> implies and rejects combinations
/// that cannot produce usable debug symbols.
/// </summary>
/// <remarks>
/// Debug information is only actionable when the generated code still resembles the source. A
/// <c>--debug</c> build therefore defaults to <c>-O0</c> when no profile was requested explicitly, and
/// refuses a non-<c>O0</c> profile or a <c>Release</c> configuration when one was: silently emitting a
/// program database whose line table no longer maps to the source it claims would be worse than a
/// clear usage error.
/// </remarks>
internal static class DebugOptionResolver
{
	/// <summary>Optimization profile a debug build uses when the caller did not ask for one.</summary>
	public const string DebugOptimizationLevel = "O0";

	/// <summary>
	/// Validates the <c>--debug</c> option combination.
	/// </summary>
	/// <param name="debug">Whether <c>--debug</c> / <c>-g</c> was supplied.</param>
	/// <param name="parseResult">Parse result used to detect whether <c>--optimize</c> was explicit.</param>
	/// <param name="optOption">The command's <c>--optimize</c> option.</param>
	/// <param name="optLevel">The optimization level currently resolved from the option.</param>
	/// <param name="configuration">The normalized build configuration.</param>
	/// <param name="resolvedOptLevel">The optimization level to compile with.</param>
	/// <param name="error">A user-facing message when the combination is rejected, otherwise null.</param>
	/// <returns>True when the combination is usable.</returns>
	public static bool TryResolve(
		bool debug,
		ParseResult parseResult,
		Option<string> optOption,
		string optLevel,
		string configuration,
		out string resolvedOptLevel,
		out string? error)
	{
		resolvedOptLevel = optLevel;
		error = null;

		if (!debug)
			return true;

		// An explicit profile is a deliberate choice, so validate it rather than overriding it.
		if (parseResult.GetResult(optOption) is { Implicit: false })
		{
			var requested = NormalizeLevel(optLevel);
			if (!requested.Equals(DebugOptimizationLevel, StringComparison.Ordinal))
			{
				error = $"--debug cannot be combined with -{requested}. Debug builds require -{DebugOptimizationLevel} so line numbers still match the source.";
				return false;
			}
		}

		// Downstream consumers parse the level as an enum name, so always hand them the
		// canonical spelling instead of the parser's alias-stripped form.
		resolvedOptLevel = DebugOptimizationLevel;

		if (string.Equals(configuration, BuildOutputLayout.ReleaseConfiguration, StringComparison.OrdinalIgnoreCase))
		{
			error = $"--debug cannot be combined with --configuration {BuildOutputLayout.ReleaseConfiguration}. Debug builds require --configuration {BuildOutputLayout.DebugConfiguration}.";
			return false;
		}

		return true;
	}

	/// <summary>Formats the rejection as a diagnostic the CLI reports consistently with other errors.</summary>
	public static void Report(string error) =>
		Console.Error.WriteLine($"error {DiagnosticIds.DebugOptionConflict}: {error}");

	/// <summary>
	/// Restores the canonical <c>O</c>-prefixed spelling of an optimization level.
	/// </summary>
	/// <remarks>
	/// <c>-O0</c> and <c>-Os</c> reach this class with the <c>-O</c> alias already stripped by the
	/// parser (<c>"0"</c> and <c>"s"</c>), while <c>--optimize O2</c> keeps its prefix. Comparing the
	/// raw strings would treat a requested <c>-O0</c> as a conflict with itself, so both spellings are
	/// folded to the same <c>O…</c> form before they are compared or reported.
	/// </remarks>
	private static string NormalizeLevel(string optLevel)
	{
		var trimmed = optLevel.Trim().TrimStart('-');
		if (trimmed.Length == 0)
			return DebugOptimizationLevel;

		if (trimmed[0] is 'O' or 'o')
			trimmed = trimmed[1..];

		return trimmed.Length == 0 ? DebugOptimizationLevel : "O" + trimmed;
	}
}