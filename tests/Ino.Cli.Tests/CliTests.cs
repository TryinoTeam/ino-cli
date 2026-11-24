using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Ino.Cli;

namespace Ino.Cli.Tests;

public class CatalogTests
{
    [Fact]
    public void Reads_are_an_allowlist_and_unknown_is_write()
    {
        // Exact equality with the engine's registry and read set is the engine-side contract test (InoLoyalty.Api.Tests/Cli); here only fixed facts.
        Assert.True(Catalog.Operations.Count >= 19);
        Assert.Equal(Catalog.Operations.Count, Catalog.Operations.Select(o => o.Name).Distinct().Count());
        Assert.Equal(OperationClass.Read, Catalog.ClassOf("customer.get360"));
        Assert.Equal(OperationClass.Write, Catalog.ClassOf("transaction.create"));
        Assert.Equal(OperationClass.Write, Catalog.ClassOf("referral.info"));
        Assert.Equal(OperationClass.Write, Catalog.ClassOf("brand.new.operation"));
    }

    [Fact]
    public async Task Api_list_json_has_every_operation_with_its_class()
    {
        using var h = new Harness();
        var (code, output, err) = await h.Run("api", "list", "--json");
        Assert.True(code == 0, err);
        var ops = JsonNode.Parse(output)!["operations"]!.AsArray();
        Assert.Equal(Catalog.Operations.Count, ops.Count);
        Assert.Equal("write", ops.First(o => o!["operation"]!.GetValue<string>() == "referral.info")!["class"]!.GetValue<string>());
    }
}

public class ProfileTests
{
    [Fact]
    public async Task Secret_goes_to_the_keychain_never_to_the_config_file()
    {
        using var h = new Harness();
        await h.AddProfile();
        var file = File.ReadAllText(h.ConfigPath);
        Assert.DoesNotContain("s3cr3t-value-123", file);
        Assert.DoesNotContain("ik_tabc1234_9f8e7d6c5b4a", file);
        Assert.Contains("ik_tabc1234…", file);
        Assert.Equal(new Credentials("ik_tabc1234_9f8e7d6c5b4a", "s3cr3t-value-123"), h.Secrets.Items["dev"]);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(h.ConfigPath));
    }

    [Fact]
    public async Task Environment_is_required_and_secrets_cannot_be_flags()
    {
        using var h = new Harness();
        var (code, _, err) = await h.RunWithInput("k\ns\n", "profile", "add", "dev", "--base-url", "https://x.example.test", "--channel", "C");
        Assert.Equal(ExitCodes.Usage, code);
        Assert.Contains("--environment", err);
        var (code2, _, _) = await h.Run("profile", "add", "dev", "--base-url", "https://x.example.test", "--channel", "C", "--environment", "test", "--secret", "x");
        Assert.Equal(ExitCodes.Usage, code2);
    }

    [Fact]
    public async Task Environment_variable_raises_to_live_but_never_lowers()
    {
        using var h = new Harness();
        await h.AddProfile("prod", "live");
        h.Env["INO_ENVIRONMENT"] = "test";
        Assert.True(h.Store.Resolve(null).Connection.IsLive);
        await h.AddProfile("dev", "test");
        h.Env["INO_ENVIRONMENT"] = "live";
        Assert.True(h.Store.Resolve("dev").Connection.IsLive);
    }

    [Fact]
    public void Env_only_connection_must_name_its_environment()
    {
        using var h = new Harness();
        h.Env["INO_BASE_URL"] = "https://x.example.test"; h.Env["INO_CHANNEL"] = "C"; h.Env["INO_API_KEY"] = "ik_x"; h.Env["INO_HMAC_SECRET"] = "secret-123";
        var e = Assert.Throws<CliException>(() => h.Store.Resolve(null));
        Assert.Equal(ExitCodes.Usage, e.ExitCode);
    }

    [Fact]
    public async Task File_store_is_opt_in_and_0600()
    {
        using var h = new Harness();
        var (code, _, err) = await h.RunWithInput("ik_k_1234567890\nsecret-file-1\n", "profile", "add", "f", "--base-url", "https://x.example.test", "--channel", "C", "--environment", "test", "--store", "file");
        Assert.Equal(0, code);
        Assert.Contains("warning", err);
        Assert.Empty(h.Secrets.Items);
        var cred = Path.Combine(h.Dir, "ino", "credentials.json");
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(cred));
        Assert.Equal("secret-file-1", h.Store.Resolve("f").Connection.Secret);
    }
}

public class CallTests
{
    [Fact]
    public async Task Body_is_sent_unchanged_and_signature_covers_the_wire_path()
    {
        using var h = new Harness();
        await h.AddProfile();
        var (code, _, _) = await h.Run("api", "call", "customer.get360", "--data", "{\"izlemeNo\":\"A1\",\"customerCode\":\"C-1\"}");
        Assert.Equal(0, code);
        var (req, body) = Assert.Single(h.Http.Seen);
        Assert.Equal("{\"izlemeNo\":\"A1\",\"customerCode\":\"C-1\"}", Encoding.UTF8.GetString(body));
        Assert.False(req.Headers.Contains("Idempotency-Key"));
        Assert.Equal("/t/tabc1234/api/v1/MODERN_ECOM/customer.get360", req.RequestUri!.PathAndQuery);
        string H(string n) => req.Headers.GetValues(n).Single();
        Assert.Equal("2", H("X-Signature-Version"));
        Assert.Matches("^[a-f0-9]{32}$", H("X-Nonce"));
        Assert.Equal(Signing.SignV2("s3cr3t-value-123", H("X-Timestamp"), H("X-Nonce"), "POST", req.RequestUri.PathAndQuery, body), H("X-Signature"));
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, "{\"ResponseCode\":\"SUCCESS\"}", 0)]
    [InlineData(HttpStatusCode.OK, "{\"ResponseCode\":\"NOT_FOUND\"}", 4)]
    [InlineData(HttpStatusCode.OK, "{\"responseCode\":\"PARAMETER_ERROR\"}", 1)]
    [InlineData(HttpStatusCode.BadRequest, "{\"ResponseCode\":\"PARAMETER_ERROR\"}", 1)]
    [InlineData(HttpStatusCode.Unauthorized, "{\"ResponseCode\":\"AUTHENTICATION_ERROR\"}", 3)]
    [InlineData(HttpStatusCode.Forbidden, "{\"ResponseCode\":\"FORBIDDEN\"}", 3)]
    [InlineData(HttpStatusCode.NotFound, "", 4)]
    [InlineData(HttpStatusCode.Conflict, "{\"ResponseCode\":\"CONFLICT\"}", 5)]
    [InlineData(HttpStatusCode.OK, "{\"ResponseCode\":\"IDEMPOTENCY_REPLAY\"}", 5)]
    [InlineData(HttpStatusCode.TooManyRequests, "", 6)]
    [InlineData(HttpStatusCode.InternalServerError, "oops", 1)]
    public async Task Exit_codes_follow_status_and_engine_result_code(HttpStatusCode status, string body, int expected)
    {
        using var h = new Harness();
        await h.AddProfile();
        h.Http = new FakeHandler(status, body);
        var (code, _, _) = await h.Run("api", "call", "customer.get360", "--data", "{}");
        Assert.Equal(expected, code);
    }

    [Fact]
    public async Task Local_usage_errors_send_nothing()
    {
        using var h = new Harness();
        await h.AddProfile();
        Assert.Equal(ExitCodes.Usage, (await h.Run("api", "call", "no.such.op")).Code);
        Assert.Equal(ExitCodes.Usage, (await h.Run("api", "call", "customer.get360", "--data", "{not json")).Code);
        Assert.Equal(ExitCodes.Usage, (await h.Run("api", "call", "customer.get360", "--data", "[1]")).Code);
        Assert.Empty(h.Http.Seen);
        Assert.Equal(0, (await h.Run("api", "call", "new.engine.op", "--allow-unknown", "--data", "{}")).Code);
    }

    [Fact]
    public async Task Redirect_is_not_followed()
    {
        using var h = new Harness();
        await h.AddProfile();
        h.Http = new FakeHandler(HttpStatusCode.Found, "", r => r.Headers.Location = new Uri("https://evil.example.test/"));
        var (code, _, err) = await h.Run("api", "call", "customer.get360", "--data", "{}");
        Assert.Equal(ExitCodes.Business, code);
        Assert.Contains("redirect", err);
        Assert.Single(h.Http.Seen);
    }

    [Fact]
    public void Plain_http_only_for_loopback()
    {
        Assert.Equal(ExitCodes.Usage, Assert.Throws<CliException>(() => ChannelClient.ValidateBase("http://engine.example.test")).ExitCode);
        ChannelClient.ValidateBase("http://localhost:5080");
        ChannelClient.ValidateBase("http://127.0.0.1:5080/t/tabc1234");
    }

    [Fact]
    public async Task After_a_401_the_profile_pauses_and_sends_nothing()
    {
        using var h = new Harness();
        await h.AddProfile();
        h.Http = new FakeHandler(HttpStatusCode.Unauthorized, "{\"ResponseCode\":\"AUTHENTICATION_ERROR\"}");
        Assert.Equal(ExitCodes.Auth, (await h.Run("api", "call", "customer.get360", "--data", "{}")).Code);
        var (code, _, err) = await h.Run("api", "call", "customer.get360", "--data", "{}");
        Assert.Equal(ExitCodes.Auth, code);
        Assert.Contains("401", err);
        Assert.Single(h.Http.Seen);
    }

    [Fact]
    public async Task Json_output_hides_a_secret_with_characters_json_escapes()
    {
        using var h = new Harness();
        await h.AddProfile(secret: "a+b<c>d&e'f-123");
        h.Http = new FakeHandler(HttpStatusCode.OK, "{\"ResponseCode\":\"SUCCESS\",\"echo\":\"a+b<c>d&e'f-123\"}");
        var (_, output, _) = await h.Run("api", "call", "customer.get360", "--data", "{}", "--json");
        Assert.DoesNotContain("a+b", output);
        Assert.DoesNotContain("\\u002B", output);
    }

    [Fact]
    public async Task Output_never_shows_the_profile_secret_or_secret_fields()
    {
        using var h = new Harness();
        await h.AddProfile(secret: "s3cr3t-value-123");
        h.Http = new FakeHandler(HttpStatusCode.OK, "{\"ResponseCode\":\"SUCCESS\",\"secret\":\"whsec_abcdef\",\"memberToken\":\"tok-123456\",\"echo\":\"s3cr3t-value-123\",\"n\":{\"hmacSecret\":\"zzzzzzzz\"}}");
        foreach (var json in new[] { false, true })
        {
            var (_, output, _) = await h.Run(json ? ["api", "call", "customer.get360", "--data", "{}", "--json"] : ["api", "call", "customer.get360", "--data", "{}"]);
            Assert.DoesNotContain("whsec_abcdef", output);
            Assert.DoesNotContain("tok-123456", output);
            Assert.DoesNotContain("s3cr3t-value-123", output);
            Assert.DoesNotContain("zzzzzzzz", output);
            Assert.Contains("[redacted]", output);
        }
    }
}

public class LiveGuardTests
{
    [Fact]
    public async Task Live_write_needs_live_and_yes_and_sends_nothing_otherwise()
    {
        using var h = new Harness();
        await h.AddProfile("prod", "live");
        foreach (var args in new[] { new[] { "--data", "{}" }, ["--data", "{}", "--yes"], ["--data", "{}", "--live"] })
        {
            var (code, _, _) = await h.Run(["api", "call", "transaction.create", .. args]);
            Assert.Equal(ExitCodes.Usage, code);
        }
        Assert.Empty(h.Http.Seen);
        Assert.Equal(0, (await h.Run("api", "call", "transaction.create", "--data", "{}", "--live", "--yes")).Code);
        Assert.Single(h.Http.Seen);
    }

    [Fact]
    public async Task Live_read_is_allowed_and_ci_never_writes_live()
    {
        using var h = new Harness();
        await h.AddProfile("prod", "live");
        var (code, _, err) = await h.Run("api", "call", "customer.get360", "--data", "{}");
        Assert.Equal(0, code);
        Assert.Contains("LIVE", err);
        h.Env["CI"] = "true";
        Assert.Equal(ExitCodes.Usage, (await h.Run("api", "call", "transaction.create", "--data", "{}", "--live", "--yes")).Code);
        Assert.Single(h.Http.Seen);
    }

    [Fact]
    public async Task Test_profile_writes_freely()
    {
        using var h = new Harness();
        await h.AddProfile();
        Assert.Equal(0, (await h.Run("api", "call", "transaction.create", "--data", "{}")).Code);
    }
}

public class DoctorAndDocsTests
{
    [Fact]
    public async Task Doctor_reports_401_as_auth_failure()
    {
        using var h = new Harness();
        await h.AddProfile();
        h.Http = new FakeHandler(HttpStatusCode.Unauthorized, "{}");
        var (code, output, _) = await h.Run("doctor", "--json");
        Assert.Equal(ExitCodes.Auth, code);
        Assert.Contains("\"key\"", output);
        Assert.Equal("/health", h.Http.Seen[0].Request.RequestUri!.AbsolutePath);
        Assert.Equal("/t/tabc1234/api/v1/MODERN_ECOM/events?limit=1", h.Http.Seen[1].Request.RequestUri!.PathAndQuery);
    }

    [Fact]
    public async Task Doctor_green_with_events_scope()
    {
        using var h = new Harness();
        await h.AddProfile();
        var (code, output, _) = await h.Run("doctor");
        Assert.Equal(0, code);
        Assert.Contains("signature accepted", output);
    }

    [Fact]
    public async Task Docs_search_works_offline()
    {
        using var h = new Harness();
        var (code, output, _) = await h.Run("docs", "search", "signature", "nonce");
        Assert.Equal(0, code);
        Assert.Contains("X-Nonce", output);
        Assert.NotNull(Docs.SectionFor("transaction.create"));
    }
}

public class DoctorExitTests
{
    [Fact]
    public async Task Doctor_without_a_profile_is_a_usage_error()
    {
        using var h = new Harness();
        Assert.Equal(ExitCodes.Usage, (await h.Run("doctor")).Code);
        Assert.Empty(h.Http.Seen);
    }

    [Fact]
    public async Task Doctor_404_points_at_the_base_url()
    {
        using var h = new Harness();
        await h.AddProfile();
        h.Http = new FakeHandler(System.Net.HttpStatusCode.NotFound, "");
        var (code, output, _) = await h.Run("doctor");
        Assert.Equal(ExitCodes.NotFound, code);
        Assert.Contains("/t/<tenant code>", output);
    }
}
