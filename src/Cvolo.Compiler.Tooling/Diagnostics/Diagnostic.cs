namespace Cvolo.Compiler.Tooling;

/// <summary>
/// A tooling-owned diagnostic: a severity plus an id, human message, primary
/// <see cref="Location"/>, and any related locations.
/// This is a DTO decoupled from compiler-internal diagnostic types.
/// </summary>
public sealed record Diagnostic(DiagnosticSeverity Severity, string Id, string Message, DiagnosticLocation Location, IReadOnlyList<DiagnosticLocation> RelatedLocations);
