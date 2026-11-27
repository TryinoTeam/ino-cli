using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Ino.Cli.Tests;

/// <summary>
/// MC1: `ino logs tail` (the engine's request log through the Management API, filters passed as query, newest last, --follow) and
/// `ino pos file-format list|set|clear` (the column summary first; pinning needs the summary's review hash).
/// </summary>
public class LogsAndPosFormatCommandTests
{
    static string Jwt(object claims) => "e30." + Convert.ToBase64String(Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(claims))).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".sig";
    const string Prefix = "/t/tabc1234/manage/v1/";
    const string Review = "r1:9a7c";

    sealed class Fake
    {
        public readonly List<(HttpMethod Method, string PathAndQuery, string Body)> Calls = [];
        public int LogReads;
        public RouteHandler Handler => new((req, body) =>
        {
            var token = Jwt(new { ino_tenant = "tabc1234", ino_env = "test", name = "Dana Whitfield", scope = "offline_access integration:read integration:write connector:install", exp = 1900000000 });
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/oauth/token") return RouteHandler.Json($$"""{"access_token":"{{token}}","refresh_token":"rt-1"}""");
            Calls.Add((req.Method, req.RequestUri.PathAndQuery, Encoding.UTF8.GetString(body)));
            switch (path)
            {
                case Prefix + "request-log":
                    LogReads++;
                    // Newest first, like the engine; the second read has one more request.
                    var rows = new List<string>
                    {
                        """{"id":"b","createdAt":"2026-11-27T10:00:05Z","channel":"STORE_TILLS","operation":"transaction.create","path":"/api/x","httpStatus":422,"durationMs":41,"resultCode":"PARAMETER_ERROR","requestId":"r-2","error":"Quantity is required.\u001b]0;owned\u0007"}""",
                        """{"id":"a","createdAt":"2026-11-27T10:00:01Z","channel":"STORE_TILLS","operation":"transaction.create","path":"/api/x","httpStatus":200,"durationMs":35,"resultCode":"SUCCESS","requestId":"r-1"}""",
                    };
                    if (LogReads > 1) rows.Insert(0, """{"id":"c","createdAt":"2026-11-27T10:00:09Z","channel":"STORE_TILLS","operation":"campaign.inquiry","path":"/api/y","httpStatus":200,"durationMs":12,"resultCode":"SUCCESS"}""");
                    return RouteHandler.Json($$$"""{"data":{"requests":[{{{string.Join(",", rows)}}}],"nextCursor":null}}""");
                case Prefix + "channels/file-formats":
                    return RouteHandler.Json("""{"data":{"fileFormats":[{"channel":"STORE_TILLS","fileFormat":"local_harbor-pos@1.0.0","allowV1":true,"pinnedAt":"2026-11-27T09:00:00Z","channelConfigured":true}],"ownPackages":true}}""");
                case Prefix + "channels/STORE_TILLS/file-format/preview":
                    return RouteHandler.Json($$$"""
                        {"data":{"channel":"STORE_TILLS","definition":"local_harbor-pos","version":"1.0.0","reviewHash":"{{{Review}}}","current":null,
                         "access":{"columns":["CustomerId = LoyaltyId","OrderNumber = ReceiptNo"],"personalData":["CustomerId"],"recordTypes":["SALE_LINE = S"],"paymentTypes":[],"amountScale":100} } }
                        """);
                case Prefix + "channels/STORE_TILLS/file-format" when req.Method == HttpMethod.Put:
                    return RouteHandler.Json("""{"data":{"channel":"STORE_TILLS","fileFormat":"local_harbor-pos@1.0.0","allowV1":true,"previous":null}}""");
                case Prefix + "channels/STORE_TILLS/file-format" when req.Method == HttpMethod.Delete:
                    return RouteHandler.Json("""{"data":{"channel":"STORE_TILLS","cleared":true,"previous":"local_harbor-pos@1.0.0"}}""");
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
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
    public async Task Logs_tail_passes_the_filters_and_shows_the_newest_last()
    {
        var (h, f) = await SignedIn();
        using var hh = h;
        var (code, output, err) = await h.Run("--profile", "dev", "logs", "tail", "--channel", "STORE_TILLS", "--outcome", "failed", "--operation", "transaction.create", "--limit", "10");
        Assert.True(code == ExitCodes.Ok, err);
        var query = f.Calls.Single(c => c.PathAndQuery.Contains("request-log", StringComparison.Ordinal)).PathAndQuery;
        Assert.Contains("channel=STORE_TILLS", query);
        Assert.Contains("outcome=failed", query);
        Assert.Contains("operation=transaction.create", query);
        Assert.Contains("limit=10", query);
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Contains("200", lines[0]);                                  // the older one first
        Assert.Contains("422", lines[1]);
        Assert.Contains("Quantity is required.", output);
        Assert.DoesNotContain('\u001b', output);                           // review MC1 1: no terminal control from a caller's text
        Assert.DoesNotContain('\u0007', output);
    }

    [Fact]
    public async Task Logs_tail_follow_shows_each_new_request_once()
    {
        var (h, f) = await SignedIn();
        using var hh = h;
        using var cts = new CancellationTokenSource();
        h.Token = cts.Token;
        h.Delay = (_, _) => { if (f.LogReads >= 2) cts.Cancel(); return Task.CompletedTask; };
        var (code, output, err) = await h.Run("--profile", "dev", "logs", "tail", "--follow", "--interval", "2");
        Assert.True(code == ExitCodes.Ok, err);
        Assert.Equal(1, output.Split('\n').Count(l => l.Contains("campaign.inquiry", StringComparison.Ordinal)));
        Assert.Equal(1, output.Split('\n').Count(l => l.Contains("Quantity", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Pos_file_format_shows_the_columns_first_and_pins_only_with_the_review_hash()
    {
        var (h, f) = await SignedIn();
        using var hh = h;
        var (listed, listOut, _) = await h.Run("--profile", "dev", "pos", "file-format", "list");
        Assert.Equal(ExitCodes.Ok, listed);
        Assert.Contains("local_harbor-pos@1.0.0", listOut);

        var (code, output, _) = await h.Run("--profile", "dev", "pos", "file-format", "set", "STORE_TILLS", "--definition", "local_harbor-pos", "--version", "1.0.0");
        Assert.Equal(ExitCodes.Usage, code);
        Assert.Contains("CustomerId = LoyaltyId   (personal data)", output);
        Assert.Contains("in cents", output);
        Assert.DoesNotContain(f.Calls, c => c.Method == HttpMethod.Put);

        var (ok, done, err) = await h.Run("--profile", "dev", "pos", "file-format", "set", "STORE_TILLS", "--definition", "local_harbor-pos", "--version", "1.0.0", "--allow-v1", "--confirm", Review);
        Assert.True(ok == ExitCodes.Ok, err);
        var put = JsonNode.Parse(f.Calls.Single(c => c.Method == HttpMethod.Put).Body)!;
        Assert.Equal((Review, true), (put["reviewHash"]!.ToString(), put["allowV1"]!.GetValue<bool>()));
        Assert.Contains("v1 files too", done);

        Assert.Equal(ExitCodes.Usage, (await h.Run("--profile", "dev", "pos", "file-format", "clear", "STORE_TILLS")).Code);
        var (cleared, clearOut, _) = await h.Run("--profile", "dev", "pos", "file-format", "clear", "STORE_TILLS", "--yes");
        Assert.Equal(ExitCodes.Ok, cleared);
        Assert.Contains("Ino POS file v1 again", clearOut);
    }
}
