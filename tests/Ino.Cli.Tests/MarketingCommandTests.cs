using System.Net;
using System.Text;

namespace Ino.Cli.Tests;

/// <summary>`ino marketing list|connect|verify|disconnect` (MCP/CLI P1c.1, docs/MCP_CLI_P1C_LOG.md).</summary>
public class MarketingCommandTests
{
    static string Jwt(object claims) => "e30." + Convert.ToBase64String(Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(claims))).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".sig";

    const string Secret = "whsec_c2VjcmV0LXNlY3JldC1zZWNyZXQtc2VjcmV0LTAx";
    const string Id = "8b0e1d0c-0000-4000-8000-000000000001";
    const string ActionToken = "ino_mat_c2VjcmV0LWFjdGlvbi10b2tlbi1zZWNyZXQtYWN0aW9u";
    const string NewSecret = "whsec_bmV3LXNlY3JldC1uZXctc2VjcmV0LW5ldy1zZWNyZXQtMDI=";

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
                case "/t/tabc1234/manage/v1/marketing/connections/8b0e1d0c-0000-4000-8000-000000000001/actions" when req.Method == HttpMethod.Get:
                    return RouteHandler.Json("""{"data":{"connectionId":"8b0e1d0c-0000-4000-8000-000000000001","status":"CONNECTED","token":{"createdAt":"2026-11-26T10:00:00Z","dailyCap":5000,"memberDailyCap":20},"endpoint":"https://api.example.test/t/tabc1234/ma/actions/8b0e1d0c-0000-4000-8000-000000000001/{action}","today":{"accepted":3,"refused":1},"actions":[{"key":"survey.completed","name":"Survey completed","enabled":true,"memberDailyCap":1,"connectionDailyCap":1000,"version":2,"today":{"accepted":3,"refused":1}}]}}""");
                case "/t/tabc1234/manage/v1/marketing/connections/8b0e1d0c-0000-4000-8000-000000000001/actions/survey.completed" when req.Method == HttpMethod.Put:
                    Posts.Add((path, null, Encoding.UTF8.GetString(body)));
                    return RouteHandler.Json("""{"data":{"connectionId":"8b0e1d0c-0000-4000-8000-000000000001","changed":true,"item":"survey.completed","version":3,"enabled":false}}""");
                case "/t/tabc1234/manage/v1/marketing/connections" when req.Method == HttpMethod.Get:
                    return RouteHandler.Json("""{"data":{"connections":[{"id":"8b0e1d0c-0000-4000-8000-000000000001","type":"CUSTOM","status":"PENDING_CONFIRM","name":"Our mailer","account":"mailer.example.com"}]}}""");
            }
            if (req.Method != HttpMethod.Post || !path.StartsWith("/t/tabc1234/manage/v1/marketing/connections", StringComparison.Ordinal)) return new HttpResponseMessage(HttpStatusCode.NotFound);
            Posts.Add((path, req.Headers.TryGetValues("Idempotency-Key", out var k) ? k.Single() : null, Encoding.UTF8.GetString(body)));
            if (path.EndsWith("/verify", StringComparison.Ordinal)) return RouteHandler.Json(VerifyAnswer);
            if (path.EndsWith("/disconnect", StringComparison.Ordinal)) return RouteHandler.Json("""{"data":{"connectionId":"8b0e1d0c-0000-4000-8000-000000000001","alreadyDisconnected":false}}""");
            if (Posts.Count <= FailFirstConnects) throw new HttpRequestException("connection reset");
            if (path.EndsWith("/action-token", StringComparison.Ordinal))
                return RouteHandler.Json($$$"""{"data":{"connectionId":"{{{Id}}}","token":"{{{ActionToken}}}","tokenId":"5d1e0000-0000-4000-8000-000000000009","renewed":true}}""");
            if (path.EndsWith("/rotate-secret", StringComparison.Ordinal))
                return RouteHandler.Json($$$"""{"data":{"connectionId":"{{{Id}}}","secret":"{{{NewSecret}}}","version":2,"previousValidUntil":"2026-11-27T10:00:00+00:00","status":"CONNECTED"}}""");
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

    [Fact]
    public async Task Rotate_secret_replaces_only_the_secret_line_uses_a_new_key_per_run_and_never_prints_it_unasked()
    {
        var (h, f) = await SignedIn();
        using var hh = h;
        var env = Path.Combine(h.Dir, "ma.env");
        await File.WriteAllTextAsync(env, $"INO_MA_CONNECTION_ID={Id}\nINO_MA_SIGNING_SECRET={Secret}\nPORT=8080\nexport INO_MA_SIGNING_SECRET={Secret}\n");
        Assert.Equal(ExitCodes.Usage, (await h.Run("--profile", "dev", "marketing", "rotate-secret", Id)).Code);          // no place for the secret
        Assert.Equal(ExitCodes.Usage, (await h.Run("--profile", "dev", "marketing", "rotate-secret", Id, "--env-file", env, "--overlap-hours", "200")).Code);
        Assert.Empty(f.Posts);
        f.FailFirstConnects = 1;      // the first attempt is lost: the retry carries the same Idempotency-Key
        var (code, output, err) = await h.Run("--profile", "dev", "marketing", "rotate-secret", Id, "--env-file", env);
        Assert.True(code == ExitCodes.Ok, err);
        Assert.Equal(2, f.Posts.Count);
        Assert.Equal(f.Posts[0].Key, f.Posts[1].Key);
        Assert.Contains("\"overlapHours\":24", f.Posts[1].Body);
        Assert.Equal([$"INO_MA_CONNECTION_ID={Id}", $"INO_MA_SIGNING_SECRET={NewSecret}", "PORT=8080"], await File.ReadAllLinesAsync(env));
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(env));
        Assert.DoesNotContain(NewSecret, output + err);
        Assert.Contains("ino marketing verify", output);
        Assert.Empty(Directory.GetFiles(h.Dir, ".ma.env.*.tmp"));
        // Running it again is a new renewal: a new key.
        Assert.Equal(ExitCodes.Ok, (await h.Run("--profile", "dev", "marketing", "rotate-secret", Id, "--show-secret", "--overlap-hours", "0")).Code);
        Assert.NotEqual(f.Posts[1].Key, f.Posts[2].Key);
        Assert.Contains("\"overlapHours\":0", f.Posts[2].Body);
        // A symlinked env file is never written through; the secret goes to stderr instead.
        if (!OperatingSystem.IsWindows())
        {
            var target = Path.Combine(h.Dir, "other.txt");
            await File.WriteAllTextAsync(target, "keep me\n");
            var link = Path.Combine(h.Dir, "link.env");
            File.CreateSymbolicLink(link, target);
            var (c2, _, err2) = await h.Run("--profile", "dev", "marketing", "rotate-secret", Id, "--env-file", link);
            Assert.Equal(ExitCodes.Ok, c2);
            Assert.Equal("keep me\n", await File.ReadAllTextAsync(target));
            Assert.Contains("symbolic link", err2);
            Assert.Contains($"INO_MA_SIGNING_SECRET={NewSecret}", err2);
        }
    }

    [Fact]
    public async Task Verify_says_when_the_tool_still_answers_with_the_previous_secret()
    {
        var (h, f) = await SignedIn();
        using var hh = h;
        f.VerifyAnswer = """{"data":{"connectionId":"8b0e1d0c-0000-4000-8000-000000000001","connected":true,"status":"CONNECTED","error":null,"capabilities":[],"onPreviousSecret":true}}""";
        var (code, output, _) = await h.Run("--profile", "dev", "marketing", "verify", Id);
        Assert.Equal(ExitCodes.Ok, code);
        Assert.Contains("previous signing secret", output);
    }

    [Fact]
    public async Task Actions_list_set_and_a_token_that_goes_only_where_asked()
    {
        var (h, f) = await SignedIn();
        using var hh = h;
        var (code, output, _) = await h.Run("--profile", "dev", "marketing", "actions", Id);
        Assert.Equal(ExitCodes.Ok, code);
        Assert.Contains("survey.completed", output);
        Assert.Contains("POST https://api.example.test/t/tabc1234/ma/actions/", output);
        Assert.Equal(ExitCodes.Usage, (await h.Run("--profile", "dev", "marketing", "action-set", Id, "survey.completed", "--on", "--off")).Code);
        (code, output, _) = await h.Run("--profile", "dev", "marketing", "action-set", Id, "survey.completed", "--off", "--version", "2");
        Assert.Equal(ExitCodes.Ok, code);
        Assert.Contains("version 3", output);
        Assert.Contains("\"enabled\":false", f.Posts.Last().Body);
        Assert.Contains("\"version\":2", f.Posts.Last().Body);
        // The token is shown once and only where asked.
        Assert.Equal(ExitCodes.Usage, (await h.Run("--profile", "dev", "marketing", "action-token", Id)).Code);
        var env = Path.Combine(h.Dir, "actions.env");
        var (c2, out2, err2) = await h.Run("--profile", "dev", "marketing", "action-token", Id, "--env-file", env);
        Assert.Equal(ExitCodes.Ok, c2);
        Assert.Equal([$"INO_MA_ACTION_TOKEN={ActionToken}"], await File.ReadAllLinesAsync(env));
        Assert.DoesNotContain(ActionToken, out2 + err2);
        Assert.NotNull(f.Posts.Last().Key);
    }
}
