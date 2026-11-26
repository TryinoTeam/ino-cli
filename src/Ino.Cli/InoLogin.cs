using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ino.Cli;

/// <summary>
/// MCP/CLI P0c.1c (docs/MCP_CLI_P0C.md): `ino login / logout` against Ino Auth. Authorization code + PKCE (S256) through the system browser and a
/// loopback listener on a free port (RFC 8252); the tenant comes from the profile's address (…/t/&lt;tenant&gt;) and is sent as ino_tenant.
/// Only the refresh token is kept, in the keychain under "login:&lt;profile&gt;"; access tokens live in memory. A file lock serialises refreshes
/// (`ino mcp serve` and a terminal share one refresh token: N8 / major 8).
/// </summary>
public static class InoLogin
{
    public const string ClientId = "ino-cli";
    public const string DefaultScopes = "offline_access docs:read integration:read";

    public sealed record Target(Uri Issuer, string Tenant);
    public sealed record Session(string AccessToken, string RefreshToken, JsonObject Claims);

    /// <summary>https://api.inoloyalty.com/t/tacme001 → issuer https://api.inoloyalty.com/oauth/, tenant tacme001.</summary>
    public static Target TargetOf(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps)
            throw new CliException(ExitCodes.Usage, "BAD_BASE_URL", "Signing in needs an https profile address like https://api.inoloyalty.com/t/<tenant>.");
        var parts = u.AbsolutePath.Trim('/').Split('/');
        if (parts.Length < 2 || parts[^2] != "t" || parts[^1].Length is < 3 or > 30)
            throw new CliException(ExitCodes.Usage, "NO_TENANT", "The profile address has no tenant (…/t/<tenant>). Sign-in is for Ino Cloud tenants.");
        return new Target(new Uri($"{u.Scheme}://{u.Authority}/oauth/"), parts[^1]);
    }

    static string Key(string profile) => "login:" + profile;
    static string B64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Browser sign-in; returns the session without storing it (the caller decides where it goes: <see cref="SaveAsync"/>).</summary>
    public static async Task<Session> LoginAsync(CliContext ctx, Target target, string scopes, CancellationToken ct)
    {
        var verifier = B64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = B64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = B64Url(RandomNumberGenerator.GetBytes(16));
        var port = FreePort();
        var redirect = $"http://127.0.0.1:{port}/callback";
        var authorize = new Uri(target.Issuer, "authorize?" + string.Join("&", new Dictionary<string, string>
        {
            ["client_id"] = ClientId, ["response_type"] = "code", ["redirect_uri"] = redirect, ["scope"] = scopes, ["state"] = state,
            ["code_challenge"] = challenge, ["code_challenge_method"] = "S256", ["ino_tenant"] = target.Tenant,
        }.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}")));

        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var waiting = listener.GetContextAsync();
        ctx.Err.WriteLine($"Opening your browser to sign in to {target.Tenant}. If it does not open, visit:\n{authorize.AbsoluteUri}");
        await (ctx.OpenBrowser ?? OpenSystemBrowser)(authorize);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        var done = await Task.WhenAny(waiting, Task.Delay(Timeout.Infinite, timeout.Token).ContinueWith(_ => { }, TaskScheduler.Default));
        if (done != waiting) throw new CliException(ExitCodes.Usage, "LOGIN_TIMEOUT", "No sign-in within 5 minutes. Run `ino login` again.");
        var call = await waiting;
        var q = System.Web.HttpUtility.ParseQueryString(call.Request.Url!.Query);
        var ok = q["state"] == state && q["code"] is { Length: > 0 };
        await Reply(call.Response, ok ? "Signed in. You can close this tab and return to the terminal." : "Sign-in did not complete. Return to the terminal.");
        if (q["state"] != state) throw new CliException(ExitCodes.Auth, "LOGIN_STATE", "The sign-in answer was not for this request. Run `ino login` again.");
        if (q["error"] is { } error) throw new CliException(ExitCodes.Auth, "LOGIN_REFUSED", $"Sign-in refused: {error}. {q["error_description"]}".Trim());

        var session = await TokenAsync(ctx, target, new()
        {
            ["grant_type"] = "authorization_code", ["client_id"] = ClientId, ["code"] = q["code"]!, ["redirect_uri"] = redirect, ["code_verifier"] = verifier,
        }, ct);
        return session;
    }

    /// <summary>
    /// Keeps a new sign-in for the profile under the profile's login lock (a refresh by `ino mcp serve` cannot overwrite it), bound to its address,
    /// and ends the sign-in it replaces (best effort) so a second `ino login` leaves no orphan session.
    /// </summary>
    public static async Task SaveAsync(CliContext ctx, string profile, Target target, Session session, CancellationToken ct)
    {
        var (_, _, store) = ctx.Profiles.ResolveForLogin(profile);
        Credentials? previous;
        using (Lock(ctx, profile))
        {
            previous = store.Read(Key(profile));
            Store(store, profile, target, session);
        }
        // Only a token bound to this address is ended here; one saved before the binding ("ino-auth") may be from another host, so it is never
        // sent anywhere (Gate S): it is overwritten and its session expires when unused for 14 days.
        if (previous is not null && previous.ApiKey == Binding(target) && previous.Secret != session.RefreshToken)
            await RevokeAsync(ctx, target, previous.Secret, ct);
    }

    /// <summary>Revokes a refresh token at its own issuer (best effort): a profile's address check refused it, or a new sign-in replaced it.</summary>
    public static async Task RevokeAsync(CliContext ctx, Target target, string refreshToken, CancellationToken ct)
    {
        using var http = Http(ctx);
        try
        {
            using var _ = await http.PostAsync(new Uri(target.Issuer, "revoke"), new FormUrlEncodedContent(new Dictionary<string, string>
                { ["client_id"] = ClientId, ["token"] = refreshToken, ["token_type_hint"] = "refresh_token" }), ct);
        }
        catch (Exception e) when (e is HttpRequestException || e is TaskCanceledException && !ct.IsCancellationRequested) { }
    }

    /// <summary>
    /// What a stored refresh token is for (B2): issuer and tenant. A token is only ever sent to the address it was issued by; a profile whose
    /// address changed (or a token saved before this binding) needs `ino login` again.
    /// </summary>
    static string Binding(Target t) => $"ino-auth|{t.Issuer.AbsoluteUri.ToLowerInvariant()}|{t.Tenant}";

    static Credentials? Saved(ISecretStore store, string profile, Target target)
    {
        if (store.Read(Key(profile)) is not { } saved) return null;
        if (saved.ApiKey != Binding(target))
            throw new CliException(ExitCodes.Auth, "LOGIN_OTHER_ADDRESS", "The saved sign-in is not for this profile's address. Run `ino login` again.");
        return saved;
    }

    /// <summary>A fresh access token from the stored refresh token (rotated and stored again), under a file lock shared by every ino process.</summary>
    public static async Task<Session?> RefreshAsync(CliContext ctx, string profile, Target target, CancellationToken ct)
    {
        var (_, _, store) = ctx.Profiles.ResolveForLogin(profile);
        using var gate = Lock(ctx, profile);
        if (Saved(store, profile, target) is not { } saved) return null;
        var session = await TokenAsync(ctx, target, new() { ["grant_type"] = "refresh_token", ["client_id"] = ClientId, ["refresh_token"] = saved.Secret }, ct);
        Store(store, profile, target, session);
        return session;
    }

    public sealed record LogoutResult(bool WasSignedIn, int? Ended, string? Note);

    /// <summary>
    /// P0c.4: ends this sign-in at Ino (its authorization, so its access token stops within a minute and it leaves the Sessions list), or with
    /// <paramref name="all"/> every session of the person in this tenant (other machines, agents). Then revokes the refresh token and removes it
    /// from the keychain. Offline: removed here, and the session ends on its own when unused for 14 days.
    /// </summary>
    public static async Task<LogoutResult> LogoutAsync(CliContext ctx, string profile, Target target, bool all, CancellationToken ct)
    {
        var (_, _, store) = ctx.Profiles.ResolveForLogin(profile);
        using var gate = Lock(ctx, profile);
        Credentials? saved;
        try { saved = Saved(store, profile, target); }
        catch (CliException e) when (e.Code == "LOGIN_OTHER_ADDRESS" && !all)
        {
            store.Delete(Key(profile));   // never sent anywhere: it is not for this address
            return new LogoutResult(true, null, "The saved sign-in was for another address; removed here without contacting Ino.");
        }
        if (saved is null) return all
            ? throw new CliException(ExitCodes.Auth, "NOT_SIGNED_IN", "Not signed in for this profile. Run `ino login`, then `ino logout --all`.")
            : new LogoutResult(false, null, null);
        Session session;
        try
        {
            session = await TokenAsync(ctx, target, new() { ["grant_type"] = "refresh_token", ["client_id"] = ClientId, ["refresh_token"] = saved.Secret }, ct);
            Store(store, profile, target, session);   // rotated: if ending fails below, the profile keeps a usable token (a retry must not look like reuse)
        }
        catch (CliException e) when (e.Code == "LOGIN_ENDED")
        {
            store.Delete(Key(profile));
            if (all) throw new CliException(ExitCodes.Auth, "SESSION_ENDED",
                "This profile's sign-in had already ended, so your other sessions could not be reached. Run `ino login`, then `ino logout --all`.");
            return new LogoutResult(true, null, "Your sign-in had already ended.");
        }
        catch (Exception e) when (e is HttpRequestException or CliException { Code: "LOGIN_FAILED" } || e is TaskCanceledException && !ct.IsCancellationRequested)
        {
            if (all) throw new CliException(ExitCodes.Network, "OFFLINE", "Ino did not answer, so your sessions could not be ended. Nothing was removed; try again.");
            store.Delete(Key(profile));
            return new LogoutResult(true, null, "Ino was not reachable: removed here; the session ends when unused for 14 days.");
        }
        int? ended = null;
        try
        {
            var res = await ManageAsync(ctx, target, session.AccessToken, HttpMethod.Post, all ? "sessions/end-mine" : "sessions/end-this", ct);
            ended = res?["ended"]?.GetValue<int>();
        }
        catch (Exception e) when (!all && (e is HttpRequestException or CliException || e is TaskCanceledException && !ct.IsCancellationRequested)) { }   // single logout: revoking the refresh token below still ends it
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new CliException(ExitCodes.Network, "TIMEOUT", "Ino did not answer in time, so your sessions may still be open. You are still signed in here; try again.");
        }
        using (var http = Http(ctx))
            try
            {
                using var _ = await http.PostAsync(new Uri(target.Issuer, "revoke"), new FormUrlEncodedContent(new Dictionary<string, string>
                    { ["client_id"] = ClientId, ["token"] = session.RefreshToken, ["token_type_hint"] = "refresh_token" }), ct);
            }
            catch (Exception e) when (e is HttpRequestException || e is TaskCanceledException && !ct.IsCancellationRequested) { }
        store.Delete(Key(profile));
        return new LogoutResult(true, ended, null);
    }

    /// <summary>P0c.4: the person's own sessions in this tenant (`ino sessions`).</summary>
    public static async Task<JsonArray> SessionsAsync(CliContext ctx, string profile, Target target, CancellationToken ct)
    {
        var s = await RefreshAsync(ctx, profile, target, ct) ?? throw new CliException(ExitCodes.Auth, "NOT_SIGNED_IN", "Not signed in for this profile. Run `ino login`.");
        return (await ManageAsync(ctx, target, s.AccessToken, HttpMethod.Get, "sessions", ct)) as JsonArray ?? [];
    }

    /// <summary>P0c.3: a Management API call as the signed-in person (refreshes the sign-in first). Returns the "data" member.</summary>
    public static async Task<JsonNode?> ManageAsync(CliContext ctx, string profile, Target target, HttpMethod method, string path, JsonNode? body, string? idempotencyKey, CancellationToken ct)
    {
        var s = await RefreshAsync(ctx, profile, target, ct) ?? throw new CliException(ExitCodes.Auth, "NOT_SIGNED_IN", "Not signed in for this profile. Run `ino login`.");
        return await ManageAsync(ctx, target, s.AccessToken, method, path, ct, body, idempotencyKey);
    }

    /// <summary>A Management API POST whose body is a file sent as is (a connector definition: the engine stores and hashes exactly these bytes).</summary>
    public static async Task<JsonNode?> ManageRawAsync(CliContext ctx, string profile, Target target, string path, byte[] raw, CancellationToken ct)
    {
        var s = await RefreshAsync(ctx, profile, target, ct) ?? throw new CliException(ExitCodes.Auth, "NOT_SIGNED_IN", "Not signed in for this profile. Run `ino login`.");
        return await ManageAsync(ctx, target, s.AccessToken, HttpMethod.Post, path, ct, raw: raw);
    }

    /// <summary>A Management API call (…/t/&lt;tenant&gt;/manage/v1/&lt;path&gt;) with the person's access token; returns the "data" member.</summary>
    static async Task<JsonNode?> ManageAsync(CliContext ctx, Target target, string accessToken, HttpMethod method, string path, CancellationToken ct,
        JsonNode? body = null, string? idempotencyKey = null, byte[]? raw = null)
    {
        using var http = Http(ctx);
        using var req = new HttpRequestMessage(method, new Uri(target.Issuer, $"/t/{target.Tenant}/manage/v1/{path}"));
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        if (idempotencyKey is not null) req.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        if (body is not null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        if (raw is not null) { req.Content = new ByteArrayContent(raw); req.Content.Headers.ContentType = new("application/json"); }
        using var res = await http.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        JsonNode? json = null;
        try { json = JsonNode.Parse(text); } catch (JsonException) { }
        // The engine's own 404 (e.g. "local_pos 1.2.0 is not saved") is shown as is; a bare 404 means the route is missing.
        if (res.StatusCode == HttpStatusCode.NotFound && json?["error_description"]?.GetValue<string>() is { } notFound)
            throw new CliException(ExitCodes.NotFound, json["error"]?.GetValue<string>()?.ToUpperInvariant() ?? "NOT_FOUND", notFound);
        if (res.StatusCode == HttpStatusCode.NotFound)
            throw new CliException(ExitCodes.NotFound, "NOT_AVAILABLE", path.StartsWith("sessions", StringComparison.Ordinal)
                ? "Sessions are not available for this tenant (Ino Auth is off, or the engine is older)."
                : "This command is not available for this tenant (Ino Auth is off, or the engine is older).");
        if (!res.IsSuccessStatusCode)
        {
            var status = (int)res.StatusCode;
            var message = json?["error_description"]?.GetValue<string>() ?? $"Ino refused the request ({status}).";
            var code = json?["error"]?.GetValue<string>()?.ToUpperInvariant();
            throw status is 401 or 403
                ? new CliException(ExitCodes.Auth, "MANAGE_REFUSED", message)
                : new CliException(status >= 500 ? ExitCodes.Network : ExitCodes.FromStatus(status), code ?? (status >= 500 ? "UNAVAILABLE" : "REFUSED"), message);
        }
        return json?["data"];
    }

    static async Task<Session> TokenAsync(CliContext ctx, Target target, Dictionary<string, string> form, CancellationToken ct)
    {
        using var http = Http(ctx);
        using var res = await http.PostAsync(new Uri(target.Issuer, "token"), new FormUrlEncodedContent(form), ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        JsonObject? json = null;
        try { json = JsonNode.Parse(body) as JsonObject; } catch (JsonException) { }
        if (!res.IsSuccessStatusCode || json?["access_token"]?.GetValue<string>() is not { } access)
        {
            var error = json?["error"]?.GetValue<string>() ?? ((int)res.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture);
            throw new CliException(ExitCodes.Auth, error == "invalid_grant" ? "LOGIN_ENDED" : "LOGIN_FAILED", error == "invalid_grant"
                ? "Your Ino sign-in ended (expired or revoked). Run `ino login` again." : $"Ino Auth refused the request ({error}).");
        }
        var refresh = json["refresh_token"]?.GetValue<string>() ?? throw new CliException(ExitCodes.Auth, "LOGIN_FAILED", "Ino Auth issued no refresh token.");
        return new Session(access, refresh, Claims(access));
    }

    /// <summary>The access token's claims (shown, not trusted: the engine verifies the signature on every call).</summary>
    public static JsonObject Claims(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length < 2) return [];
        var p = parts[1].Replace('-', '+').Replace('_', '/');
        try { return JsonNode.Parse(Convert.FromBase64String(p.PadRight(p.Length + (4 - p.Length % 4) % 4, '='))) as JsonObject ?? []; }
        catch (Exception e) when (e is FormatException or JsonException) { return []; }
    }

    static void Store(ISecretStore store, string profile, Target target, Session s) => store.Write(Key(profile), new Credentials(Binding(target), s.RefreshToken));

    static HttpClient Http(CliContext ctx) => new(ctx.AuthHandler ?? new SocketsHttpHandler { AllowAutoRedirect = false }, disposeHandler: ctx.AuthHandler is null)
        { Timeout = TimeSpan.FromSeconds(30) };

    static IDisposable Lock(CliContext ctx, string profile)
    {
        var dir = Path.Combine(Path.GetDirectoryName(ctx.Profiles.ConfigPath)!, "state");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"login-{profile}.lock");
        for (var i = 0; ; i++)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose); }
            catch (IOException) when (i < 100) { Thread.Sleep(100); }
        }
    }

    static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    static async Task Reply(HttpListenerResponse response, string text)
    {
        var bytes = Encoding.UTF8.GetBytes($"<!doctype html><meta charset=utf-8><title>ino</title><p style=\"font:16px system-ui;margin:48px\">{WebUtility.HtmlEncode(text)}</p>");
        response.ContentType = "text/html; charset=utf-8";
        response.Headers["Content-Security-Policy"] = "default-src 'none'; style-src 'unsafe-inline'";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
        response.Close();
    }

    static Task OpenSystemBrowser(Uri url)
    {
        // AbsoluteUri keeps the escaping (Uri.ToString() turns %20 back into spaces, and the address then splits into several arguments),
        // and the address is one argument, never parsed by a command line.
        var target = url.AbsoluteUri;
        var psi = OperatingSystem.IsMacOS() ? new System.Diagnostics.ProcessStartInfo("open") : OperatingSystem.IsWindows() ? new System.Diagnostics.ProcessStartInfo("rundll32") : new System.Diagnostics.ProcessStartInfo("xdg-open");
        if (OperatingSystem.IsWindows()) psi.ArgumentList.Add("url.dll,FileProtocolHandler");
        psi.ArgumentList.Add(target);
        psi.UseShellExecute = false;
        try { System.Diagnostics.Process.Start(psi)?.Dispose(); }
        catch (System.ComponentModel.Win32Exception) { }   // no browser: the URL is printed above
        return Task.CompletedTask;
    }
}
