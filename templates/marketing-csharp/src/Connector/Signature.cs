using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Connector;

/// <summary>
/// Verifies an Ino request (Standard Webhooks) on the RAW body, before JSON parsing. Ino signs every call with the connection's secret.
/// A request older or newer than 5 minutes is refused, and each message id is accepted once (a captured request cannot be played again).
/// </summary>
public sealed class Signature
{
    public sealed record Check(bool Ok, string? Id = null, string? Reason = null);

    readonly byte[] _key;
    readonly ConcurrentDictionary<string, long> _seen = new();   // webhook-id -> expiry (unix seconds)

    public Signature(string? secret) => _key = KeyOf(secret);

    /// <summary>The HMAC key: the secret without whsec_, base64-decoded. A malformed secret is refused instead of guessed.</summary>
    public static byte[] KeyOf(string? secret)
    {
        if (secret is null || !secret.StartsWith("whsec_", StringComparison.Ordinal))
            throw new InvalidOperationException("INO_MA_SIGNING_SECRET (whsec_…) is required.");
        var raw = secret[6..];
        if (raw.Length == 0 || raw.Length % 4 != 0 || !Regex.IsMatch(raw, "^[A-Za-z0-9+/]+={0,2}$"))
            throw new InvalidOperationException("INO_MA_SIGNING_SECRET is not a valid whsec_ secret.");
        return Convert.FromBase64String(raw);
    }

    public Check Verify(byte[] rawBody, Func<string, string?> header, long? nowSeconds = null)
    {
        string? id = header("webhook-id"), ts = header("webhook-timestamp"), sigs = header("webhook-signature");
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(ts) || string.IsNullOrEmpty(sigs)) return new(false, Reason: "missing signature headers");
        var now = nowSeconds ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (!Regex.IsMatch(ts, "^[0-9]{1,12}$") || Math.Abs(now - long.Parse(ts)) > 300) return new(false, Reason: "timestamp outside 5 minutes");
        byte[] message = [.. Encoding.UTF8.GetBytes($"{id}.{ts}."), .. rawBody];
        var expected = Encoding.ASCII.GetBytes(Convert.ToBase64String(HMACSHA256.HashData(_key, message)));
        // The header may carry more than one signature (space separated) while a secret is being renewed.
        var ok = sigs.Split(' ').Any(s => s.StartsWith("v1,", StringComparison.Ordinal)
            && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(s[3..]), expected));
        if (!ok) return new(false, Reason: "signature does not match");
        foreach (var (k, until) in _seen) if (until < now) _seen.TryRemove(k, out _);
        if (!_seen.TryAdd(id, now + 600)) return new(false, Reason: "message already received");
        return new(true, id);
    }

    /// <summary>The ping answer that proves this tool holds the secret: base64(HMAC-SHA256(key, challenge)).</summary>
    public string Proof(string challenge) => Convert.ToBase64String(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(challenge)));
}
