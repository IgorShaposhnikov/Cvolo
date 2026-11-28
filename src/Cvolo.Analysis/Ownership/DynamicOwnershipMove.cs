using Cvolo.Core.AST.Base;
using Cvolo.Core.Diagnostics;

namespace Cvolo.Analysis.Ownership;

/// <summary>
/// Records a successful ownership-consuming read from one runtime-selected element of a tracked
/// local fixed array. The emitter clears the element's live flag after loading the payload so the
/// same element cannot be destroyed again by scope cleanup.
/// </summary>
public sealed record DynamicOwnershipMove(
	string ArrayName,
	ExpressionSyntax Index,
	int ArraySize,
	TextSpan Span);
