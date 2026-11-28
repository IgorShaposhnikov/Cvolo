using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Analysis.Ownership;

/// <summary>
/// A compiler-owned query of the runtime liveness bit for one element of a local fixed array.
/// The query is bound during validation and consumed by both the ownership analyzer (for
/// compile-time proof) and the emitter (for the runtime flag load).
/// </summary>
public sealed record IsLiveQuery(string ArrayName, ExpressionSyntax Index, TextSpan Span);
