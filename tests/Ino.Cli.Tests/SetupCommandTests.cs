using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Ino.Cli.Tests;

/// <summary>P1.1 (docs/MCP_CLI_P1_LOG.md): `ino setup` reads the engine's checklist; --json passes it through unchanged; nothing from the engine controls the terminal.</summary>
public class SetupCommandTests
{
    static string Jwt(object claims) => "e30." + Convert.ToBase64String(Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(claims))).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".sig";

    const string Checklist = """
        {"schemaVersion":1,"tenant":"tabc1234","environment":"test","generatedAt":"2026-11-27T12:00:00Z","summary":{"ok":1,"todo":1,"warn":1,"info":0,"unknown":1},
         "steps":[
          {"key":"program","title":"Program settings","status":"ok","detail":"Active program settings with a point value and currency."},
          {"key":"channels","title":"Channels and keys","status":"todo","detail":"2 channel(s).","items":[
            {"key":"STORE_TILLS","status":"todo","detail":"no active key.","next":"ino keys create --channel STORE_TILLS"},
            {"key":"WEB\u001b[2J","status":"warn","detail":"a key has no IP limit.\u0007"}]},
          {"key":"campaigns","title":"Campaigns","status":"warn","detail":"x","next":"Power Apps → Ino Loyalty → Campaigns"}]}
        """;

    [Fact]
    public async Task Setup_shows_each_step_with_the_next_thing_to_do()
    {
        using var h = new Harness();
        var calls = new List<string>();
        h.Browser = async url =>
        {
            var q = System.Web.HttpUtility.ParseQueryString(url.Query);
            _ = Task.Run(async () => { using var c = new HttpClient(); await c.GetAsync($"{q["redirect_uri"]}?code=code-1&state={q["state"]}"); });
            await Task.CompletedTask;
        };
        h.Auth = new RouteHandler((req, _) =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/oauth/token")
                return RouteHandler.Json($$"""{"access_token":"{{Jwt(new { ino_tenant = "tabc1234", ino_env = "test", name = "Dana Whitfield", scope = "offline_access integration:read", exp = 1900000000 })}}","refresh_token":"rt-1"}""");
            calls.Add(req.Method + " " + path);
            return path == "/t/tabc1234/manage/v1/setup" ? RouteHandler.Json($$"""{"data":{{Checklist}}}""") : new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        await h.AddProfile();
        Assert.Equal(ExitCodes.Ok, (await h.Run("--profile", "dev", "login")).Code);

        var (code, output, err) = await h.Run("--profile", "dev", "setup");
        Assert.True(code == ExitCodes.Ok, err);
        Assert.Equal(["GET /t/tabc1234/manage/v1/setup"], calls);
        Assert.Contains("✓ Program settings", output);
        Assert.Contains("○ STORE_TILLS", output);
        Assert.Contains("→ ino keys create --channel STORE_TILLS", output);
        Assert.Contains("1 to do, 1 to check, 1 done. 1 could not be read right now.", output);
        Assert.DoesNotContain('\u001b', output);
        Assert.DoesNotContain('\u0007', output);

        var (jcode, joutput, _) = await h.Run("--profile", "dev", "setup", "--json");
        Assert.Equal(ExitCodes.Ok, jcode);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Checklist), JsonNode.Parse(joutput)));   // the assistant reads exactly what the engine said

        var (_, help, _) = await h.Run("setup", "--help");
        Assert.Contains("ino agent setup", help);
    }
}
