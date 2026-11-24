using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Ino.Cli;

/// <summary>
/// Two layers keep secrets out of what the CLI prints: response fields whose name says they hold a secret or token are replaced, and the profile's own
/// key and secret are removed from any text as plain strings.
/// </summary>
public static partial class Redaction
{
    public const string Mask = "[redacted]";

    [GeneratedRegex("^(secret|token|password|apikey|api_key|hmacsecret|hmac_secret|clientsecret|client_secret|.*token|.*secret)$", RegexOptions.IgnoreCase)]
    private static partial Regex SecretField();

    public static bool IsSecretField(string name) => SecretField().IsMatch(name);

    public static JsonNode? Fields(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var (key, value) in o.ToList())
                    o[key] = IsSecretField(key) && value is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.String
                        ? JsonValue.Create(Mask) : Fields(value?.DeepClone());
                return o;
            case JsonArray a:
                for (var i = 0; i < a.Count; i++) a[i] = Fields(a[i]?.DeepClone());
                return a;
            default:
                return node;
        }
    }

    [GeneratedRegex(@"\b(ik_[A-Za-z0-9_\-]{8,}|iak_[A-Za-z0-9_\-]{8,}|whsec_[A-Za-z0-9+/=]{16,})")]
    private static partial Regex SecretPattern();

    /// <summary>Masks anything shaped like an Ino key or a webhook secret (MCP output: the model never sees one, whatever field it sits in).</summary>
    public static string Patterns(string text) => SecretPattern().Replace(text, Mask);

    public static string Strings(string text, IEnumerable<string?> secrets)
    {
        foreach (var s in secrets)
            if (!string.IsNullOrEmpty(s) && s.Length >= 6) text = text.Replace(s, Mask, StringComparison.Ordinal);
        return text;
    }
}
