using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Ino.Cli.Tests;

/// <summary>`ino connector package save|list|delete` (docs/INO_HUB.md §22, H1b.2).</summary>
public class ConnectorPackageCommandTests
{
    static string Jwt(object claims) => "e30." + Convert.ToBase64String(Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(claims))).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".sig";

    sealed class Fake
    {
        public readonly List<(string Path, byte[] Body, string? ContentType)> Posts = [];
        public int FailFirstPosts;
        public readonly List<(string Path, string? Key, string Body)> Installs = [];   // preview and install calls with their Idempotency-Key
        public HttpResponseMessage? InstallAnswer;
        public RouteHandler Handler => new((req, body) =>
        {
            var form = System.Web.HttpUtility.ParseQueryString(Encoding.UTF8.GetString(body));
            var token = Jwt(new { ino_tenant = "tabc1234", ino_env = "test", name = "Dana Whitfield", scope = "offline_access integration:read integration:write", exp = 1900000000 });
            switch (req.RequestUri!.AbsolutePath)
            {
                case "/oauth/token" when form["grant_type"] == "authorization_code": return RouteHandler.Json($$"""{"access_token":"{{token}}","refresh_token":"rt-1"}""");
                case "/oauth/token": return RouteHandler.Json($$"""{"access_token":"{{token}}","refresh_token":"rt-x"}""");
                case "/t/tabc1234/manage/v1/connector-packages" when req.Method == HttpMethod.Get:
                    return RouteHandler.Json("""{"data":{"packages":[{"id":"local_pos","version":"1.0.0","status":"loaded","installations":0,"bytes":420}]}}""");
                case "/t/tabc1234/manage/v1/connector-packages" when req.Method == HttpMethod.Post:
                    Posts.Add((req.RequestUri.AbsolutePath, body, req.Content?.Headers.ContentType?.MediaType));
                    if (Posts.Count <= FailFirstPosts) throw new HttpRequestException("connection reset");
                    return RouteHandler.Json("""{"data":{"id":"local_pos","version":"1.0.0","sha256":"abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789","created":true,"access":{"requestHost":{"defaultHost":"pos.example.com"}}}}""");
                case "/t/tabc1234/manage/v1/connector-packages/local_pos/9.9.9/delete":
                    return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{"error":"not_found","error_description":"local_pos 9.9.9 is not saved in this environment."}""", Encoding.UTF8, "application/json") };
                case var p when (p.EndsWith("/preview", StringComparison.Ordinal) || p.EndsWith("/install", StringComparison.Ordinal)) && req.Method == HttpMethod.Post:
                    Installs.Add((p, req.Headers.TryGetValues("Idempotency-Key", out var k) ? k.Single() : null, Encoding.UTF8.GetString(body)));
                    if (InstallAnswer is { } answer) return answer;
                    return p.EndsWith("/preview", StringComparison.Ordinal)
                        ? RouteHandler.Json("""{"data":{"id":"local_pos","version":"1.0.0","sha256":"abcdef0123456789abcdef","host":"pos.example.com","accessHash":"a1:feed","channels":["POS"],"access":{"events":["transaction.completed"],"actions":[{"method":"POST","path":"/x","events":["transaction.completed"]}],"writes":[{"operation":"transaction.create","financial":true,"profile":false}],"reads":[],"memberMatch":[],"webhooks":false}}}""")
                        : RouteHandler.Json("""{"data":{"id":"6f1c0e0a-0000-4000-8000-000000000001","created":true}}""");
                case var p when p.StartsWith("/t/tabc1234/manage/v1/connector-packages/", StringComparison.Ordinal) && req.Method == HttpMethod.Post:
                    Posts.Add((p, body, null));
                    return RouteHandler.Json("""{"data":{"ok":true,"id":"local_pos","version":"1.0.0"}}""");
                default: return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        });
        public Func<Uri, Task> Browser => async url =>
        {
            var q = System.Web.HttpUtility.ParseQueryString(url.Query);
            _ = Task.Run(async () => { using var c = new HttpClient(); await c.GetAsync($"{q["redirect_uri"]}?code=code-1&state={q["state"]}"); });
            await Task.CompletedTask;
        };
    }

    static async Task<(Harness H, Fake F)> SignedIn()
    {
        var h = new Harness();
        var f = new Fake();
        h.Browser = f.Browser; h.Auth = f.Handler;
        await h.AddProfile();
        Assert.Equal(ExitCodes.Ok, (await h.Run("--profile", "dev", "login")).Code);
        return (h, f);
    }

    [Fact]
    public async Task Save_sends_the_file_bytes_as_is_and_retries_a_dropped_connection()
    {
        var (h, f) = await SignedIn();
        using var hh = h;
        f.FailFirstPosts = 1;
        var file = Path.Combine(h.Dir, "connector.json");
        var bytes = Encoding.UTF8.GetBytes("{ \"id\": \"local_pos\",\n  \"version\": \"1.0.0\" }");
        await File.WriteAllBytesAsync(file, bytes);
        var (code, output, err) = await h.Run("--profile", "dev", "connector", "package", "save", file);
        Assert.True(code == ExitCodes.Ok, err);
        Assert.Equal(2, f.Posts.Count);
        Assert.Equal(bytes, f.Posts[^1].Body);                 // the engine hashes exactly what was sent
        Assert.Equal("application/json", f.Posts[^1].ContentType);
        Assert.Contains("Saved local_pos 1.0.0", output);
    }

    [Fact]
    public async Task List_and_delete_ask_for_yes_and_show_the_engines_not_found()
    {
        var (h, f) = await SignedIn();
        using var hh = h;
        var (_, list, _) = await h.Run("--profile", "dev", "connector", "package", "list");
        Assert.Contains("local_pos", list);
        var (code, _, err) = await h.Run("--profile", "dev", "connector", "package", "delete", "local_pos", "1.0.0");
        Assert.Equal(ExitCodes.Usage, code);
        Assert.Contains("--yes", err);
        Assert.Empty(f.Posts);
        Assert.Equal(ExitCodes.Ok, (await h.Run("--profile", "dev", "connector", "package", "delete", "local_pos", "1.0.0", "--yes")).Code);
        (code, _, err) = await h.Run("--profile", "dev", "connector", "package", "delete", "local_pos", "9.9.9", "--yes");
        Assert.Equal(ExitCodes.NotFound, code);
        Assert.Contains("is not saved in this environment", err);
    }

    [Fact]
    public async Task A_missing_or_oversized_file_is_refused_before_any_call()
    {
        var (h, f) = await SignedIn();
        using var hh = h;
        Assert.Equal(ExitCodes.Usage, (await h.Run("--profile", "dev", "connector", "package", "save", Path.Combine(h.Dir, "nope.json"))).Code);
        var big = Path.Combine(h.Dir, "big.json");
        await File.WriteAllBytesAsync(big, new byte[512 * 1024 + 1]);
        Assert.Equal(ExitCodes.Usage, (await h.Run("--profile", "dev", "connector", "package", "save", big)).Code);
        Assert.Empty(f.Posts);
    }

    // ------------------------------------------------------------------ H1b.1d (docs/INO_HUB.md §25): preview and install

    [Fact]
    public async Task Install_shows_what_it_touches_and_installs_nothing_without_yes()
    {
        var (h, f) = await SignedIn();
        using var hh = h;
        var (code, _, err) = await h.Run("--profile", "dev", "connector", "package", "install", "local_pos", "1.0.0", "--set", "baseUrl=https://pos.example.com",
            "--set", "live=false", "--set", "stores=[\"S1\",\"S2\"]");
        Assert.Equal(ExitCodes.Usage, code);
        Assert.Contains("calls host      pos.example.com", err);
        Assert.Contains("transaction.create (points or money)", err);
        Assert.Contains("--yes", err);
        Assert.Single(f.Installs);                                  // the preview only
        Assert.EndsWith("/preview", f.Installs[0].Path);
        var values = System.Text.Json.Nodes.JsonNode.Parse(f.Installs[0].Body)!["values"]!;
        Assert.Equal("https://pos.example.com", values["baseUrl"]!.GetValue<string>());
        Assert.False(values["live"]!.GetValue<bool>());
        Assert.Equal(2, values["stores"]!.AsArray().Count);
    }

    [Fact]
    public async Task Install_with_yes_sends_the_previewed_hash_and_the_same_key_every_time()
    {
        var (h, f) = await SignedIn();
        using var hh = h;
        string[] args = ["--profile", "dev", "connector", "package", "install", "local_pos", "1.0.0", "--set", "baseUrl=https://pos.example.com", "--name", "Tills", "--yes"];
        var (code, output, err) = await h.Run(args);
        Assert.True(code == ExitCodes.Ok, err);
        Assert.Contains("Installed local_pos 1.0.0", output);
        var install = f.Installs.Single(i => i.Path.EndsWith("/install", StringComparison.Ordinal));
        Assert.Equal("a1:feed", System.Text.Json.Nodes.JsonNode.Parse(install.Body)!["accessHash"]!.GetValue<string>());
        Assert.Matches("^[0-9a-f]{64}$", install.Key!);
        // Running the same command again (after a crash) sends the same Idempotency-Key: the engine returns the first installation.
        Assert.Equal(ExitCodes.Ok, (await h.Run(args)).Code);
        var keys = f.Installs.Where(i => i.Path.EndsWith("/install", StringComparison.Ordinal)).Select(i => i.Key).ToList();
        Assert.Equal(2, keys.Count);
        Assert.Equal(keys[0], keys[1]);
        // Another name is another installation.
        args[^2] = "Tills 2";
        Assert.Equal(ExitCodes.Ok, (await h.Run(args)).Code);
        Assert.NotEqual(keys[0], f.Installs.Last().Key);
    }

    [Fact]
    public async Task A_refusal_names_the_scope_or_role_to_ask_for()
    {
        var (h, f) = await SignedIn();
        using var hh = h;
        f.InstallAnswer = new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent(
            """{"error":"forbidden","error_description":"Your Power Apps role lacks prvCreatetry_connectorinstall. Ask your Power Apps administrator for the \"Ino Loyalty - Connector Installer\" role."}""",
            Encoding.UTF8, "application/json") };
        var (code, _, err) = await h.Run("--profile", "dev", "connector", "package", "preview", "local_pos", "1.0.0", "--set", "baseUrl=https://pos.example.com");
        Assert.Equal(ExitCodes.Auth, code);
        Assert.Contains("Ino Loyalty - Connector Installer", err);
        Assert.Equal(ExitCodes.Usage, (await h.Run("--profile", "dev", "connector", "package", "preview", "local_pos", "1.0.0", "--set", "nokey")).Code);
        var (bad, _, badErr) = await h.Run("--profile", "dev", "connector", "package", "preview", "local_pos", "1.0.0", "--set", "stores=[S1");
        Assert.Equal(ExitCodes.Usage, bad);
        Assert.Contains("a list is JSON", badErr);
    }
}
