using System.Net;
using System.Text;

namespace Ino.Cli.Tests;

/// <summary>`ino marketing list|connect|verify|disconnect` (MCP/CLI P1c.1, docs/MCP_CLI_P1C_LOG.md).</summary>
public class MarketingCommandTests
{
    static string Jwt(object claims) => "e30." + Convert.ToBase64String(Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(claims))).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".sig";

    const string Secret = "whsec_c2VjcmV0LXNlY3JldC1zZWNyZXQtc2VjcmV0LTAx";
    const string Id = "8b0e1d0c-0000-4000-8000-000000000001";

    sealed class Fake
    {
        public readonly List<(string Path, string? Key, string Body)> Posts = [];
        public int FailFirstConnects;
        public string VerifyAnswer = """{"data":{"connectionId":"8b0e1d0c-0000-4000-8000-000000000001","connected":true,"status":"CONNECTED","error":null,"capabilities":[]}}""";
        public RouteHandler Handler => new((req, body) =>
        {
            var form = System.Web.HttpUtility.ParseQueryString(Encoding.UTF8.GetString(body));
            var token = Jwt(new { ino_tenant = "tabc1234", ino_env = "test", name = "Dana Whitfield", scope = "offline_access integration:read integration:write", exp = 1900000000 });
            var path = req.RequestUri!.AbsolutePath;
            switch (path)
            {
                case "/oauth/token": return RouteHandler.Json($$"""{"access_token":"{{token}}","refresh_token":"rt-1"}""");
                case "/t/tabc1234/manage/v1/marketing/connections" when req.Method == HttpMethod.Get:
                    return RouteHandler.Json("""{"data":{"connections":[{"id":"8b0e1d0c-0000-4000-8000-000000000001","type":"CUSTOM","status":"PENDING_CONFIRM","name":"Our mailer","account":"mailer.example.com"}]}}""");
            }
            if (req.Method != HttpMethod.Post || !path.StartsWith("/t/tabc1234/manage/v1/marketing/connections", StringComparison.Ordinal)) return new HttpResponseMessage(HttpStatusCode.NotFound);
            Posts.Add((path, req.Headers.TryGetValues("Idempotency-Key", out var k) ? k.Single() : null, Encoding.UTF8.GetString(body)));
            if (path.EndsWith("/verify", StringComparison.Ordinal)) return RouteHandler.Json(VerifyAnswer);
            if (path.EndsWith("/disconnect", StringComparison.Ordinal)) return RouteHandler.Json("""{"data":{"connectionId":"8b0e1d0c-0000-4000-8000-000000000001","alreadyDisconnected":false}}""");
            if (Posts.Count <= FailFirstConnects) throw new HttpRequestException("connection reset");
            return RouteHandler.Json($$$"""{"data":{"connectionId":"{{{Id}}}","name":"Our mailer","host":"mailer.example.com","secret":"{{{Secret}}}","status":"PENDING_CONFIRM"}}""");
        });
        public Func<Uri, Task> Browser => async url =>
        {
            var q = System.Web.HttpUtility.ParseQueryString(url.Query);
            _ = Task.Run(async () => { using var c = new HttpClient(); await c.GetAsync($"{q["redirect_uri"]}?code=code-1&state={q["state"]}"); });
            await Task.CompletedTask;
        };
    }

    static async Task<(Harness H, Fake F)> SignedIn()
    {
        var h = new Harness();
        var f = new Fake();
        h.Browser = f.Browser; h.Auth = f.Handler;
        await h.AddProfile();
        Assert.Equal(ExitCodes.Ok, (await h.Run("--profile", "dev", "login")).Code);
        return (h, f);
    }

    [Fact]
    public async Task Connect_needs_a_place_for_the_secret_before_any_call()
    {
        var (h, f) = await SignedIn();
        using var hh = h;
        var (code, _, err) = await h.Run("--profile", "dev", "marketing", "connect", "--name", "Our mailer", "--url", "https://mailer.example.com/ino");
        Assert.Equal(ExitCodes.Usage, code);
        Assert.Contains("--env-file", err);
        var existing = Path.Combine(h.Dir, "taken.env");
        await File.WriteAllTextAsync(existing, "x");
        Assert.Equal(ExitCodes.Usage, (await h.Run("--profile", "dev", "marketing", "connect", "--name", "Our mailer", "--url", "https://mailer.example.com/ino", "--env-file", existing)).Code);
        Assert.Empty(f.Posts);
    }

    [Fact]
    public async Task Connect_retries_with_the_same_idempotency_key_and_writes_the_secret_only_to_the_env_file()
    {
        var (h, f) = await SignedIn();
        using var hh = h;
        f.FailFirstConnects = 1;
        var env = Path.Combine(h.Dir, "tool.env");
        var (code, output, err) = await h.Run("--profile", "dev", "marketing", "connect", "--name", "Our mailer", "--url", "https://mailer.example.com/ino", "--env-file", env);
        Assert.True(code == ExitCodes.Ok, err);
        Assert.Equal(2, f.Posts.Count);
        Assert.NotNull(f.Posts[0].Key);
        Assert.Equal(f.Posts[0].Key, f.Posts[1].Key);            // a retry gets the first answer, never a second connection
        Assert.Equal($"INO_MA_CONNECTION_ID={Id}\nINO_MA_SIGNING_SECRET={Secret}\n", await File.ReadAllTextAsync(env));
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(env));
        Assert.DoesNotContain(Secret, output + err);
        Assert.Contains($"ino marketing verify {Id}", output);
        // Gate K 3: the same command again sends the same key (the engine returns the first connection, never a second one).
        Assert.Equal(ExitCodes.Ok, (await h.Run("--profile", "dev", "marketing", "connect", "--name", "Our mailer", "--url", "https://mailer.example.com/ino", "--show-secret")).Code);
        Assert.Equal(f.Posts[0].Key, f.Posts[^1].Key);
    }

    [Fact]
    public async Task Verify_reports_a_failed_check_with_a_hint_and_disconnect_asks_for_yes()
    {
        var (h, f) = await SignedIn();
        using var hh = h;
        f.VerifyAnswer = """{"data":{"connectionId":"8b0e1d0c-0000-4000-8000-000000000001","connected":false,"status":"PENDING_CONFIRM","error":"PROOF_MISMATCH","capabilities":[]}}""";
        var (code, _, err) = await h.Run("--profile", "dev", "marketing", "verify", Id);
        Assert.Equal(ExitCodes.Business, code);
        Assert.Contains("PROOF_MISMATCH", err);
        Assert.Contains("HMAC-SHA256", err);

        Assert.Equal(ExitCodes.Usage, (await h.Run("--profile", "dev", "marketing", "disconnect", Id)).Code);
        Assert.DoesNotContain(f.Posts, p => p.Path.EndsWith("/disconnect", StringComparison.Ordinal));
        Assert.Equal(ExitCodes.Ok, (await h.Run("--profile", "dev", "marketing", "disconnect", Id, "--yes")).Code);
        var (_, list, _) = await h.Run("--profile", "dev", "marketing", "list");
        Assert.Contains("CUSTOM", list);
        Assert.Contains("mailer.example.com", list);
    }
}
