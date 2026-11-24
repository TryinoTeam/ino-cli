using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Ino.Cli;

namespace Ino.Cli.Tests;

/// <summary>A scripted engine: catalog, a feed whose pages are given per cursor, webhook and job endpoints.</summary>
sealed class FakeEngine
{
    public readonly Dictionary<string, (string[] Ids, string Next, bool More)> Pages = new();
    public string Head = "c0";
    public Queue<HttpResponseMessage> Overrides = new();
    public RouteHandler Handler = null!;

    public static string Event(string id, string type = "transaction.completed") =>
        "{\"specversion\":\"1.0\",\"id\":\"" + id + "\",\"type\":\"" + type + "\",\"source\":\"ino\",\"time\":\"2026-11-23T10:00:00Z\",\"dataversion\":2,\"data\":{\"customerid\":\"c-1\",\"points\":10}}";

    public const string Catalog = """
        {"events":[
          {"type":"transaction.completed","version":1,"group":"Transactions","title":"Sale committed","visibility":"public","example":{"specversion":"1.0","id":"x","type":"transaction.completed","time":"t","dataversion":1,"data":{"points":5}}},
          {"type":"transaction.completed","version":2,"group":"Transactions","title":"Sale committed","visibility":"public","example":{"specversion":"1.0","id":"x","type":"transaction.completed","time":"t","dataversion":2,"data":{"points":5,"net":true}}},
          {"type":"internal.thing","version":1,"group":"X","title":"Internal","visibility":"internal","example":{}}]}
        """;

    public bool PerEventCursors;

    string Feed(string[] ids, string cursor, bool more) =>
        "{\"ResponseCode\":\"SUCCESS\",\"Data\":{\"events\":[" + string.Join(",", ids.Select(i => Event(i))) + "],\"cursor\":\"" + cursor + "\",\"hasMore\":" + (more ? "true" : "false")
        + (PerEventCursors ? ",\"cursors\":[" + string.Join(",", ids.Select(i => "\"after-" + i + "\"")) + "]" : "") + "}}";

    public FakeEngine()
    {
        Handler = new RouteHandler((req, _) =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/api/v1/meta/events") return RouteHandler.Json(Catalog);
            if (path.EndsWith("/events", StringComparison.Ordinal))
            {
                if (Overrides.Count > 0) return Overrides.Dequeue();
                var q = System.Web.HttpUtility.ParseQueryString(req.RequestUri.Query);
                var after = q["after"];
                if (after is null) return RouteHandler.Json(Feed([], Head, false));
                var (ids, next, more) = Pages.TryGetValue(after, out var p) ? p : ([], after, false);
                return RouteHandler.Json(Feed(ids, next, more));
            }
            return RouteHandler.Json("""{"ResponseCode":"SUCCESS","Data":{}}""");
        });
    }
}

/// <summary>A local receiver that records deliveries and refuses chosen event ids.</summary>
sealed class Receiver
{
    public readonly HashSet<string> Refuse = [];
    public readonly List<(HttpRequestMessage Request, string Body)> Got = [];
    public RouteHandler Handler => new((req, body) =>
    {
        var text = Encoding.UTF8.GetString(body);
        var id = JsonNode.Parse(text)!["id"]!.ToString();
        Got.Add((req, text));
        return new HttpResponseMessage(Refuse.Contains(id) ? HttpStatusCode.InternalServerError : HttpStatusCode.OK);
    });
}

public class ListenTests
{
    static (Harness h, FakeEngine engine, Receiver rx, CancellationTokenSource cts) Setup(int polls = 3)
    {
        var h = new Harness();
        var engine = new FakeEngine();
        var rx = new Receiver();
        var cts = new CancellationTokenSource();
        var n = 0;
        h.Api = engine.Handler;
        h.Local = rx.Handler;
        h.Token = cts.Token;
        h.Delay = (d, _) => { if (d >= TimeSpan.FromSeconds(2) && ++n >= polls) cts.Cancel(); return Task.CompletedTask; };
        return (h, engine, rx, cts);
    }

    [Fact]
    public async Task Forwards_events_with_real_delivery_headers_and_moves_the_cursor()
    {
        var (h, engine, rx, _) = Setup();
        using (h)
        {
            await h.AddProfile();
            engine.Pages["c0"] = (["e1", "e2"], "c1", false);
            var (code, output, err) = await h.Run("listen", "--forward-to", "http://localhost:4000/hooks");
            Assert.Equal(0, code);                           // Ctrl-C (cancellation) ends listen normally
            Assert.Equal(["e1", "e2"], rx.Got.Select(g => JsonNode.Parse(g.Body)!["id"]!.ToString()));
            var first = rx.Got[0].Request;
            string H(string n) => first.Headers.GetValues(n).Single();
            var secret = h.Store.DeviceSecret(h.Store.Resolve(null));
            Assert.Equal(StandardWebhooks.Sign(secret, H("webhook-id"), long.Parse(H("webhook-timestamp")), Encoding.UTF8.GetBytes(rx.Got[0].Body)), H("webhook-signature"));
            Assert.Equal(StandardWebhooks.SignLegacy(secret, H("X-Timestamp"), Encoding.UTF8.GetBytes(rx.Got[0].Body)), H("X-Ino-Signature"));
            Assert.StartsWith("msg_", H("webhook-id"));
            Assert.NotEqual("e1", H("webhook-id"));
            Assert.Equal("InoLoyalty-Webhooks/1.0", first.Headers.UserAgent.ToString());
            Assert.False(first.Headers.Contains("X-Ino-Cli-Synthetic"));
            Assert.Contains("transaction.completed", output);
            Assert.Equal("c1", h.Store.Cursors.Read("dev")!.Cursor);
            Assert.DoesNotContain(secret, output + err);
        }
    }

    [Fact]
    public async Task Failure_mid_page_stops_and_resume_sends_only_the_rest()
    {
        var (h, engine, rx, _) = Setup();
        using (h)
        {
            await h.AddProfile();
            engine.Pages["c0"] = (["e1", "e2", "e3"], "c1", false);
            rx.Refuse.Add("e2");
            var (code, _, err) = await h.Run("listen", "--forward-to", "http://127.0.0.1:4000/");
            Assert.Equal(ExitCodes.Business, code);
            Assert.Contains("--resume", err);
            Assert.Equal(1 + 4, rx.Got.Count);               // e1 once, e2 four tries
            var state = h.Store.Cursors.Read("dev")!;
            Assert.Equal("c0", state.Cursor);
            Assert.Equal(["e1"], state.Done);

            rx.Refuse.Clear(); rx.Got.Clear();
            var cts = new CancellationTokenSource(); var n = 0;
            h.Token = cts.Token; h.Delay = (d, _) => { if (d >= TimeSpan.FromSeconds(2) && ++n >= 2) cts.Cancel(); return Task.CompletedTask; };
            await h.Run("listen", "--forward-to", "http://127.0.0.1:4000/", "--resume");
            Assert.Equal(["e2", "e3"], rx.Got.Select(g => JsonNode.Parse(g.Body)!["id"]!.ToString()));
        }
    }

    [Fact]
    public async Task With_per_event_cursors_the_state_moves_exactly_past_each_event()
    {
        var (h, engine, rx, _) = Setup();
        using (h)
        {
            await h.AddProfile();
            engine.PerEventCursors = true;
            engine.Pages["c0"] = (["e1", "e2", "e3"], "c1", false);
            rx.Refuse.Add("e2");
            Assert.Equal(ExitCodes.Business, (await h.Run("listen", "--forward-to", "http://127.0.0.1:4000/")).Code);
            var state = h.Store.Cursors.Read("dev")!;
            Assert.Equal("after-e1", state.Cursor);
            Assert.Empty(state.Done);
        }
    }

    [Fact]
    public async Task Waits_out_429_with_retry_after_without_moving_the_cursor()
    {
        var (h, engine, rx, _) = Setup();
        using (h)
        {
            await h.AddProfile();
            var delays = new List<TimeSpan>();
            var inner = h.Delay;
            h.Delay = (d, t) => { delays.Add(d); return inner(d, t); };
            engine.Pages["c0"] = (["e1"], "c1", false);
            // head call first, then a 429 on the first page call
            var r429 = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            r429.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            engine.Overrides.Enqueue(RouteHandler.Json("""{"ResponseCode":"SUCCESS","Data":{"events":[],"cursor":"c0","hasMore":false}}"""));
            engine.Overrides.Enqueue(r429);
            await h.Run("listen", "--forward-to", "http://localhost:4000/");
            Assert.Contains(TimeSpan.FromSeconds(30), delays);
            Assert.Single(rx.Got);
        }
    }

    [Fact]
    public async Task Expired_cursor_is_dropped_with_a_clear_message()
    {
        var (h, engine, _, _) = Setup();
        using (h)
        {
            await h.AddProfile();
            h.Store.Cursors.Write("dev", new ListenState("old", []));
            engine.Overrides.Enqueue(RouteHandler.Json("""{"ResponseCode":"CURSOR_EXPIRED"}""", HttpStatusCode.Gone));
            var (code, _, err) = await h.Run("listen", "--forward-to", "http://localhost:4000/", "--resume");
            Assert.Equal(ExitCodes.Business, code);
            Assert.Contains("no longer valid", err);
            Assert.Null(h.Store.Cursors.Read("dev"));
        }
    }

    [Fact]
    public async Task Resume_without_or_with_a_broken_state_refuses_instead_of_skipping_events()
    {
        var (h, engine, _, _) = Setup();
        using (h)
        {
            await h.AddProfile();
            Assert.Equal(ExitCodes.Usage, (await h.Run("listen", "--forward-to", "http://localhost:4000/", "--resume")).Code);
            Directory.CreateDirectory(Path.Combine(h.Dir, "ino", "state"));
            File.WriteAllText(Path.Combine(h.Dir, "ino", "state", "cursor-dev"), "{not json");
            var (code, _, err) = await h.Run("listen", "--forward-to", "http://localhost:4000/", "--resume");
            Assert.Equal(ExitCodes.Usage, code);
            Assert.Contains("cannot be read", err);
            Assert.Empty(engine.Handler.Seen);
        }
    }

    [Fact]
    public async Task Live_profile_needs_live_flag_and_sends_nothing_without_it()
    {
        var (h, engine, _, _) = Setup();
        using (h)
        {
            await h.AddProfile("prod", "live");
            var (code, _, _) = await h.Run("listen", "--forward-to", "http://localhost:4000/");
            Assert.Equal(ExitCodes.Usage, code);
            Assert.Empty(engine.Handler.Seen);
        }
    }

    [Theory]
    [InlineData("https://evil.example/hooks")]
    [InlineData("http://10.0.0.5:3000/")]
    [InlineData("http://localhost.evil.example/")]
    [InlineData("ftp://localhost/")]
    [InlineData("http://user:pw@localhost/")]
    public async Task Only_local_targets(string url)
    {
        var (h, engine, _, _) = Setup();
        using (h)
        {
            await h.AddProfile();
            Assert.Equal(ExitCodes.Usage, (await h.Run("listen", "--forward-to", url)).Code);
            Assert.Empty(engine.Handler.Seen);
        }
    }

    [Theory]
    [InlineData("127.1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::1")]
    public void Loopback_forms_are_loopback(string ip) => Assert.True(StandardWebhooks.IsLoopback(IPAddress.Parse(ip)));

    [Fact]
    public async Task Dialer_refuses_a_non_loopback_address_even_if_the_name_passed()
    {
        using var client = new HttpClient(StandardWebhooks.LoopbackOnlyHandler());
        var e = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("http://10.255.255.1:9/"));
        Assert.Contains("loopback", e.Message);
    }

    [Fact]
    public async Task Listen_secret_never_collides_with_a_profile_and_goes_with_remove()
    {
        using var h = new Harness();
        await h.AddProfile("dev");
        await h.AddProfile("listen-dev");
        var secret = h.Store.DeviceSecret(h.Store.Resolve("dev"));
        Assert.StartsWith("whsec_", secret);
        Assert.Equal("s3cr3t-value-123", h.Store.Resolve("listen-dev").Connection.Secret);
        Assert.Equal(secret, h.Store.DeviceSecret(h.Store.Resolve("dev")));
        await h.Run("profile", "remove", "dev");
        Assert.DoesNotContain(h.Secrets.Items.Keys, k => k.Contains(":dev", StringComparison.Ordinal) || k == "dev");
    }

    [Fact]
    public void Delivery_id_is_stable_per_event_and_profile()
    {
        Assert.Equal(StandardWebhooks.DeliveryId("dev", "e1"), StandardWebhooks.DeliveryId("dev", "e1"));
        Assert.NotEqual(StandardWebhooks.DeliveryId("dev", "e1"), StandardWebhooks.DeliveryId("prod", "e1"));
    }

    [Fact]
    public async Task Unknown_or_internal_event_types_are_refused_before_polling()
    {
        var (h, engine, _, _) = Setup();
        using (h)
        {
            await h.AddProfile();
            Assert.Equal(ExitCodes.Usage, (await h.Run("listen", "--forward-to", "http://localhost:4000/", "--events", "internal.thing")).Code);
            Assert.DoesNotContain(engine.Handler.Seen, s => s.Request.RequestUri!.AbsolutePath.Contains("/MODERN_ECOM/events", StringComparison.Ordinal));
        }
    }
}

public class TriggerTests
{
    [Fact]
    public async Task Synthetic_event_from_the_catalog_is_signed_and_marked()
    {
        using var h = new Harness();
        var engine = new FakeEngine(); var rx = new Receiver();
        h.Api = engine.Handler; h.Local = rx.Handler;
        await h.AddProfile();
        var (code, output, _) = await h.Run("trigger", "transaction.completed", "--forward-to", "http://localhost:4000/", "--override", "data.points=50", "--dataversion", "1");
        Assert.Equal(0, code);
        var (req, body) = Assert.Single(rx.Got);
        var e = JsonNode.Parse(body)!;
        Assert.Equal(50, e["data"]!["points"]!.GetValue<int>());
        Assert.Equal(1, e["dataversion"]!.GetValue<int>());
        Assert.NotEqual("x", e["id"]!.ToString());
        Assert.Equal("true", req.Headers.GetValues("X-Ino-Cli-Synthetic").Single());
        Assert.Equal("/api/v1/meta/events", engine.Handler.Seen.Single().Request.RequestUri!.AbsolutePath);
        Assert.Contains("→ 200", output);
    }

    [Fact]
    public async Task Unknown_type_is_not_found()
    {
        using var h = new Harness();
        h.Api = new FakeEngine().Handler; h.Local = new Receiver().Handler;
        await h.AddProfile();
        Assert.Equal(ExitCodes.NotFound, (await h.Run("trigger", "internal.thing", "--forward-to", "http://localhost:4000/")).Code);
    }
}

public class WebhookAndJobTests
{
    [Fact]
    public async Task Create_shows_the_secret_once_on_stderr_and_masks_it_in_the_response()
    {
        using var h = new Harness();
        await h.AddProfile();
        h.Http = new FakeHandler(HttpStatusCode.Created, """{"ResponseCode":"SUCCESS","Data":{"subscription":{"id":"s1"},"secret":"whsec_c2VjcmV0c2VjcmV0"}}""");
        var (code, output, err) = await h.Run("webhooks", "create", "--url", "https://rx.example.test/hooks", "--events", "transaction.completed");
        Assert.Equal(0, code);
        Assert.Contains("whsec_c2VjcmV0c2VjcmV0", err);
        Assert.DoesNotContain("whsec_c2VjcmV0c2VjcmV0", output);
        var (req, body) = Assert.Single(h.Http.Seen);
        Assert.Equal("/t/tabc1234/api/v1/MODERN_ECOM/webhooks/subscriptions", req.RequestUri!.AbsolutePath);
        Assert.Equal("transaction.completed", JsonNode.Parse(body)!["eventTypes"]![0]!.ToString());
    }

    [Fact]
    public async Task Create_on_an_existing_url_says_it_was_updated()
    {
        using var h = new Harness();
        await h.AddProfile();
        h.Http = new FakeHandler(HttpStatusCode.OK, """{"ResponseCode":"SUCCESS","Data":{"subscription":{"id":"s1"},"secret":null}}""");
        var (_, _, err) = await h.Run("webhooks", "create", "--url", "https://rx.example.test/hooks");
        Assert.Contains("already existed", err);
    }

    [Theory]
    [InlineData("create", "--url", "https://rx.example.test/h")]
    [InlineData("delete", "11111111-1111-1111-1111-111111111111", null)]
    [InlineData("test", "11111111-1111-1111-1111-111111111111", null)]
    [InlineData("rotate-secret", "11111111-1111-1111-1111-111111111111", null)]
    [InlineData("resend", "11111111-1111-1111-1111-111111111111", "22222222-2222-2222-2222-222222222222")]
    public async Task Live_webhook_changes_need_live_and_yes(string cmd, string a, string? b)
    {
        using var h = new Harness();
        await h.AddProfile("prod", "live");
        string[] args = b is null ? ["webhooks", cmd, a] : ["webhooks", cmd, a, b];
        Assert.Equal(ExitCodes.Usage, (await h.Run(args)).Code);
        Assert.Empty(h.Http.Seen);
        Assert.Equal(0, (await h.Run([.. args, "--live", "--yes"])).Code);
    }

    [Fact]
    public async Task Deliveries_and_attempts_are_reads_with_the_right_paths()
    {
        using var h = new Harness();
        await h.AddProfile("prod", "live");
        Assert.Equal(0, (await h.Run("webhooks", "deliveries", "11111111-1111-1111-1111-111111111111", "--status", "FAILED")).Code);
        Assert.Equal("/t/tabc1234/api/v1/MODERN_ECOM/webhooks/subscriptions/11111111-1111-1111-1111-111111111111/deliveries?limit=50&status=FAILED", h.Http.Seen[^1].Request.RequestUri!.PathAndQuery);
        Assert.Equal(0, (await h.Run("webhooks", "attempts", "11111111-1111-1111-1111-111111111111", "22222222-2222-2222-2222-222222222222")).Code);
        Assert.EndsWith("/deliveries/22222222-2222-2222-2222-222222222222/attempts", h.Http.Seen[^1].Request.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task Webhook_list_is_a_read_and_delete_sends_no_body()
    {
        using var h = new Harness();
        await h.AddProfile("prod", "live");
        Assert.Equal(0, (await h.Run("webhooks", "list")).Code);
        await h.Run("webhooks", "delete", "11111111-1111-1111-1111-111111111111", "--live", "--yes");
        Assert.Equal(HttpMethod.Delete, h.Http.Seen[^1].Request.Method);
        Assert.Empty(h.Http.Seen[^1].Body);
    }

    [Fact]
    public async Task Job_result_file_goes_to_out_and_never_to_the_terminal()
    {
        using var h = new Harness();
        await h.AddProfile();
        h.Http = new FakeHandler(HttpStatusCode.OK, "id,points\n1,5\n", r => r.Content.Headers.ContentType = new("text/csv"));
        var id = "22222222-2222-2222-2222-222222222222";
        var (code, _, err) = await h.Run("jobs", "result", id);
        Assert.Equal(ExitCodes.Usage, code);
        Assert.Contains("--out", err);
        var file = Path.Combine(h.Dir, "r.csv");
        Assert.Equal(0, (await h.Run("jobs", "result", id, "--out", file)).Code);
        Assert.Equal("id,points\n1,5\n", File.ReadAllText(file));
    }

    [Fact]
    public async Task Job_not_ready_and_expired_have_clear_messages()
    {
        using var h = new Harness();
        await h.AddProfile();
        h.Http = new FakeHandler(HttpStatusCode.BadRequest, """{"ResponseCode":"PARAMETER_ERROR"}""");
        var (c1, _, e1) = await h.Run("jobs", "result", "22222222-2222-2222-2222-222222222222");
        Assert.Equal(ExitCodes.Business, c1); Assert.Contains("not READY", e1);
        h.Http = new FakeHandler(HttpStatusCode.NotFound, """{"ResponseCode":"NOT_FOUND"}""");
        Assert.Equal(ExitCodes.NotFound, (await h.Run("jobs", "result", "22222222-2222-2222-2222-222222222222")).Code);
    }

    [Fact]
    public async Task Job_result_on_live_needs_live()
    {
        using var h = new Harness();
        await h.AddProfile("prod", "live");
        Assert.Equal(ExitCodes.Usage, (await h.Run("jobs", "result", "22222222-2222-2222-2222-222222222222")).Code);
        Assert.Empty(h.Http.Seen);
    }

    [Fact]
    public async Task Job_submit_on_live_needs_live_and_yes()
    {
        using var h = new Harness();
        await h.AddProfile("prod", "live");
        Assert.Equal(ExitCodes.Usage, (await h.Run("jobs", "submit", "point_load", "--data", "{}")).Code);
        Assert.Empty(h.Http.Seen);
    }
}

public class ConnectorCheckTests
{
    [Fact]
    public async Task Status_lists_health_and_counts()
    {
        using var h = new Harness();
        await h.AddProfile();
        h.Http = new FakeHandler(System.Net.HttpStatusCode.OK, "{\"ResponseCode\":\"SUCCESS\",\"Data\":{\"connectors\":[{\"name\":\"shop-sync\",\"version\":\"1.0.1\",\"health\":\"degraded\",\"lastSeen\":\"2026-11-23T20:00:00Z\",\"requests24h\":40,\"errors24h\":2,\"queueDepth\":120,\"detail\":\"120 sales waiting\"}]}}");
        var (code, output, _) = await h.Run("connector", "status");
        Assert.Equal(0, code);
        Assert.Contains("degraded  shop-sync", output);
        Assert.Contains("errors 2", output);
        Assert.Equal("/t/tabc1234/api/v1/MODERN_ECOM/connector/status", h.Http.Seen[0].Request.RequestUri!.AbsolutePath);
    }

    const string Report = "{\"ResponseCode\":\"SUCCESS\",\"Data\":{\"suite\":\"pos\",\"hours\":72,\"scope\":\"this API key\",\"requestLogEnabled\":true,\"passed\":1,\"total\":2,\"scenarios\":["
        + "{\"code\":\"S1\",\"title\":\"Online sale priced first\",\"passed\":true,\"evidence\":\"3 sales\",\"howTo\":\"x\"},"
        + "{\"code\":\"S2\",\"title\":\"Safe retry\",\"passed\":false,\"evidence\":\"none seen\",\"howTo\":\"Send the same requestId twice\"}]}}";

    [Fact]
    public async Task Check_shows_each_scenario_and_fails_until_all_pass()
    {
        using var h = new Harness();
        await h.AddProfile();
        h.Http = new FakeHandler(System.Net.HttpStatusCode.OK, Report);
        var (code, output, _) = await h.Run("connector", "check", "--hours", "24");
        Assert.Equal(ExitCodes.Business, code);
        Assert.Contains("1/2 passed", output);
        Assert.Contains("to do: Send the same requestId twice", output);
        Assert.Equal("/t/tabc1234/api/v1/MODERN_ECOM/certification?suite=pos&hours=24", h.Http.Seen[0].Request.RequestUri!.PathAndQuery);
    }
}
