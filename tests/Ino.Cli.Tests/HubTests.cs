using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Ino.Cli.Hub;

namespace Ino.Cli.Tests;

/// <summary>MCP/CLI P6a: `ino hub` (docs/MCP_CLI_P6A_LOG.md, K1, K4, T7).</summary>
public class HubTests
{
    const string Commit = "0123456789abcdef0123456789abcdef01234567";
    static readonly ECDsa Key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    static string ExtraKey => "dev-1:" + Convert.ToBase64String(Key.ExportSubjectPublicKeyInfo());

    static string Envelope(long version, params string[] ids)
    {
        var now = DateTimeOffset.UtcNow;
        var entries = new JsonArray(ids.Select(id => (JsonNode)new JsonObject
        {
            ["id"] = id, ["name"] = "Demo " + id, ["kind"] = id.StartsWith("mail", StringComparison.Ordinal) ? "marketing" : "sales", ["category"] = "ECOMMERCE",
            ["vendor"] = "Harbor Lane Outfitters", ["languages"] = new JsonArray("python"), ["license"] = "MIT", ["version"] = "1.0.0", ["suite"] = "ecommerce",
            ["approvedAt"] = "2026-11-20T00:00:00Z", ["reviewer"] = "ST",
            ["source"] = new JsonObject { ["repository"] = $"https://github.com/harbor-lane/{id}", ["commit"] = Commit, ["tag"] = "v1.0.0", ["mirror"] = $"https://github.com/TryinoTeam/hub-{id}" },
        }).ToArray());
        var payload = Encoding.UTF8.GetBytes(new JsonObject
        {
            ["version"] = version, ["issuedAt"] = now.AddHours(-1).ToString("O"), ["expiresAt"] = now.AddDays(20).ToString("O"), ["entries"] = entries, ["withdrawn"] = new JsonArray(),
        }.ToJsonString());
        return HubEnvelope.Sign(payload, Key, "dev-1");
    }

    static Harness WithCatalog(Func<string> body)
    {
        var h = new Harness { Hub = new RouteHandler((_, _) => RouteHandler.Json(body())) };
        h.Env["INO_HUB_EXTRA_KEYS"] = ExtraKey;
        return h;
    }

    [Fact]
    public async Task List_verifies_the_catalog_says_test_catalog_and_hides_marketing()
    {
        using var h = WithCatalog(() => Envelope(3, "shop-sync", "mail-bridge"));
        var (code, output, _) = await h.Run("hub", "list");
        Assert.Equal(ExitCodes.Ok, code);
        Assert.Contains("TEST catalog v3", output);
        Assert.Contains("shop-sync", output);
        Assert.DoesNotContain("mail-bridge", output);
    }

    [Fact]
    public async Task A_replayed_older_catalog_is_refused_and_an_untrusted_one_shows_nothing()
    {
        using var h = WithCatalog(() => Envelope(4, "shop-sync"));
        Directory.CreateDirectory(Path.Combine(h.Dir, "ino", "state"));
        File.WriteAllText(Path.Combine(h.Dir, "ino", "state", "hub-version"), "5");   // this machine already saw a Tryino catalog v5 (T3)
        var (code, output, err) = await h.Run("hub", "list");
        Assert.Equal(ExitCodes.Business, code);
        Assert.Contains("Rollback", err);
        Assert.DoesNotContain("shop-sync", output);

        // A test catalog (extra key) never raises the remembered version: a later, lower Tryino version still passes.
        using var t = WithCatalog(() => Envelope(900, "shop-sync"));
        Assert.Equal(ExitCodes.Ok, (await t.Run("hub", "list")).Code);
        Assert.False(File.Exists(Path.Combine(t.Dir, "ino", "state", "hub-version")));

        using var unsigned = new Harness { Hub = new RouteHandler((_, _) => RouteHandler.Json(Envelope(1, "shop-sync"))) };   // key not trusted
        Assert.Equal(ExitCodes.Business, (await unsigned.Run("hub", "list")).Code);
    }

    [Fact]
    public async Task Get_prints_the_mirror_and_the_exact_commit_and_runs_nothing()
    {
        using var h = WithCatalog(() => Envelope(1, "shop-sync"));
        var (code, output, _) = await h.Run("hub", "get", "shop-sync");
        Assert.Equal(ExitCodes.Ok, code);
        Assert.Contains("git clone https://github.com/TryinoTeam/hub-shop-sync shop-sync", output);
        Assert.Contains($"git checkout {Commit}", output);
        Assert.Equal(ExitCodes.NotFound, (await h.Run("hub", "get", "no-such")).Code);
    }

    [Theory]
    [InlineData("git@github.com:harbor-lane/shop-sync.git", "https://github.com/harbor-lane/shop-sync")]
    [InlineData("https://user:ghp_secret123@github.com/harbor-lane/shop-sync.git", "https://github.com/harbor-lane/shop-sync")]
    [InlineData("https://gitlab.com/harbor-lane/shop-sync/", "https://gitlab.com/harbor-lane/shop-sync")]
    [InlineData("https://git.internal.example/x/y", null)]
    [InlineData("http://github.com/x/y", null)]
    public void Remotes_become_public_https_urls_without_credentials(string remote, string? expected) =>
        Assert.Equal(expected, HubTool.PublicRepositoryUrl(remote));

    static string Project(string description = "Sends orders to Ino.")
    {
        var dir = Directory.CreateTempSubdirectory("ino-hub").FullName;
        File.WriteAllText(Path.Combine(dir, "ino-connector.json"), new JsonObject
        {
            ["id"] = "shop-sync", ["name"] = "Shop sync", ["kind"] = "sales", ["category"] = "ECOMMERCE", ["platform"] = "Harbor Lane Commerce", ["language"] = "node",
            ["version"] = "1.0.0", ["license"] = "MIT", ["repository"] = "", ["description"] = description,
        }.ToJsonString());
        return dir;
    }

    static Func<string, string[], string, (int, string)> Git(string remote = "https://user:tok_abcdef123@github.com/harbor-lane/shop-sync.git", string status = "", bool pushed = true) => (_, args, _) => args[2..] switch
    {
        ["rev-parse", "--show-toplevel"] => (0, "/x\n"),
        ["status", "--porcelain"] => (0, status),
        ["rev-parse", "HEAD"] => (0, Commit + "\n"),
        ["remote", "get-url", "origin"] => (0, remote + "\n"),
        ["describe", "--tags", "--exact-match", "HEAD"] => (0, "v1.0.0\n"),
        ["branch", "-r", "--contains", "HEAD"] => (0, pushed ? "  origin/main\n" : ""),
        _ => (1, ""),
    };

    [Fact]
    public async Task Submit_sends_an_allow_listed_body_with_no_tenant_channel_request_ids_or_credentials()   // T7
    {
        using var h = new Harness { Capture = Git() };
        var intake = new RouteHandler((_, _) => RouteHandler.Json("{\"submissionId\":\"HS-1A2B\"}"));
        h.Hub = intake;
        h.Api = new RouteHandler((req, _) => req.RequestUri!.AbsolutePath.EndsWith("/whoami", StringComparison.Ordinal)
            ? RouteHandler.Json("{\"ResponseCode\":\"SUCCESS\",\"Data\":{\"tenant\":\"tabc1234\",\"environment\":\"test\",\"channel\":\"MODERN_ECOM\"}}")
            : RouteHandler.Json("{\"ResponseCode\":\"SUCCESS\",\"Data\":{\"suite\":\"ecommerce\",\"hours\":720,\"scope\":\"key ik_tabc1234\",\"channel\":\"MODERN_ECOM\",\"passed\":1,\"total\":2," +
                "\"scenarios\":[{\"code\":\"E1\",\"title\":\"Basket\",\"passed\":true,\"evidence\":\"1000+ sales\",\"example\":\"ORDER-77812\"},{\"code\":\"E2\",\"title\":\"Sale\",\"passed\":false}]}}"));
        await h.AddProfile();
        var dir = Project();
        var (code, output, err) = await h.Run("hub", "submit", "--dir", dir, "--company", "Harbor Lane Outfitters", "--contact-name", "Dana Whitfield",
            "--contact-email", "dana.whitfield@harborlane.example", "--yes");
        Assert.True(code == ExitCodes.Ok, err);
        Assert.Contains("HS-1A2B", output);
        var sent = Encoding.UTF8.GetString(Assert.Single(intake.Seen).Body);
        foreach (var leak in new[] { "tabc1234", "MODERN_ECOM", "ORDER-77812", "1000+", "tok_abcdef123", "ik_", "engine.example.test" })
            Assert.DoesNotContain(leak, sent);
        var body = JsonNode.Parse(sent)!;
        Assert.Equal("https://github.com/harbor-lane/shop-sync", body["source"]!["repository"]!.GetValue<string>());
        Assert.Equal(Commit, body["source"]!["commit"]!.GetValue<string>());
        Assert.Equal("test", body["compatibility"]!["environment"]!.GetValue<string>());
        Assert.Equal(2, body["compatibility"]!["scenarios"]!.AsArray().Count);
    }

    [Fact]
    public async Task Submit_refuses_secrets_dirty_trees_and_unconfirmed_sends_and_reports_intake_failures()
    {
        using var h = new Harness { Capture = Git(), Hub = new RouteHandler((_, _) => RouteHandler.Json("{}", HttpStatusCode.ServiceUnavailable)) };
        string[] Args(string dir, params string[] more) => ["hub", "submit", "--dir", dir, "--company", "Harbor Lane", "--contact-name", "Dana Whitfield",
            "--contact-email", "dana.whitfield@harborlane.example", "--no-check", .. more];

        var leaky = await h.Run(Args(Project("Use key ik_tabc1234_9f8e7d6c5b4a to test"), "--yes"));
        Assert.Equal(ExitCodes.Usage, leaky.Code);
        Assert.Contains("looks like a key", leaky.Err);

        Assert.Equal(ExitCodes.Usage, (await h.Run(Args(Project()))).Code);   // no --yes and not a terminal: never sends unconfirmed
        var dry = await h.Run(Args(Project(), "--dry-run"));
        Assert.Equal(ExitCodes.Ok, dry.Code);
        Assert.Contains("nothing sent", dry.Out);

        var down = await h.Run(Args(Project(), "--yes"));
        Assert.Equal(ExitCodes.Network, down.Code);
        Assert.Contains("nothing was recorded", down.Err);

        using var dirty = new Harness { Capture = Git(status: " M src/server.js\n") };
        Assert.Contains("uncommitted changes", (await dirty.Run(Args(Project(), "--dry-run"))).Err);
        using var local = new Harness { Capture = Git(pushed: false) };
        Assert.Contains("not on the remote", (await local.Run(Args(Project(), "--dry-run"))).Err);
        using var foreign = new Harness { Capture = Git(remote: "https://git.internal.example/x/y.git") };
        Assert.Contains("'origin' remote", (await foreign.Run(Args(Project(), "--dry-run"))).Err);
    }
}
