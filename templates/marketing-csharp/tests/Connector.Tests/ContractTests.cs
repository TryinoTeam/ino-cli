// Contract tests for the template (dotnet test). Each call is signed like Ino signs it, against the real server on a free local port.
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Connector;
using Xunit;

public class ContractTests
{
    static readonly string Secret = "whsec_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    static HttpRequestMessage Signed(string url, byte[] body, string? key = null, long? ts = null, string? id = null)
    {
        id ??= "msg_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));   // a new id per request, as Ino sends
        var t = (ts ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()).ToString();
        byte[] message = [.. Encoding.UTF8.GetBytes($"{id}.{t}."), .. body];
        var sig = Convert.ToBase64String(HMACSHA256.HashData(Signature.KeyOf(key ?? Secret), message));
        var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(body) };
        req.Headers.Add("webhook-id", id);
        req.Headers.Add("webhook-timestamp", t);
        req.Headers.Add("webhook-signature", "v1," + sig);
        return req;
    }

    static byte[] Body(string type, JsonObject data, int contract = 1) =>
        Encoding.UTF8.GetBytes(new JsonObject { ["type"] = type, ["contract"] = contract, ["connectionId"] = "c1", ["data"] = data }.ToJsonString());

    static async Task WithServer(Func<HttpClient, string, Task> run)
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        await using var app = Server.Build(Secret, new Tool(), $"http://127.0.0.1:{port}");
        await app.StartAsync();
        using var http = new HttpClient();
        await run(http, $"http://127.0.0.1:{port}/ino");
    }

    static async Task<(int Status, JsonObject Body)> Send(HttpClient http, HttpRequestMessage req)
    {
        using var r = await http.SendAsync(req);
        return ((int)r.StatusCode, JsonNode.Parse(await r.Content.ReadAsStringAsync())!.AsObject());
    }

    static Task<(int Status, JsonObject Body)> Call(HttpClient http, string url, string type, JsonObject data) => Send(http, Signed(url, Body(type, data)));

    [Fact]
    public Task Refuses_unsigned_wrongly_signed_and_old_requests() => WithServer(async (http, url) =>
    {
        var body = Body("ping", new JsonObject { ["challenge"] = "x" });
        Assert.Equal(401, (await Send(http, new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(body) })).Status);
        Assert.Equal(401, (await Send(http, Signed(url, body, key: "whsec_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))))).Status);
        Assert.Equal(401, (await Send(http, Signed(url, body, ts: DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 600))).Status);
    });

    [Fact]
    public Task Refuses_a_replayed_message_and_a_future_contract_version() => WithServer(async (http, url) =>
    {
        var body = Body("ping", new JsonObject { ["challenge"] = "r" });
        Assert.Equal(200, (await Send(http, Signed(url, body, id: "msg_same"))).Status);
        Assert.Equal(401, (await Send(http, Signed(url, body, id: "msg_same"))).Status);
        Assert.Equal(400, (await Send(http, Signed(url, Body("ping", new JsonObject { ["challenge"] = "v2" }, contract: 2)))).Status);
    });

    [Fact]
    public Task Ping_proves_the_secret() => WithServer(async (http, url) =>
    {
        var (status, body) = await Call(http, url, "ping", new JsonObject { ["challenge"] = "abc" });
        Assert.Equal(200, status);
        Assert.Equal("abc", body["challenge"]!.GetValue<string>());
        Assert.Equal(Convert.ToBase64String(HMACSHA256.HashData(Signature.KeyOf(Secret), "abc"u8.ToArray())), body["proof"]!.GetValue<string>());
    });

    [Fact]
    public Task Profile_event_subscription_unsubscribe_delete() => WithServer(async (http, url) =>
    {
        JsonObject Upsert(string? id, JsonObject contact, JsonObject props, params string[] unset) => new()
        {
            ["externalId"] = "m1", ["profileId"] = id, ["contact"] = contact, ["properties"] = props, ["unset"] = new JsonArray([.. unset.Select(u => (JsonNode)u)]),
        };
        JsonObject Event(string id, string eventId) => new() { ["profileId"] = id, ["name"] = "points.earned", ["eventId"] = eventId, ["time"] = DateTimeOffset.UtcNow.ToString("O"), ["properties"] = new JsonObject() };

        var up = await Call(http, url, "profile.upsert", Upsert(null, new JsonObject { ["email"] = "avery@example.com" }, new JsonObject { ["Ino Tier"] = "Gold" }));
        Assert.Equal(200, up.Status);
        var id = up.Body["profileId"]!.GetValue<string>();
        Assert.Equal(id, (await Call(http, url, "profile.upsert", Upsert(id, new JsonObject { ["email"] = "avery@example.com" }, [], "Ino Tier"))).Body["profileId"]!.GetValue<string>());
        Assert.Equal(202, (await Call(http, url, "event.track", Event(id, "e1"))).Status);
        Assert.Equal(202, (await Call(http, url, "event.track", Event(id, "e1"))).Status);
        var sub = (await Call(http, url, "profile.subscription", new JsonObject { ["profileId"] = id })).Body;
        Assert.Equal((true, false), (sub["email"]!["reachable"]!.GetValue<bool>(), sub["sms"]!["reachable"]!.GetValue<bool>()));
        Assert.Equal(202, (await Call(http, url, "profile.unsubscribe", new JsonObject { ["profileId"] = id, ["channel"] = "EMAIL" })).Status);
        Assert.Equal("UNSUBSCRIBED", (await Call(http, url, "profile.subscription", new JsonObject { ["profileId"] = id })).Body["email"]!["consent"]!.GetValue<string>());
        Assert.Equal(400, (await Call(http, url, "profile.unsubscribe", new JsonObject { ["profileId"] = id, ["channel"] = "PUSH" })).Status);
        Assert.Equal(202, (await Call(http, url, "profile.delete", new JsonObject { ["profileId"] = id })).Status);
        Assert.Equal(404, (await Call(http, url, "profile.delete", new JsonObject { ["profileId"] = id })).Status);
        Assert.Equal(404, (await Call(http, url, "event.track", Event(id, "e2"))).Status);
        Assert.Equal(404, (await Call(http, url, "profile.upsert", Upsert(id, [], []))).Status);
        Assert.Equal(400, (await Call(http, url, "nope", [])).Status);
    });

    [Fact]
    public void A_missing_or_malformed_secret_stops_the_server_at_start()
    {
        Assert.Throws<InvalidOperationException>(() => Server.Build(null, new Tool(), "http://127.0.0.1:0"));
        Assert.Throws<InvalidOperationException>(() => Server.Build("whsec_not*base64", new Tool(), "http://127.0.0.1:0"));
    }
}
