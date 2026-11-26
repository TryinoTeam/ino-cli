// The tool's endpoint for Ino Loyalty. Register its public https address with `ino marketing connect --url https://…/ino`, then run
// `ino marketing verify <id>`. Locally: `ino connector test --kind marketing --url http://localhost:8080/ino --env-file <file>`.
using System.Text.Json;
using System.Text.Json.Nodes;
using Connector;

Server.LoadEnv();
var port = int.TryParse(Environment.GetEnvironmentVariable("PORT"), out var p) ? p : 8080;
var app = Server.Build(Environment.GetEnvironmentVariable("INO_MA_SIGNING_SECRET"), new Tool(), $"http://0.0.0.0:{port}");
Console.WriteLine($"listening on :{port} (POST /ino, GET /health)");
await app.RunAsync();

public static class Server
{
    const int MaxBody = 64 * 1024;

    /// <summary>Reads a .env file in the working folder; values already in the environment win.</summary>
    public static void LoadEnv(string path = ".env")
    {
        if (!File.Exists(path)) return;
        foreach (var line in File.ReadAllLines(path))
        {
            var m = System.Text.RegularExpressions.Regex.Match(line, @"^\s*([A-Z0-9_]+)\s*=\s*(.*?)\s*$");
            if (m.Success && Environment.GetEnvironmentVariable(m.Groups[1].Value) is null) Environment.SetEnvironmentVariable(m.Groups[1].Value, m.Groups[2].Value);
        }
    }

    public static WebApplication Build(string? secret, Tool tool, string url)
    {
        var signature = new Signature(secret);   // a missing or malformed secret stops the server at start, not at the first request
        var contract = new Contract(tool, signature);
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls(url);
        builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = MaxBody);
        builder.Logging.ClearProviders().AddSimpleConsole();   // never log request bodies: they hold personal data
        var app = builder.Build();

        app.MapGet("/health", () => Results.Json(new JsonObject { ["status"] = "ok" }));
        app.MapPost("/ino", async (HttpRequest req) =>
        {
            byte[] raw;
            try
            {
                using var ms = new MemoryStream();
                await req.Body.CopyToAsync(ms);
                raw = ms.ToArray();
            }
            catch (BadHttpRequestException e) when (e.StatusCode == StatusCodes.Status413PayloadTooLarge)
            {
                return Results.Json(new JsonObject { ["error"] = "too large" }, statusCode: 413);
            }
            var check = signature.Verify(raw, name => req.Headers.TryGetValue(name, out var v) ? v.ToString() : null);
            if (!check.Ok) return Results.Json(new JsonObject { ["error"] = check.Reason }, statusCode: 401);
            JsonObject? request;
            try { request = JsonNode.Parse(raw) as JsonObject; }
            catch (JsonException) { return Results.Json(new JsonObject { ["error"] = "not JSON" }, statusCode: 400); }
            try
            {
                var reply = await contract.HandleAsync(request);
                return Results.Json(reply.Body, statusCode: reply.Status);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Console.Error.WriteLine($"ino request failed: {e.GetType().Name}: {e.Message}");   // never log the request body
                return Results.Json(new JsonObject { ["error"] = "try again later" }, statusCode: 503);
            }
        });
        return app;
    }
}
