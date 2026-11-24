using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Connector;

/// <summary>
/// Signed calls to the Ino Loyalty channel API (signature version 2). Rules that matter:
/// sign exactly the bytes you send, over the path as called (on Ino Cloud it includes /t/&lt;tenant&gt;);
/// every request body carries a GUID requestId, and a retry sends the SAME body and requestId, signed again with a NEW nonce and timestamp;
/// never retry a 401 in a loop: 30 failed sign-ins a minute block the caller's IP (and the tills behind it).
/// </summary>
public sealed class InoClient(Settings settings, HttpClient http, Func<DateTimeOffset>? now = null, Func<TimeSpan, Task>? wait = null)
{
    static readonly HashSet<int> Retryable = [429, 502, 503, 504];
    readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.UtcNow);
    readonly Func<TimeSpan, Task> _wait = wait ?? (d => Task.Delay(d));

    public sealed record Result(int Status, JsonNode? Body, string RequestId);

    /// <summary>An HttpClient that never follows redirects: a redirect would resend the signature elsewhere.</summary>
    public static HttpClient CreateHttpClient() => new(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };

    public static string SignV2(string secret, string timestamp, string nonce, string method, string pathAndQuery, byte[] body)
    {
        var prefix = Encoding.UTF8.GetBytes($"v2.{timestamp}.{nonce}.{method.ToUpperInvariant()}.{pathAndQuery}.");
        byte[] message = [.. prefix, .. body];
        return Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), message));
    }

    HttpRequestMessage Signed(Uri url, byte[] body)
    {
        var ts = _now().ToUnixTimeSeconds().ToString();
        var nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));   // new for every try: a nonce is accepted once
        var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(body) };
        req.Content.Headers.ContentType = new("application/json");
        req.Headers.Add("X-Api-Key", settings.ApiKey);
        req.Headers.Add("X-Timestamp", ts);
        req.Headers.Add("X-Signature-Version", "2");
        req.Headers.Add("X-Nonce", nonce);
        req.Headers.Add("X-Signature", SignV2(settings.Secret, ts, nonce, "POST", url.AbsolutePath + url.Query, body));
        return req;
    }

    /// <summary>Reports this connector to Ino (Power Apps → Integrations → Connectors, `ino connector status`). No member data.</summary>
    public async Task<int> HeartbeatAsync(string name, string version, string? platform = null, string status = "ok", string? detail = null,
        int? queueDepth = null, CancellationToken ct = default)
    {
        var payload = new JsonObject { ["name"] = name, ["version"] = version, ["status"] = status };
        if (platform is not null) payload["platform"] = platform;
        if (detail is not null) payload["detail"] = detail;
        if (queueDepth is not null) payload["queueDepth"] = queueDepth;
        using var res = await http.SendAsync(Signed(new Uri($"{settings.BaseUrl}/api/v1/{settings.Channel}/connector/heartbeat"),
            Encoding.UTF8.GetBytes(payload.ToJsonString())), ct);
        return (int)res.StatusCode;
    }

    /// <summary>Calls one operation. body["requestId"] is added when missing and kept across retries.</summary>
    public async Task<Result> CallAsync(string operation, JsonObject? body = null, int attempts = 4, CancellationToken ct = default)
    {
        var payload = body?.DeepClone().AsObject() ?? [];
        payload["requestId"] ??= Guid.NewGuid().ToString();
        var requestId = payload["requestId"]!.GetValue<string>();
        var bytes = Encoding.UTF8.GetBytes(payload.ToJsonString());   // serialized once: the same bytes are signed and sent on every try
        var url = new Uri($"{settings.BaseUrl}/api/v1/{settings.Channel}/{operation}");
        Result? last = null;
        for (var i = 0; i < attempts; i++)
        {
            if (i > 0) await _wait(TimeSpan.FromMilliseconds(Math.Min(30_000, 1000 * Math.Pow(2, i - 1)) + Random.Shared.Next(250)));
            HttpResponseMessage res;
            try { res = await http.SendAsync(Signed(url, bytes), ct); }
            catch (HttpRequestException e) { last = new(0, JsonValue.Create(e.Message), requestId); continue; }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested) { last = new(0, JsonValue.Create("timeout"), requestId); continue; }
            using (res)
            {
                var text = await res.Content.ReadAsStringAsync(ct);
                JsonNode? parsed;
                try { parsed = text.Length > 0 ? JsonNode.Parse(text) : null; } catch (JsonException) { parsed = JsonValue.Create(text); }
                last = new((int)res.StatusCode, parsed, requestId);
                if ((int)res.StatusCode == 429 && res.Headers.RetryAfter?.Delta is { } delta) await _wait(delta);
                if (!Retryable.Contains((int)res.StatusCode)) return last;   // 2xx and 4xx (including 401: fix the key, do not loop) are final
            }
        }
        return last!;
    }
}
