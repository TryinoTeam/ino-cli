using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ino.Cli;

/// <summary>What the CLI needs to call one channel: never printed, never written to the config file.</summary>
public sealed record Connection(string BaseUrl, string Channel, string ApiKey, string Secret, string Environment)
{
    public bool IsLive => Environment == "live";
}

/// <summary>The engine's answer to whoami. Environment is test, live or unknown (only the control plane sets it).</summary>
public sealed record EngineIdentity(string? Tenant, string Environment, string? Channel, string? KeyPrefix, string[] Scopes);

public sealed record ChannelResponse(int Status, string Method, string Path, JsonNode? Body, string RawBody, DateTimeOffset? ServerDate,
    string? ContentType = null, byte[]? Bytes = null, TimeSpan? RetryAfter = null);

/// <summary>
/// After a 401 the profile is paused for 60 seconds (a marker file next to the config): an agent re-running the CLI cannot reach the engine's limit of
/// 30 failed sign-ins a minute per IP, which would also block other systems behind the same address (a shop's tills).
/// </summary>
public sealed class AuthLock(string path, Func<DateTimeOffset>? clock = null)
{
    public static readonly TimeSpan Pause = TimeSpan.FromSeconds(60);
    readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);

    public DateTimeOffset? Until()
    {
        try { return File.Exists(path) && long.TryParse(File.ReadAllText(path).Trim(), out var s) ? DateTimeOffset.FromUnixTimeSeconds(s) : null; }
        catch (IOException) { return null; }
    }

    public void ThrowIfLocked()
    {
        if (Until() is { } u && u > now())
            throw new CliException(ExitCodes.Auth, "PAUSED_AFTER_401", $"The last signed call was refused (401). Wait {(int)Math.Ceiling((u - now()).TotalSeconds)} s, then check the key with `ino doctor`.",
                "The pause protects your IP address from the engine's failed sign-in block.");
    }

    public void Set()
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, (now() + Pause).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture)); }
        catch (IOException) { }
    }
}

/// <summary>
/// Signed calls to the channel API. The body is serialized once and those bytes are signed and sent; redirects are not followed (headers never reach
/// another host); no retries (repeated 401s lock the caller's IP for a minute); 20-second timeout.
/// </summary>
public sealed class ChannelClient(Connection conn, HttpMessageHandler? handler = null, Func<DateTimeOffset>? clock = null)
{
    public static readonly string UserAgent = "ino-cli/" + Commands.Version;
    readonly HttpClient http = new(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }, disposeHandler: handler is null)
    { Timeout = TimeSpan.FromSeconds(20) };
    readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);

    public static Uri ValidateBase(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var u) || (u.Scheme != "https" && u.Scheme != "http"))
            throw new CliException(ExitCodes.Usage, "BAD_BASE_URL", "The base URL is not a valid http(s) address.");
        if (u.Scheme == "http" && !u.IsLoopback)
            throw new CliException(ExitCodes.Usage, "INSECURE_BASE_URL", "The base URL must use https (http only for localhost).");
        if (!string.IsNullOrEmpty(u.Query) || !string.IsNullOrEmpty(u.Fragment) || !string.IsNullOrEmpty(u.UserInfo))
            throw new CliException(ExitCodes.Usage, "BAD_BASE_URL", "The base URL must not contain a query, fragment or user info.");
        return u;
    }

    public static void ValidateChannel(string channel)
    {
        if (channel.Length is 0 or > 64 || !channel.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))
            throw new CliException(ExitCodes.Usage, "BAD_CHANNEL", "The channel code is not valid.");
    }

    Uri UrlFor(string relative)
    {
        var b = ValidateBase(conn.BaseUrl);
        ValidateChannel(conn.Channel);
        return new Uri(b.GetLeftPart(UriPartial.Path).TrimEnd('/') + $"/api/v1/{conn.Channel}/{relative}");
    }

    /// <summary>POST /api/v1/{channel}/{operation}. The body is sent exactly as given: field names depend on the channel's mapping, so nothing is added.</summary>
    public Task<ChannelResponse> CallOperationAsync(string operation, JsonObject? body, CancellationToken ct = default)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(operation, "^[a-z][a-z0-9]*(\\.[a-z0-9]+)+$"))
            throw new CliException(ExitCodes.Usage, "BAD_OPERATION", "Use an operation name such as customer.get360 (see `ino api list`).");
        return SendAsync(HttpMethod.Post, UrlFor(operation), Encoding.UTF8.GetBytes((body ?? []).ToJsonString()), ct);
    }

    /// <summary>GET on a channel path (events feed). Signed over an empty body.</summary>
    public Task<ChannelResponse> GetAsync(string relativeWithQuery, CancellationToken ct = default)
        => SendAsync(HttpMethod.Get, UrlFor(relativeWithQuery), [], ct);

    /// <summary>Any method on a channel path below /api/v1/{channel}/ (webhooks, jobs). A body is sent as given; DELETE and GET have none.</summary>
    public Task<ChannelResponse> SendAsync(HttpMethod method, string relativeWithQuery, JsonNode? body, CancellationToken ct = default)
        => SendAsync(method, UrlFor(relativeWithQuery), body is null ? [] : Encoding.UTF8.GetBytes(body.ToJsonString()), ct);

    /// <summary>Unsigned GET {origin}/health: reachability and the server clock (Date header) before any signed call.</summary>
    public async Task<DateTimeOffset?> HealthAsync(CancellationToken ct = default)
    {
        var b = ValidateBase(conn.BaseUrl);
        try
        {
            using var res = await http.GetAsync(new Uri(b.GetLeftPart(UriPartial.Authority) + "/health"), ct);
            return res.Headers.Date;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new CliException(ExitCodes.Network, "TIMEOUT", $"{b.Host} did not answer within 20 seconds."); }
        catch (HttpRequestException e) { throw new CliException(ExitCodes.Network, "NETWORK", $"Cannot reach {b.Host}: {e.Message}"); }
    }

    /// <summary>The engine's event catalog: unsigned GET {origin}/api/v1/meta/events (the host root, so it does not open a tenant shell on Ino Cloud).</summary>
    public async Task<JsonNode> CatalogAsync(CancellationToken ct = default)
    {
        var b = ValidateBase(conn.BaseUrl);
        foreach (var url in new[] { b.GetLeftPart(UriPartial.Authority) + "/api/v1/meta/events" })
        {
            try
            {
                using var res = await http.GetAsync(url, ct);
                if (!res.IsSuccessStatusCode) continue;
                var raw = await res.Content.ReadAsStringAsync(ct);
                try { return JsonNode.Parse(raw) ?? throw new JsonException(); } catch (JsonException) { continue; }
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new CliException(ExitCodes.Network, "TIMEOUT", "The event catalog did not answer within 20 seconds."); }
            catch (HttpRequestException e) { throw new CliException(ExitCodes.Network, "NETWORK", $"Cannot reach {b.Host}: {e.Message}"); }
        }
        throw new CliException(ExitCodes.Business, "NO_CATALOG", "The engine did not return its event catalog (/api/v1/meta/events).");
    }

    /// <summary>What the engine says about this key (GET /api/v1/{channel}/whoami). Null when the engine is older and has no whoami (404).</summary>
    public async Task<EngineIdentity?> WhoAmIAsync(CancellationToken ct = default)
    {
        var res = await GetAsync("whoami", ct);
        if (res.Status is 404 or 405) return null;   // an engine without whoami (the GET falls on the POST-only operation route: 405)
        var code = ExitCodeOf(res);
        if (code != ExitCodes.Ok) throw new CliException(code, "WHOAMI_FAILED", $"whoami answered HTTP {res.Status} {ResponseCodeOf(res.Body)}.", HintFor(res));
        var d = (res.Body as JsonObject)?["Data"] as JsonObject;
        return new EngineIdentity(d?["tenant"]?.ToString(), d?["environment"]?.ToString() ?? "unknown", d?["channel"]?.ToString(), d?["keyPrefix"]?.ToString(),
            (d?["scopes"] as JsonArray)?.Select(x => x?.ToString() ?? "").ToArray() ?? []);
    }

    /// <summary>Set by the caller to stop signed calls after a 401 (see <see cref="AuthLock"/>).</summary>
    public AuthLock? Lock { get; init; }

    async Task<ChannelResponse> SendAsync(HttpMethod method, Uri url, byte[] bytes, CancellationToken ct)
    {
        Lock?.ThrowIfLocked();
        var ts = now().ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var nonce = Signing.NewNonce();
        using var req = new HttpRequestMessage(method, url);
        // Sign what goes on the wire: the request URI's own path and query (an Ino Cloud base keeps its /t/<code> prefix).
        var pathAndQuery = req.RequestUri!.PathAndQuery;
        if (method != HttpMethod.Get && method != HttpMethod.Delete)
        {
            req.Content = new ByteArrayContent(bytes);
            req.Content.Headers.ContentType = new("application/json") { CharSet = "utf-8" };
        }
        req.Headers.TryAddWithoutValidation("X-Api-Key", conn.ApiKey);
        req.Headers.TryAddWithoutValidation("X-Timestamp", ts);
        req.Headers.TryAddWithoutValidation("X-Signature-Version", "2");
        req.Headers.TryAddWithoutValidation("X-Nonce", nonce);
        req.Headers.TryAddWithoutValidation("X-Signature", Signing.SignV2(conn.Secret, ts, nonce, method.Method, pathAndQuery, bytes));
        req.Headers.UserAgent.ParseAdd(UserAgent);

        HttpResponseMessage res;
        try { res = await http.SendAsync(req, ct); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        { throw new CliException(ExitCodes.Network, "TIMEOUT", "The call took longer than 20 seconds."); }
        catch (HttpRequestException e)
        { throw new CliException(ExitCodes.Network, "NETWORK", $"The call failed: {e.Message}"); }

        using (res)
        {
            var resBytes = await res.Content.ReadAsByteArrayAsync(ct);
            var contentType = res.Content.Headers.ContentType?.MediaType;
            var raw = Encoding.UTF8.GetString(resBytes);
            JsonNode? node = null;
            if (contentType is null or "application/json" || contentType.EndsWith("+json", StringComparison.Ordinal))
                try { node = raw.Length > 0 ? JsonNode.Parse(raw) : null; } catch (JsonException) { }
            var status = (int)res.StatusCode;
            var retryAfter = res.Headers.RetryAfter is { } ra ? ra.Delta ?? (ra.Date is { } d ? d - now() : null) : null;
            if (status is >= 300 and < 400)
                throw new CliException(ExitCodes.Business, "REDIRECT", $"The server answered with a redirect ({status}); it was not followed. Check the base URL.");
            if (status == 401) Lock?.Set();
            return new ChannelResponse(status, method.Method, url.AbsolutePath, node, raw, res.Headers.Date, contentType, resBytes, retryAfter);
        }
    }

    /// <summary>The engine's own result code (ResponseCode / responseCode) when the body has one. A channel mapping may answer a failure with HTTP 200.</summary>
    public static string? ResponseCodeOf(JsonNode? body) =>
        body is JsonObject o && (o["ResponseCode"] ?? o["responseCode"]) is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    /// <summary>Exit code from the HTTP status, overridden by a non-success engine result code.</summary>
    public static int ExitCodeOf(ChannelResponse res) => ResponseCodeOf(res.Body) switch
    {
        null or "SUCCESS" => ExitCodes.FromStatus(res.Status),
        "AUTHENTICATION_ERROR" or "FORBIDDEN" => ExitCodes.Auth,
        "NOT_FOUND" => ExitCodes.NotFound,
        "CONFLICT" or "IDEMPOTENCY_REPLAY" => ExitCodes.Conflict,
        "TOO_MANY_REQUESTS" => ExitCodes.RateLimited,
        _ => ExitCodes.Business,
    };

    public static string? HintFor(int status) => status switch
    {
        401 => "401 = key, secret, channel code, clock (±300 s) or signature. Signed calls on this profile pause for 60 s: 30 failures a minute block your IP (and others behind the same address). Run `ino doctor`.",
        403 => "403 = the key may not call this (operation list, scope such as events/webhooks/jobs) or your IP is not allowed. Check the key in Settings → Developers → API keys.",
        429 => "429 = rate limit; wait before the next call.",
        _ => null,
    };

    public static string? HintFor(ChannelResponse res) =>
        res.Status == 429 && ResponseCodeOf(res.Body) == "TOO_MANY_REQUESTS"
            ? "Too many failed sign-ins from your IP address: it is blocked for a minute. Check the key and secret with `ino doctor` after the wait."
            : HintFor(res.Status);
}
