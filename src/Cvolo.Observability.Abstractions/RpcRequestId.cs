using System.Globalization;

namespace Cvolo.Observability.Abstractions;

/// <summary>
/// Typed JSON-RPC request id preserved across the protocol boundary (§5, §21.1).
/// Numeric and string ids must never be merged into one representation:
/// <c>{"type":"number","value":42}</c> stays distinct from <c>{"type":"string","value":"42"}</c>.
/// </summary>
public readonly record struct RpcRequestId
{
	/// <summary>"number" or "string" — the JSON type of the protocol id.</summary>
	public string Type { get; }

	/// <summary>Textual value; numeric ids keep invariant culture form and are re-emitted as JSON numbers.</summary>
	public string Value { get; }

	private RpcRequestId(string type, string value)
	{
		Type = type;
		Value = value;
	}

	/// <summary>True when the protocol id was a JSON number.</summary>
	public bool IsNumber => Type == "number";

	public static RpcRequestId Number(long value) => new("number", value.ToString(CultureInfo.InvariantCulture));

	public static RpcRequestId Number(string rawValue) => new("number", rawValue);

	public static RpcRequestId Text(string value) => new("string", value);

	public override string ToString() => $"{Type}:{Value}";
}
