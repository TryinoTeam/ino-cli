using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Ino.Cli.Hub;

namespace Ino.Cli.Tests;

/// <summary>
/// IL9b (docs/INTEGRATION_LAYER.md §14.2-14.3): a definition package in the signed Hub catalog. `ino hub get` downloads the file the catalog names
/// from the mirror's raw address, checks size, SHA-256 and the file's own id/version/category/kind against the catalog, and writes it only then
/// (T35-T37); `--as-own` replaces only the top-level id's bytes (v2 M2); an existing file is never overwritten.
/// </summary>
public class HubDefinitionTests
{
    const string Commit = "89abcdef0123456789abcdef0123456789abcdef";
    const string Id = "harborlane_harbor-pos";
    static readonly ECDsa Key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    static string ExtraKey => "dev-1:" + Convert.ToBase64String(Key.ExportSubjectPublicKeyInfo());

    // Non-ASCII text and a nested "id" on purpose: the splice must not touch either (v2 M2).
    static readonly byte[] File1 = Encoding.UTF8.GetBytes("""
        {
          "id": "harborlane_harbor-pos", "name": "Harbor POS night file", "vendor": "Harbor Lane", "category": "POS", "version": "1.2.0", "schemaVersion": 1,
          "supportLevel": "community", "description": "Gece dosyası — Şube kasaları", "settings": [],
          "posFile": { "columns": { "RecordType": "Kind", "OrderNumber": "ReceiptNo", "id": "Id" }, "recordTypes": { "SALE_LINE": ["S"] } }
        }
        """);

    static string Envelope(byte[] file, Action<JsonObject>? change = null)
    {
        var now = DateTimeOffset.UtcNow;
        var entry = new JsonObject
        {
            ["id"] = Id, ["name"] = "Harbor POS night file", ["kind"] = "sales", ["category"] = "POS", ["vendor"] = "Harbor Lane", ["license"] = "MIT", ["version"] = "1.2.0",
            ["approvedAt"] = "2026-11-25T00:00:00Z", ["reviewer"] = "ST", ["runtime"] = "definition",
            ["source"] = new JsonObject { ["repository"] = "https://github.com/harbor-lane/harbor-pos", ["commit"] = Commit, ["mirror"] = $"https://github.com/TryinoTeam/hub-{Id}", ["path"] = "connectors/harbor-pos/connector.json" },
            ["definition"] = new JsonObject { ["format"] = 1, ["shape"] = "posFile", ["category"] = "POS", ["version"] = "1.2.0", ["bytes"] = file.Length,
                ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(file)) },
        };
        change?.Invoke(entry);
        var payload = Encoding.UTF8.GetBytes(new JsonObject
        {
            ["version"] = 5, ["issuedAt"] = now.AddHours(-1).ToString("O"), ["expiresAt"] = now.AddDays(20).ToString("O"), ["entries"] = new JsonArray(entry), ["withdrawn"] = new JsonArray(),
        }.ToJsonString());
        return HubEnvelope.Sign(payload, Key, "dev-1");
    }

    sealed record Served(byte[] Body, HttpStatusCode Status = HttpStatusCode.OK);

    static Harness With(string envelope, Served file, List<Uri>? seen = null)
    {
        var h = new Harness
        {
            Hub = new RouteHandler((req, _) =>
            {
                if (req.RequestUri!.Host != "raw.githubusercontent.com" || !req.RequestUri.AbsolutePath.Contains("/connectors/", StringComparison.Ordinal))
                    return RouteHandler.Json(envelope);
                seen?.Add(req.RequestUri);
                var res = new HttpResponseMessage(file.Status) { Content = new ByteArrayContent(file.Body) };
                if (file.Status == HttpStatusCode.Found) res.Headers.Location = new Uri("https://evil.example.com/x.json");
                return res;
            }),
        };
        h.Env["INO_HUB_EXTRA_KEYS"] = ExtraKey;
        return h;
    }

    [Fact]
    public async Task Get_downloads_the_approved_file_from_the_mirror_and_as_own_replaces_only_the_top_level_id()
    {
        var seen = new List<Uri>();
        using var h = With(Envelope(File1), new(File1), seen);
        var (list, listed, _) = await h.Run("hub", "list");
        Assert.Equal(ExitCodes.Ok, list);
        Assert.Contains("[definition: posFile]", listed);

        var own = Path.Combine(h.Dir, "local_harbor-pos.connector.json");
        var (code, output, err) = await h.Run("hub", "get", Id, "--as-own", "local_harbor-pos", "--out", own);
        Assert.True(code == ExitCodes.Ok, err);
        Assert.Equal($"https://raw.githubusercontent.com/TryinoTeam/hub-{Id}/{Commit}/connectors/harbor-pos/connector.json", Assert.Single(seen).ToString());
        Assert.Contains("TEST catalog", output);
        Assert.Contains("POS file format", output);
        var written = await File.ReadAllBytesAsync(own);
        // Byte for byte the approved file except the top-level id; the nested "id" column and the Turkish text are untouched.
        Assert.Equal(Encoding.UTF8.GetString(File1).Replace("\"harborlane_harbor-pos\"", "\"local_harbor-pos\""), Encoding.UTF8.GetString(written));

        // Never over an existing file.
        Assert.Equal(ExitCodes.Usage, (await h.Run("hub", "get", Id, "--as-own", "local_harbor-pos", "--out", own)).Code);
        // Without --as-own the approved file is written as it is, and the person is told it needs their own id to be saved.
        var asIs = Path.Combine(h.Dir, Id + ".connector.json");
        var (plain, plainOut, _) = await h.Run("hub", "get", Id, "--out", asIs);
        Assert.Equal(ExitCodes.Ok, plain);
        Assert.Contains("--as-own", plainOut);
        Assert.Equal(File1, await File.ReadAllBytesAsync(asIs));
        Assert.Equal(ExitCodes.Usage, (await h.Run("hub", "get", Id, "--as-own", "acme_harbor", "--out", Path.Combine(h.Dir, "x.json"))).Code);
    }

    [Fact]
    public async Task A_changed_file_a_redirect_or_a_file_that_is_not_what_the_catalog_says_writes_nothing()
    {
        var tampered = (byte[])File1.Clone();
        tampered[^3] ^= 1;
        foreach (var (envelope, served, why) in new[]
        {
            (Envelope(File1), new Served(tampered), "DIGEST_MISMATCH"),
            (Envelope(File1), new Served(File1, HttpStatusCode.Found), "DOWNLOAD_FAILED"),
            (Envelope(File1), new Served([.. File1, (byte)' ']), "DIGEST_MISMATCH"),
            // The catalog says "connector" but the file is a POS file format (T37).
            (Envelope(File1, e => e["definition"]!["shape"] = "connector"), new Served(File1), "DEFINITION_MISMATCH"),
        })
        {
            using var h = With(envelope, served);
            var target = Path.Combine(h.Dir, "local_harbor-pos.connector.json");
            var (code, _, err) = await h.Run("hub", "get", Id, "--as-own", "local_harbor-pos", "--out", target, "--json");
            Assert.NotEqual(ExitCodes.Ok, code);
            Assert.Contains(why, err);
            Assert.False(File.Exists(target));
        }
    }

    [Theory]
    [InlineData("../harbor/connector.json")]
    [InlineData("/connectors/connector.json")]
    [InlineData("connectors//connector.json")]
    [InlineData("connectors/./connector.json")]
    [InlineData("connectors/connector.yaml")]
    public async Task An_entry_whose_path_or_mirror_could_lead_elsewhere_is_dropped(string path)
    {
        using var h = With(Envelope(File1, e => e["source"]!["path"] = path), new(File1));
        Assert.DoesNotContain(Id, (await h.Run("hub", "list")).Out);
        using var g = With(Envelope(File1, e => e["source"]!["mirror"] = $"https://gitlab.com/TryinoTeam/hub-{Id}"), new(File1));
        Assert.DoesNotContain(Id, (await g.Run("hub", "list")).Out);
    }
}
