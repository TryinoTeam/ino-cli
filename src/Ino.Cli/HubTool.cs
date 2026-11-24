using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Ino.Cli.Hub;

namespace Ino.Cli;

/// <summary>
/// MCP/CLI P6a (docs/MCP_CLI_P6A_LOG.md): `ino hub`. Submissions are built from an allow-list and sent privately to Tryino; the store catalog
/// is verified with the engine's own code (HubEnvelope) and the highest accepted version is remembered on this machine.
/// </summary>
public static partial class HubTool
{
    public const string DefaultCatalogUrl = "https://raw.githubusercontent.com/TryinoTeam/ino-hub/main/catalog.envelope.json";
    public const string DefaultIntakeUrl = "https://chat.tryino.com/hub-submission";
    public const string ManifestFile = "ino-connector.json";

    [GeneratedRegex("^[a-z][a-z0-9-]{1,49}$")] private static partial Regex IdRe();
    [GeneratedRegex("^[0-9a-f]{40}$")] private static partial Regex CommitRe();
    [GeneratedRegex(@"^[^\s@]+@[^\s@]+\.[^\s@]+$")] private static partial Regex EmailRe();
    [GeneratedRegex(@"^(?:ssh://)?git@([^:/]+)[:/](.+?)(?:\.git)?/?$")] private static partial Regex SshRemoteRe();
    // Things that must never leave in a submission (T7): Ino keys and secrets, bearer tokens, long hex/base64 strings, e-mail addresses in free text.
    [GeneratedRegex(@"\bik_[A-Za-z0-9_]{8,}|whsec_[A-Za-z0-9+/=]{8,}|\bBearer\s+\S{12,}|\b[A-Fa-f0-9]{40,}\b|[A-Za-z0-9+/]{40,}={0,2}|[^\s@""]+@[^\s@""]+\.[A-Za-z]{2,}")]
    private static partial Regex SecretLikeRe();

    public sealed record Manifest(string Id, string Name, string Kind, string Category, string Platform, string Language, string Version,
        string License, string Repository, string Description);

    public static Manifest LoadManifest(string dir)
    {
        var path = Path.Combine(dir, ManifestFile);
        if (!File.Exists(path)) throw new CliException(ExitCodes.Usage, "NO_MANIFEST", $"No {ManifestFile} in {dir}. Projects made by `ino connector new` have one.");
        JsonObject o;
        try { o = JsonNode.Parse(File.ReadAllText(path))!.AsObject(); }
        catch (JsonException) { throw new CliException(ExitCodes.Usage, "BAD_MANIFEST", $"{ManifestFile} is not valid JSON."); }
        string S(string k, int max, bool required = true)
        {
            var v = o[k] is JsonValue jv && jv.TryGetValue<string>(out var s) ? s.Trim() : "";
            if ((required && v.Length == 0) || v.Length > max || v.Any(c => char.IsControl(c) && c != '\n'))
                throw new CliException(ExitCodes.Usage, "BAD_MANIFEST", $"{ManifestFile}: \"{k}\" must be plain text of at most {max} characters.");
            return v;
        }
        var m = new Manifest(S("id", 50), S("name", 80), S("kind", 20), S("category", 30), S("platform", 80), S("language", 20), S("version", 40),
            S("license", 40), S("repository", 300, required: false), S("description", 2000));
        if (!IdRe().IsMatch(m.Id)) throw new CliException(ExitCodes.Usage, "BAD_MANIFEST", "\"id\" must look like my-shop-connector.");
        if (m.Kind is not ("sales" or "marketing")) throw new CliException(ExitCodes.Usage, "BAD_MANIFEST", "\"kind\" must be sales or marketing.");
        return m;
    }

    /// <summary>
    /// Turns a git remote into a public https URL: <c>git@github.com:org/repo.git</c> and <c>https://user:token@github.com/org/repo</c> both
    /// become <c>https://github.com/org/repo</c>. Credentials in the remote never leave this machine (Gate T #2).
    /// </summary>
    public static string? PublicRepositoryUrl(string remote)
    {
        remote = remote.Trim();
        if (SshRemoteRe().Match(remote) is { Success: true } ssh) remote = $"https://{ssh.Groups[1].Value}/{ssh.Groups[2].Value}";
        if (!Uri.TryCreate(remote, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps) return null;
        var path = u.AbsolutePath.TrimEnd('/');
        if (path.EndsWith(".git", StringComparison.Ordinal)) path = path[..^4];
        var clean = $"https://{u.Host}{path}";
        return HubEnvelope.SafeUrl(clean) ? clean : null;
    }

    public sealed record GitState(string Repository, string Commit, string? Tag);

    public static GitState ReadGit(CliContext ctx, string dir)
    {
        var run = ctx.Capture ?? RunProcess;
        (int Code, string Out) Git(params string[] args) => run("git", ["-C", dir, .. args], dir);
        var top = Git("rev-parse", "--show-toplevel");
        if (top.Code != 0) throw new CliException(ExitCodes.Usage, "NOT_A_REPOSITORY", "This folder is not a git repository. Push the connector to a public repository first.");
        if (Git("status", "--porcelain").Out.Trim().Length > 0)
            throw new CliException(ExitCodes.Usage, "UNCOMMITTED", "There are uncommitted changes. Commit and push them: Tryino reviews exactly the commit you submit.");
        var commit = Git("rev-parse", "HEAD").Out.Trim();
        if (!CommitRe().IsMatch(commit)) throw new CliException(ExitCodes.Usage, "NO_COMMIT", "No commit yet.");
        var remote = Git("remote", "get-url", "origin");
        var repo = remote.Code == 0 ? PublicRepositoryUrl(remote.Out) : null;
        if (repo is null) throw new CliException(ExitCodes.Usage, "BAD_REMOTE", "The 'origin' remote must be a repository on github.com, gitlab.com or dev.azure.com.");
        // Tryino mirrors and reviews the commit from the remote: it must be pushed (review finding).
        if (Git("branch", "-r", "--contains", "HEAD").Out.Trim().Length == 0)
            throw new CliException(ExitCodes.Usage, "NOT_PUSHED", "This commit is not on the remote yet. Push it (git push), then submit.");
        var tag = Git("describe", "--tags", "--exact-match", "HEAD");
        return new GitState(repo, commit, tag.Code == 0 && tag.Out.Trim() is { Length: > 0 and <= 100 } t ? t : null);
    }

    static (int Code, string Out) RunProcess(string file, string[] args, string dir)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = dir };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = System.Diagnostics.Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(30_000);
            return (p.ExitCode, output);
        }
        catch (System.ComponentModel.Win32Exception) { return (127, ""); }
    }

    /// <summary>
    /// The submission, from an allow-list only (T7): manifest fields, the public repository, commit and tag, and pass/fail per scenario code.
    /// Never the tenant or channel code, base URL, request ids, evidence counts or keys.
    /// </summary>
    public static JsonObject BuildSubmission(Manifest m, GitState git, JsonObject? check, string company, string contactName, string contactEmail, string? note)
    {
        if (m.Repository.Length > 0)
        {
            var declared = PublicRepositoryUrl(m.Repository)
                ?? throw new CliException(ExitCodes.Usage, "BAD_MANIFEST", $"{ManifestFile}: \"repository\" must be a public https URL on github.com, gitlab.com or dev.azure.com.");
            if (declared != git.Repository)
                throw new CliException(ExitCodes.Usage, "REPOSITORY_MISMATCH", $"{ManifestFile} says {declared}, but 'origin' is {git.Repository}.");
        }
        if (!EmailRe().IsMatch(contactEmail) || contactEmail.Length > 200) throw new CliException(ExitCodes.Usage, "BAD_CONTACT", "Give a valid --contact-email.");
        var o = new JsonObject
        {
            ["schema"] = 1,
            ["connector"] = new JsonObject
            {
                ["id"] = m.Id, ["name"] = m.Name, ["kind"] = m.Kind, ["category"] = m.Category, ["platform"] = m.Platform, ["language"] = m.Language,
                ["version"] = m.Version, ["license"] = m.License, ["description"] = m.Description,
            },
            ["source"] = new JsonObject { ["repository"] = git.Repository, ["commit"] = git.Commit, ["tag"] = git.Tag },
            ["submitter"] = new JsonObject { ["company"] = Clip(company, 160), ["name"] = Clip(contactName, 120), ["email"] = contactEmail },
            ["note"] = note is null ? null : Clip(note, 1000),
            ["cli"] = Commands.Version,
        };
        if (check is not null)
        {
            var scenarios = (check["scenarios"] as JsonArray)?.OfType<JsonObject>()
                .Select(x => (JsonNode)new JsonObject { ["code"] = Clip(x["code"]?.ToString() ?? "", 10), ["passed"] = x["passed"]?.GetValue<bool>() == true }).ToArray() ?? [];
            o["compatibility"] = new JsonObject
            {
                ["suite"] = Clip(check["suite"]?.ToString() ?? "", 20), ["hours"] = check["hours"]?.GetValue<int>() ?? 0,
                ["passed"] = check["passed"]?.GetValue<int>() ?? 0, ["total"] = check["total"]?.GetValue<int>() ?? 0,
                ["environment"] = check["environment"]?.ToString() is "test" or "live" ? check["environment"]!.ToString() : null,
                ["scenarios"] = new JsonArray(scenarios), ["selfReported"] = true,
            };
        }
        // Free text written by people: no keys, secrets or other people's addresses in it.
        foreach (var (field, text) in new[] { ("name", m.Name), ("description", m.Description), ("platform", m.Platform), ("company", company), ("note", note ?? "") })
            if (SecretLikeRe().IsMatch(text))
                throw new CliException(ExitCodes.Usage, "LOOKS_SECRET", $"The {field} contains something that looks like a key, token or e-mail address. Remove it and try again.");
        return o;
    }

    static string Clip(string s, int max) => s.Trim() is var t && t.Length > max ? t[..max] : s.Trim();

    // ---------- catalog ----------

    public sealed record Catalog(HubVerifyResult Result, bool SignedByTryino, long Remembered);

    /// <summary>Fetches and verifies the store catalog; remembers the highest version accepted on this machine (T3).</summary>
    public static async Task<Catalog> ReadCatalogAsync(CliContext ctx, CancellationToken ct)
    {
        var url = ctx.Env.TryGetValue("INO_HUB_CATALOG_URL", out var u) && !string.IsNullOrWhiteSpace(u) ? u! : DefaultCatalogUrl;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new CliException(ExitCodes.Usage, "BAD_URL", "INO_HUB_CATALOG_URL must be an https URL.");
        using var http = new HttpClient(ctx.HubHandler ?? new SocketsHttpHandler { AllowAutoRedirect = false }, disposeHandler: ctx.HubHandler is null)
        { Timeout = TimeSpan.FromSeconds(15), MaxResponseContentBufferSize = HubEnvelope.MaxBytes };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"ino-cli/{Commands.Version}");
        string body;
        try
        {
            using var res = await http.GetAsync(uri, ct);
            if (!res.IsSuccessStatusCode) throw new CliException(ExitCodes.Network, "STORE_UNREACHABLE", $"The store catalog answered {(int)res.StatusCode} ({uri.Host}).");
            body = await res.Content.ReadAsStringAsync(ct);
        }
        catch (HttpRequestException e) { throw new CliException(ExitCodes.Network, "STORE_UNREACHABLE", $"Cannot reach {uri.Host}: {e.Message}"); }

        var extra = ctx.Env.TryGetValue("INO_HUB_EXTRA_KEYS", out var x) ? x : null;
        var statePath = Path.Combine(Path.GetDirectoryName(ctx.Profiles.ConfigPath)!, "state", "hub-version");
        long remembered = 0;
        try { if (File.Exists(statePath)) long.TryParse(File.ReadAllText(statePath).Trim(), out remembered); } catch (IOException) { }
        var floor = Math.Max(HubKeys.ReleaseFloor, remembered);
        var r = HubEnvelope.Verify(body, HubKeys.With(extra), floor, DateTimeOffset.UtcNow, HubKeys.Revoked, jumpFrom: remembered > 0 ? remembered : null);
        var signedByTryino = r.KeyId is { } kid && HubKeys.BuiltIn.ContainsKey(kid);
        // Only Tryino-signed catalogs raise the remembered version: a test catalog must not lock out real ones.
        if (r.Ok && signedByTryino && r.Payload!.Version > remembered)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
            File.WriteAllText(statePath, r.Payload.Version.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        return new Catalog(r, signedByTryino, remembered);
    }

    /// <summary>Sales connectors only until P1c (Gate T N7).</summary>
    public static IEnumerable<HubEntry> Listed(HubCatalogPayload p) => p.Entries.Where(e => e.Kind == "sales");

    public static string GetCommands(HubEntry e) =>
        $"git clone {e.Mirror} {e.Id}\ncd {e.Id} && git checkout {e.Commit}\n# Tryino approved this exact commit ({e.ApprovedAt:yyyy-MM-dd}). Original repository: {e.Repository}";
}
