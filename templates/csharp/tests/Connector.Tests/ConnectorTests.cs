using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Connector;
using Xunit;

public class ConnectorTests
{
    static readonly Settings Cfg = new("https://engine.example.test/t/tabc1234", "MY_CHANNEL", "ik_test_key", "test-secret");

    sealed class Recorder(params int[] statuses) : HttpMessageHandler
    {
        readonly Queue<int> _statuses = new(statuses);
        public List<(HttpRequestMessage Req, byte[] Body)> Seen { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen.Add((request, await request.Content!.ReadAsByteArrayAsync(ct)));
            return new HttpResponseMessage((HttpStatusCode)(_statuses.Count > 0 ? _statuses.Dequeue() : 200)) { Content = new StringContent("{\"ResponseCode\":\"SUCCESS\"}") };
        }
    }

    static string H(HttpRequestMessage r, string name) => r.Headers.GetValues(name).Single();

    [Fact]
    public void Signature_v2_covers_method_path_and_exact_body()
    {
        var want = Convert.ToHexStringLower(HMACSHA256.HashData("test-secret"u8.ToArray(),
            Encoding.UTF8.GetBytes("v2.1700000000.n1.POST./t/tabc1234/api/v1/MY_CHANNEL/transaction.create." + "{\"a\":1}")));
        Assert.Equal(want, InoClient.SignV2("test-secret", "1700000000", "n1", "post", "/t/tabc1234/api/v1/MY_CHANNEL/transaction.create", "{\"a\":1}"u8.ToArray()));
    }

    [Fact]
    public async Task Retry_sends_same_body_with_new_nonce_and_401_is_final()
    {
        var rec = new Recorder(503, 200);
        var r = await new InoClient(Cfg, new HttpClient(rec), wait: _ => Task.CompletedTask).CallAsync("transaction.create", new JsonObject { ["orderNumber"] = "A1" });
        Assert.Equal(200, r.Status);
        Assert.Equal(2, rec.Seen.Count);
        Assert.Equal(rec.Seen[0].Body, rec.Seen[1].Body);
        Assert.NotEqual(H(rec.Seen[0].Req, "X-Nonce"), H(rec.Seen[1].Req, "X-Nonce"));
        Assert.Matches("^[0-9a-f-]{36}$", JsonNode.Parse(rec.Seen[0].Body)!["requestId"]!.GetValue<string>());

        var rec401 = new Recorder(401, 401, 401);
        Assert.Equal(401, (await new InoClient(Cfg, new HttpClient(rec401), wait: _ => Task.CompletedTask).CallAsync("transaction.create")).Status);
        Assert.Single(rec401.Seen);
    }

    [Fact]
    public void Webhook_valid_tampered_stale()
    {
        var key = Enumerable.Repeat((byte)7, 32).ToArray();
        var secret = "whsec_" + Convert.ToBase64String(key);
        var raw = "{\"id\":\"e1\",\"type\":\"points.earned\"}"u8.ToArray();
        const string ts = "1700000000";
        byte[] signed = [.. Encoding.UTF8.GetBytes($"msg_1.{ts}."), .. raw];
        var sig = "v1," + Convert.ToBase64String(HMACSHA256.HashData(key, signed));
        var h = new Dictionary<string, string> { ["webhook-id"] = "msg_1", ["webhook-timestamp"] = ts, ["webhook-signature"] = sig };
        Assert.True(Webhook.Verify(raw, h, secret, 1700000010).Ok);
        Assert.False(Webhook.Verify("{\"id\":\"e2\"}"u8.ToArray(), h, secret, 1700000010).Ok);
        Assert.False(Webhook.Verify(raw, h, secret, 1700009999).Ok);
    }

    [Fact]
    public async Task Offline_queue_keeps_order_and_stops_at_first_refusal()
    {
        var q = new OfflineQueue(Path.Combine(Directory.CreateTempSubdirectory("ino-q-").FullName, "q.jsonl"));
        foreach (var n in new[] { "A1", "A2", "A3" }) q.Add("transaction.create", new JsonObject { ["orderNumber"] = n, ["requestId"] = "r" + n });
        var statuses = new Queue<int>([200, 400]);
        var sent = await q.FlushAsync((_, _) =>
        {
            var s = statuses.Count > 0 ? statuses.Dequeue() : 200;
            return Task.FromResult(new InoClient.Result(s, new JsonObject { ["ResponseCode"] = s == 200 ? "SUCCESS" : "PARAMETER_ERROR" }, "x"));
        });
        Assert.Equal(1, sent);
        Assert.Equal(["A2", "A3"], q.Items().Select(i => i["body"]!["orderNumber"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Heartbeat_is_signed_and_carries_no_member_data()
    {
        var rec = new Recorder(200);
        Assert.Equal(200, await new InoClient(Cfg, new HttpClient(rec)).HeartbeatAsync("demo", "0.1.0", queueDepth: 3));
        var (req, body) = rec.Seen[0];
        Assert.Equal("https://engine.example.test/t/tabc1234/api/v1/MY_CHANNEL/connector/heartbeat", req.RequestUri!.ToString());
        Assert.Equal(H(req, "X-Signature"), InoClient.SignV2("test-secret", H(req, "X-Timestamp"), H(req, "X-Nonce"), "POST", req.RequestUri.AbsolutePath, body));
        Assert.Equal(["name", "queueDepth", "status", "version"], JsonNode.Parse(body)!.AsObject().Select(p => p.Key).Order());
    }
}
