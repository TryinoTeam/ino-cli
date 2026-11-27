using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Ino.Cli.Tests;

/// <summary>P1.5a (docs/MCP_CLI_P1_LOG.md): `ino doctor --go-live` reads the engine's go-live items; a blocking or unchecked item exits 1; optional setup
/// steps never decide the exit; an engine without the go-live step is refused instead of passing.</summary>
public class DoctorGoLiveTests
{
    static string Jwt(object claims) => "e30." + Convert.ToBase64String(Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(claims))).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".sig";

    static string Setup(string? goLive) => $$"""
        {"schemaVersion":1,"tenant":"tabc1234","environment":"test","generatedAt":"2026-11-27T12:00:00Z","summary":{"ok":1,"todo":1,"warn":0,"info":0,"unknown":0,"fail":0},
         "steps":[{"key":"campaigns","title":"Campaigns","status":"todo","detail":"No approved, active campaign."}{{(goLive is null ? "" : "," + goLive)}}]}
        """;

    static string Step(params (string Key, string Status)[] items) =>
        """{"key":"goLive","title":"Ready to go live","status":"x","detail":"d (a rehearsal: this is a test firm).","items":[""" +
        string.Join(",", items.Select(i => $$"""{"key":"{{i.Key}}","status":"{{i.Status}}","detail":"{{i.Key}} detail\u001b[2J","next":"do it"}""")) + "]}";

    [Fact]
    public async Task Go_live_exits_by_its_own_items_and_refuses_an_old_engine()
    {
        using var h = new Harness();
        string? body = null;
        h.Browser = async url =>
        {
            var q = System.Web.HttpUtility.ParseQueryString(url.Query);
            _ = Task.Run(async () => { using var c = new HttpClient(); await c.GetAsync($"{q["redirect_uri"]}?code=code-1&state={q["state"]}"); });
            await Task.CompletedTask;
        };
        var seen = new List<string>();
        h.Auth = new RouteHandler((req, _) =>
        {
            if (req.RequestUri!.AbsolutePath == "/oauth/token")
                return RouteHandler.Json($$"""{"access_token":"{{Jwt(new { ino_tenant = "tabc1234", ino_env = "test", name = "Dana Whitfield", scope = "offline_access integration:read", exp = 1900000000 })}}","refresh_token":"rt-1"}""");
            seen.Add(req.RequestUri.PathAndQuery);
            return req.RequestUri.AbsolutePath == "/t/tabc1234/manage/v1/setup" ? RouteHandler.Json($$"""{"data":{{body}}}""") : new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        await h.AddProfile();
        Assert.Equal(ExitCodes.Ok, (await h.Run("--profile", "dev", "login")).Code);

        body = Setup(Step(("program", "ok"), ("signatureV2", "ok"), ("keyIpLimits", "warn"), ("approvers", "info")));
        var (ok, okOut, okErr) = await h.Run("--profile", "dev", "doctor", "--go-live");
        Assert.True(ok == ExitCodes.Ok, okErr);                                   // campaigns `todo` in the setup does not block
        Assert.Contains("/t/tabc1234/manage/v1/setup?goLive=true", seen);
        Assert.Contains("Nothing blocks going live.", okOut);
        Assert.DoesNotContain('\u001b', okOut);

        body = Setup(Step(("program", "ok"), ("debugCapture", "fail")));
        var (failed, failedOut, _) = await h.Run("--profile", "dev", "doctor", "--go-live");
        Assert.Equal(ExitCodes.Business, failed);
        Assert.Contains("✗ debugCapture", failedOut);
        Assert.Contains("→ do it", failedOut);

        body = Setup(Step(("program", "unknown")));
        Assert.Equal(ExitCodes.Business, (await h.Run("--profile", "dev", "doctor", "--go-live")).Code);   // unchecked is not green

        body = Setup(Step(("program", "ok")));
        var (j, jOut, _) = await h.Run("--profile", "dev", "doctor", "--go-live", "--json");
        Assert.Equal(ExitCodes.Ok, j);
        Assert.Equal("goLive", JsonNode.Parse(jOut)!["key"]!.ToString());

        body = Setup("""{"key":"goLive","title":"Ready to go live","status":"unknown","detail":"Could not be read right now."}""");
        var (unread, unreadOut, _) = await h.Run("--profile", "dev", "doctor", "--go-live");
        Assert.Equal(ExitCodes.Business, unread);                                  // a step that could not be read is never "nothing blocks"
        Assert.DoesNotContain("Nothing blocks", unreadOut);

        body = Setup(null);
        var (old, _, oldErr) = await h.Run("--profile", "dev", "doctor", "--go-live");
        Assert.Equal(ExitCodes.Usage, old);
        Assert.Contains("too old", oldErr);
    }
}
