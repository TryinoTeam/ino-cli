using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace Ino.Cli;

/// <summary>
/// MCP/CLI P1.3 (docs/MCP_CLI_P1_LOG.md, design v2): the difference between a setup folder and a firm, written nowhere. Rules: a section without a
/// file, or not exported on either side, is not managed (nothing is ever removed for it); a key missing from the folder is not managed (never null);
/// folder values are read by the target column's type; fields that differ by environment are listed apart and never counted as changes.
/// </summary>
public static class SetupPlan
{
    public const int MaxBytes = 1024 * 1024, MaxDepth = 16;

    /// <summary>Program fields that differ between environments by nature (shown apart; `programstatus` is never applied).</summary>
    public static readonly IReadOnlySet<string> EnvironmentFields = new HashSet<string>(StringComparer.Ordinal) { "programstatus", "programurl", "programtermsurl", "walletlogourl", "smsfromnumber" };

    static readonly IReadOnlyDictionary<string, string> ChannelTypes = new Dictionary<string, string> { ["backdateDays"] = "Integer", ["walletPercent"] = "Decimal", ["walletCap"] = "Decimal" };

    public sealed record Change(string Section, string Key, string? Field, string Action, string? From, string? To);

    public sealed class Result
    {
        public string Source = "", Target = "";
        public readonly List<Change> Changes = [], ByEnvironment = [];
        public readonly List<string> Warnings = [];
        public readonly SortedDictionary<string, string> Sections = new(StringComparer.Ordinal);
    }

    // ------------------------------------------------------------------ safe YAML: limits checked on parser events, before any tree; no recursion

    /// <summary>A YAML scalar as written: its text and whether it was a plain (unquoted) scalar.</summary>
    public sealed record Scalar(string Text, bool Plain);

    /// <summary>Reads a setup file: one document of mappings, sequences and scalars; no anchors or aliases, only standard tags, bounded depth and size.</summary>
    public static object? Load(string path)
    {
        try { return LoadCore(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw Bad(path, $"could not be read: {Printable(e.Message)}"); }
    }

    static object? LoadCore(string path)
    {
        // A regular file, read through a cap (the length on disk is not trusted: a FIFO or a growing file).
        if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.Device | FileAttributes.ReparsePoint)) != 0) throw Bad(path, "is not a regular file");
        var buffer = new byte[MaxBytes + 1];
        int read = 0;
        using (var file = File.OpenRead(path))
            for (int n; read < buffer.Length && (n = file.Read(buffer, read, buffer.Length - read)) > 0;) read += n;
        if (read > MaxBytes) throw Bad(path, $"is larger than {MaxBytes / 1024} KB");
        using var reader = new StreamReader(new MemoryStream(buffer, 0, read), new System.Text.UTF8Encoding(false));
        var parser = new Parser(reader);
        var stack = new Stack<(object Node, string? PendingKey)>();
        object? root = null;
        var documents = 0;
        try
        {
            while (parser.MoveNext())
            {
                var e = parser.Current!;
                switch (e)
                {
                    case StreamStart or StreamEnd or DocumentEnd: continue;
                    case DocumentStart: if (++documents > 1) throw Bad(path, "has more than one document"); continue;
                    case AnchorAlias: throw Bad(path, "uses an alias (*name); write values out instead");
                }
                if (e is NodeEvent n)
                {
                    if (!n.Anchor.IsEmpty) throw Bad(path, "uses an anchor (&name); write values out instead");
                    if (!n.Tag.IsEmpty && !StandardTag(n.Tag.Value)) throw Bad(path, $"uses the tag {Printable(n.Tag.Value)}, which is not allowed");
                }
                switch (e)
                {
                    case MappingStart or SequenceStart:
                        if (stack.Count >= MaxDepth) throw Bad(path, $"is nested deeper than {MaxDepth} levels");
                        object container = e is MappingStart ? new Dictionary<string, object?>(StringComparer.Ordinal) : new List<object?>();
                        Attach(container);
                        stack.Push((container, null));
                        break;
                    case MappingEnd or SequenceEnd:
                        var (done, _) = stack.Pop();
                        if (stack.Count == 0) root = done;
                        break;
                    case YamlDotNet.Core.Events.Scalar s:
                        var value = new Scalar(s.Value, s.Style == ScalarStyle.Plain && s.Tag.IsEmpty);   // a tag (`!!str 5`) is read like a quoted value
                        if (stack.Count == 0) { root = value; break; }
                        var (top, key) = stack.Peek();
                        if (top is Dictionary<string, object?> && key is null)
                        {
                            stack.Pop(); stack.Push((top, s.Value));       // a key
                        }
                        else Attach(value);
                        break;
                }
            }
        }
        catch (YamlException e) { throw Bad(path, $"is not valid YAML (line {e.Start.Line}): {Printable(e.Message)}"); }
        catch (ArgumentException e) { throw Bad(path, $"is not valid YAML: {Printable(e.Message)}"); }       // e.g. a verbatim tag that is not a URI
        return root;

        void Attach(object value)
        {
            if (stack.Count == 0) return;
            var (top, key) = stack.Pop();
            if (top is List<object?> list) list.Add(value);
            else if (top is Dictionary<string, object?> map)
            {
                if (key is null) throw Bad(path, "has a mapping or list used as a key");
                if (!map.TryAdd(key, value)) throw Bad(path, $"has the key '{Printable(key)}' twice");
                key = null;
            }
            stack.Push((top, key));
        }
    }

    static bool StandardTag(string tag) => tag is "tag:yaml.org,2002:str" or "tag:yaml.org,2002:int" or "tag:yaml.org,2002:float" or "tag:yaml.org,2002:bool"
        or "tag:yaml.org,2002:null" or "tag:yaml.org,2002:map" or "tag:yaml.org,2002:seq" or "!";

    static CliException Bad(string path, string what) => new(ExitCodes.Usage, "BAD_SETUP_FILE", $"{Path.GetFileName(path)} {what}.");

    static string Printable(string s) => new(s.Select(c => char.IsControl(c) ? '?' : c).ToArray());

    // ------------------------------------------------------------------ values: folder (by column type) and target (JSON) to one canonical form

    /// <summary>Canonical text of a value, or null for "empty". Throws for a folder value that does not fit its column's type.</summary>
    internal static string? FromFolder(object? v, string type, string where)
    {
        if (v is null) return null;
        if (v is List<object?> list)
            return type == "MultiPicklist" ? "[" + string.Join(",", list.Select(x => FromFolder(x, "Picklist", where))) + "]"
                : throw new CliException(ExitCodes.Usage, "BAD_VALUE", $"{where}: a list is not a {type}.");
        if (v is not Scalar s) throw new CliException(ExitCodes.Usage, "BAD_VALUE", $"{where}: a mapping is not a {type}.");
        if (s.Plain && s.Text is "" or "~" or "null" or "Null" or "NULL") return null;
        string Fail() => throw new CliException(ExitCodes.Usage, "BAD_VALUE", $"{where}: '{Printable(s.Text)}' is not a valid {type}.");
        return type switch
        {
            "String" or "Memo" or "DateTime" => s.Text.Length == 0 ? null : Text(s.Text),
            "Integer" => s.Plain && long.TryParse(s.Text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l) ? l.ToString(CultureInfo.InvariantCulture) : Fail(),
            "Decimal" or "Money" => s.Plain && decimal.TryParse(s.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? Number(d) : Fail(),
            "Boolean" => s.Plain && bool.TryParse(s.Text, out var b) ? (b ? "true" : "false") : Fail(),
            "Picklist" => s.Plain && int.TryParse(s.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? "#" + n : "o:" + s.Text,
            _ => Text(s.Text),
        };
    }

    internal static string? FromTarget(JsonNode? v, string type) => v switch
    {
        null => null,
        JsonArray a => "[" + string.Join(",", a.Select(x => FromTarget(x, "Picklist"))) + "]",
        JsonValue j => j.GetValueKind() switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Number when type == "Picklist" => "#" + j.ToJsonString(),
            JsonValueKind.Number => Number(decimal.Parse(j.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture)),
            JsonValueKind.String when type == "Picklist" => "o:" + j.GetValue<string>(),
            JsonValueKind.String => j.GetValue<string>() is { Length: > 0 } t ? Text(t) : null,
            _ => j.ToJsonString(),
        },
        _ => v.ToJsonString(),
    };

    static string Number(decimal d) => (d / 1.000000000000000000000000000000000m).ToString(CultureInfo.InvariantCulture);

    /// <summary>Text; a JSON object or array inside it (tier rules) compares by content, not by spacing.</summary>
    static string Text(string t)
    {
        var trimmed = t.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
            try { return "j:" + JsonNode.Parse(t)!.ToJsonString(); } catch (JsonException) { }
        return "s:" + t;
    }

    /// <summary>The value as a person reads it.</summary>
    internal static string Show(string? canonical) => canonical switch
    {
        null => "(empty)",
        _ when canonical.StartsWith("s:", StringComparison.Ordinal) || canonical.StartsWith("o:", StringComparison.Ordinal) || canonical.StartsWith("j:", StringComparison.Ordinal)
            => Printable(canonical[2..].Length > 80 ? canonical[2..80] + "…" : canonical[2..]),
        _ when canonical.StartsWith('#') => canonical[1..],
        _ => Printable(canonical),
    };

    // ------------------------------------------------------------------ the plan

    public static Result Build(string dir, JsonObject target, bool otherFirm, bool includeEnvironment)
    {
        var r = new Result();
        var manifestPath = Path.Combine(dir, SetupFolder.Manifest);
        if (!File.Exists(manifestPath)) throw new CliException(ExitCodes.Usage, "NOT_A_SETUP_FOLDER", $"{dir} has no {SetupFolder.Manifest}. Run `ino init` first.");
        var manifest = Load(manifestPath) as Dictionary<string, object?> ?? throw Bad(manifestPath, "is not a mapping");
        string? Str(object? o) => (o as Scalar)?.Text;
        var source = manifest.GetValueOrDefault("source") as Dictionary<string, object?>;
        var sourceTenant = Printable(Str(source?.GetValueOrDefault("tenant")) ?? "");
        var targetTenant = Printable(target["tenant"]?.ToString() ?? "");
        r.Source = $"{sourceTenant} ({Printable(Str(source?.GetValueOrDefault("environment")) ?? "")})";
        r.Target = $"{targetTenant} ({Printable(target["environment"]?.ToString() ?? "")})";
        if (sourceTenant.Length == 0) throw Bad(manifestPath, "does not say which firm it was written from (source.tenant)");
        if (!string.Equals(sourceTenant, targetTenant, StringComparison.Ordinal) && !otherFirm)
            throw new CliException(ExitCodes.Usage, "OTHER_FIRM", $"This folder was written from {sourceTenant}, the profile is {targetTenant}. Add --other-firm if you mean to compare them.");
        if (sourceTenant != targetTenant) r.Warnings.Add($"Comparing a folder from {sourceTenant} with {targetTenant}: codes it refers to (segments, categories, voucher batches) may not exist there.");
        r.Warnings.Add("References (segments, stores, brands, categories, products, voucher batches) are not checked against the firm yet.");

        // An engine without the plan fields would make typed reading and "reactivate" impossible (review P1.3 T2-1).
        if (target["fieldTypes"] is not JsonObject || target["inactiveTierCodes"] is not JsonArray)
            throw new CliException(ExitCodes.Usage, "ENGINE_TOO_OLD", $"The engine of {targetTenant} is too old for `ino plan`; it needs the version with P1.3.");
        var folderSections = manifest.GetValueOrDefault("sections") as Dictionary<string, object?> ?? [];
        var targetSections = target["sections"] as JsonObject ?? [];
        var types = target["fieldTypes"] as JsonObject ?? [];

        bool FolderManaged(string key, string file) =>
            (folderSections.GetValueOrDefault(key) as Dictionary<string, object?>)?.GetValueOrDefault("exported") is Scalar { Text: "true" } && File.Exists(Path.Combine(dir, file));
        bool TargetReadable(string key, out string reason)
        {
            var s = targetSections[key];
            reason = Printable(s?["reason"]?.ToString() ?? "not exported by the firm");
            return s?["exported"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
        }

        foreach (var (key, file) in new[] { ("program", SetupFolder.Program), ("tiers", SetupFolder.Tiers), ("channels", SetupFolder.Channels) })
        {
            if (!FolderManaged(key, file)) { r.Sections[key] = "not managed by this folder"; continue; }
            if (!TargetReadable(key, out var why)) { r.Sections[key] = "could not compare: " + why; continue; }
            var doc = Load(Path.Combine(dir, file)) as Dictionary<string, object?> ?? throw Bad(Path.Combine(dir, file), "is not a mapping");
            var before = r.Changes.Count;
            switch (key)
            {
                case "program": Program(doc, target["program"] as JsonObject, types["program"] as JsonObject ?? [], r, includeEnvironment); break;
                case "tiers": TierPlan(doc, folderSections, target, types, r); break;
                case "channels": Channels(doc, target["channels"] as JsonArray ?? [], r); break;
            }
            r.Sections[key] = r.Changes.Count == before ? "no change" : $"{r.Changes.Count - before} change(s)";
        }
        foreach (var (key, node) in targetSections)
            if (!r.Sections.ContainsKey(key)) r.Sections[key] = "not managed: " + Printable(node?["reason"]?.ToString() ?? "");
        return r;
    }

    static void Fields(string section, string key, Dictionary<string, object?> folder, JsonObject? target, JsonObject types, Result r,
        ISet<string>? skip = null, Func<string, bool>? environment = null, IReadOnlyCollection<string>? notInTarget = null)
    {
        foreach (var (field, value) in folder)
        {
            if (skip?.Contains(field) == true) continue;
            var where = Printable($"{section}{(key.Length > 0 ? " " + key : "")}.{field}");
            if (!(types[field] is JsonValue tv && tv.TryGetValue<string>(out var type))) { r.Warnings.Add($"{Printable(where)} is not a field this firm knows; it is left out."); continue; }
            if (notInTarget?.Contains(field) == true || target is not null && !target.ContainsKey(field))
            { r.Warnings.Add($"{where}: the firm's environment does not have this field yet; it is left out."); continue; }
            var from = Canonical(field, FromTarget(target?[field], type));
            var to = Canonical(field, FromFolder(value, type, where));
            if (from == to) continue;
            // An option one engine version does not know is a number on one side and a name on the other: say so, do not change it.
            if (type == "Picklist" && (from?.StartsWith('#') == true) != (to?.StartsWith('#') == true) && from is not null && to is not null)
            { r.Warnings.Add($"{where}: {Show(from)} and {Show(to)} may be the same option seen by two engine versions; not changed."); continue; }
            var change = new Change(section, key, field, "change", Show(from), Show(to));
            if (environment?.Invoke(field) == true) r.ByEnvironment.Add(change); else r.Changes.Add(change);
        }
    }

    /// <summary>Field-specific forms: category earn rates `CAT:points:amount;…` compare by entry, not by spacing, case or order.</summary>
    static string? Canonical(string field, string? value)
    {
        if (field != "earnrateoverrides" || value is null || !value.StartsWith("s:", StringComparison.Ordinal)) return value;
        var entries = value[2..].Split(new[] { ';', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(e => e.Split(':', StringSplitOptions.TrimEntries))
            .Select(f => f.Length == 3 && decimal.TryParse(f[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var p) && decimal.TryParse(f[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var a)
                ? $"{f[0].ToUpperInvariant()}:{Number(p)}:{Number(a)}" : string.Join(':', f))
            .Order(StringComparer.Ordinal);
        return "s:" + string.Join(";", entries);
    }

    static void Program(Dictionary<string, object?> doc, JsonObject? target, JsonObject types, Result r, bool includeEnvironment)
    {
        if (doc.GetValueOrDefault("values") is not Dictionary<string, object?> values) { r.Warnings.Add("program.yaml has no values."); return; }
        var targetValues = target?["values"] as JsonObject;
        if (targetValues is null) r.Warnings.Add("The firm has no active program settings row: every value in the folder would be set.");
        var missing = (target?["notExported"] as JsonArray ?? []).Select(n => n?.ToString() ?? "").ToHashSet(StringComparer.Ordinal);
        Fields("program", "", values, targetValues, types, r, environment: f => !includeEnvironment && EnvironmentFields.Contains(f), notInTarget: missing);
        if (doc.GetValueOrDefault("needsTargetCredentials") is List<object?> creds)
            foreach (var c in creds.OfType<Scalar>().Select(c => c.Text))
                if (r.Changes.Any(ch => ch.Section == "program" && ch.Field == c && ch.To is not "false" and not "(empty)"))
                    r.Warnings.Add($"program.{Printable(c)} needs the firm's own credentials (SMS provider or wallet certificates) before it works.");
    }

    static void TierPlan(Dictionary<string, object?> doc, Dictionary<string, object?> folderSections, JsonObject target, JsonObject types, Result r)
    {
        var tierTypes = types["tier"] as JsonObject ?? [];
        string Code(string? c) => (c ?? "").Trim().ToUpperInvariant();
        if (Rows(doc, "tiers", SetupFolder.Tiers, r) is not { } tierRows) return;
        var folder = Keyed(tierRows, m => Code((m.GetValueOrDefault("tiercode") as Scalar)?.Text), "folder", r);
        var targetTiers = Keyed((target["tiers"]?["tiers"] as JsonArray ?? []).OfType<JsonObject>().ToList(), o => Code(o["tiercode"]?.ToString()), "firm", r);
        var inactive = (target["inactiveTierCodes"] as JsonArray ?? []).Select(n => n?.ToString() ?? "").ToHashSet(StringComparer.Ordinal);
        foreach (var (code, f) in folder)
        {
            if (targetTiers.TryGetValue(code, out var t)) Fields("tiers", code, f, t, tierTypes, r);
            else r.Changes.Add(new Change("tiers", code, null, inactive.Contains(code) ? "reactivate" : "add", null, null));
        }
        foreach (var code in targetTiers.Keys.Where(c => !folder.ContainsKey(c))) r.Changes.Add(new Change("tiers", code, null, "remove", null, null));

        // Benefits: only when both sides exported them (a missing `benefits` key is "not managed", never "none").
        var folderBenefitsOff = (folderSections.GetValueOrDefault("tiers") as Dictionary<string, object?>)?.GetValueOrDefault("benefits") is Dictionary<string, object?> fb
            && fb.GetValueOrDefault("exported") is Scalar { Text: "false" };
        var targetBenefitsOff = target["sections"]?["tiers"]?["benefits"]?["exported"] is JsonValue tb && tb.TryGetValue<bool>(out var on) && !on;
        if (folderBenefitsOff) { r.Warnings.Add("Tier benefits are not managed by this folder."); return; }
        if (Rows(doc, "benefits", SetupFolder.Tiers, r) is not { } benefitRows) return;
        if (targetBenefitsOff || target["tiers"]?["benefits"] is not JsonArray targetBenefits) { r.Warnings.Add("Tier benefits could not be compared (the firm did not export them)."); return; }
        Benefits(benefitRows, targetBenefits.OfType<JsonObject>().ToList(), types["benefit"] as JsonObject ?? [], r);
    }

    /// <summary>
    /// The rows under a key: missing or empty means "not managed" (null, with a warning), never "none"; anything but a list of mappings is an error
    /// (review P1.3 K1: a deleted `tiers:` must not turn into "remove every tier").
    /// </summary>
    static List<object?>? Rows(Dictionary<string, object?> doc, string key, string file, Result r)
    {
        var v = doc.GetValueOrDefault(key);
        if (v is null || v is Scalar { Plain: true, Text: "" or "~" or "null" or "Null" or "NULL" })
        { r.Warnings.Add($"{file} has no {key}: they are not managed by this folder."); return null; }
        if (v is not List<object?> list) throw new CliException(ExitCodes.Usage, "BAD_VALUE", $"{file}: {key} must be a list.");
        if (list.Any(x => x is not Dictionary<string, object?>)) throw new CliException(ExitCodes.Usage, "BAD_VALUE", $"{file}: every entry of {key} must be a mapping (key: value lines).");
        return list;
    }

    static Dictionary<string, Dictionary<string, object?>> Keyed(List<object?> rows, Func<Dictionary<string, object?>, string> key, string side, Result r)
    {
        var map = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);
        foreach (var m in rows.OfType<Dictionary<string, object?>>())
        {
            var k = key(m);
            if (k.Length == 0) r.Warnings.Add($"A tier without a code in the {side} is left out.");
            else if (!map.TryAdd(k, m)) r.Warnings.Add($"The tier code {Printable(k)} appears twice in the {side}; only the first is compared.");
        }
        return map;
    }

    static Dictionary<string, JsonObject> Keyed(List<JsonObject> rows, Func<JsonObject, string> key, string side, Result r)
    {
        var map = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var o in rows)
        {
            var k = key(o);
            if (k.Length == 0) r.Warnings.Add($"A tier without a code in the {side} is left out.");
            else if (!map.TryAdd(k, o)) r.Warnings.Add($"The tier code {Printable(k)} appears twice in the {side}; only the first is compared.");
        }
        return map;
    }

    static void Benefits(List<object?> folderRows, List<JsonObject> targetRows, JsonObject types, Result r)
    {
        static string Norm(string? s) => (s ?? "").Trim().ToUpperInvariant();
        var folder = folderRows.OfType<Dictionary<string, object?>>().Select(m => (Tier: Norm((m.GetValueOrDefault("tier") as Scalar)?.Text),
            Type: Norm((m.GetValueOrDefault("benefittype") as Scalar)?.Text), Name: ((m.GetValueOrDefault("name") as Scalar)?.Text ?? "").Trim(), Row: m)).ToList();
        var target = targetRows.Select(o => (Tier: Norm(o["tier"]?.ToString()), Type: Norm(o["benefittype"]?.ToString()), Name: (o["name"]?.ToString() ?? "").Trim(), Row: o)).ToList();
        // (tier, type) when that pair is unique on both sides, else (tier, type, name).
        string Key(string tier, string type, string name) =>
            folder.Count(f => f.Tier == tier && f.Type == type) <= 1 && target.Count(t => t.Tier == tier && t.Type == type) <= 1 ? $"{tier}/{type}" : $"{tier}/{type}/{name}";
        var f2 = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);
        var t2 = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var f in folder) if (!f2.TryAdd(Key(f.Tier, f.Type, f.Name), f.Row)) r.Warnings.Add(Printable($"Two benefits of {f.Tier} look the same in the folder ({f.Type}, {f.Name}); compare them in Power Apps."));
        foreach (var t in target) if (!t2.TryAdd(Key(t.Tier, t.Type, t.Name), t.Row)) r.Warnings.Add(Printable($"Two benefits of {t.Tier} look the same in the firm ({t.Type}, {t.Name}); compare them in Power Apps."));
        var changedTiers = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (k, f) in f2)
        {
            var tier = k.Split('/')[0];
            if (t2.TryGetValue(k, out var t))
            {
                var skip = new HashSet<string>(StringComparer.Ordinal) { "tier", "voucherBatchLinked" };
                // The batch code only when both sides could read it.
                bool Unreadable(object? linked, object? code) => linked is Scalar { Text: "true" } && (code is null || code is Scalar { Plain: true, Text: "" or "~" or "null" });
                if (Unreadable(f.GetValueOrDefault("voucherBatchLinked"), f.GetValueOrDefault("voucherBatch"))
                    || t["voucherBatchLinked"] is JsonValue lv && lv.TryGetValue<bool>(out var linkedOn) && linkedOn && t["voucherBatch"] is null) skip.Add("voucherBatch");
                var bt = new JsonObject(types.Select(kv => KeyValuePair.Create(kv.Key, kv.Value?.DeepClone())));
                bt["voucherBatch"] = "String";
                Fields("benefits", k, f, t, bt, r, skip);
            }
            else { r.Changes.Add(new Change("benefits", k, null, "add", null, null)); changedTiers.Add(tier); }
        }
        foreach (var k in t2.Keys.Where(k => !f2.ContainsKey(k))) { r.Changes.Add(new Change("benefits", k, null, "remove", null, null)); changedTiers.Add(k.Split('/')[0]); }
        foreach (var tier in changedTiers)
            r.Warnings.Add(Printable($"The benefits of {tier} change: review their translations, and note that a benefit created again may be granted to members again."));
    }

    static void Channels(Dictionary<string, object?> doc, JsonArray target, Result r)
    {
        var types = new JsonObject(ChannelTypes.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)JsonValue.Create(kv.Value))));
        var t = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var o in target.OfType<JsonObject>().Where(o => o["code"] is not null))
            if (!t.TryAdd(o["code"]!.ToString(), o)) r.Warnings.Add($"The channel {Printable(o["code"]!.ToString())} appears twice in the firm.");
        if (Rows(doc, "channels", SetupFolder.Channels, r) is not { } channelRows) return;
        var f = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);
        foreach (var m in channelRows.OfType<Dictionary<string, object?>>())
            if ((m.GetValueOrDefault("code") as Scalar)?.Text is { Length: > 0 } code && !f.TryAdd(code, m)) r.Warnings.Add($"The channel {Printable(code)} appears twice in the folder.");
        foreach (var (code, m) in f)
        {
            if (t.TryGetValue(code, out var o)) Fields("channels", code, m, o, types, r, new HashSet<string> { "code" });
            else r.Changes.Add(new Change("channels", code, null, "add", null, null));
        }
        foreach (var code in t.Keys.Where(c => !f.ContainsKey(c))) r.Changes.Add(new Change("channels", code, null, "reset", null, "defaults"));
    }
}
