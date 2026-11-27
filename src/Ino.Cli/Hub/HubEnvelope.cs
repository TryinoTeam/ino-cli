using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

// The CLI compiles this file too (linked source); there it gets its own namespace so a project that references both (the API tests reference
// the CLI) sees two different types, not a clash.
#if INO_CLI
namespace Ino.Cli.Hub;
#else
namespace InoLoyalty.Api.Hub;
#endif

// MCP/CLI P6a (docs/MCP_CLI_P6A_LOG.md): the connector store's catalog is published as ONE envelope file signed with a Tryino key.
// This file has no dependency beyond the BCL: the engine (HubCatalog) and the CLI (`ino hub`, linked source) verify with the same code.

/// <summary>One approved connector in the store. Text only; the engine never fetches <see cref="Repository"/> (T8).</summary>
public sealed record HubEntry(
    string Id, string Name, string Kind, string Category, string Platform, string Vendor, string Description,
    IReadOnlyList<string> Languages, string License, string Version, string Suite, bool SelfReported,
    DateTimeOffset ApprovedAt, string Reviewer, string SupportLevel, string Runtime,
    string Repository, string Commit, string? Tag, string Mirror, HubDefinitionArtifact? Definition = null);

/// <summary>
/// IL9b (docs/INTEGRATION_LAYER.md §14.2-14.3): a definition package the catalog carries by reference: the file at <see cref="Path"/> in the mirror at the
/// entry's commit, whose bytes hash to <see cref="Sha256"/>. <see cref="Shape"/>: connector (a shop or POS connector), marketing (a marketing tool),
/// posFile (a POS file format). The engine only lists it; the CLI downloads and checks it (T35-T37).
/// </summary>
public sealed record HubDefinitionArtifact(string Path, string Sha256, int Bytes, string Version, string Category, string Shape, IReadOnlyList<string> Requires);

public sealed record HubWithdrawal(string Id, string Reason, DateTimeOffset At);

public sealed record HubCatalogPayload(long Version, DateTimeOffset IssuedAt, DateTimeOffset ExpiresAt, IReadOnlyList<string> RevokedKeys,
    IReadOnlyList<HubEntry> Entries, IReadOnlyList<HubWithdrawal> Withdrawn, int DroppedEntries);

public enum HubVerifyStatus { Ok, Malformed, UnknownKey, RevokedKey, BadSignature, Expired, Rollback }

public sealed record HubVerifyResult(HubVerifyStatus Status, HubCatalogPayload? Payload = null, string? KeyId = null)
{
    public bool Ok => Status == HubVerifyStatus.Ok;
}

public static partial class HubEnvelope
{
    public const int MaxBytes = 512 * 1024;
    public const int MaxEntries = 200;
    static readonly TimeSpan MaxLifetime = TimeSpan.FromDays(45);
    /// <summary>A catalog may be at most this many versions above the last accepted one (Gate T N1: a leaked key can't jump to 2^63).</summary>
    public const long MaxVersionJump = 1000;
    static readonly string[] Hosts = ["github.com", "gitlab.com", "dev.azure.com"];

    [GeneratedRegex("^[0-9a-f]{40}$")] private static partial Regex CommitRe();
    [GeneratedRegex("^[0-9a-f]{64}$")] private static partial Regex Sha256Re();
    // A definition's mirror is exactly https://github.com/<owner>/<repo> (review IL9b: the raw address is derived from it).
    [GeneratedRegex("^https://github\\.com/(?<owner>[A-Za-z0-9-]{1,39})/(?<repo>(?!\\.{1,2}$)[A-Za-z0-9._-]{1,100})$")] public static partial Regex GitHubRepoRe();
    // A relative path of plain segments ending in .json: no leading '/', no empty, '.' or '..' segment.
    [GeneratedRegex("^(?!.*(?:^|/)\\.{1,2}(?:/|$))[A-Za-z0-9_-][A-Za-z0-9._-]*(?:/[A-Za-z0-9_-][A-Za-z0-9._-]*)*\\.json$")] private static partial Regex DefinitionPathRe();
    public const int MaxDefinitionBytes = 512 * 1024;
    public static readonly string[] DefinitionShapes = ["connector", "marketing", "posFile"];
    [GeneratedRegex("^[A-Za-z0-9_.-]{1,40}$")] private static partial Regex KeyIdRe();
    // Repository and mirror links are shown and printed as commands: plain path characters only (no newline, ;, $, backtick, quote).
    [GeneratedRegex("^https://[A-Za-z0-9.-]+/[A-Za-z0-9._~/-]+$")] private static partial Regex PlainUrlRe();

    /// <summary>
    /// Verifies a catalog envelope <c>{"payload": base64(catalog JSON), "sig": base64(DER ECDSA P-256 SHA-256 over the payload bytes), "keyId": "…"}</c>.
    /// Order: shape → key known and not revoked → signature → expiry (fetched copies only) → version not below <paramref name="minVersion"/>, and
    /// at most <see cref="MaxVersionJump"/> above <paramref name="jumpFrom"/> when a version was remembered (a fresh install or one that lost its
    /// cache has nothing to measure a jump from). A payload's own <c>revokedKeys</c> is not trusted (a leaked key would revoke the good one,
    /// Gate T N1); keys are revoked with a release through <paramref name="revokedKeys"/>.
    /// </summary>
    public static HubVerifyResult Verify(string envelopeJson, IReadOnlyDictionary<string, byte[]> trustedKeys, long minVersion, DateTimeOffset now,
        IReadOnlyCollection<string>? revokedKeys = null, bool snapshot = false, long? jumpFrom = null)
    {
        if (string.IsNullOrEmpty(envelopeJson) || Encoding.UTF8.GetByteCount(envelopeJson) > MaxBytes) return new(HubVerifyStatus.Malformed);
        byte[] payload, sig; string keyId;
        try
        {
            var env = JsonNode.Parse(envelopeJson)!.AsObject();
            keyId = env["keyId"]!.GetValue<string>();
            payload = Convert.FromBase64String(env["payload"]!.GetValue<string>());
            sig = Convert.FromBase64String(env["sig"]!.GetValue<string>());
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException or FormatException or NullReferenceException or ArgumentNullException)
        {
            return new(HubVerifyStatus.Malformed);
        }
        if (!KeyIdRe().IsMatch(keyId)) return new(HubVerifyStatus.Malformed);
        if (!trustedKeys.TryGetValue(keyId, out var spki)) return new(HubVerifyStatus.UnknownKey, KeyId: keyId);
        if (revokedKeys?.Contains(keyId) == true) return new(HubVerifyStatus.RevokedKey, KeyId: keyId);

        using (var ecdsa = ECDsa.Create())
        {
            try { ecdsa.ImportSubjectPublicKeyInfo(spki, out _); }
            catch (CryptographicException) { return new(HubVerifyStatus.UnknownKey, KeyId: keyId); }
            if (ecdsa.KeySize != 256 || !ecdsa.VerifyData(payload, sig, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
                return new(HubVerifyStatus.BadSignature, KeyId: keyId);
        }

        var parsed = ParsePayload(payload);
        if (parsed is null) return new(HubVerifyStatus.Malformed, KeyId: keyId);
        if (parsed.ExpiresAt - parsed.IssuedAt > MaxLifetime) return new(HubVerifyStatus.Malformed, KeyId: keyId);
        // The image snapshot was signed at build time and may be older than its expiry; it is shown as "catalog as of <date>" (Gate T N3).
        if (!snapshot && (parsed.ExpiresAt <= now || parsed.IssuedAt > now.AddHours(1))) return new(HubVerifyStatus.Expired, parsed, keyId);
        if (parsed.Version < minVersion || (jumpFrom is { } from && parsed.Version > from + MaxVersionJump)) return new(HubVerifyStatus.Rollback, parsed, keyId);
        return new(HubVerifyStatus.Ok, parsed, keyId);
    }

    /// <summary>Signs a payload (tests and the dev signing path; production signs in Azure Key Vault with the same format).</summary>
    public static string Sign(byte[] payload, ECDsa key, string keyId) => new JsonObject
    {
        ["payload"] = Convert.ToBase64String(payload),
        ["sig"] = Convert.ToBase64String(key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)),
        ["keyId"] = keyId,
    }.ToJsonString();

    static HubCatalogPayload? ParsePayload(byte[] payload)
    {
        try
        {
            var o = JsonNode.Parse(payload)!.AsObject();
            var version = o["version"]!.GetValue<long>();
            if (version < 0) return null;
            var issued = DateTimeOffset.Parse(o["issuedAt"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture);
            var expires = DateTimeOffset.Parse(o["expiresAt"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture);
            var revoked = (o["revokedKeys"] as JsonArray)?.Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).OfType<string>().ToList() ?? [];
            var entries = new List<HubEntry>();
            var dropped = 0;
            foreach (var n in (o["entries"] as JsonArray ?? []).Take(MaxEntries))
            {
                if (n is JsonObject e && ParseEntry(e) is { } entry && entries.All(x => x.Id != entry.Id)) entries.Add(entry);
                else dropped++;
            }
            var withdrawn = new List<HubWithdrawal>();
            foreach (var n in (o["withdrawn"] as JsonArray ?? []).Take(MaxEntries))
                if (n is JsonObject w && Text(w, "id", ConnectorIds.MaxLength) is { } wid && ConnectorIds.IsHubId(wid))
                    withdrawn.Add(new(wid, Text(w, "reason", 500) ?? "", Date(w, "at") ?? DateTimeOffset.MinValue));
            // A withdrawn id never shows in the store, even if an entry with that id is still listed.
            entries.RemoveAll(e => withdrawn.Any(w => w.Id == e.Id));
            return new(version, issued, expires, revoked, entries, withdrawn, dropped);
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException or FormatException or NullReferenceException)
        {
            return null;
        }
    }

    /// <summary>Strict entry check (T5): unknown kinds, bad ids or commits, non-https or foreign hosts and oversize text drop the entry.</summary>
    static readonly (string Name, int Max)[] TextFields =
        [("name", 80), ("vendor", 120), ("description", 2000), ("category", 30), ("platform", 80), ("license", 40), ("version", 40), ("suite", 20), ("reviewer", 40)];

    static HubEntry? ParseEntry(JsonObject e)
    {
        // A text field that is present but not plain text within its limit drops the whole entry (T5); it is never shown cut or empty.
        foreach (var (field, max) in TextFields)
            if (e[field] is not null && Text(e, field, max) is null) return null;
        var id = Text(e, "id", ConnectorIds.MaxLength);
        if (id is null || !ConnectorIds.IsHubId(id)) return null;
        var kind = Text(e, "kind", 20);
        if (kind is not ("sales" or "marketing")) return null;
        var runtime = Text(e, "runtime", 20) ?? "self-hosted";
        if (runtime is not ("self-hosted" or "definition")) return null;
        if (e["source"] is not JsonObject src) return null;
        var commit = Text(src, "commit", 40);
        if (commit is null || !CommitRe().IsMatch(commit)) return null;
        var repo = Text(src, "repository", 300);
        var mirror = Text(src, "mirror", 300);
        if (!SafeUrl(repo) || !SafeUrl(mirror)) return null;
        var name = Text(e, "name", 80);
        var vendor = Text(e, "vendor", 120);
        var approved = Date(e, "approvedAt");
        if (name is null || vendor is null || approved is null) return null;
        if (e["languages"] is { } langs && (langs is not JsonArray arr || arr.Any(l => l is not JsonValue lv || !lv.TryGetValue<string>(out _)))) return null;
        var languages = (e["languages"] as JsonArray ?? []).Select(l => l!.GetValue<string>()).Where(l => l is "node" or "python" or "csharp" or "java" or "go").Distinct().ToList();
        var selfReported = true;
        if (e["selfReported"] is { } sr && (sr is not JsonValue srv || !srv.TryGetValue(out selfReported))) return null;
        HubDefinitionArtifact? definition = null;
        if (runtime == "definition" && (definition = ParseDefinition(e, src, id, kind, mirror!)) is null) return null;
        return new HubEntry(id, name, kind, Text(e, "category", 30) ?? "OTHER", Text(e, "platform", 80) ?? "", vendor, Text(e, "description", 2000) ?? "",
            languages, Text(e, "license", 40) ?? "", Text(e, "version", 40) ?? "", Text(e, "suite", 20) ?? "", selfReported,
            approved.Value, Text(e, "reviewer", 40) ?? "", "approved", runtime, repo!, commit, Text(src, "tag", 100), mirror!, definition);
    }

    /// <summary>
    /// IL9b: a definition entry names one file (format 1) by path, digest and size, with the package's own version, category and shape; the shape
    /// matches the kind (marketing ↔ marketing). Anything else drops the entry, like every other malformed entry (T36, T40).
    /// </summary>
    static HubDefinitionArtifact? ParseDefinition(JsonObject e, JsonObject src, string id, string kind, string mirror)
    {
        if (e["definition"] is not JsonObject d) return null;
        if (d["format"] is not JsonValue fv || !fv.TryGetValue<int>(out var format) || format != 1) return null;
        if (!ConnectorIds.IsPackage(id) || ConnectorIds.Publisher(id) == ConnectorIds.LocalPublisher) return null;
        if (!GitHubRepoRe().IsMatch(mirror) || mirror.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) return null;
        var path = Text(src, "path", 200);
        if (path is null || !DefinitionPathRe().IsMatch(path) || path.Contains("//", StringComparison.Ordinal)) return null;
        var sha = Text(d, "sha256", 64);
        if (sha is null || !Sha256Re().IsMatch(sha)) return null;
        if (d["bytes"] is not JsonValue bv || !bv.TryGetValue<int>(out var bytes) || bytes is < 1 or > MaxDefinitionBytes) return null;
        var version = Text(d, "version", 20);
        var category = Text(d, "category", 20);
        var shape = Text(d, "shape", 20);
        if (version is null || category is not ("ECOMMERCE" or "POS" or "MARKETING") || shape is null || !DefinitionShapes.Contains(shape)) return null;
        if ((shape == "marketing") != (kind == "marketing") || (shape == "marketing") != (category == "MARKETING") || (shape == "posFile" && category != "POS")) return null;
        var requires = new List<string>();
        if (d["requires"] is { } rq)
        {
            if (rq is not JsonArray ra || ra.Count > 20) return null;
            foreach (var r in ra)
                if (r is JsonValue rv && rv.TryGetValue<string>(out var feature) && feature.Length is > 0 and <= 40 && feature.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_')) requires.Add(feature);
                else return null;
        }
        return new HubDefinitionArtifact(path, sha, bytes, version, category, shape, requires);
    }

    static string? Text(JsonObject o, string name, int max)
    {
        if (o[name] is not JsonValue v || !v.TryGetValue<string>(out var s)) return null;
        s = s.Trim();
        return s.Length == 0 || s.Length > max || s.Any(c => char.IsControl(c) && c is not '\n') ? null : s;
    }

    static DateTimeOffset? Date(JsonObject o, string name) =>
        Text(o, name, 40) is { } s && DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    /// <summary>https only, on the allowed code hosts, no userinfo, no port.</summary>
    public static bool SafeUrl(string? url) =>
        url is not null && PlainUrlRe().IsMatch(url) && Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && u.IsDefaultPort
        && string.IsNullOrEmpty(u.UserInfo) && Hosts.Contains(u.Host, StringComparer.OrdinalIgnoreCase);
}

/// <summary>Tryino's catalog signing keys built into this release (keyId → SPKI DER), and the lowest catalog version this release accepts.</summary>
public static class HubKeys
{
    /// <summary>
    /// Tryino's catalog signing keys. tryino-2026a: ES256, non-exportable, Azure Key Vault tryino-hub-kv / hub-signing-2026 (2026-11-24).
    /// A new key ships here one release before it signs; a leaked one moves to <see cref="Revoked"/>.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, byte[]> BuiltIn = new Dictionary<string, byte[]>
    {
        ["tryino-2026a"] = Convert.FromBase64String("MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAExZAKVwQeJ7GGowTSfd6b2iqtQ+JF9fjAIerHL9j6TMmssQl5ezorvRay7gL3tKVh45FdtNy2oPFXU8tm6obUfg=="),
    };

    /// <summary>Keys revoked with this release (Gate T N1: revocation comes with a release, never from a catalog payload).</summary>
    public static readonly IReadOnlyCollection<string> Revoked = [];

    /// <summary>Raised with each release that ships a newer snapshot, so an older signed catalog can't come back after a Redis loss (T3).</summary>
    public const long ReleaseFloor = 0;

    /// <summary>Built-in keys plus operator-configured extra keys ("keyId:base64 SPKI", comma separated; dev and test stamps only).</summary>
    public static IReadOnlyDictionary<string, byte[]> With(string? extra, IReadOnlyDictionary<string, byte[]>? builtIn = null)
    {
        builtIn ??= BuiltIn;
        var all = new Dictionary<string, byte[]>(builtIn);
        foreach (var part in (extra ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var i = part.IndexOf(':');
            if (i <= 0) continue;
            if (builtIn.ContainsKey(part[..i])) continue;   // an extra key never replaces or poses as a Tryino key (review finding)
            try { all[part[..i]] = Convert.FromBase64String(part[(i + 1)..]); } catch (FormatException) { }
        }
        return all;
    }
}
