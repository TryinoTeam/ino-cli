using System.Security.Cryptography;
using System.Text;

namespace Ino.Cli;

/// <summary>
/// Channel request signature, version 2 only: <c>hex(HMACSHA256(utf8(secret), "v2.{ts}.{nonce}.{METHOD}.{path+query as called}." + raw body))</c>.
/// The path is the exact path of the URL that is sent (an Ino Cloud base keeps its <c>/t/&lt;code&gt;</c> prefix). Version 1 is never sent.
/// A contract test compares this with the engine's own signer.
/// </summary>
public static class Signing
{
    public static string SignV2(string secret, string timestamp, string nonce, string method, string pathAndQuery, ReadOnlySpan<byte> body)
    {
        var prefix = Encoding.UTF8.GetBytes($"v2.{timestamp}.{nonce}.{method.ToUpperInvariant()}.{pathAndQuery}.");
        var payload = new byte[prefix.Length + body.Length];
        prefix.CopyTo(payload, 0);
        body.CopyTo(payload.AsSpan(prefix.Length));
        return Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), payload));
    }

    public static string NewNonce() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
}
