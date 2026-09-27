namespace Cvolo.Core.AST.Declarations;

/// <summary>
/// Maps overloadable operator spellings to the stable mangled token used for declaration
/// registration, name mangling and symbol identity.
/// </summary>
/// <remarks>
/// Operators are receiverless associated callables, so they flow through exactly the same
/// registration path as a leading-dot function. They still need a name, and the raw spelling
/// ('+', '==') is not usable as one: it is punctuation that can collide with mangled signature
/// separators and it is ambiguous between the unary and binary form of '+' and '-'. The token
/// table below gives each overloadable operator a unique 'op_xxx' identifier, which keeps
/// mangled names collision-free and lets tooling recover the source spelling for hover,
/// completion and semantic tokens.
/// <para>
/// Assignment ('=') is deliberately absent: assignment is compiler-owned (initialization,
/// copy/move, lifetime, mutability, references and borrow analysis) and is not overloadable.
/// </para>
/// </remarks>
public static class OperatorTokens
{
	/// <summary>Common prefix of every mangled operator token.</summary>
	public const string Prefix = "op_";

	private static readonly Dictionary<string, string> BinaryTokens = new(StringComparer.Ordinal)
	{
		["+"] = Prefix + "add",
		["-"] = Prefix + "sub",
		["*"] = Prefix + "mul",
		["/"] = Prefix + "div",
		["%"] = Prefix + "mod",
		["=="] = Prefix + "eq",
		["!="] = Prefix + "neq",
		["<"] = Prefix + "lt",
		["<="] = Prefix + "lte",
		[">"] = Prefix + "gt",
		[">="] = Prefix + "gte",
		["&"] = Prefix + "and",
		["|"] = Prefix + "or",
		["^"] = Prefix + "xor",
		["<<"] = Prefix + "lshift",
		[">>"] = Prefix + "rshift"
	};

	private static readonly Dictionary<string, string> UnaryTokens = new(StringComparer.Ordinal)
	{
		["+"] = Prefix + "pos",
		["-"] = Prefix + "neg",
		["!"] = Prefix + "not",
		["~"] = Prefix + "bnot"
	};

	private static readonly Dictionary<string, string> Spellings = new(StringComparer.Ordinal)
	{
		[Prefix + "add"] = "+",
		[Prefix + "sub"] = "-",
		[Prefix + "mul"] = "*",
		[Prefix + "div"] = "/",
		[Prefix + "mod"] = "%",
		[Prefix + "eq"] = "==",
		[Prefix + "neq"] = "!=",
		[Prefix + "lt"] = "<",
		[Prefix + "lte"] = "<=",
		[Prefix + "gt"] = ">",
		[Prefix + "gte"] = ">=",
		[Prefix + "and"] = "&",
		[Prefix + "or"] = "|",
		[Prefix + "xor"] = "^",
		[Prefix + "lshift"] = "<<",
		[Prefix + "rshift"] = ">>",
		[Prefix + "pos"] = "+",
		[Prefix + "neg"] = "-",
		[Prefix + "not"] = "!",
		[Prefix + "bnot"] = "~"
	};

	/// <summary>Spellings that are overloadable in binary position, in declaration-set order.</summary>
	public static IReadOnlyCollection<string> BinarySpellings => BinaryTokens.Keys;

	/// <summary>Spellings that are overloadable in unary position, in declaration-set order.</summary>
	public static IReadOnlyCollection<string> UnarySpellings => UnaryTokens.Keys;

	/// <summary>
	/// True when the spelling can be overloaded in the given position.
	/// </summary>
	public static bool IsOverloadable(string spelling, bool unary)
		=> unary ? UnaryTokens.ContainsKey(spelling) : BinaryTokens.ContainsKey(spelling);

	/// <summary>
	/// The mangled token for a spelling in the given position, or null when the spelling is not
	/// overloadable there (which includes assignment, for every position).
	/// </summary>
	public static string? TryGetToken(string spelling, bool unary)
		=> (unary ? UnaryTokens : BinaryTokens).TryGetValue(spelling, out var token) ? token : null;

	/// <summary>
	/// The source spelling a mangled token was declared with, or null when the token does not
	/// belong to the overloadable operator set.
	/// </summary>
	public static string? TryGetSpelling(string token)
		=> Spellings.TryGetValue(token, out var spelling) ? spelling : null;

	/// <summary>True when the name is a mangled operator token rather than an ordinary identifier.</summary>
	public static bool IsOperatorToken(string? name)
		=> name is not null && name.StartsWith(Prefix, StringComparison.Ordinal) && Spellings.ContainsKey(name);

	/// <summary>
	/// True when the token declares a unary operator, which changes both the expected parameter
	/// count (one) and the receiver arity used at call sites.
	/// </summary>
	public static bool IsUnary(string token) => UnaryTokens.Values.Contains(token, StringComparer.Ordinal);

	/// <summary>
	/// The number of operands an operator declaration of this token takes.
	/// </summary>
	public static int OperandCount(string token) => IsUnary(token) ? 1 : 2;

	/// <summary>
	/// Human-readable rendering used by hover, signature help and completion, e.g. 'operator +'.
	/// </summary>
	public static string Display(string token) => TryGetSpelling(token) is { } spelling ? $"operator {spelling}" : token;
}
