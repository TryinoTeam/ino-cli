// {{platform}} ↔ Ino Loyalty connector. Replace the /platform/* handlers with your platform's events.
using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Connector;

Settings.LoadEnv();
var settings = Settings.FromEnvironment();
var ino = new InoClient(settings, InoClient.CreateHttpClient());
var queue = new OfflineQueue(settings.QueueFile);
var seen = new ConcurrentDictionary<string, bool>();   // webhook-ids already handled (use a database in production)
const string Name = "{{name}}", Platform = "{{platform}}";
var version = typeof(Settings).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";

var builder = WebApplication.CreateSlimBuilder(args);
builder.WebHost.UseUrls($"http://0.0.0.0:{settings.Port}");
builder.Logging.ClearProviders().AddSimpleConsole();   // requests may carry member data: no request logging
var app = builder.Build();

app.MapGet("/health", () => Results.Json(new { ok = true, queued = queue.Items().Count }));

app.MapPost("/webhooks/ino", async (HttpRequest req) =>
{
    using var ms = new MemoryStream();
    await req.Body.CopyToAsync(ms);
    var raw = ms.ToArray();
    if (settings.WebhookSecret is null) return Results.Json(new { error = "INO_WEBHOOK_SECRET is not set" }, statusCode: 500);
    var v = Webhook.Verify(raw, req.Headers.ToDictionary(h => h.Key, h => h.Value.ToString()), settings.WebhookSecret);
    if (!v.Ok) return Results.Json(new { error = v.Reason }, statusCode: 401);
    if (!seen.TryAdd(v.Id!, true)) return Results.Json(new { duplicate = true });
    var evt = JsonNode.Parse(raw)!.AsObject();
    if (evt["test"]?.GetValue<bool>() == true || req.Headers["X-Ino-Test"] == "true") return Results.Json(new { test = true });   // a test event: do not act on it
    // TODO: apply the event in {{platform}} (points balance, tier, campaign changed …), after answering.
    return Results.Json(new { received = evt["type"]?.GetValue<string>() });
});

// A paid order on the platform → one transaction.create. Network trouble → offline queue with the original time.
app.MapPost("/platform/order-paid", async (JsonObject order) =>
{
    var body = new JsonObject
    {
        ["requestId"] = order["requestId"]?.DeepClone(),   // keep it with the order, so a retry of the same order is safe
        ["orderNumber"] = order["orderNumber"]?.DeepClone(),
        ["customerId"] = order["customerId"]?.DeepClone(),
        ["transactionDate"] = order["paidAt"]?.DeepClone() ?? DateTimeOffset.UtcNow.ToString("O"),
        ["cart"] = order["cart"]?.DeepClone(),
        ["payments"] = order["payments"]?.DeepClone(),
    };
    foreach (var k in body.Where(p => p.Value is null).Select(p => p.Key).ToList()) body.Remove(k);
    var r = await ino.CallAsync("transaction.create", body);
    if (r.Status == 0 || r.Status >= 500)
    {
        body["requestId"] = r.RequestId;
        queue.Add("transaction.create", body);
        return Results.Json(new { queued = true, requestId = r.RequestId });
    }
    return Results.Json(new { status = r.Status, result = r.Body, requestId = r.RequestId });
});

app.MapPost("/platform/flush-queue", async () => Results.Json(new { sent = await queue.FlushAsync((op, b) => ino.CallAsync(op, b)) }));

// Retry the offline queue every minute; heartbeat every 5 minutes. Ino shows the connector as healthy, degraded (queue growing)
// or silent (no heartbeat for 15 minutes).
async Task Beat()
{
    var depth = queue.Items().Count;
    try { await ino.HeartbeatAsync(Name, version, Platform, depth > 100 ? "degraded" : "ok", depth > 100 ? $"{depth} sales waiting in the offline queue" : null, depth); }
    catch (Exception) { /* a missed heartbeat must not stop the connector */ }
}
_ = Task.Run(async () =>
{
    var tick = 0;
    await Beat();
    using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
    while (await timer.WaitForNextTickAsync())
    {
        try { await queue.FlushAsync((op, b) => ino.CallAsync(op, b)); } catch (Exception) { }
        if (++tick % 5 == 0) await Beat();
    }
});

Console.Error.WriteLine($"{Name} listening on :{settings.Port} (channel {settings.Channel})");
app.Run();
