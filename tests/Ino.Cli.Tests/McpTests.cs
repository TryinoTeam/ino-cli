using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ino.Cli;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Ino.Cli.Tests;

/// <summary>A tiny local engine for MCP tests: answers whoami with a chosen environment and records every request.</summary>
sealed class LocalEngine : IDisposable
{
    readonly HttpListener http = new();
    public readonly List<string> Paths = [];
    public string Url { get; }
    public string Environment { get; set; } = "test";

    public LocalEngine()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        Url = $"http://127.0.0.1:{port}";
        http.Prefixes.Add(Url + "/");
        http.Start();
        _ = Task.Run(async () =>
        {
            while (http.IsListening)
            {
                HttpListenerContext c;
                try { c = await http.GetContextAsync(); } catch { return; }
                lock (Paths) Paths.Add(c.Request.Url!.AbsolutePath);
                var body = c.Request.Url!.AbsolutePath.EndsWith("/whoami", StringComparison.Ordinal)
                    ? "{\"ResponseCode\":\"SUCCESS\",\"Data\":{\"tenant\":\"tabc1234\",\"environment\":\"" + Environment + "\",\"channel\":\"MODERN_ECOM\"}}"
                    : """{"ResponseCode":"SUCCESS","httpStatus":200,"email":"jane@example.com","note":"ik_leaked_key_value_123 and whsec_QUJDREVGR0hJSktMTU5PUFFSU1RVVldY"}""";
                var bytes = Encoding.UTF8.GetBytes(body);
                c.Response.ContentType = "application/json";
                await c.Response.OutputStream.WriteAsync(bytes);
                c.Response.Close();
            }
        });
    }

    public void Dispose() { try { http.Stop(); } catch { } }
}

/// <summary>`ino mcp serve` driven by the SDK's own stdio client: a real process, real JSON-RPC on stdin/stdout.</summary>
public class McpTests
{
    /// <summary>A stored profile in a temp config folder (file store, so the child process needs no keychain).</summary>
    static string Config(string baseUrl, string environment, bool mcpWrites)
    {
        var dir = Directory.CreateTempSubdirectory("ino-mcp-test").FullName;
        Directory.CreateDirectory(Path.Combine(dir, "ino"));
        File.WriteAllText(Path.Combine(dir, "ino", "config.json"), JsonSerializer.Serialize(new
        {
            version = 1, defaultProfile = "p",
            profiles = new Dictionary<string, object> { ["p"] = new { baseUrl = baseUrl + "/t/tabc1234", channel = "MODERN_ECOM", environment, keyHint = "ik_mcp_test…", store = "file", mcpWrites } },
        }));
        File.WriteAllText(Path.Combine(dir, "ino", "credentials.json"), JsonSerializer.Serialize(new Dictionary<string, object>
            { ["p"] = new { ApiKey = "ik_mcp_test_0000", Secret = "mcp-test-secret-123" } }));
        return dir;
    }

    static async Task<McpClient> Start(string configDir, Dictionary<string, string?>? env = null, params string[] extra)
    {
        var vars = new Dictionary<string, string?> { ["XDG_CONFIG_HOME"] = configDir, ["INO_WEBHOOK_SECRET"] = "whsec_" + Convert.ToBase64String(new byte[32]) };
        foreach (var (k, v) in env ?? []) vars[k] = v;
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "ino", Command = "dotnet", Arguments = [typeof(Commands).Assembly.Location, "mcp", "serve", .. extra], EnvironmentVariables = vars,
        });
        return await McpClient.CreateAsync(transport);
    }

    static string Text(CallToolResult r) => string.Join("", r.Content.OfType<TextContentBlock>().Select(c => c.Text));
    static Dictionary<string, object?> Args(params (string, object?)[] a) => a.ToDictionary(x => x.Item1, x => x.Item2);

    [Fact]
    public async Task Handshake_lists_read_only_tools_and_no_write_tool_by_default()
    {
        using var engine = new LocalEngine();
        await using var c = await Start(Config(engine.Url, "test", mcpWrites: true));
        var tools = await c.ListToolsAsync();
        Assert.Contains(tools, t => t.Name == "ino_api_read");
        Assert.DoesNotContain(tools, t => t.Name == "ino_api_write");
        Assert.DoesNotContain(tools, t => t.Name == "ino_webhooks_list");
        Assert.All(tools, t => Assert.True(t.ProtocolTool.Annotations?.ReadOnlyHint));
        Assert.Contains("untrusted", c.ServerInstructions);
    }

    [Fact]
    public async Task Writes_need_the_profile_opt_in_and_the_flag_and_an_engine_confirmed_test()
    {
        using var engine = new LocalEngine();
        await using (var c = await Start(Config(engine.Url, "test", mcpWrites: true), null, "--allow-writes"))
        {
            var write = (await c.ListToolsAsync()).Single(t => t.Name == "ino_api_write");
            Assert.True(write.ProtocolTool.Annotations?.DestructiveHint);
            var r = await c.CallToolAsync("ino_api_write", Args(("operation", "transaction.create"), ("body", "{\"orderNumber\":\"A1\"}")));
            Assert.False(r.IsError, Text(r));
            Assert.Matches("\"requestId\":\"[0-9a-f-]{36}\"", Text(r));
        }
        // Profile says test but the engine says live: live rules, no write tool.
        engine.Environment = "live";
        await using (var c = await Start(Config(engine.Url, "test", mcpWrites: true), null, "--allow-writes"))
            Assert.DoesNotContain(await c.ListToolsAsync(), t => t.Name == "ino_api_write");
        // Without the profile's opt-in the server refuses to start with --allow-writes.
        engine.Environment = "test";
        await Assert.ThrowsAnyAsync<Exception>(() => Start(Config(engine.Url, "test", mcpWrites: false), null, "--allow-writes"));
    }

    [Fact]
    public async Task Engine_that_cannot_confirm_means_live_rules()
    {
        await using var c = await Start(Config("http://127.0.0.1:9", "test", mcpWrites: true), null, "--toolsets", "dev,integration");
        var list = JsonNode.Parse(Text(await c.CallToolAsync("ino_api_list", Args())))!;
        Assert.Equal("live", list["environment"]!.ToString());
        Assert.Equal(["order.loyalty", "transaction.return.inquiry", "transaction.return.quote"],
            list["operations"]!.AsArray().Where(o => o!["callableNow"]!.GetValue<bool>()).Select(o => o!["operation"]!.ToString()).Order(StringComparer.Ordinal));
        var r = await c.CallToolAsync("ino_api_read", Args(("operation", "game.programs"), ("body", "{}")));
        Assert.True(r.IsError);
    }

    [Fact]
    public async Task Personal_data_and_voucher_touching_operations_are_not_reads()
    {
        using var engine = new LocalEngine();
        await using var c = await Start(Config(engine.Url, "test", mcpWrites: false));
        foreach (var op in new[] { "customer.get360", "campaign.inquiry", "voucher.validate", "transaction.create" })
        {
            var r = await c.CallToolAsync("ino_api_read", Args(("operation", op), ("body", "{}")));
            Assert.True(r.IsError, op);
        }
        Assert.DoesNotContain(engine.Paths, p => p.EndsWith("/customer.get360", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Environment_credentials_and_project_file_are_ignored()
    {
        using var engine = new LocalEngine();
        var cfg = Config(engine.Url, "test", mcpWrites: false);
        var env = new Dictionary<string, string?> { ["INO_BASE_URL"] = "http://127.0.0.1:1", ["INO_CHANNEL"] = "OTHER", ["INO_API_KEY"] = "ik_env_000000000", ["INO_HMAC_SECRET"] = "env-secret-1", ["INO_ENVIRONMENT"] = "test" };
        await using var c = await Start(cfg, env);
        var r = await c.CallToolAsync("ino_api_read", Args(("operation", "order.loyalty"), ("body", "{\"orderNumber\":\"A1\"}")));
        Assert.Contains(engine.Paths, p => p == "/t/tabc1234/api/v1/MODERN_ECOM/order.loyalty");
        Assert.Contains("\"profile\":\"p\"", Text(r));
    }

    [Fact]
    public async Task Engine_data_is_untrusted_and_secret_shapes_are_masked()
    {
        using var engine = new LocalEngine();
        await using var c = await Start(Config(engine.Url, "test", mcpWrites: false));
        var text = Text(await c.CallToolAsync("ino_api_read", Args(("operation", "order.loyalty"), ("body", "{\"orderNumber\":\"A1\"}"))));
        var o = JsonNode.Parse(text)!;
        Assert.NotNull(o["untrusted"]);
        Assert.DoesNotContain("ik_leaked_key_value_123", text);
        Assert.DoesNotContain("whsec_QUJD", text);
        Assert.DoesNotContain("mcp-test-secret-123", text);
        Assert.Equal("test", o["environment"]!.ToString());
    }

    [Fact]
    public async Task Errors_are_tool_errors_with_a_code()
    {
        await using var c = await Start(Config("http://127.0.0.1:9", "test", mcpWrites: false));
        var r = await c.CallToolAsync("ino_api_read", Args(("operation", "order.loyalty"), ("body", "{\"orderNumber\":\"A1\"}")));
        Assert.True(r.IsError);
        Assert.Equal("NETWORK", JsonNode.Parse(Text(r))!["error"]!.ToString());
        Assert.DoesNotContain("mcp-test-secret-123", Text(r));
    }

    [Fact]
    public async Task Docs_samples_and_webhook_check_work_offline_and_take_no_secret_argument()
    {
        await using var c = await Start(Config("http://127.0.0.1:9", "test", mcpWrites: false));
        Assert.Contains("X-Nonce", Text(await c.CallToolAsync("ino_docs_search", Args(("query", "nonce signature")))));
        Assert.Contains("HMAC", Text(await c.CallToolAsync("ino_code_sample", Args(("kind", "sign_call"), ("language", "csharp")))), StringComparison.OrdinalIgnoreCase);
        var verify = (await c.ListToolsAsync()).Single(t => t.Name == "ino_verify_webhook");
        Assert.DoesNotContain("\"secret", verify.ProtocolTool.InputSchema.ToString(), StringComparison.OrdinalIgnoreCase);
        var secret = "whsec_" + Convert.ToBase64String(new byte[32]);
        var body = "{\"id\":\"e1\"}";
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var headers = new JsonObject { ["webhook-id"] = "msg_1", ["webhook-timestamp"] = ts.ToString(), ["webhook-signature"] = StandardWebhooks.Sign(secret, "msg_1", ts, Encoding.UTF8.GetBytes(body)) };
        Assert.Contains("\"signature\":\"valid\"", Text(await c.CallToolAsync("ino_verify_webhook", Args(("body", body), ("headers", headers.ToJsonString())))));
    }

    [Fact]
    public void Webhook_urls_lose_their_query()
    {
        var body = JsonNode.Parse("{\"Data\":{\"subscriptions\":[{\"url\":\"https://rx.example.test/hook?token=abc123\",\"id\":\"s1\"}]}}");
        var o = McpServe.StripUrlQueries(body)!.ToJsonString();
        Assert.DoesNotContain("abc123", o);
        Assert.Contains("https://rx.example.test/hook?[query removed]", o);
    }
}
