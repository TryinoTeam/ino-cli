using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Ino.Cli.Tests;

/// <summary>MCP/CLI P0c.1c: `ino login / login status / logout` against a fake Ino Auth (docs/MCP_CLI_P0C.md).</summary>
public class LoginTests
{
    static string Jwt(object claims) => "e30." + Convert.ToBase64String(Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(claims))).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".sig";

    sealed class FakeAuth
    {
        public string? Challenge;
        public int Refreshes;
        public readonly List<string> Revoked = [];
        public readonly List<string> Managed = [];
        public bool ManageDown;
        public int KeyCalls, FailFirstKeyCalls, UnavailableFirstKeyCalls;
        public readonly List<string?> IdempotencyKeys = [];
        public string? LastKeyBody;
        public RouteHandler Handler => new((req, body) =>
        {
            var form = System.Web.HttpUtility.ParseQueryString(Encoding.UTF8.GetString(body));
            var token = Jwt(new { ino_tenant = "tacme001", ino_env = "test", name = "Dana Whitfield", oid = "11111111-2222-3333-4444-555555555555", scope = "offline_access integration:read", exp = 1900000000 });
            switch (req.RequestUri!.AbsolutePath)
            {
                case "/oauth/token" when form["grant_type"] == "authorization_code":
                    var expected = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"] ?? ""))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
                    if (form["code"] != "code-1" || expected != Challenge || form["client_id"] != "ino-cli") return RouteHandler.Json("""{"error":"invalid_grant"}""", HttpStatusCode.BadRequest);
                    return RouteHandler.Json($$"""{"access_token":"{{token}}","refresh_token":"rt-1","token_type":"Bearer","expires_in":1800}""");
                case "/oauth/token" when form["grant_type"] == "refresh_token":
                    Refreshes++;
                    return form["refresh_token"] == $"rt-{Refreshes}" ? RouteHandler.Json($$"""{"access_token":"{{token}}","refresh_token":"rt-{{Refreshes + 1}}"}""")
                        : RouteHandler.Json("""{"error":"invalid_grant"}""", HttpStatusCode.BadRequest);
                case "/oauth/revoke":
                    Revoked.Add(form["token"]!);
                    return new HttpResponseMessage(HttpStatusCode.OK);
                case var path when path.StartsWith("/t/tabc1234/manage/v1/", StringComparison.Ordinal):
                    if (req.Headers.Authorization?.Parameter != token) return RouteHandler.Json("""{"error":"invalid_token"}""", HttpStatusCode.Unauthorized);
                    if (ManageDown) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                    Managed.Add($"{req.Method} {path["/t/tabc1234/manage/v1/".Length..]}");
                    if (path.EndsWith("/api-keys", StringComparison.Ordinal) && req.Method == HttpMethod.Post)
                    {
                        KeyCalls++;
                        IdempotencyKeys.Add(req.Headers.TryGetValues("Idempotency-Key", out var ik) ? ik.Single() : null);
                        LastKeyBody = Encoding.UTF8.GetString(body);
                        if (KeyCalls <= FailFirstKeyCalls) throw new HttpRequestException("connection reset");
                        if (KeyCalls <= UnavailableFirstKeyCalls) return RouteHandler.Json("""{"error":"temporarily_unavailable","error_description":"busy"}""", HttpStatusCode.ServiceUnavailable);
                        return RouteHandler.Json("""{"data":{"id":"5c1d0000-0000-0000-0000-000000000001","channel":"MODERN_ECOM","keyPrefix":"ik_live_ab1","apiKey":"ik_live_ab1cdefghijklmnopqrstuvwxyz12","secret":"s3cr3t-value-0001"}}""");
                    }
                    if (path.EndsWith("/api-keys", StringComparison.Ordinal))
                        return RouteHandler.Json("""{"data":{"keys":[{"keyPrefix":"ik_live_ab1","channel":"MODERN_ECOM","status":"ACTIVE","name":"POS"}]}}""");
                    if (path.EndsWith("/channels", StringComparison.Ordinal))
                        return RouteHandler.Json("""{"data":{"channels":[{"code":"MODERN_ECOM","hasMapping":true,"activeKeys":1,"requests24h":12,"failed24h":0}]}}""");
                    return path.EndsWith("/sessions", StringComparison.Ordinal)
                        ? RouteHandler.Json("""{"data":[{"id":"9a8b7c6d-0000-0000-0000-000000000001","current":true,"started":"2026-11-24T10:00:00Z","lastRefresh":"2026-11-24T12:00:00Z","expiresIfUnused":"2026-12-08T12:00:00Z","scopes":["docs:read"]}]}""")
                        : RouteHandler.Json(path.EndsWith("end-mine", StringComparison.Ordinal) ? """{"data":{"ended":3}}""" : """{"data":{"ended":1}}""");
                default: return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        });
    }

    static async Task<Harness> Profile()
    {
        var h = new Harness();
        await h.AddProfile();   // base URL https://engine.example.test/t/tabc1234 in the fakes; login uses its tenant part
        return h;
    }

    /// <summary>A browser that checks the authorize request and lands on the CLI's loopback listener with a code (or an error).</summary>
    static Func<Uri, Task> Browser(FakeAuth auth, string? error = null, bool wrongState = false) => async url =>
    {
        var q = System.Web.HttpUtility.ParseQueryString(url.Query);
        Assert.Equal("https://engine.example.test/oauth/authorize", url.GetLeftPart(UriPartial.Path));
        Assert.Equal("ino-cli", q["client_id"]);
        Assert.Equal("S256", q["code_challenge_method"]);
        Assert.Equal("tabc1234", q["ino_tenant"]);
        Assert.Matches(@"^http://127\.0\.0\.1:\d+/callback$", q["redirect_uri"]);
        Assert.Contains("offline_access", q["scope"]);
        Assert.DoesNotContain(' ', url.AbsoluteUri);   // the address handed to the browser keeps %20 (2026-11-24: `open` got it split at the spaces)
        auth.Challenge = q["code_challenge"];
        var state = wrongState ? "other" : q["state"];
        var back = error is null ? $"{q["redirect_uri"]}?code=code-1&state={state}" : $"{q["redirect_uri"]}?error={error}&state={state}";
        _ = Task.Run(async () => { using var c = new HttpClient(); await c.GetAsync(back); });
        await Task.CompletedTask;
    };

    [Fact]
    public async Task Login_with_PKCE_keeps_only_the_refresh_token_and_status_rotates_it()
    {
        using var h = await Profile();
        var auth = new FakeAuth();
        h.Browser = Browser(auth); h.Auth = auth.Handler;
        var (code, output, err) = await h.Run("login");
        Assert.True(code == ExitCodes.Ok, err);
        Assert.Contains("Dana Whitfield · tenant tacme001 (test)", output);
        Assert.Equal("rt-1", h.Secrets.Items["login:dev"].Secret);
        Assert.DoesNotContain(h.Secrets.Items.Values, c => c.Secret.StartsWith("e30.", StringComparison.Ordinal));   // no access token stored

        Assert.Equal(ExitCodes.Ok, (await h.Run("login", "status")).Code);
        Assert.Equal("rt-2", h.Secrets.Items["login:dev"].Secret);   // rotated

        var (outCode, outText, _) = await h.Run("logout");
        Assert.Equal(ExitCodes.Ok, outCode);
        Assert.Contains("Signed out", outText);
        Assert.Equal(["POST sessions/end-this"], auth.Managed);   // P0c.4: the session itself ends at Ino, not only its refresh token
        Assert.Equal(["rt-3"], auth.Revoked);
        Assert.False(h.Secrets.Items.ContainsKey("login:dev"));
        Assert.Equal(ExitCodes.Auth, (await h.Run("login", "status")).Code);
    }

    [Fact]
    public async Task Logout_all_ends_every_session_of_the_person_and_sessions_lists_them()
    {
        using var h = await Profile();
        var auth = new FakeAuth();
        h.Browser = Browser(auth); h.Auth = auth.Handler;
        Assert.Equal(ExitCodes.Ok, (await h.Run("login")).Code);
        var (lc, list, lerr) = await h.Run("sessions");
        Assert.True(lc == ExitCodes.Ok, lerr);
        Assert.Contains("* 9a8b7c6d-0000-0000-0000-000000000001", list);
        var (code, text, err) = await h.Run("logout", "--all");
        Assert.True(code == ExitCodes.Ok, err);
        Assert.Contains("3 session(s)", text);
        Assert.Equal(["GET sessions", "POST sessions/end-mine"], auth.Managed);
        Assert.False(h.Secrets.Items.ContainsKey("login:dev"));
    }

    [Fact]
    public async Task Logout_all_with_an_ended_sign_in_says_how_to_reach_the_other_sessions()
    {
        using var h = await Profile();
        var auth = new FakeAuth();
        h.Browser = Browser(auth); h.Auth = auth.Handler;
        Assert.Equal(ExitCodes.Ok, (await h.Run("login")).Code);
        auth.Refreshes = 5;                                 // the stored refresh token is no longer accepted (revoked by an admin)
        var (code, _, err) = await h.Run("logout", "--all");
        Assert.Equal(ExitCodes.Auth, code);
        Assert.Contains("Run `ino login`, then `ino logout --all`", err);
        Assert.Empty(auth.Managed);
        Assert.False(h.Secrets.Items.ContainsKey("login:dev"));
        // A plain logout in the same state still cleans up locally.
        auth.Refreshes = 0;
        Assert.Equal(ExitCodes.Ok, (await h.Run("login")).Code);
        auth.Refreshes = 5;
        var (c2, t2, _) = await h.Run("logout");
        Assert.Equal(ExitCodes.Ok, c2);
        Assert.Contains("already ended", t2);
        Assert.False(h.Secrets.Items.ContainsKey("login:dev"));
    }

    [Fact]
    public async Task Logout_all_that_fails_keeps_a_usable_rotated_token_so_a_retry_works()
    {
        using var h = await Profile();
        var auth = new FakeAuth();
        h.Browser = Browser(auth); h.Auth = auth.Handler;
        Assert.Equal(ExitCodes.Ok, (await h.Run("login")).Code);
        auth.ManageDown = true;
        Assert.NotEqual(ExitCodes.Ok, (await h.Run("logout", "--all")).Code);
        Assert.Equal("rt-2", h.Secrets.Items["login:dev"].Secret);   // the rotated token, not the redeemed rt-1
        auth.ManageDown = false;
        var (code, text, err) = await h.Run("logout", "--all");
        Assert.True(code == ExitCodes.Ok, err);
        Assert.Contains("3 session(s)", text);
    }

    [Fact]
    public async Task A_refused_sign_in_or_a_foreign_state_stores_nothing()
    {
        using var h = await Profile();
        var auth = new FakeAuth();
        h.Browser = Browser(auth, error: "access_denied"); h.Auth = auth.Handler;
        Assert.Equal(ExitCodes.Auth, (await h.Run("login")).Code);
        Assert.False(h.Secrets.Items.ContainsKey("login:dev"));

        h.Browser = Browser(auth, wrongState: true);
        var (code, _, err) = await h.Run("login");
        Assert.Equal(ExitCodes.Auth, code);
        Assert.Contains("not for this request", err);
        Assert.False(h.Secrets.Items.ContainsKey("login:dev"));
    }

    [Theory]
    [InlineData("https://api.inoloyalty.com/t/tacme001", "https://api.inoloyalty.com/oauth/", "tacme001")]
    [InlineData("https://api.inoloyalty.com/t/tacme001/", "https://api.inoloyalty.com/oauth/", "tacme001")]
    public void The_tenant_and_issuer_come_from_the_profile_address(string baseUrl, string issuer, string tenant)
    {
        var t = InoLogin.TargetOf(baseUrl);
        Assert.Equal(issuer, t.Issuer.ToString());
        Assert.Equal(tenant, t.Tenant);
    }

    [Theory]
    [InlineData("http://api.inoloyalty.com/t/tacme001")]
    [InlineData("https://dedicated.example.com")]
    public void Sign_in_needs_an_https_Ino_Cloud_tenant_address(string baseUrl) =>
        Assert.Equal(ExitCodes.Usage, Assert.Throws<CliException>(() => InoLogin.TargetOf(baseUrl)).ExitCode);

    // MCP/CLI P0c.3: keys and channels through the Management API.
    async Task<(Harness H, FakeAuth Auth)> SignedIn()
    {
        var h = await Profile();
        var auth = new FakeAuth();
        h.Browser = Browser(auth); h.Auth = auth.Handler;
        Assert.Equal(ExitCodes.Ok, (await h.Run("login")).Code);
        return (h, auth);
    }

    [Fact]
    public async Task Keys_create_needs_a_place_for_the_secret_and_never_prints_it_unless_asked()
    {
        var (h, auth) = await SignedIn();
        using var _ = h;
        var (code, _, err) = await h.Run("keys", "create", "--channel", "MODERN_ECOM", "--name", "POS", "--scopes", "transaction.create");
        Assert.Equal(ExitCodes.Usage, code);
        Assert.Contains("only once", err);
        Assert.Equal(0, auth.KeyCalls);                     // refused before anything is issued

        var (c2, out2, _) = await h.Run("--json", "keys", "create", "--channel", "MODERN_ECOM", "--name", "POS", "--scopes", "transaction.create,member.get", "--save-profile", "pos-test");
        Assert.Equal(ExitCodes.Ok, c2);
        Assert.DoesNotContain("s3cr3t", out2);
        Assert.DoesNotContain("ik_live_ab1cdefghijklmnopqrstuvwxyz12", out2);
        Assert.Equal(new Credentials("ik_live_ab1cdefghijklmnopqrstuvwxyz12", "s3cr3t-value-0001"), h.Secrets.Read("pos-test"));
        var sent = JsonNode.Parse(auth.LastKeyBody!)!;
        Assert.Equal("transaction.create", sent["scopes"]![0]!.GetValue<string>());
        Assert.False(sent["fullAccess"]!.GetValue<bool>());

        var (c3, out3, _) = await h.Run("keys", "create", "--channel", "MODERN_ECOM", "--name", "POS", "--full-access", "--show-secret");
        Assert.Equal(ExitCodes.Ok, c3);
        Assert.Contains("s3cr3t-value-0001", out3);
    }

    [Fact]
    public async Task Keys_create_retries_a_dropped_connection_with_the_same_idempotency_key()
    {
        var (h, auth) = await SignedIn();
        using var _ = h;
        auth.FailFirstKeyCalls = 1;
        var (code, _, err) = await h.Run("keys", "create", "--channel", "MODERN_ECOM", "--name", "POS", "--scopes", "transaction.create", "--save-profile", "pos-a");
        Assert.True(code == ExitCodes.Ok, err);
        Assert.Equal(2, auth.KeyCalls);
        Assert.NotNull(auth.IdempotencyKeys[0]);
        Assert.Equal(auth.IdempotencyKeys[0], auth.IdempotencyKeys[1]);
        // A new command is a new key request.
        Assert.Equal(ExitCodes.Ok, (await h.Run("keys", "create", "--channel", "MODERN_ECOM", "--name", "POS", "--scopes", "transaction.create", "--save-profile", "pos-b")).Code);
        Assert.NotEqual(auth.IdempotencyKeys[0], auth.IdempotencyKeys[2]);
    }

    [Fact]
    public async Task Env_file_is_new_and_private()
    {
        var (h, _) = await SignedIn();
        using var __ = h;
        var file = Path.Combine(h.Dir, "pos.env");
        Assert.Equal(ExitCodes.Ok, (await h.Run("keys", "create", "--channel", "MODERN_ECOM", "--name", "POS", "--scopes", "transaction.create", "--env-file", file)).Code);
        // The names the connector templates and the CLI's own environment override read (INO_HMAC_SECRET, not INO_API_SECRET).
        var lines = File.ReadAllLines(file).Select(l => l.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);
        Assert.Equal(["INO_BASE_URL", "INO_CHANNEL", "INO_ENVIRONMENT", "INO_API_KEY", "INO_HMAC_SECRET"], lines.Keys);
        Assert.Equal("s3cr3t-value-0001", lines["INO_HMAC_SECRET"]);
        Assert.Equal("MODERN_ECOM", lines["INO_CHANNEL"]);
        Assert.Equal("test", lines["INO_ENVIRONMENT"]);
        Assert.EndsWith("/t/tabc1234", lines["INO_BASE_URL"]);
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
        var (code, _, err) = await h.Run("keys", "create", "--channel", "MODERN_ECOM", "--name", "POS", "--scopes", "transaction.create", "--env-file", file);
        Assert.Equal(ExitCodes.Usage, code);
        Assert.Contains("exists", err);
    }

    [Fact]
    public async Task Channels_and_keys_list_read_as_the_signed_in_person()
    {
        var (h, auth) = await SignedIn();
        using var _ = h;
        var (c1, channels, _) = await h.Run("channels", "list");
        Assert.Equal(ExitCodes.Ok, c1);
        Assert.Contains("MODERN_ECOM", channels);
        var (c2, keys, _) = await h.Run("keys", "list");
        Assert.Equal(ExitCodes.Ok, c2);
        Assert.Contains("ik_live_ab1", keys);
        Assert.Equal(["GET channels", "GET api-keys"], auth.Managed);
    }

    [Fact]
    public async Task Keys_create_retries_5xx_with_the_same_key_and_says_when_the_outcome_is_unknown()
    {
        var (h, auth) = await SignedIn();
        using var _ = h;
        auth.UnavailableFirstKeyCalls = 1;
        Assert.Equal(ExitCodes.Ok, (await h.Run("keys", "create", "--channel", "MODERN_ECOM", "--name", "POS", "--scopes", "transaction.create", "--save-profile", "pos-a")).Code);
        Assert.Equal(auth.IdempotencyKeys[0], auth.IdempotencyKeys[1]);
        auth.KeyCalls = 0; auth.UnavailableFirstKeyCalls = 9;
        var (code, _, err) = await h.Run("keys", "create", "--channel", "MODERN_ECOM", "--name", "POS", "--scopes", "transaction.create", "--save-profile", "pos-b");
        Assert.Equal(ExitCodes.Network, code);
        Assert.Contains("ino keys list", err);
        Assert.Equal(3, auth.KeyCalls);
    }

    [Fact]
    public async Task Keys_create_checks_the_destinations_before_asking_for_a_key()
    {
        var (h, auth) = await SignedIn();
        using var _ = h;
        // The signed-in profile itself (or any existing one) is never overwritten.
        var (c1, _, e1) = await h.Run("keys", "create", "--channel", "MODERN_ECOM", "--name", "POS", "--scopes", "transaction.create", "--save-profile", "default");
        var existing = h.Store.Load().Profiles.Keys.First();
        var (c2, _, e2) = await h.Run("keys", "create", "--channel", "MODERN_ECOM", "--name", "POS", "--scopes", "transaction.create", "--save-profile", existing);
        Assert.Equal(ExitCodes.Usage, c2);
        Assert.Contains("exists", e2);
        var (c3, _, e3) = await h.Run("keys", "create", "--channel", "MODERN_ECOM", "--name", "POS", "--scopes", "transaction.create", "--env-file", Path.Combine(h.Dir, "no-such-folder", "pos.env"));
        Assert.Equal(ExitCodes.Usage, c3);
        Assert.Contains("does not exist", e3);
        Assert.Equal(c1 == ExitCodes.Ok ? 1 : 0, auth.KeyCalls);
    }
}
