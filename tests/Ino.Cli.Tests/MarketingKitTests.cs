using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Ino.Cli.Tests;

/// <summary>MCP/CLI P1c.2 (docs/MCP_CLI_P1C_LOG.md): `ino connector new --kind marketing` and `ino connector test --kind marketing`.</summary>
public class MarketingKitTests
{
    static string NewSecret() => "whsec_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    /// <summary>A tool that takes everything (no signature check, no profile id): what the compliance test exists to catch.</summary>
    sealed class Careless : HttpMessageHandler
    {
        public readonly List<string> Bodies = [];
        public readonly List<string> Headers = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = await request.Content!.ReadAsStringAsync(ct);
            lock (Bodies) { Bodies.Add(body); Headers.Add(request.Headers.ToString()); }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public async Task New_marketing_writes_the_tool_template_without_any_secret()
    {
        using var h = new Harness();
        var dir = Path.Combine(h.Dir, "mailer");
        var (code, output, err) = await h.Run("connector", "new", "acme-mailer", "--kind", "marketing", "--platform", "Acme Mail", "--dir", dir);
        Assert.True(code == ExitCodes.Ok, err);
        foreach (var f in new[] { "src/server.js", "src/contract.js", "src/signature.js", "src/tool.js", "test/contract.test.mjs", "README.md", ".gitignore", ".env.example", "ino-connector.json" })
            Assert.True(File.Exists(Path.Combine(dir, f)), f);
        Assert.Contains(".env", await File.ReadAllTextAsync(Path.Combine(dir, ".gitignore")));
        Assert.Contains("\"kind\": \"marketing\"", await File.ReadAllTextAsync(Path.Combine(dir, "ino-connector.json")));
        // No secret value anywhere (the template only reads it from the environment; its own test makes a random one at run time).
        Assert.DoesNotContain(Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories), f => System.Text.RegularExpressions.Regex.IsMatch(File.ReadAllText(f), "whsec_[A-Za-z0-9+/]{20,}"));
        Assert.Contains("ino connector test --kind marketing", output);
        Assert.Equal(ExitCodes.Usage, (await h.Run("connector", "new", "acme-mailer2", "--kind", "marketing", "--lang", "csharp", "--dir", Path.Combine(h.Dir, "cs"))).Code);
        var py = Path.Combine(h.Dir, "py");
        Assert.Equal(ExitCodes.Ok, (await h.Run("connector", "new", "acme-mailer3", "--kind", "marketing", "--lang", "python", "--dir", py)).Code);
        foreach (var f in new[] { "connector/server.py", "connector/contract.py", "connector/signature.py", "connector/tool.py", "tests/test_contract.py", ".gitignore" })
            Assert.True(File.Exists(Path.Combine(py, f)), f);
    }

    [Fact]
    public async Task A_careless_tool_fails_and_the_secret_is_never_printed_or_sent()
    {
        using var h = new Harness();
        var careless = new Careless();
        h.Local = careless;
        var secret = NewSecret();
        var env = Path.Combine(h.Dir, "ma.env");
        await File.WriteAllTextAsync(env, $"INO_MA_CONNECTION_ID=conn-1\nINO_MA_SIGNING_SECRET={secret}\n");
        var (code, output, err) = await h.Run("connector", "test", "--kind", "marketing", "--url", "http://localhost:18999/ino", "--env-file", env);
        Assert.Equal(ExitCodes.Business, code);
        Assert.Contains("✗ M1", output);       // took an unsigned request
        Assert.Contains("✗ M4", output);       // no proof
        Assert.Contains("✗ M5", output);       // no profileId
        Assert.Contains("skipped", output);    // the profile steps did not run without a profile
        Assert.DoesNotContain(secret, output + err);
        Assert.DoesNotContain(careless.Bodies, b => b.Contains(secret[6..]));
        Assert.DoesNotContain(careless.Headers, x => x.Contains(secret[6..]));
        Assert.Contains(careless.Bodies, b => b.Contains("\"connectionId\":\"conn-1\""));
    }

    [Fact]
    public async Task Addresses_off_this_machine_need_https_and_a_yes()
    {
        using var h = new Harness();
        h.Local = new Careless();
        var env = Path.Combine(h.Dir, "ma.env");
        await File.WriteAllTextAsync(env, $"INO_MA_SIGNING_SECRET={NewSecret()}\n");
        Assert.Equal(ExitCodes.Usage, (await h.Run("connector", "test", "--kind", "marketing", "--url", "http://tool.example.com/ino", "--env-file", env)).Code);
        var (code, _, err) = await h.Run("connector", "test", "--kind", "marketing", "--url", "https://tool.example.com/ino", "--env-file", env);
        Assert.Equal(ExitCodes.Usage, code);
        Assert.Contains("--yes", err);
        Assert.Equal(ExitCodes.Usage, (await h.Run("connector", "test", "--kind", "marketing", "--url", "http://localhost:18999/ino")).Code);   // no secret
    }

    [Fact]
    public void A_contract_test_report_becomes_the_hub_submissions_compatibility_without_evidence_text()
    {
        using var h = new Harness();
        var report = Path.Combine(h.Dir, "report.json");
        File.WriteAllText(report, MarketingCompliance.ToJson([new("M1", "Refuses an unsigned request", true, "HTTP 401"), new("M5", "Creates a profile", false, "no profileId avery@example.com")]).ToJsonString());
        var c = HubTool.MarketingCheck(report);
        Assert.Equal(("marketing", 2, 1), (c["suite"]!.ToString(), c["total"]!.GetValue<int>(), c["passed"]!.GetValue<int>()));
        Assert.DoesNotContain("avery@example.com", c.ToJsonString());      // evidence text never leaves the machine
        File.WriteAllText(report, "{\"something\":1}");
        Assert.Equal("BAD_REPORT", Assert.Throws<CliException>(() => HubTool.MarketingCheck(report)).Code);
    }

    [Fact]
    public void Event_id_matches_the_engines()
    {
        // The engine's CustomMarketingConnector.EventId: first 32 hex of sha256("{outboxId:D}|{name}").
        var outbox = Guid.Parse("8b0e1d0c-0000-4000-8000-000000000001");
        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("8b0e1d0c-0000-4000-8000-000000000001|points.earned")))[..32].ToLowerInvariant();
        Assert.Equal(expected, MarketingCompliance.EventId(outbox, "points.earned"));
    }

    [Theory]
    [InlineData("node", "node", "src/server.js")]
    [InlineData("python", "python3", "-m connector.server")]
    public async Task The_generated_tool_passes_every_check_when_its_runtime_is_installed(string lang, string runtime, string args)
    {
        if (!Available(runtime)) return;      // the template's own tests cover it where the runtime exists
        using var h = new Harness();
        var dir = Path.Combine(h.Dir, "mailer");
        Assert.Equal(ExitCodes.Ok, (await h.Run("connector", "new", "acme-mailer", "--kind", "marketing", "--lang", lang, "--dir", dir)).Code);
        var secret = NewSecret();
        var port = FreePort();
        using var server = Process.Start(new ProcessStartInfo(runtime, args) { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true,
            Environment = { ["INO_MA_SIGNING_SECRET"] = secret, ["PORT"] = port.ToString() } })!;
        try
        {
            await WaitForAsync(port);
            var env = Path.Combine(h.Dir, "ma.env");
            await File.WriteAllTextAsync(env, $"INO_MA_SIGNING_SECRET={secret}\n");
            var (code, output, err) = await h.Run("connector", "test", "--kind", "marketing", "--url", $"http://localhost:{port}/ino", "--env-file", env);
            Assert.True(code == ExitCodes.Ok, output + err);
            Assert.Contains("All 13 checks passed.", output);
        }
        finally { server.Kill(); }
    }

    static bool Available(string runtime)
    {
        try { using var p = Process.Start(new ProcessStartInfo(runtime, "--version") { RedirectStandardOutput = true })!; p.WaitForExit(5000); return p.ExitCode == 0; }
        catch (Exception) { return false; }
    }

    static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    static async Task WaitForAsync(int port)
    {
        using var c = new HttpClient();
        for (var i = 0; i < 50; i++)
        {
            try { if ((await c.GetAsync($"http://127.0.0.1:{port}/health")).IsSuccessStatusCode) return; } catch (HttpRequestException) { }
            await Task.Delay(100);
        }
        throw new TimeoutException("the generated tool did not start");
    }
}
