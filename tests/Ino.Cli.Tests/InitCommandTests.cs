using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Ino.Cli.Tests;

/// <summary>P1.2 (docs/MCP_CLI_P1_LOG.md): `ino init` writes the engine's export as fixed-name YAML files; no file for a section that was not exported;
/// a folder with other content needs --force; values that look like other YAML types stay strings; the export time is only in ino.yaml.</summary>
public class InitCommandTests
{
    static string Jwt(object claims) => "e30." + Convert.ToBase64String(Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(claims))).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".sig";

    static string Export(string at, bool tiers = true) => $$$"""
        {"schemaVersion":1,"tenant":"tabc1234","environment":"test","exportedAt":"{{{at}}}",
         "sections":{"program":{"exported":true},"tiers":{{{(tiers ? """{"exported":true}""" : """{"exported":false,"reason":"Your role lacks read access to Tiers."}""")}}},
                     "channels":{"exported":true},"webhooks":{"exported":false,"reason":"Not exported yet."}},
         "program":{"values":{"name":"Harbor: Rewards # 1","pointvalue":0.01,"roundingmode":"HALF_UP","currencycode":"NO","maxredeempercent":null,"programdescription":"line one\r\nline two","programurl":"2026-11-27"},"notExported":["pointpendingdays"],"needsTargetCredentials":[]},
         "tiers":{"tiers":[{"tiercode":"GOLD","name":"Gold","rank":2,"rule":"{\"op\":\"AND\"}"}],"benefits":[]},
         "channels":[{"code":"ON","backdateDays":14,"walletPercent":25,"walletCap":null}],
         "references":{"segments":["VIP"],"stores":[],"brands":[],"categories":["0123"],"products":[],"voucherBatches":[]},
         "warnings":["A tier rule could not be read.\u001b[2J"]}
        """;

    [Fact]
    public async Task Init_writes_the_setup_files_and_only_the_exported_sections()
    {
        using var h = new Harness();
        var at = "2026-11-27T12:00:00Z";
        var tiersOn = true;
        h.Browser = async url =>
        {
            var q = System.Web.HttpUtility.ParseQueryString(url.Query);
            _ = Task.Run(async () => { using var c = new HttpClient(); await c.GetAsync($"{q["redirect_uri"]}?code=code-1&state={q["state"]}"); });
            await Task.CompletedTask;
        };
        h.Auth = new RouteHandler((req, _) => req.RequestUri!.AbsolutePath switch
        {
            "/oauth/token" => RouteHandler.Json($$"""{"access_token":"{{Jwt(new { ino_tenant = "tabc1234", ino_env = "test", name = "Dana Whitfield", scope = "offline_access integration:read", exp = 1900000000 })}}","refresh_token":"rt-1"}"""),
            "/t/tabc1234/manage/v1/config" => RouteHandler.Json($$"""{"data":{{Export(at, tiersOn)}}}"""),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        await h.AddProfile();
        Assert.Equal(ExitCodes.Ok, (await h.Run("--profile", "dev", "login")).Code);
        var dir = Path.Combine(h.Dir, "setup");

        var (code, output, err) = await h.Run("--profile", "dev", "init", dir);
        Assert.True(code == ExitCodes.Ok, err);
        Assert.Contains("webhooks: Not exported yet.", output);
        Assert.DoesNotContain('\u001b', output);
        string Read(string rel) => File.ReadAllText(Path.Combine(dir, rel));
        var program = Read("program/program.yaml");
        Assert.StartsWith("# Program settings of tabc1234 (test), written by `ino init`. No secrets.", program);
        Assert.Contains("pointvalue: 0.01", program);
        Assert.Contains("maxredeempercent: \n", program.Replace("\r", ""));                       // empty stays empty
        Assert.Contains("currencycode: \"NO\"", program);                                           // not read as false by YAML 1.1
        var back = new YamlDotNet.Serialization.DeserializerBuilder().Build().Deserialize<Dictionary<string, object>>(program);
        var vals = (Dictionary<object, object>)back["values"];
        Assert.Equal("Harbor: Rewards # 1", vals["name"]);                // a value with ": " and " #" round-trips
        Assert.Equal("line one\r\nline two", vals["programdescription"]);  // CRLF kept (review P1.2 K2)
        Assert.Contains("programurl: \"2026-11-27\"", program);           // not a date to YAML 1.1 readers
        Assert.Contains("code: \"ON\"", Read("channels/settings.yaml"));
        Assert.Contains("- \"0123\"", Read("ino.yaml"));
        Assert.DoesNotContain(at, program);                                                          // the time only in ino.yaml
        Assert.Contains(at, Read("ino.yaml"));
        Assert.True(File.Exists(Path.Combine(dir, "program/tiers.yaml")));
        Assert.False(Directory.Exists(Path.Combine(dir, "webhooks")));

        // The same firm later: identical section files. A section no longer exported loses its old file (no file = not managed).
        Assert.Equal(ExitCodes.Conflict, (await h.Run("--profile", "dev", "init", dir)).Code);
        at = "2026-11-28T08:00:00Z"; tiersOn = false;
        File.WriteAllText(Path.Combine(dir, "README.md"), "mine");
        Assert.Equal(ExitCodes.Ok, (await h.Run("--profile", "dev", "init", dir, "--force")).Code);
        Assert.Equal(program, Read("program/program.yaml"));
        Assert.False(File.Exists(Path.Combine(dir, "program/tiers.yaml")));
        Assert.Equal("mine", Read("README.md"));
        Assert.Contains("Your role lacks read access to Tiers.", Read("ino.yaml"));
        Assert.False(Directory.EnumerateFiles(dir, "*.tmp", SearchOption.AllDirectories).Any());

        // A file where the folder should be: a clear usage error, no stack trace.
        var file = Path.Combine(h.Dir, "afile");
        File.WriteAllText(file, "x");
        var (fcode, _, ferr) = await h.Run("--profile", "dev", "init", file);
        Assert.Equal(ExitCodes.Usage, fcode);
        Assert.Contains("is a file", ferr);
    }
}
