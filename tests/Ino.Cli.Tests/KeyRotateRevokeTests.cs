using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Ino.Cli.Tests;

/// <summary>`ino keys rotate / revoke` (docs/MCP_CLI_P0C.md §2.6, R4–R6).</summary>
public class KeyRotateRevokeTests
{
    const string OldId = "5c1d0000-0000-0000-0000-00000000000a";
    const string NewKey = "ik_tabc1234_newkeyabcdefghijklmnopqrstuv";

    static string Jwt(object claims) => "e30." + Convert.ToBase64String(Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(claims))).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".sig";

    sealed class Fake
    {
        public readonly List<(string Path, string Body, string? Idempotency)> Posts = [];
        public int FailFirstPosts;
        /// <summary>Keys the list returns: the harness profile's key (prefix ik_9f8e7d6c), a second active one and a revoked one sharing a prefix.</summary>
        public string Keys = """
            {"data":{"keys":[
              {"id":"OLD_ID","keyPrefix":"ik_9f8e7d6c","channel":"MODERN_ECOM","status":"ACTIVE","name":"Store POS fleet"},
              {"id":"5c1d0000-0000-0000-0000-00000000000b","keyPrefix":"ik_dup00000","channel":"MODERN_ECOM","status":"ACTIVE","name":"A"},
              {"id":"5c1d0000-0000-0000-0000-00000000000c","keyPrefix":"ik_dup00000","channel":"MODERN_ECOM","status":"REVOKED","name":"B"},
              {"id":"5c1d0000-0000-0000-0000-00000000000d","keyPrefix":"ik_other000","channel":"WEB","status":"ACTIVE","name":"Web"}]}}
            """.Replace("OLD_ID", OldId);
        public RouteHandler Handler => new((req, body) =>
        {
            var text = Encoding.UTF8.GetString(body);
            var form = System.Web.HttpUtility.ParseQueryString(text);
            var token = Jwt(new { ino_tenant = "tabc1234", ino_env = "test", name = "Dana Whitfield", scope = "offline_access integration:read integration:write", exp = 1900000000 });
            switch (req.RequestUri!.AbsolutePath)
            {
                case "/oauth/token" when form["grant_type"] == "authorization_code":
                    return RouteHandler.Json($$"""{"access_token":"{{token}}","refresh_token":"rt-1"}""");
                case "/oauth/token":
                    return RouteHandler.Json($$"""{"access_token":"{{token}}","refresh_token":"rt-x"}""");
                case "/t/tabc1234/manage/v1/api-keys" when req.Method == HttpMethod.Get:
                    return RouteHandler.Json(Keys);
                case var p when p.StartsWith("/t/tabc1234/manage/v1/api-keys/", StringComparison.Ordinal) && req.Method == HttpMethod.Post:
                    Posts.Add((p["/t/tabc1234/manage/v1/".Length..], text, req.Headers.TryGetValues("Idempotency-Key", out var ik) ? ik.Single() : null));
                    if (Posts.Count <= FailFirstPosts) throw new HttpRequestException("connection reset");
                    return p.EndsWith("/rotate", StringComparison.Ordinal)
                        ? RouteHandler.Json("""{"data":{"id":"5c1d0000-0000-0000-0000-0000000000ff","channel":"MODERN_ECOM","keyPrefix":"ik_newkeyab","apiKey":"NEW_KEY","secret":"new-s3cr3t-0002","previousValidUntil":"2026-11-26T09:00:00+00:00"}}""".Replace("NEW_KEY", NewKey))
                        : RouteHandler.Json("""{"data":{"ok":true}}""");
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
        await h.AddProfile();   // dev: MODERN_ECOM, key ik_tabc1234_9f8e7d6c5b4a (engine prefix ik_9f8e7d6c)
        h.Store.SetMcpWrites("dev", true);
        Assert.Equal(ExitCodes.Ok, (await h.Run("--profile", "dev", "login")).Code);
        return (h, f);
    }

    [Fact]
    public void Key_prefix_matches_the_engine_for_cloud_and_older_keys()
    {
        Assert.Equal("ik_9f8e7d6c", ProfileStore.KeyPrefix("ik_tabc1234_9f8e7d6c5b4a"));
        Assert.Equal("ik_abcdefgh", ProfileStore.KeyPrefix("ik_abcdefghijklmnopqrstuvwxyz234567"));
        Assert.Equal("abcd…", ProfileStore.KeyPrefix("abcdefgh"));
    }

    [Fact]
    public async Task R4_rotate_by_prefix_updates_the_profile_that_holds_the_key_and_keeps_its_settings()
    {
        var (h, f) = await SignedIn();
        using var hh = h;
        var (code, output, err) = await h.Run("--profile", "dev", "keys", "rotate", "ik_9f8e7d6c", "--update-profile", "dev", "--overlap-hours", "2");
        Assert.True(code == ExitCodes.Ok, err);
        var post = Assert.Single(f.Posts);
        Assert.Equal($"api-keys/{OldId}/rotate", post.Path);
        Assert.Equal(2, JsonNode.Parse(post.Body)!["overlapHours"]!.GetValue<int>());
        Assert.NotNull(post.Idempotency);
        Assert.Equal(NewKey, h.Secrets.Items["dev"].ApiKey);
        Assert.Equal("new-s3cr3t-0002", h.Secrets.Items["dev"].Secret);
        var p = h.Store.Load().Profiles["dev"];
        Assert.True(p.McpWrites);   // M4: other settings stay
        Assert.Equal("MODERN_ECOM", p.Channel);
        Assert.Contains("Rotated ik_9f8e7d6c", output);
        Assert.DoesNotContain("new-s3cr3t-0002", output + err);
    }

    [Fact]
    public async Task R4_a_profile_that_holds_another_key_or_channel_is_refused_before_the_call()
    {
        var (h, f) = await SignedIn();
        using var hh = h;
        await h.RunWithInput("ik_tabc1234_zzzzzzzz5b4a\ns3cr3t-value-123\n", "profile", "add", "pos", "--base-url", "https://engine.example.test/t/tabc1234", "--channel", "MODERN_ECOM", "--environment", "test");
        var (code, _, err) = await h.Run("--profile", "dev", "keys", "rotate", "ik_9f8e7d6c", "--update-profile", "pos");
        Assert.Equal(ExitCodes.Usage, code);
        Assert.Contains("holds ik_zzzzzzzz", err);
        (code, _, err) = await h.Run("--profile", "dev", "keys", "rotate", "ik_other000", "--update-profile", "dev");
        Assert.Equal(ExitCodes.Usage, code);
        Assert.Contains("not WEB", err);
        Assert.Empty(f.Posts);
        Assert.Equal(ExitCodes.Usage, (await h.Run("--profile", "dev", "keys", "rotate", "ik_9f8e7d6c")).Code);   // no destination
        Assert.Equal(ExitCodes.Usage, (await h.Run("--profile", "dev", "keys", "rotate", "ik_9f8e7d6c", "--show-secret", "--overlap-hours", "200")).Code);
        Assert.Empty(f.Posts);
    }

    [Fact]
    public async Task R4_a_dropped_connection_is_retried_with_the_same_idempotency_key()
    {
        var (h, f) = await SignedIn();
        using var hh = h;
        f.FailFirstPosts = 1;
        var (code, _, err) = await h.Run("--profile", "dev", "keys", "rotate", OldId, "--update-profile", "dev");
        Assert.True(code == ExitCodes.Ok, err);
        Assert.Equal(2, f.Posts.Count);
        Assert.Equal(f.Posts[0].Idempotency, f.Posts[1].Idempotency);
    }

    [Fact]
    public async Task R5_revoke_asks_for_yes_skips_revoked_keys_and_refuses_ambiguous_or_partial_prefixes()
    {
        var (h, f) = await SignedIn();
        using var hh = h;
        var (code, _, err) = await h.Run("--profile", "dev", "keys", "revoke", "ik_other000");
        Assert.Equal(ExitCodes.Usage, code);
        Assert.Contains("--yes", err);
        Assert.Contains("ik_other000 (Web, WEB)", err);
        Assert.Empty(f.Posts);

        Assert.Equal(ExitCodes.NotFound, (await h.Run("--profile", "dev", "keys", "revoke", "ik_", "--yes")).Code);   // never part of a prefix
        // Two keys share ik_dup00000, but one is revoked: revoke sees only the active one.
        (code, _, err) = await h.Run("--profile", "dev", "keys", "revoke", "ik_dup00000", "--yes", "--reason", "Leaked in a build log");
        Assert.True(code == ExitCodes.Ok, err);
        var post = Assert.Single(f.Posts);
        Assert.Equal("api-keys/5c1d0000-0000-0000-0000-00000000000b/revoke", post.Path);
        Assert.Equal("Leaked in a build log", JsonNode.Parse(post.Body)!["reason"]!.GetValue<string>());
        // Rotate sees both and asks for the id.
        (code, _, err) = await h.Run("--profile", "dev", "keys", "rotate", "ik_dup00000", "--show-secret");
        Assert.Equal(ExitCodes.Usage, code);
        Assert.Contains("give the id", err);
    }

    [Fact]
    public async Task A_full_key_is_matched_by_its_prefix_and_list_shows_ids()
    {
        var (h, f) = await SignedIn();
        using var hh = h;
        var (code, _, err) = await h.Run("--profile", "dev", "keys", "revoke", "ik_tabc1234_9f8e7d6c5b4a", "--yes");
        Assert.True(code == ExitCodes.Ok, err);
        Assert.Equal($"api-keys/{OldId}/revoke", Assert.Single(f.Posts).Path);
        var (_, list, _) = await h.Run("--profile", "dev", "keys", "list");
        Assert.Contains(OldId, list);
    }
}
