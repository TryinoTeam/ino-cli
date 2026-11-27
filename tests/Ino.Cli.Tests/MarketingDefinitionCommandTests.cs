using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Ino.Cli.Tests;

/// <summary>
/// IL3b-3 (docs/INTEGRATION_LAYER.md §8): `ino marketing connect --definition`, `ino marketing key`, `ino marketing definition new`. The summary comes
/// first and connecting needs its review hash (T20, Gate T M2); the key is read from a variable or a file, sealed with the engine's transport key and
/// never appears in the command line, the request in plain text or the output (T18).
/// </summary>
public class MarketingDefinitionCommandTests
{
    static string Jwt(object claims) => "e30." + Convert.ToBase64String(Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(claims))).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".sig";

    const string ToolKey = "acme-live-key-7f3c9a1e5b2d";
    const string Id = "8b0e1d0c-0000-4000-8000-0000000000d1";
    const string Review = "r1:5f0c";

    sealed class Fake : IDisposable
    {
        public readonly RSA Rsa = RSA.Create(2048);
        public readonly Guid Kid = Guid.NewGuid();
        public readonly List<(string Path, string Body)> Posts = [];
        public int TransportKeyReads;
        public bool Existing;
        public string ReviewHash = Review;

        public RouteHandler Handler => new((req, body) =>
        {
            var token = Jwt(new { ino_tenant = "tabc1234", ino_env = "test", name = "Dana Whitfield", scope = "offline_access integration:read integration:write connector:install", exp = 1900000000 });
            var path = req.RequestUri!.AbsolutePath;
            var text = Encoding.UTF8.GetString(body);
            const string prefix = "/t/tabc1234/manage/v1/";
            switch (path)
            {
                case "/oauth/token": return RouteHandler.Json($$$"""{"access_token":"{{{token}}}","refresh_token":"rt-1"}""");
                case prefix + "transport-key":
                    TransportKeyReads++;
                    return RouteHandler.Json($$$"""{"data":{"kid":"{{{Kid}}}","publicKey":"{{{Convert.ToBase64String(Rsa.ExportSubjectPublicKeyInfo())}}}"}}""");
                case prefix + "marketing/definitions/local_acme-mailer/1.0.0/preview":
                    Posts.Add((path, text));
                    return RouteHandler.Json("""
                        {"data":{"definition":"local_acme-mailer","version":"1.0.0","sha256":"abcdef0123456789","hosts":["api.acme-mailer.example.com"],"accessHash":"a1:x",
                         "reviewHash":"__RH__","certified":false,"warning":"Ino checks only the subscription fields the package declares.",
                         "access":{"contact":["email"],"attributes":true,"operations":[{"operation":"upsertProfile","method":"POST","path":"/profiles","query":[],
                         "bodyPaths":["profile.email_status","profile.marketing_ok"]}],"constants":["unsubscribed = \"unsubscribed\""],
                         "consent":["profile.email_status (EMAIL) = \"unsubscribed\""],"events":["points.earned → ino_points_earned"]}}}
                        """.Replace("__RH__", ReviewHash));
                case prefix + "marketing/connections" when req.Method == HttpMethod.Post:
                    Posts.Add((path, text));
                    return RouteHandler.Json("""{"data":{"connectionId":"__ID__","definition":"local_acme-mailer","version":"1.0.0","hosts":["api.acme-mailer.example.com"],"status":"PENDING_CONFIRM","existing":__EX__}}"""
                        .Replace("__ID__", Id).Replace("__EX__", Existing ? "true" : "false"));
                case prefix + "marketing/connections/" + Id + "/repin/preview":
                    Posts.Add((path, text));
                    return RouteHandler.Json("""
                        {"data":{"connectionId":"__ID__","definition":"local_acme-mailer","from":"1.0.0","version":"1.1.0","sha256":"0123abcd","hosts":["api.acme-mailer.example.com"],
                         "reviewHash":"__RH__","statusAfter":"PAUSED","diff":{"previousUnavailable":false,"operationsAdded":[],"operationsRemoved":[],
                         "fieldsAdded":["POST /events (trackEvent): kind"],"fieldsRemoved":[],"eventsNewlySent":[],"eventsNoLongerSent":[],"settingsAdded":["region"],
                         "settingsDropped":[],"unsubscribeReadingLost":true,"unsubscribeReadingAdded":false,"identityChanged":false}}}
                        """.Replace("__ID__", Id).Replace("__RH__", ReviewHash));
                case prefix + "marketing/connections/" + Id + "/repin":
                    Posts.Add((path, text));
                    return RouteHandler.Json($$$"""{"data":{"connectionId":"{{{Id}}}","definition":"local_acme-mailer","from":"1.0.0","version":"1.1.0","sha256":"0123abcd","status":"PAUSED","unchanged":false}}""");
                case prefix + "marketing/connections/" + Id + "/key":
                    Posts.Add((path, text));
                    return RouteHandler.Json($$$"""{"data":{"connectionId":"{{{Id}}}","status":"PAUSED","verify":true}}""");
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        /// <summary>Opens a sealed key as the engine's TransportKeys.OpenAsync does (base64url; GCM tag at the end of ct).</summary>
        public string Open(JsonNode sealedKey)
        {
            Assert.Equal(Kid.ToString(), sealedKey["kid"]!.ToString());
            static byte[] B64(string s) { var b = s.Replace('-', '+').Replace('_', '/'); return Convert.FromBase64String(b.PadRight(b.Length + (4 - b.Length % 4) % 4, '=')); }
            var key = Rsa.Decrypt(B64(sealedKey["ek"]!.ToString()), RSAEncryptionPadding.OaepSHA256);
            var iv = B64(sealedKey["iv"]!.ToString());
            var data = B64(sealedKey["ct"]!.ToString());
            var plain = new byte[data.Length - 16];
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(iv, data.AsSpan(0, plain.Length), data.AsSpan(plain.Length), plain);
            return Encoding.UTF8.GetString(plain);
        }

        public Func<Uri, Task> Browser => async url =>
        {
            var q = System.Web.HttpUtility.ParseQueryString(url.Query);
            _ = Task.Run(async () => { using var c = new HttpClient(); await c.GetAsync($"{q["redirect_uri"]}?code=code-1&state={q["state"]}"); });
            await Task.CompletedTask;
        };

        public void Dispose() => Rsa.Dispose();
    }

    static async Task<(Harness H, Fake F)> SignedIn()
    {
        var h = new Harness();
        var f = new Fake();
        h.Browser = f.Browser; h.Auth = f.Handler;
        h.Env["ACME_KEY"] = ToolKey;
        await h.AddProfile();
        Assert.Equal(ExitCodes.Ok, (await h.Run("--profile", "dev", "login")).Code);
        return (h, f);
    }

    static string[] Connect(params string[] extra) =>
        ["--profile", "dev", "marketing", "connect", "--definition", "local_acme-mailer", "--version", "1.0.0", "--name", "Acme", "--setting", "apiUrl=https://api.acme-mailer.example.com/v2",
         "--processor", "Acme Mailer Inc.", "--country", "United States", "--key-env", "ACME_KEY", .. extra];

    [Fact]
    public async Task Without_the_review_hash_only_the_summary_is_shown_and_nothing_is_sealed_or_saved()
    {
        var (h, f) = await SignedIn();
        using var hh = h; using var ff = f;
        var (code, output, err) = await h.Run(Connect());
        Assert.Equal(ExitCodes.Usage, code);
        Assert.Contains("api.acme-mailer.example.com", output);
        Assert.Contains("[checked] profile.email_status", output);              // the declared subscription field
        Assert.Contains("          profile.marketing_ok", output);              // undeclared: shown without the mark
        Assert.Contains("NOTE", output);
        Assert.Contains($"--confirm {Review}", err);
        Assert.Equal(0, f.TransportKeyReads);
        Assert.DoesNotContain(f.Posts, p => p.Path.EndsWith("/marketing/connections", StringComparison.Ordinal));
        // A stale review (the package or the hosts changed since): shown again, nothing saved.
        var (stale, _, staleErr) = await h.Run(Connect("--confirm", "r1:old"));
        Assert.Equal(ExitCodes.Usage, stale);
        Assert.Contains("changed since that review", staleErr);
        Assert.DoesNotContain(f.Posts, p => p.Path.EndsWith("/marketing/connections", StringComparison.Ordinal));
    }

    [Fact]
    public async Task With_the_review_hash_the_key_is_sealed_for_the_engine_and_never_appears_in_plain_text()
    {
        var (h, f) = await SignedIn();
        using var hh = h; using var ff = f;
        var (code, output, err) = await h.Run(Connect("--confirm", Review));
        Assert.True(code == ExitCodes.Ok, err);
        var post = f.Posts.Single(p => p.Path.EndsWith("/marketing/connections", StringComparison.Ordinal));
        var body = JsonNode.Parse(post.Body)!;
        Assert.Equal(Review, body["reviewHash"]!.ToString());
        Assert.Equal("https://api.acme-mailer.example.com/v2", body["settings"]!["apiUrl"]!.ToString());
        Assert.Equal(ToolKey, f.Open(body["key"]!));                              // the engine opens exactly the key
        Assert.DoesNotContain(ToolKey, post.Body);
        Assert.DoesNotContain(ToolKey, output + err);
        Assert.Contains($"ino marketing verify {Id}", output);
        // --json output has no key either.
        var (jcode, joutput, jerr) = await h.Run([.. Connect("--confirm", Review), "--json"]);
        Assert.Equal(ExitCodes.Ok, jcode);
        Assert.DoesNotContain(ToolKey, joutput + jerr);
    }

    [Fact]
    public async Task A_rerun_finds_the_existing_connection_and_says_how_to_change_its_key()
    {
        var (h, f) = await SignedIn();
        using var hh = h; using var ff = f;
        f.Existing = true;
        var (code, output, _) = await h.Run(Connect("--confirm", Review));
        Assert.Equal(ExitCodes.Ok, code);
        Assert.Contains("already connected", output);
        Assert.Contains($"ino marketing key {Id}", output);
    }

    [Fact]
    public async Task The_key_comes_only_from_a_variable_or_a_regular_file_and_package_options_do_not_mix_with_an_own_tool()
    {
        var (h, f) = await SignedIn();
        using var hh = h; using var ff = f;
        Assert.Equal(ExitCodes.Usage, (await h.Run([.. Connect("--confirm", Review), "--url", "https://x.example.com"])).Code);
        var noSource = Connect("--confirm", Review).Where(a => a is not ("--key-env" or "ACME_KEY")).ToArray();
        var (code, _, err) = await h.Run(noSource);
        Assert.Equal(ExitCodes.Usage, code);
        Assert.Contains("never on the command line", err);
        if (!OperatingSystem.IsWindows())
        {
            var real = Path.Combine(h.Dir, "key.txt");
            await File.WriteAllTextAsync(real, ToolKey + "\n");
            File.SetUnixFileMode(real, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var link = Path.Combine(h.Dir, "key-link.txt");
            File.CreateSymbolicLink(link, real);
            var viaLink = noSource.Concat(new[] { "--key-file", link }).ToArray();
            Assert.Equal(ExitCodes.Usage, (await h.Run(viaLink)).Code);
            var viaFile = noSource.Concat(new[] { "--key-file", real }).ToArray();
            Assert.Equal(ExitCodes.Ok, (await h.Run(viaFile)).Code);
            Assert.Equal(ToolKey, f.Open(JsonNode.Parse(f.Posts.Last(p => p.Path.EndsWith("/marketing/connections", StringComparison.Ordinal)).Body)!["key"]!));   // trailing newline trimmed
        }
    }

    [Fact]
    public async Task A_new_key_is_sealed_too()
    {
        var (h, f) = await SignedIn();
        using var hh = h; using var ff = f;
        h.Env["ACME_KEY"] = "acme-rotated-key-0002";
        var (code, output, err) = await h.Run("--profile", "dev", "marketing", "key", Id, "--key-env", "ACME_KEY");
        Assert.True(code == ExitCodes.Ok, err);
        var post = f.Posts.Single(p => p.Path.EndsWith("/key", StringComparison.Ordinal));
        Assert.Equal("acme-rotated-key-0002", f.Open(JsonNode.Parse(post.Body)!["key"]!));
        Assert.DoesNotContain("acme-rotated-key-0002", post.Body + output + err);
        Assert.Contains($"ino marketing verify {Id}", output);
    }

    [Fact]
    public async Task Definition_new_writes_a_package_skeleton_and_refuses_a_bad_id_or_a_used_folder()
    {
        using var h = new Harness();
        var dir = Path.Combine(h.Dir, "acme");
        var (code, output, err) = await h.Run("marketing", "definition", "new", dir, "--id", "local_acme-mailer");
        Assert.True(code == ExitCodes.Ok, err);
        var json = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(dir, "connector.json")))!;
        Assert.Equal("local_acme-mailer", json["id"]!.ToString());
        Assert.Equal("Acme Mailer", json["name"]!.ToString());
        Assert.True(File.Exists(Path.Combine(dir, "RULES.md")));
        Assert.Contains("ino connector package save", output);
        Assert.Equal(ExitCodes.Usage, (await h.Run("marketing", "definition", "new", dir, "--id", "local_acme-mailer")).Code);
        Assert.Equal(ExitCodes.Usage, (await h.Run("marketing", "definition", "new", Path.Combine(h.Dir, "b"), "--id", "acme")).Code);
    }

    [Fact]
    public async Task Repin_shows_what_changes_first_and_moves_only_with_the_review_hash()
    {
        var (h, f) = await SignedIn();
        using var hh = h; using var ff = f;
        var (code, output, _) = await h.Run("--profile", "dev", "marketing", "repin", Id, "--version", "1.1.0", "--setting", "region=us");
        Assert.Equal(ExitCodes.Usage, code);
        Assert.Contains("1.0.0 → 1.1.0", output);
        Assert.Contains("POST /events (trackEvent): kind", output);
        Assert.Contains("no longer reads unsubscribes", output);
        Assert.DoesNotContain(f.Posts, p => p.Path.EndsWith("/repin", StringComparison.Ordinal));
        Assert.Equal("us", JsonNode.Parse(f.Posts.Single(p => p.Path.EndsWith("/repin/preview", StringComparison.Ordinal)).Body)!["settings"]!["region"]!.ToString());

        Assert.Equal(ExitCodes.Usage, (await h.Run("--profile", "dev", "marketing", "repin", Id, "--version", "1.1.0", "--confirm", "r1:old")).Code);
        var (ok, moved, err) = await h.Run("--profile", "dev", "marketing", "repin", Id, "--version", "1.1.0", "--setting", "region=us", "--confirm", Review);
        Assert.True(ok == ExitCodes.Ok, err);
        var body = JsonNode.Parse(f.Posts.Single(p => p.Path.EndsWith("/repin", StringComparison.Ordinal)).Body)!;
        Assert.Equal((Review, "1.1.0"), (body["reviewHash"]!.ToString(), body["version"]!.ToString()));
        Assert.Contains($"ino marketing verify {Id}", moved);
    }
}
