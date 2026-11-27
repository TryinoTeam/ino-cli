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
        var m = new Manifest(S("id", ConnectorIds.MaxLength), S("name", 80), S("kind", 20), S("category", 30), S("platform", 80), S("language", 20), S("version", 40),
            S("license", 40), S("repository", 300, required: false), S("description", 2000));
        if (ConnectorIds.Publisher(m.Id) == ConnectorIds.LocalPublisher)
            throw new CliException(ExitCodes.Usage, "BAD_MANIFEST", "\"id\": local_ ids are for your own environment only; give the connector its own publisher_name to share it.");
        if (!ConnectorIds.IsValid(m.Id)) throw new CliException(ExitCodes.Usage, "BAD_MANIFEST", "\"id\" must look like my-shop-connector (or publisher_name, e.g. acme_netsuite).");
        if (ConnectorIds.Publisher(m.Id) is { } pub && ConnectorIds.IsReservedPublisher(pub))
            throw new CliException(ExitCodes.Usage, "BAD_MANIFEST", $"\"id\": the publisher '{pub}' is reserved for Tryino.");
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

    /// <summary>The compatibility part from `ino connector test --kind marketing --json` (codes and pass/fail only; no evidence text).</summary>
    public static JsonObject MarketingCheck(string file)
    {
        if (!File.Exists(file)) throw new CliException(ExitCodes.Usage, "NO_REPORT", $"{file} does not exist.");
        if (new FileInfo(file).Length > 256 * 1024) throw new CliException(ExitCodes.Usage, "BAD_REPORT", $"{file} is too large to be a contract test report.");
        JsonObject? root;
        try { root = JsonNode.Parse(File.ReadAllText(file)) as JsonObject; }
        catch (System.Text.Json.JsonException) { root = null; }
        var checks = (root?["checks"] as JsonArray)?.OfType<JsonObject>().ToList();
        if (checks is not { Count: > 0 }) throw new CliException(ExitCodes.Usage, "BAD_REPORT", $"{file} is not the output of `ino connector test --kind marketing --json`.");
        return new JsonObject
        {
            ["suite"] = "marketing", ["hours"] = 0, ["total"] = checks.Count,
            ["passed"] = checks.Count(c => c["passed"]?.GetValue<bool>() == true),
            ["scenarios"] = new JsonArray(checks.Select(c => (JsonNode)new JsonObject { ["code"] = c["code"]?.ToString(), ["passed"] = c["passed"]?.GetValue<bool>() == true }).ToArray()),
        };
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
    /// <summary>Approved connectors: sales connectors and (P1c.2) marketing tools; anything else a future catalog adds is left out.</summary>
    public static IEnumerable<HubEntry> Listed(HubCatalogPayload p) => p.Entries.Where(e => e.Kind is "sales" or "marketing");

    // ---------- definition packages (IL9b, docs/INTEGRATION_LAYER.md §14.2-14.3) ----------

    /// <summary>The file's raw address, derived only from the entry's github.com mirror, its approved commit and its path (T36).</summary>
    public static Uri RawUrl(HubEntry e)
    {
        // From the mirror string itself (review IL9b-1 2): no Uri normalisation of dot segments.
        var m = HubEnvelope.GitHubRepoRe().Match(e.Mirror);
        if (!m.Success) throw new CliException(ExitCodes.Business, "STORE_NOT_TRUSTED", $"{e.Id}'s mirror is not a github.com repository.");
        return new Uri($"https://raw.githubusercontent.com/{m.Groups["owner"].Value}/{m.Groups["repo"].Value}/{e.Commit}/{e.Definition!.Path}");
    }

    public sealed record DownloadedDefinition(string File, string Id, string Version, string Shape, int Bytes, bool AsOwn);

    /// <summary>
    /// Downloads the definition the signed catalog names and writes it only when every check passes (T35, T37): status 200 without a redirect, at most
    /// the catalog's size, the catalog's SHA-256, the same reading as saving (no BOM, strict UTF-8, no duplicate keys), and the file's own id, version,
    /// category, shape and requires equal to the catalog's. <paramref name="asOwn"/>: only the top-level "id" string's bytes are replaced (v2 M2).
    /// The file is created new (never over an existing file or a link) and is not left half written.
    /// </summary>
    public static async Task<DownloadedDefinition> GetDefinitionAsync(CliContext ctx, HubEntry e, string? outPath, string? asOwn, CancellationToken ct)
    {
        var d = e.Definition ?? throw new CliException(ExitCodes.Usage, "NOT_A_DEFINITION", $"{e.Id} is not a definition package.");
        if (asOwn is not null && (!ConnectorIds.IsPackage(asOwn) || ConnectorIds.Publisher(asOwn) != ConnectorIds.LocalPublisher))
            throw new CliException(ExitCodes.Usage, "BAD_ID", "--as-own takes your own package id: local_, then 2-39 lower-case letters, digits and single hyphens.");
        var file = Path.GetFullPath(outPath ?? Path.Combine(ctx.WorkingDir ?? Directory.GetCurrentDirectory(), (asOwn ?? e.Id) + ".connector.json"));

        var uri = RawUrl(e);
        byte[] body;
        using (var http = new HttpClient(ctx.HubHandler ?? new SocketsHttpHandler { AllowAutoRedirect = false, AutomaticDecompression = System.Net.DecompressionMethods.None },
            disposeHandler: ctx.HubHandler is null) { Timeout = Timeout.InfiniteTimeSpan })
        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            cts.CancelAfter(TimeSpan.FromSeconds(30));   // the whole download, body included
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"ino-cli/{Commands.Version}");
            try
            {
                using var res = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                if (res.StatusCode != System.Net.HttpStatusCode.OK)
                    throw new CliException(ExitCodes.Network, "DOWNLOAD_FAILED", $"{uri.Host} answered {(int)res.StatusCode} for {d.Path}; nothing was written.");
                if (res.Content.Headers.ContentLength is { } len && len != d.Bytes)
                    throw new CliException(ExitCodes.Business, "DIGEST_MISMATCH", $"The file is {len} bytes, the approved one {d.Bytes}; nothing was written.");
                await using var stream = await res.Content.ReadAsStreamAsync(cts.Token);
                var buffer = new byte[d.Bytes + 1];
                var read = 0;
                int n;
                while (read < buffer.Length && (n = await stream.ReadAsync(buffer.AsMemory(read), cts.Token)) > 0) read += n;
                if (read != d.Bytes) throw new CliException(ExitCodes.Business, "DIGEST_MISMATCH", $"The file is not the approved size ({d.Bytes} bytes); nothing was written.");
                body = buffer[..read];
            }
            catch (HttpRequestException x) { throw new CliException(ExitCodes.Network, "DOWNLOAD_FAILED", $"Cannot reach {uri.Host}: {x.Message}"); }
            catch (IOException x) { throw new CliException(ExitCodes.Network, "DOWNLOAD_FAILED", $"The download from {uri.Host} broke off: {x.Message}"); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new CliException(ExitCodes.Network, "DOWNLOAD_FAILED", $"{uri.Host} did not answer within 30 seconds."); }
        }
        if (Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(body)) != d.Sha256)
            throw new CliException(ExitCodes.Business, "DIGEST_MISMATCH", $"The file at {uri.Host} is not the one Tryino approved (SHA-256 differs); nothing was written.");

        // The same reading as saving: no BOM, strict UTF-8, no duplicate keys (review IL9b m2).
        if (body.Length >= 3 && body[0] == 0xEF && body[1] == 0xBB && body[2] == 0xBF) throw Mismatch("it starts with a byte order mark");
        try { _ = new System.Text.UTF8Encoding(false, true).GetString(body); } catch (System.Text.DecoderFallbackException) { throw Mismatch("it is not valid UTF-8"); }
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(body, new JsonDocumentOptions { AllowDuplicateProperties = false, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 64 });
            root = doc.RootElement.Clone();
        }
        catch (JsonException) { throw Mismatch("it is not valid JSON (or repeats a key)"); }
        if (root.ValueKind != JsonValueKind.Object) throw Mismatch("it is not a JSON object");
        string? S(string k) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var shape = root.TryGetProperty("marketing", out _) ? "marketing" : root.TryGetProperty("posFile", out _) ? "posFile" : "connector";
        var requires = root.TryGetProperty("requires", out var rq) && rq.ValueKind == JsonValueKind.Array ? rq.EnumerateArray().Select(x => x.ToString()).Order(StringComparer.Ordinal).ToList() : [];
        if (S("id") != e.Id || S("version") != d.Version || S("category") != d.Category || shape != d.Shape
            || !requires.SequenceEqual(d.Requires.Order(StringComparer.Ordinal), StringComparer.Ordinal))
            throw Mismatch("its id, version, category, kind or requires are not what the catalog says");

        var bytes = asOwn is null ? body : ReplaceTopLevelId(body, asOwn);
        if (bytes.Length > HubEnvelope.MaxDefinitionBytes) throw new CliException(ExitCodes.Business, "TOO_LARGE", "With your id the file is over 512 KB; choose a shorter id.");
        FileStream fs;
        try { fs = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 1); }
        catch (IOException) when (File.Exists(file) || Directory.Exists(file))
        { throw new CliException(ExitCodes.Usage, "FILE_EXISTS", $"{file} already exists; nothing was written. Choose another --out."); }
        // Review (IL9b-1 1): a failed write or flush removes the file; it is never left half written.
        try { await using (fs) { await fs.WriteAsync(bytes, ct); await fs.FlushAsync(ct); } }
        catch { try { File.Delete(file); } catch (IOException) { } throw; }
        return new DownloadedDefinition(file, asOwn ?? e.Id, d.Version, d.Shape, bytes.Length, asOwn is not null);

        CliException Mismatch(string why) => new(ExitCodes.Business, "DEFINITION_MISMATCH", $"The approved file cannot be used: {why}; nothing was written.");
    }

    /// <summary>Replaces only the bytes of the top-level "id" string (shared with the engine: HubEnvelope.ReplaceTopLevelId).</summary>
    public static byte[] ReplaceTopLevelId(byte[] body, string id) => HubEnvelope.ReplaceTopLevelId(body, id)
        ?? throw new CliException(ExitCodes.Business, "DEFINITION_MISMATCH", "The file has no top-level id.");

    public static string GetCommands(HubEntry e) =>
        $"git clone {e.Mirror} {e.Id}\ncd {e.Id} && git checkout {e.Commit}\n# Tryino approved this exact commit ({e.ApprovedAt:yyyy-MM-dd}). Original repository: {e.Repository}";
}
