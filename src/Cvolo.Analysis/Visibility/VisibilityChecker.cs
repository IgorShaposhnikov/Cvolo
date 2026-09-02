using Cvolo.Core.AST;
using Cvolo.Core.AST.Base;

namespace Cvolo.Analysis.VisibilityChecks;

/// <summary>
/// Central access-control rule for the Visibility &amp; Access Control Specification.
///
/// Today every file in a compilation <c>build</c>/<c>check</c> invocation forms one
/// module (no package separation exists yet), so <c>internal</c> is always reachable
/// and <c>private</c> is the only real gate: it permits access solely within the
/// defining source file. The rule is shared by ValidationPass (member access,
/// struct literals, switch patterns) and SafetyPass (unbound refvar sandbox).
/// </summary>
public static class VisibilityChecker
{
	public static bool IsAccessible(Visibility target, CompilationUnitSyntax? accessingUnit, CompilationUnitSyntax? declaringUnit, bool legacy = false)
	{
		if (legacy)
			return true;

		// Primitives, shared stdlib symbols and unknown origins are always reachable.
		if (declaringUnit is null || accessingUnit is null)
			return true;

		return target switch
		{
			Visibility.Public => true,
			// Module scope: the whole compilation is currently one module.
			Visibility.Internal => true,
			Visibility.Private => ReferenceEquals(accessingUnit, declaringUnit),
			_ => true,
		};
	}

	/// <summary>True when <paramref name="candidate"/> is at most as wide as <paramref name="enclosing"/>.</summary>
	public static bool CanNarrowTo(Visibility candidate, Visibility enclosing) =>
		candidate switch
		{
			Visibility.Private => true,
			Visibility.Internal => enclosing is not Visibility.Private,
			Visibility.Public => enclosing == Visibility.Public,
			_ => false,
		};
}