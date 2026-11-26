using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Ino.Cli.Tests;

/// <summary>MCP/CLI login-only profile (docs/MCP_CLI_P0C.md §2.5, L1–L6): sign in with an address only, no channel key.</summary>
public class LoginOnlyProfileTests
{
    const string Base = "https://engine.example.test/t/tabc1234";

    static string Jwt(object claims) => "e30." + Convert.ToBase64String(Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(claims))).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".sig";

    sealed class FakeAuth
    {
        public string? Challenge;
        public int BrowserOpened;
        public readonly List<string> Managed = [];
        public readonly List<string> Revoked = [];
        public int Codes;
        public RouteHandler Handler => new((req, body) =>
        {
            var form = System.Web.HttpUtility.ParseQueryString(Encoding.UTF8.GetString(body));
            var token = Jwt(new { ino_tenant = "tabc1234", ino_env = "test", name = "Dana Whitfield", oid = "11111111-2222-3333-4444-555555555555", scope = "offline_access integration:read", exp = 1900000000 });
            switch (req.RequestUri!.AbsolutePath)
            {
                case "/oauth/token" when form["grant_type"] == "authorization_code":
                    var expected = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"] ?? ""))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
                    Codes++;
                    return expected == Challenge ? RouteHandler.Json($$"""{"access_token":"{{token}}","refresh_token":"rt-{{(Codes == 1 ? "1" : "c" + Codes)}}"}""") : RouteHandler.Json("""{"error":"invalid_grant"}""", HttpStatusCode.BadRequest);
                case "/oauth/token":
                    return RouteHandler.Json($$"""{"access_token":"{{token}}","refresh_token":"rt-2"}""");
                case "/oauth/revoke":
                    Revoked.Add(form["token"]!);
                    return new HttpResponseMessage(HttpStatusCode.OK);
                case var path when path.StartsWith("/t/tabc1234/manage/v1/", StringComparison.Ordinal):
                    Managed.Add(path["/t/tabc1234/manage/v1/".Length..]);
                    if (path.EndsWith("/api-keys", StringComparison.Ordinal))
                        return RouteHandler.Json("""{"data":{"id":"5c1d0000-0000-0000-0000-000000000001","channel":"MODERN_ECOM","keyPrefix":"ik_test_ab1","apiKey":"ik_test_ab1cdefghijklmnopqrstuvwxyz12","secret":"s3cr3t-value-0001"}}""");
                    return path.EndsWith("/channels", StringComparison.Ordinal)
                        ? RouteHandler.Json("""{"data":{"channels":[{"code":"MODERN_ECOM","hasMapping":true,"activeKeys":1,"requests24h":12,"failed24h":0}]}}""")
                        : RouteHandler.Json("""{"data":{"ended":1}}""");
                default: return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        });

        public Func<Uri, Task> Browser(string? error = null) => async url =>
        {
            BrowserOpened++;
            var q = System.Web.HttpUtility.ParseQueryString(url.Query);
            Assert.Equal("https://engine.example.test/oauth/authorize", url.GetLeftPart(UriPartial.Path));
            Assert.Equal("tabc1234", q["ino_tenant"]);
            Challenge = q["code_challenge"];
            var back = error is null ? $"{q["redirect_uri"]}?code=code-1&state={q["state"]}" : $"{q["redirect_uri"]}?error={error}&state={q["state"]}";
            _ = Task.Run(async () => { using var c = new HttpClient(); await c.GetAsync(back); });
            await Task.CompletedTask;
        };
    }

    static (Harness H, FakeAuth Auth) New()
    {
        var h = new Harness();
        var auth = new FakeAuth();
        h.Browser = auth.Browser(); h.Auth = auth.Handler;
        return (h, auth);
    }

    [Fact]
    public async Task L1_login_with_an_address_creates_a_login_only_profile_named_after_the_tenant()
    {
        var (h, auth) = New();
        using var _ = h;
        var (code, output, err) = await h.Run("login", "--base-url", Base + "/");
        Assert.True(code == ExitCodes.Ok, err);
        Assert.Contains("Dana Whitfield", output);
        var cfg = h.Store.Load();
        var p = cfg.Profiles["tabc1234"];
        Assert.True(p.LoginOnly);
        Assert.Equal(Base, p.BaseUrl);
        Assert.Equal("", p.Channel);
        Assert.Null(p.KeyHint);
        Assert.Equal("", p.Environment);   // the token's ino_env is shown by `ino login status`, never stored
        Assert.Equal("tabc1234", cfg.DefaultProfile);
        Assert.Equal("rt-1", h.Secrets.Items["login:tabc1234"].Secret);
        Assert.False(h.Secrets.Items.ContainsKey("tabc1234"));   // no channel credentials

        var (_, list, _) = await h.Run("profile", "list");
        Assert.Contains("login only", list);
        var (lc, channels, lerr) = await h.Run("channels", "list");
        Assert.True(lc == ExitCodes.Ok, lerr);
        Assert.Contains("MODERN_ECOM", channels);
    }

    [Fact]
    public async Task L1_the_profile_flag_names_it_and_a_second_login_reuses_it()
    {
        var (h, auth) = New();
        using var _ = h;
        Assert.Equal(ExitCodes.Ok, (await h.Run("--profile", "acme-it", "login", "--base-url", Base)).Code);
        Assert.True(h.Store.Load().Profiles["acme-it"].LoginOnly);
        Assert.Equal(ExitCodes.Ok, (await h.Run("--profile", "acme-it", "login", "--base-url", Base)).Code);
        Assert.Equal(ExitCodes.Ok, (await h.Run("--profile", "acme-it", "login")).Code);
        Assert.Single(h.Store.Load().Profiles);
    }

    [Fact]
    public async Task L2_another_address_on_an_existing_profile_is_refused_and_nothing_changes()
    {
        var (h, auth) = New();
        using var _ = h;
        await h.AddProfile();   // dev → https://engine.example.test/t/tabc1234
        var before = File.ReadAllText(h.ConfigPath);
        var (code, _, err) = await h.Run("--profile", "dev", "login", "--base-url", "https://evil.example.test/t/tabc1234");
        Assert.Equal(ExitCodes.Usage, code);
        Assert.Contains("is for https://engine.example.test/t/tabc1234, not", err);
        Assert.Equal(0, auth.BrowserOpened);
        Assert.Equal(before, File.ReadAllText(h.ConfigPath));
        Assert.False(h.Secrets.Items.ContainsKey("login:dev"));

        // The same address on a channel profile just signs in; the profile stays a channel profile.
        Assert.Equal(ExitCodes.Ok, (await h.Run("--profile", "dev", "login", "--base-url", Base)).Code);
        Assert.False(h.Store.Load().Profiles["dev"].LoginOnly);
        Assert.Equal("MODERN_ECOM", h.Store.Load().Profiles["dev"].Channel);
    }

    [Theory]
    [InlineData("events", "list")]
    [InlineData("listen")]
    [InlineData("webhooks", "list")]
    [InlineData("mcp", "serve")]
    [InlineData("agent", "setup", "--client", "cursor", "--dry-run")]
    public async Task L3_channel_commands_stop_before_any_request_on_a_login_only_profile(params string[] args)
    {
        var (h, _) = New();
        using var __ = h;
        Assert.Equal(ExitCodes.Ok, (await h.Run("login", "--base-url", Base)).Code);
        var (code, _, err) = await h.Run(args);
        Assert.Equal(ExitCodes.Usage, code);
        Assert.Contains("for signing in only", err);
        Assert.Contains("ino keys create", err);
        Assert.Empty(h.Http.Seen);
    }

    [Fact]
    public async Task L3_doctor_says_the_profile_is_login_only()
    {
        var (h, _) = New();
        using var __ = h;
        Assert.Equal(ExitCodes.Ok, (await h.Run("login", "--base-url", Base)).Code);
        var (code, output, _) = await h.Run("doctor");
        Assert.Equal(ExitCodes.Ok, code);
        Assert.Contains("login only", output);
        Assert.Empty(h.Http.Seen);
    }

    [Fact]
    public async Task L5_profile_remove_also_removes_the_sign_in()
    {
        var (h, auth) = New();
        using var __ = h;
        await h.AddProfile();
        Assert.Equal(ExitCodes.Ok, (await h.Run("--profile", "dev", "login")).Code);
        Assert.True(h.Secrets.Items.ContainsKey("login:dev"));
        Assert.Equal(ExitCodes.Ok, (await h.Run("profile", "remove", "dev")).Code);
        Assert.False(h.Secrets.Items.ContainsKey("login:dev"));
        Assert.Contains("sessions/end-this", auth.Managed);   // ended at Ino too, not only removed here
        Assert.NotEmpty(auth.Revoked);
    }

    [Theory]
    [InlineData("http://engine.example.test/t/tabc1234")]
    [InlineData("https://engine.example.test/")]
    [InlineData("https://engine.example.test/t/tabc1234?x=1")]
    public async Task L6_a_bad_address_is_refused_before_anything_is_written(string url)
    {
        var (h, auth) = New();
        using var _ = h;
        var (code, _, _) = await h.Run("login", "--base-url", url);
        Assert.Equal(ExitCodes.Usage, code);
        Assert.Equal(0, auth.BrowserOpened);
        Assert.False(File.Exists(h.ConfigPath));
        Assert.Empty(h.Secrets.Items);
    }

    [Fact]
    public async Task L6_a_bad_profile_name_is_refused()
    {
        var (h, auth) = New();
        using var _ = h;
        Assert.Equal(ExitCodes.Usage, (await h.Run("--profile", "a:b", "login", "--base-url", Base)).Code);
        Assert.Equal(0, auth.BrowserOpened);
    }

    [Fact]
    public async Task L6_a_failed_sign_in_leaves_no_new_profile_but_keeps_an_existing_one()
    {
        var (h, auth) = New();
        using var _ = h;
        h.Browser = auth.Browser(error: "access_denied");
        Assert.Equal(ExitCodes.Auth, (await h.Run("login", "--base-url", Base)).Code);
        Assert.Empty(h.Store.Load().Profiles);
        Assert.Empty(h.Secrets.Items);

        h.Browser = auth.Browser();
        Assert.Equal(ExitCodes.Ok, (await h.Run("login", "--base-url", Base)).Code);
        h.Browser = auth.Browser(error: "access_denied");
        Assert.Equal(ExitCodes.Auth, (await h.Run("login", "--base-url", Base)).Code);
        Assert.True(h.Store.Load().Profiles["tabc1234"].LoginOnly);
        Assert.Equal("rt-1", h.Secrets.Items["login:tabc1234"].Secret);   // the earlier sign-in is untouched
    }

    [Fact]
    public async Task Environment_variables_and_project_files_do_not_create_profiles()
    {
        var (h, auth) = New();
        using var _ = h;
        h.Env["INO_PROFILE"] = "from-env";
        Directory.CreateDirectory(Path.Combine(h.Dir, ".ino"));
        File.WriteAllText(Path.Combine(h.Dir, ".ino", "config.json"), """{"profile":"from-repo"}""");
        Assert.Equal(ExitCodes.Ok, (await h.Run("login", "--base-url", Base)).Code);
        Assert.Equal(["tabc1234"], h.Store.Load().Profiles.Keys);
    }

    [Fact]
    public async Task B2_the_sign_in_is_never_sent_to_another_address()
    {
        var (h, auth) = New();
        using var _ = h;
        await h.AddProfile();
        Assert.Equal(ExitCodes.Ok, (await h.Run("--profile", "dev", "login")).Code);
        Assert.StartsWith("ino-auth|https://engine.example.test/oauth/|tabc1234", h.Secrets.Items["login:dev"].ApiKey);

        // Replacing the profile with another address drops the sign-in and the listen secret.
        await h.RunWithInput("ik_x_9f8e7d6c5b4a\ns3cr3t-value-123\n", "profile", "add", "dev", "--base-url", "https://other.example.test/t/tabc1234", "--channel", "MODERN_ECOM", "--environment", "test");
        Assert.False(h.Secrets.Items.ContainsKey("login:dev"));

        // A token saved for another address (or before the binding) is refused without any request.
        h.Secrets.Items["login:dev"] = new Credentials("ino-auth", "rt-old");
        var before = ((RouteHandler)h.Auth!).Seen.Count;
        var (code, _, err) = await h.Run("--profile", "dev", "sessions");
        Assert.Equal(ExitCodes.Auth, code);
        Assert.Contains("not for this profile's address", err);
        Assert.Equal(before, ((RouteHandler)h.Auth!).Seen.Count);
        Assert.Equal(ExitCodes.Ok, (await h.Run("--profile", "dev", "logout")).Code);   // removed here, nothing sent
        Assert.False(h.Secrets.Items.ContainsKey("login:dev"));
        Assert.Equal(before, ((RouteHandler)h.Auth!).Seen.Count);
    }

    [Fact]
    public async Task M1_a_profile_an_older_cli_saved_without_the_flag_is_still_login_only()
    {
        var (h, _) = New();
        using var __ = h;
        Directory.CreateDirectory(Path.GetDirectoryName(h.ConfigPath)!);
        File.WriteAllText(h.ConfigPath, """{"version":1,"defaultProfile":"old","profiles":{"old":{"baseUrl":"BASE","channel":"","environment":""}}}""".Replace("BASE", Base));
        var (code, _, err) = await h.Run("events", "list");
        Assert.Equal(ExitCodes.Usage, code);
        Assert.Contains("for signing in only", err);
    }

    [Fact]
    public async Task M4_a_saved_channel_profile_replaces_a_login_only_default()
    {
        var (h, _) = New();
        using var __ = h;
        Assert.Equal(ExitCodes.Ok, (await h.Run("login", "--base-url", Base)).Code);
        var (code, _, err) = await h.Run("keys", "create", "--channel", "MODERN_ECOM", "--name", "POS", "--full-access", "--save-profile", "pos-test");
        Assert.True(code == ExitCodes.Ok, err);
        Assert.Equal("pos-test", h.Store.Load().DefaultProfile);
    }

    [Fact]
    public async Task A_second_login_ends_the_sign_in_it_replaces()
    {
        var (h, auth) = New();
        using var _ = h;
        Assert.Equal(ExitCodes.Ok, (await h.Run("login", "--base-url", Base)).Code);
        Assert.Equal(ExitCodes.Ok, (await h.Run("login")).Code);
        Assert.Equal(["rt-1"], auth.Revoked);
        Assert.Equal("rt-c2", h.Secrets.Items["login:tabc1234"].Secret);
    }

    [Fact]
    public async Task A_sign_in_that_cannot_be_kept_is_ended_at_Ino()
    {
        var (h, auth) = New();
        using var _ = h;
        Assert.Equal(ExitCodes.Ok, (await h.Run("login", "--base-url", Base)).Code);
        // Another process points the profile elsewhere while the browser is open: the new token is revoked, nothing is saved.
        h.Browser = async url =>
        {
            File.WriteAllText(h.ConfigPath, File.ReadAllText(h.ConfigPath).Replace("engine.example.test", "other.example.test"));
            await auth.Browser()(url);
        };
        Assert.Equal(ExitCodes.Usage, (await h.Run("login", "--base-url", Base)).Code);
        Assert.Contains("rt-c2", auth.Revoked);
        Assert.Equal("rt-1", h.Secrets.Items["login:tabc1234"].Secret);
    }

    [Fact]
    public async Task A_tenant_code_that_is_not_a_profile_name_asks_for_one()
    {
        var (h, auth) = New();
        using var _ = h;
        var (code, _, err) = await h.Run("login", "--base-url", "https://engine.example.test/t/acme.co");
        Assert.Equal(ExitCodes.Usage, code);
        Assert.Contains("--profile", err);
        Assert.Equal(0, auth.BrowserOpened);
    }
}
