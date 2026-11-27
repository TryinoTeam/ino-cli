using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Ino.Cli.Tests;

/// <summary>P1.3 (docs/MCP_CLI_P1_LOG.md): `ino plan` compares the setup folder with the firm's export and changes nothing.</summary>
public class PlanCommandTests
{
    static string Jwt(object claims) => "e30." + Convert.ToBase64String(Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(claims))).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".sig";

    const string Base = """
        {"schemaVersion":1,"tenant":"tabc1234","environment":"test","exportedAt":"2026-11-27T12:00:00Z",
         "sections":{"program":{"exported":true},"tiers":{"exported":true,"benefits":{"exported":true}},"channels":{"exported":true},
                     "webhooks":{"exported":false,"reason":"Not exported yet."}},
         "program":{"values":{"name":"Harbor Rewards","pointvalue":0.01,"roundingmode":"HALF_UP","programstatus":"DRAFT","earnrateoverrides":"SHOES:2:10;BAGS:1:5",
                              "smsverificationrequired":false,"maxredeempercent":null},
                    "notExported":["pointpendingdays"],"needsTargetCredentials":["smsverificationrequired"]},
         "tiers":{"tiers":[{"tiercode":"GOLD","name":"Gold","rank":2,"rule":"{\"op\":\"AND\",\"children\":[]}"}],
                  "benefits":[{"name":"Birthday","benefittype":"VOUCHER","value":10,"tier":"GOLD","voucherBatch":"WELCOME10","voucherBatchLinked":true}]},
         "channels":[{"code":"STORE_TILLS","backdateDays":14,"walletPercent":25,"walletCap":null}],
         "references":{"segments":[],"stores":[],"brands":[],"categories":[],"products":[],"voucherBatches":["WELCOME10"]},
         "warnings":[],
         "fieldTypes":{"program":{"name":"String","pointvalue":"Decimal","roundingmode":"Picklist","programstatus":"Picklist","earnrateoverrides":"String",
                                  "smsverificationrequired":"Boolean","maxredeempercent":"Decimal","pointpendingdays":"Integer"},
                       "tier":{"tiercode":"String","name":"String","rank":"Integer","rule":"Memo"},
                       "benefit":{"name":"String","benefittype":"Picklist","value":"Decimal"}},
         "inactiveTierCodes":[]}
        """;

    sealed class Firm
    {
        public JsonObject Export = JsonNode.Parse(Base)!.AsObject();
        public RouteHandler Handler(Func<string> token) => new((req, _) => req.RequestUri!.AbsolutePath switch
        {
            "/oauth/token" => RouteHandler.Json($$"""{"access_token":"{{token()}}","refresh_token":"rt-1"}"""),
            "/t/tabc1234/manage/v1/config" => RouteHandler.Json($$"""{"data":{{Export.ToJsonString()}}}"""),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
    }

    static async Task<(Harness H, Firm F, string Dir)> Folder()
    {
        var h = new Harness();
        var f = new Firm();
        h.Browser = async url =>
        {
            var q = System.Web.HttpUtility.ParseQueryString(url.Query);
            _ = Task.Run(async () => { using var c = new HttpClient(); await c.GetAsync($"{q["redirect_uri"]}?code=code-1&state={q["state"]}"); });
            await Task.CompletedTask;
        };
        h.Auth = f.Handler(() => Jwt(new { ino_tenant = "tabc1234", ino_env = "test", name = "Dana Whitfield", scope = "offline_access integration:read", exp = 1900000000 }));
        await h.AddProfile();
        Assert.Equal(ExitCodes.Ok, (await h.Run("--profile", "dev", "login")).Code);
        var dir = Path.Combine(h.Dir, "setup");
        Assert.Equal(ExitCodes.Ok, (await h.Run("--profile", "dev", "init", dir)).Code);
        return (h, f, dir);
    }

    static void Edit(string dir, string rel, string from, string to)
    {
        var p = Path.Combine(dir, rel);
        var text = File.ReadAllText(p);
        Assert.Contains(from, text);
        File.WriteAllText(p, text.Replace(from, to));
    }

    [Fact]
    public async Task A_fresh_folder_has_no_changes_and_equal_values_in_other_forms_stay_equal()
    {
        var (h, f, dir) = await Folder();
        using var hh = h;
        var (code, output, err) = await h.Run("--profile", "dev", "plan", dir);
        Assert.True(code == ExitCodes.Ok, err);
        Assert.Contains("No changes.", output);
        Assert.Contains("webhooks: not managed", output);
        // The same values written differently by the firm: trailing zeros, earn-rate order and case, rule spacing.
        f.Export["program"]!["values"]!["pointvalue"] = JsonValue.Create(0.0100m);
        f.Export["program"]!["values"]!["earnrateoverrides"] = "bags:1.0:5; SHOES:2:10";
        f.Export["tiers"]!["tiers"]![0]!["rule"] = "{ \"op\": \"AND\", \"children\": [] }";
        var again = (await h.Run("--profile", "dev", "plan", dir)).Out;
        Assert.True(again.Contains("No changes."), again);
        // The tier still matches when the firm wrote its code in another case or with spaces; that difference itself is shown (review P1.3 T-2).
        f.Export["tiers"]!["tiers"]![0]!["tiercode"] = " gold ";
        var cased = (await h.Run("--profile", "dev", "plan", dir)).Out;
        Assert.Contains("~ GOLD.tiercode:  gold  → GOLD", cased);
        Assert.DoesNotContain("+ GOLD", cased);
    }

    [Fact]
    public async Task Changes_are_shown_by_section_and_nothing_unmanaged_is_removed()
    {
        var (h, f, dir) = await Folder();
        using var hh = h;
        Edit(dir, "program/program.yaml", "pointvalue: 0.01", "pointvalue: 0.02");
        Edit(dir, "program/program.yaml", "smsverificationrequired: false", "smsverificationrequired: true");
        // The firm since: another program status, an extra tier, GOLD inactive, benefits it cannot export, a channel the folder does not have.
        f.Export["program"]!["values"]!["programstatus"] = "PUBLISHED";
        f.Export["tiers"]!["tiers"] = JsonNode.Parse("""[{"tiercode":"SILVER","name":"Silver","rank":1}]""");
        f.Export["inactiveTierCodes"] = JsonNode.Parse("""["GOLD"]""");
        f.Export["sections"]!["tiers"]!["benefits"] = JsonNode.Parse("""{"exported":false,"reason":"no"}""");
        f.Export["tiers"]!.AsObject().Remove("benefits");
        f.Export["channels"]!.AsArray().Add(JsonNode.Parse("""{"code":"WEB","backdateDays":3,"walletPercent":null,"walletCap":null}"""));
        // A field the folder does not have is not managed (removed from the folder file).
        var programPath = Path.Combine(dir, "program/program.yaml");
        File.WriteAllLines(programPath, File.ReadAllLines(programPath).Where(l => !l.TrimStart().StartsWith("maxredeempercent:")));

        var (code, output, err) = await h.Run("--profile", "dev", "plan", dir, "--json");
        Assert.True(code == ExitCodes.Ok, err);
        var plan = JsonNode.Parse(output)!;
        var changes = plan["changes"]!.AsArray().Select(c => $"{c!["section"]}|{c["key"]}|{c["field"]}|{c["action"]}|{c["from"]}|{c["to"]}").ToList();
        Assert.Contains("program||pointvalue|change|0.01|0.02", changes);
        Assert.Contains("tiers|GOLD||reactivate||", changes);
        Assert.Contains("tiers|SILVER||remove||", changes);
        Assert.Contains("channels|WEB||reset||defaults", changes);
        Assert.DoesNotContain(changes, c => c.Contains("programstatus") || c.StartsWith("benefits") || c.Contains("maxredeempercent"));
        Assert.Contains(plan["byEnvironment"]!.AsArray(), c => c!["field"]!.ToString() == "programstatus");
        var warnings = plan["warnings"]!.AsArray().Select(w => w!.ToString()).ToList();
        Assert.Contains(warnings, w => w.Contains("smsverificationrequired needs the firm's own credentials"));
        Assert.Contains(warnings, w => w.Contains("benefits could not be compared"));

        var (_, human, _) = await h.Run("--profile", "dev", "plan", dir);
        Assert.Contains("~ pointvalue: 0.01 → 0.02", human);
        Assert.Contains("Nothing was changed.", human);
    }

    [Fact]
    public async Task A_deleted_list_is_not_managed_never_removed()
    {
        var (h, f, dir) = await Folder();
        using var hh = h;
        var tiers = Path.Combine(dir, "program/tiers.yaml");
        File.WriteAllLines(tiers, File.ReadAllLines(tiers).TakeWhile(l => !l.StartsWith("tiers:")).Append("benefits:"));   // tiers gone, benefits empty
        var (code, output, err) = await h.Run("--profile", "dev", "plan", dir, "--json");
        Assert.True(code == ExitCodes.Ok, err);
        var plan = JsonNode.Parse(output)!;
        Assert.Empty(plan["changes"]!.AsArray());                                     // no "remove GOLD", no benefit removed
        Assert.Contains(plan["warnings"]!.AsArray(), w => w!.ToString().Contains("has no tiers"));
    }

    [Fact]
    public async Task Unsafe_or_mistyped_files_other_firms_and_old_engines_are_refused()
    {
        var (h, f, dir) = await Folder();
        using var hh = h;
        var program = Path.Combine(dir, "program/program.yaml");
        var good = File.ReadAllText(program);

        async Task<string> Refused(string why)
        {
            var (code, _, err) = await h.Run("--profile", "dev", "plan", dir);
            Assert.Equal(ExitCodes.Usage, code);
            Assert.Contains(why, err);
            return err;
        }
        File.WriteAllText(program, "values: &a\n  name: x\nother: *a\n");
        await Refused("anchor");
        File.WriteAllText(program, "values: !!python/object:os.system\n  name: x\n");
        await Refused("is not allowed");
        File.WriteAllText(program, string.Concat(Enumerable.Repeat("- ", 40)) + "x\n");
        await Refused("nested deeper");
        File.WriteAllText(program, "values:\n  name: a\n  name: b\n");
        await Refused("twice");
        File.WriteAllText(program, good.Replace("pointvalue: 0.01", "pointvalue: 0,02"));
        await Refused("is not a valid Decimal");
        File.WriteAllText(program, "values: !<tag:a%1B[2J> x\n");                    // a tag the parser cannot even read: no stack trace
        Assert.DoesNotContain('\u001b', await Refused("not valid YAML"));
        File.WriteAllText(program, "values:\n  \"k\\e[2J\": 1\n");                  // an escape in a key never reaches the terminal
        var (_, escOut, escErr) = await h.Run("--profile", "dev", "plan", dir);
        Assert.DoesNotContain('\u001b', escOut + escErr);
        File.WriteAllText(program, good);
        var channels = Path.Combine(dir, "channels/settings.yaml");
        var goodChannels = File.ReadAllText(channels);
        File.WriteAllText(channels, "channels: x\n");
        await Refused("must be a list");
        File.WriteAllText(channels, goodChannels);

        f.Export["tenant"] = "tother99";
        var other = await Refused("--other-firm");
        Assert.Equal(ExitCodes.Ok, (await h.Run("--profile", "dev", "plan", dir, "--other-firm")).Code);
        f.Export["tenant"] = "tabc1234";
        f.Export.Remove("fieldTypes");
        await Refused("too old");
    }
}
