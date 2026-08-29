using Cvolo.Analysis.Symbols.Base;
using Cvolo.Core.AST.Declarations;

namespace Cvolo.Analysis.Symbols.Structs;

/// <summary>
/// Phase-1 canonical structural member tokens for protocols. Each token is
/// "{Return}:{Name}({Param1},{Param2},...)" with fully-qualified type names
/// resolved against the given binding context, so structural matching is
/// O(1) set membership that is topological and independent of local naming.
///
/// Generic type parameters normalize to positional $-placeholders ($T0, $T1, ...)
/// so width-lock matching ignores how each side names its type parameter.
/// `Self` stays a literal anchor by default, but conformance time can pass a
/// <paramref name="selfReplacement"/> (the enclosing concrete type's name) to
/// substitute the anchor literally. ref/refvar/*/[]/[N] wrappers are preserved
/// around the resolved inner token. Types that cannot be resolved yet (forward
/// references) fall back to their raw, space-free text; Phase-2 semantic
/// validation re-checks them via TypeSymbol.Equals.
/// </summary>
internal static class ProtocolCanonicalizer
{
	/// <summary>All canonical member tokens for a protocol, joined into a pre-match set.</summary>
	public static IReadOnlySet<string> BuildCanonicalMembers(ProtocolDeclarationSyntax protocolDecl, BindingContext context, IReadOnlyList<string>? concreteTypeArguments = null)
	{
		var tokens = new HashSet<string>();
		foreach (var member in protocolDecl.Members)
			tokens.Add(BuildMemberToken(member, protocolDecl.GenericParameters, context, selfReplacement: null, concreteTypeArguments));
		return tokens;
	}

	/// <summary>
	/// The canonical token for a single protocol member. When
	/// <paramref name="selfReplacement"/> is non-null, every literal `Self` in the
	/// member's return or parameter types is replaced with it (used at conformance
	/// time to compare against a candidate concrete type's canonical token). When
	/// <paramref name="concreteTypeArguments"/> is non-null (a generic protocol
	/// instantiation, e.g. IContainer&lt;int&gt;), each positional $-placeholder is
	/// replaced with its concrete type argument instead.
	/// </summary>
	public static string BuildMemberToken(ProtocolMethodDeclarationSyntax member, IReadOnlyList<string> genericParameters, BindingContext context, string? selfReplacement, IReadOnlyList<string>? concreteTypeArguments = null)
	{
		var returnToken = CanonicalizeProtocolType(member.ReturnType, genericParameters, context, selfReplacement, concreteTypeArguments);
		var paramTokens = member.Parameters.Select(p => CanonicalizeProtocolType(p.Type, genericParameters, context, selfReplacement, concreteTypeArguments));
		return string.Join(":", returnToken, $"{member.Name}({string.Join(",", paramTokens)})");
	}

	/// <summary>
	/// The canonical token for a default-implementation method written in an
	/// extension block on a protocol definition. Mirrors <see cref="BuildMemberToken"/>
	/// for a <see cref="FunctionDeclarationSyntax"/> (the method has an implicit
	/// `this` receiver of the protocol type, which is outside the token).
	/// </summary>
	public static string BuildFunctionToken(FunctionDeclarationSyntax method, IReadOnlyList<string> genericParameters, BindingContext context, IReadOnlyList<string>? concreteTypeArguments = null)
	{
		var returnToken = CanonicalizeProtocolType(method.ReturnType, genericParameters, context, selfReplacement: null, concreteTypeArguments);
		var paramTokens = method.Parameters.Select(p => CanonicalizeProtocolType(p.Type, genericParameters, context, selfReplacement: null, concreteTypeArguments));
		return string.Join(":", returnToken, $"{method.Name}({string.Join(",", paramTokens)})");
	}

	/// <summary>
	/// Removes any leading ref/refvar and trailing * / [N] / [] wrappers, leaving
	/// the bare inner type name (e.g. "refvar Self[4]" -> "Self").
	/// </summary>
	public static string StripWrappers(string rawType)
	{
		var t = rawType.Trim();
		while (true)
		{
			if (t.StartsWith("refvar ", StringComparison.Ordinal)) { t = t[7..]; }
			else if (t.StartsWith("ref ", StringComparison.Ordinal)) { t = t[4..]; }
			else if (t.EndsWith("[]", StringComparison.Ordinal)) { t = t[..^2]; }
			else if (t.EndsWith("*", StringComparison.Ordinal)) { t = t[..^1]; }
			else if (t.EndsWith("]", StringComparison.Ordinal))
			{
				var openBracket = t.LastIndexOf('[');
				if (openBracket <= 0) return t;
				t = t[..openBracket];
			}
			else
			{
				return t;
			}
		}
	}

	private static string CanonicalizeProtocolType(string rawType, IReadOnlyList<string> genericParameters, BindingContext context, string? selfReplacement, IReadOnlyList<string>? concreteTypeArguments)
	{
		var prefix = "";
		var suffix = "";

		var t = rawType.Trim();
		while (true)
		{
			if (t.StartsWith("refvar ", StringComparison.Ordinal)) { prefix += "refvar "; t = t[7..]; }
			else if (t.StartsWith("ref ", StringComparison.Ordinal)) { prefix += "ref "; t = t[4..]; }
			else if (t.EndsWith("[]", StringComparison.Ordinal)) { suffix = "[]" + suffix; t = t[..^2]; }
			else if (t.EndsWith("*", StringComparison.Ordinal)) { suffix = "*" + suffix; t = t[..^1]; }
			else if (t.EndsWith("]", StringComparison.Ordinal))
			{
				var openBracket = t.LastIndexOf('[');
				if (openBracket <= 0) break;
				suffix = t[openBracket..] + suffix;
				t = t[..openBracket];
			}
			else
			{
				break;
			}
		}

		return prefix + CanonicalizeProtocolInner(t, genericParameters, context, selfReplacement, concreteTypeArguments) + suffix;
	}

	private static string CanonicalizeProtocolInner(string t, IReadOnlyList<string> genericParameters, BindingContext context, string? selfReplacement, IReadOnlyList<string>? concreteTypeArguments)
	{
		// `Self` is a compile-time anchor resolving to the enclosing concrete type:
		// kept verbatim in stored tokens, substituted literally at conformance time.
		if (t == "Self") return selfReplacement ?? "Self";

		// Generic type parameters normalize to positional width-lock placeholders,
		// so `protocol IContainer<T> { void Store(T); }` matches a same-topology
		// `struct Bag<T>` regardless of how each side names its type parameter.
		// A concrete instantiation (e.g. IContainer<int>) substitutes the argument
		// in that position, keeping the pre-match O(1).
		var genericIndex = IndexOfGeneric(genericParameters, t);
		if (genericIndex >= 0)
			return concreteTypeArguments is not null && genericIndex < concreteTypeArguments.Count
				? concreteTypeArguments[genericIndex]
				: $"$T{genericIndex}";

		var resolved = context.ResolveType(t);
		if (resolved is not null) return resolved.Name;

		// Unresolvable at Pass 0 (e.g. a generic instantiation whose argument is
		// itself a generic parameter or Self): reconstruct canonically from parts.
		if (t.Contains('<'))
		{
			var openBracket = t.IndexOf('<');
			var closeBracket = t.LastIndexOf('>');
			if (closeBracket > openBracket)
			{
				var baseName = t[..openBracket];
				var baseIndex = IndexOfGeneric(genericParameters, baseName);
				var baseToken = baseIndex >= 0 ? $"$T{baseIndex}" : NormalizeRawTypeName(baseName);
				var args = t[(openBracket + 1)..closeBracket].Split(',').Select(a => CanonicalizeProtocolInner(a.Trim(), genericParameters, context, selfReplacement, concreteTypeArguments));
				return $"{baseToken}<{string.Join(",", args)}>";
			}
		}

		return NormalizeRawTypeName(t);
	}

	private static string NormalizeRawTypeName(string t)
	{
		return t.Replace(" ", "");
	}

	private static int IndexOfGeneric(IReadOnlyList<string> genericParameters, string name)
	{
		for (var i = 0; i < genericParameters.Count; i++)
		{
			if (genericParameters[i] == name) return i;
		}
		return -1;
	}
}