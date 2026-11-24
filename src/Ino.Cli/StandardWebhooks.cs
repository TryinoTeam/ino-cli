using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Ino.Cli;

/// <summary>
/// Standard Webhooks signing (https://www.standardwebhooks.com), the same formula the engine uses for deliveries:
/// <c>webhook-signature: v1,base64(HMAC-SHA256(base64decode(secret without "whsec_"), "{id}.{timestamp}." + body))</c>.
/// `ino listen` and `ino trigger` sign with a per-profile device secret so the local endpoint verifies exactly as it will in production.
/// </summary>
public static class StandardWebhooks
{
    public static string NewSecret() => "whsec_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    public static string Sign(string secret, string messageId, long timestamp, ReadOnlySpan<byte> body)
    {
        var key = Convert.FromBase64String(secret.StartsWith("whsec_", StringComparison.Ordinal) ? secret[6..] : secret);
        var prefix = Encoding.UTF8.GetBytes($"{messageId}.{timestamp}.");
        var content = new byte[prefix.Length + body.Length];
        prefix.CopyTo(content, 0);
        body.CopyTo(content.AsSpan(prefix.Length));
        return "v1," + Convert.ToBase64String(HMACSHA256.HashData(key, content));
    }

    /// <summary>
    /// Forwarding only to this machine: the event data can hold personal data, so `listen` never sends it elsewhere. The host must be a loopback
    /// literal or the name localhost / *.localhost (which RFC 6761 reserves for loopback and the CLI pins to 127.0.0.1 itself, so DNS cannot redirect it).
    /// </summary>
    public static Uri ValidateLocalTarget(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https") || !string.IsNullOrEmpty(u.UserInfo))
            throw new CliException(ExitCodes.Usage, "BAD_FORWARD_URL", "--forward-to must be an http(s) address such as http://localhost:3000/webhooks.");
        var host = u.IdnHost.TrimEnd('.').ToLowerInvariant();
        var ok = host == "localhost" || host.EndsWith(".localhost", StringComparison.Ordinal)
                 || (IPAddress.TryParse(host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));
        if (!ok) throw new CliException(ExitCodes.Usage, "FORWARD_NOT_LOCAL",
            $"--forward-to must point at this machine (localhost, 127.0.0.1 or [::1]); {u.Host} is not. Events can hold personal data, so ino never forwards them elsewhere.");
        return u;
    }

    public const string EngineUserAgent = "InoLoyalty-Webhooks/1.0";

    /// <summary>Legacy header (X-Ino-Signature): hex(HMAC-SHA256(utf8(secret), "{ts}." + body)), as the engine still sends with scheme BOTH.</summary>
    public static string SignLegacy(string secret, string timestamp, ReadOnlySpan<byte> body)
    {
        var prefix = Encoding.UTF8.GetBytes($"{timestamp}.");
        var content = new byte[prefix.Length + body.Length];
        prefix.CopyTo(content, 0);
        body.CopyTo(content.AsSpan(prefix.Length));
        return Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), content));
    }

    /// <summary>The headers a real delivery carries for a scheme (standard, legacy or both; subscriptions default to both).</summary>
    public static List<KeyValuePair<string, string>> Headers(string scheme, string secret, string messageId, long timestamp, ReadOnlySpan<byte> body)
    {
        var ts = timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var h = new List<KeyValuePair<string, string>>(6);
        if (scheme is "standard" or "both")
        {
            h.Add(new("webhook-id", messageId));
            h.Add(new("webhook-timestamp", ts));
            h.Add(new("webhook-signature", Sign(secret, messageId, timestamp, body)));
        }
        if (scheme is "legacy" or "both")
        {
            h.Add(new("X-Ino-Signature", SignLegacy(secret, ts, body)));
            h.Add(new("X-Timestamp", ts));
            h.Add(new("X-Webhook-Id", messageId));
        }
        return h;
    }

    /// <summary>
    /// A delivery-style message id: real deliveries use the delivery id (per subscription, stable across retries), not the event id. Here it is
    /// derived from the profile and the event id, so retries and replays of the same event carry the same id.
    /// </summary>
    public static string DeliveryId(string profile, string eventId) =>
        "msg_" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{profile}:{eventId}")))[..24];

    /// <summary>
    /// The forwarder: no proxy, no redirects, and the connection itself is checked. Whatever a name resolves to (hosts file, DNS, *.localhost),
    /// only loopback addresses (IPv4-mapped IPv6 included) are dialled.
    /// </summary>
    public static SocketsHttpHandler LoopbackOnlyHandler() => new()
    {
        UseProxy = false, AllowAutoRedirect = false, UseCookies = false,
        ConnectCallback = async (context, ct) =>
        {
            var host = context.DnsEndPoint.Host.Trim('[', ']');
            var addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, ct);
            var loopback = addresses.Where(IsLoopback).ToList();
            if (loopback.Count == 0)
                throw new HttpRequestException($"{context.DnsEndPoint.Host} does not resolve to this machine; ino only forwards to loopback addresses.");
            Exception? last = null;
            foreach (var ip in loopback)
            {
                var socket = new System.Net.Sockets.Socket(ip.AddressFamily, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(ip, context.DnsEndPoint.Port), ct);
                    if (socket.RemoteEndPoint is IPEndPoint remote && !IsLoopback(remote.Address))
                        throw new HttpRequestException("Connected to a non-loopback address; refused.");
                    return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
                }
                catch (Exception e) when (e is System.Net.Sockets.SocketException or HttpRequestException) { socket.Dispose(); last = e; }
            }
            throw new HttpRequestException($"Cannot connect to {context.DnsEndPoint}: {last?.Message}");
        },
    };

    public static bool IsLoopback(IPAddress ip) => IPAddress.IsLoopback(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip);
}
