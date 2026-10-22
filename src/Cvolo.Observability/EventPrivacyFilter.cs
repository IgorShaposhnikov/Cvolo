using Cvolo.Observability.Abstractions;

namespace Cvolo.Observability;

/// <summary>
/// Defense in depth for the metadata-only recording modes (§60). Sanitization
/// precedes queue insertion, so neither an in-memory collector nor the JSONL
/// writer can recover the omitted payload from a StampedEvent.
///
/// This guards attributes only. A full privacy contract must also classify
/// scalar envelope fields and implement separately consented repro capture.
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
        if (attributes is null || attributes.Count == 0)
            return value;

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

        return value with { Attributes = sanitized.ToArray() };
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
