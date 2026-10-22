using Cvolo.Observability.Abstractions;

namespace Cvolo.Observability;

/// <summary>
/// Defense in depth for the metadata-only recording modes (§60). Sanitization
/// precedes queue insertion, so neither an in-memory collector nor the JSONL
/// writer can recover the omitted payload from a StampedEvent.
///
/// Reason and string RPC IDs are sanitized as well as attributes. Full
/// source consent/revoke and envelope-wide field policies remain outstanding.
/// </summary>
internal static class EventPrivacyFilter
{
    private const int MaxKeyLength = 96;
    private const int MaxValueLength = 256;

    /// <summary>
    /// Materializes an immutable snapshot: caller-owned lists cannot be mutated
    /// after Emit to smuggle raw content into the async writer.
    /// </summary>
    internal static CompilerEvent Project(in CompilerEvent value)
    {
        var attributes = value.Attributes;
        var safeReason = SafeMachineReason(value.Reason);
        var safeRpcId = SafeRpcId(value.RpcRequestId);
        if (attributes is null || attributes.Count == 0)
            return value with { Reason = safeReason, RpcRequestId = safeRpcId };

        var sanitized = new List<KeyValuePair<string, string>>(attributes.Count + 1);
        var omitted = 0;

        foreach (var attribute in attributes)
        {
            if (IsSensitiveKey(attribute.Key) || IsSensitiveValue(attribute.Value))
            {
                omitted++;
                continue;
            }

            sanitized.Add(attribute);
        }

        if (omitted > 0)
            sanitized.Add(new("redaction.omittedAttributeCount", omitted.ToString(
                System.Globalization.CultureInfo.InvariantCulture)));

        return value with
        {
            Reason = safeReason,
            RpcRequestId = safeRpcId,
            Attributes = sanitized.ToArray(),
        };
    }

    private static string? SafeMachineReason(string? reason)
    {
        if (reason is null)
            return null;
        if (reason.Length is < 1 or > 96 || IsSensitiveValue(reason))
            return "redacted_evidence";

        // Machine reason codes are not free-form exception messages.
        foreach (char ch in reason)
        {
            if (!((ch >= 'a' && ch <= 'z')
                || (ch >= 'A' && ch <= 'Z')
                || (ch >= '0' && ch <= '9')
                || ch is '.' or '_' or '-' or ':'))
                return "redacted_evidence";
        }
        return reason;
    }

    private static RpcRequestId? SafeRpcId(RpcRequestId? rpcRequestId)
    {
        if (rpcRequestId is not { IsNumber: false } textId)
            return rpcRequestId;

        // JSON-RPC allows arbitrary string request IDs. The generated
        // RequestUid remains the correlation identity even if the client's
        // original protocol ID has to be redacted.
        string raw = textId.Value;
        if (raw.Length is < 1 or > 96 || IsSensitiveValue(raw))
            return RpcRequestId.Text("[redacted]");

        foreach (char ch in raw)
        {
            if (!((ch >= 'a' && ch <= 'z')
                || (ch >= 'A' && ch <= 'Z')
                || (ch >= '0' && ch <= '9')
                || ch is '.' or '_' or '-' or ':'))
                return RpcRequestId.Text("[redacted]");
        }
        return rpcRequestId;
    }

    private static bool IsSensitiveKey(string? key)
    {
        if (string.IsNullOrEmpty(key) || key.Length > MaxKeyLength)
            return true;

        // Key names are protocol identifiers, never arbitrary user text.
        foreach (var ch in key)
        {
            if (!((ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z')
                || (ch >= '0' && ch <= '9') || ch is '.' or '_' or '-'))
                return true;
        }

        return Contains(key, "source")
            || Contains(key, "content")
            || Contains(key, "text")
            || Contains(key, "path")
            || Contains(key, "uri")
            || Contains(key, "symbolname")
            || Contains(key, "exception.message")
            || Contains(key, "stack")
            || Contains(key, "password")
            || Contains(key, "secret")
            || Contains(key, "token")
            || Contains(key, "authorization")
            || Contains(key, "cookie")
            || Contains(key, "commandline")
            || Contains(key, "environment")
            || Contains(key, "apikey");
    }

    private static bool IsSensitiveValue(string? value)
    {
        if (value is null || value.Length > MaxValueLength)
            return true;

        if (Contains(value, "file://") || Contains(value, "Bearer ")
            || Contains(value, "-----BEGIN ") || Contains(value, "ghp_")
            || Contains(value, "github_pat_")
            || Contains(value, "sk-"))
            return true;

        // Common absolute filesystem paths. Do not suppress legitimate LSP
        // machine methods such as "textDocument/definition".
        if (value.StartsWith("/home/", StringComparison.Ordinal)
            || value.StartsWith("/Users/", StringComparison.Ordinal)
            || value.StartsWith("/tmp/", StringComparison.Ordinal)
            || value.StartsWith("/root/", StringComparison.Ordinal)
            || value.StartsWith("/workspace/", StringComparison.Ordinal)
            || value.StartsWith("/mnt/", StringComparison.Ordinal))
            return true;

        for (int i = 0; i + 2 < value.Length; i++)
        {
            if (((value[i] >= 'a' && value[i] <= 'z')
                    || (value[i] >= 'A' && value[i] <= 'Z'))
                && value[i + 1] == ':'
                && value[i + 2] is '\\' or '/')
                return true;
        }

        return false;
    }

    private static bool Contains(string text, string needle)
        => text.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
