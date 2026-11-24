using System.Security.Cryptography;
using System.Text;

namespace Connector;

/// <summary>
/// Verifies an Ino webhook (Standard Webhooks) on the RAW body, before JSON parsing.
/// Deduplicate on webhook-id: delivery is at least once. Answer 2xx within 10 seconds and do the work afterwards.
/// </summary>
public static class Webhook
{
    public sealed record Check(bool Ok, string? Id = null, string? Reason = null);

    public static Check Verify(byte[] rawBody, IReadOnlyDictionary<string, string> headers, string secret, long? nowSeconds = null)
    {
        var h = headers.ToDictionary(p => p.Key.ToLowerInvariant(), p => p.Value);
        if (!h.TryGetValue("webhook-id", out var id) || !h.TryGetValue("webhook-timestamp", out var ts) || !h.TryGetValue("webhook-signature", out var sigs))
            return new(false, Reason: "missing Standard Webhooks headers");
        var now = nowSeconds ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (!long.TryParse(ts, out var t) || Math.Abs(now - t) > 300) return new(false, Reason: "timestamp older than 5 minutes");
        var key = Convert.FromBase64String(secret.StartsWith("whsec_", StringComparison.Ordinal) ? secret[6..] : secret);
        byte[] message = [.. Encoding.UTF8.GetBytes($"{id}.{ts}."), .. rawBody];
        var expected = Encoding.ASCII.GetBytes(Convert.ToBase64String(HMACSHA256.HashData(key, message)));
        foreach (var s in sigs.Split(' '))
        {
            var got = Encoding.ASCII.GetBytes(s.StartsWith("v1,", StringComparison.Ordinal) ? s[3..] : s);
            if (CryptographicOperations.FixedTimeEquals(got, expected)) return new(true, id);
        }
        return new(false, Reason: "signature does not match");
    }
}
