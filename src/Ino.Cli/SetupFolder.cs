using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.EventEmitters;

namespace Ino.Cli;

/// <summary>
/// MCP/CLI P1.2 (docs/MCP_CLI_P1_LOG.md, design v3): the setup folder `ino init` writes from the engine's export. File names are fixed (never made from
/// firm data); a section the engine did not export gets no file, and `ino.yaml` says why (later commands never change a section without a file). The
/// export time is only in `ino.yaml`, so two exports of an unchanged firm give the same section files.
/// </summary>
public static partial class SetupFolder
{
    public const string Manifest = "ino.yaml", Program = "program/program.yaml", Tiers = "program/tiers.yaml", Channels = "channels/settings.yaml";
    public static readonly IReadOnlyList<string> Files = [Manifest, Program, Tiers, Channels];

    static readonly ISerializer Yaml = new SerializerBuilder()
        .WithQuotingNecessaryStrings()
        .WithEventEmitter(next => new Yaml11Safe(next))   // and for YAML 1.1 readers too: "NO", "ON", "0123", "1_000" stay strings
        .DisableAliases()
        .Build();

    /// <summary>
    /// Writes the folder; returns the files written (relative). Refuses a folder with other content unless <paramref name="force"/>. Each file is written
    /// to a temporary name and moved into place; stale files of sections no longer exported are removed only after that, and ino.yaml goes last, so
    /// an interrupted run never leaves a manifest that promises a file that is not there (review P1.2 K3).
    /// </summary>
    public static IReadOnlyList<string> Write(string dir, JsonObject export, bool force)
    {
        try { return WriteCore(dir, export, force); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new CliException(ExitCodes.Usage, "FOLDER_NOT_WRITABLE", $"Could not write to {dir}: {e.Message}");
        }
    }

    static IReadOnlyList<string> WriteCore(string dir, JsonObject export, bool force)
    {
        var root = Path.GetFullPath(dir);
        if (File.Exists(root)) throw new CliException(ExitCodes.Usage, "NOT_A_FOLDER", $"{dir} is a file. Give a folder.");
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any() && !force)
            throw new CliException(ExitCodes.Conflict, "FOLDER_NOT_EMPTY", $"{dir} is not empty. Use an empty folder, or --force to replace the setup files in it.");
        Directory.CreateDirectory(root);

        var tenant = Text(export["tenant"]);
        var env = Text(export["environment"]);
        var sections = export["sections"] as JsonObject ?? [];
        bool Exported(string key) => sections[key]?["exported"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
        string Header(string what) =>
            $"# {what} of {tenant} ({env}), written by `ino init`. No secrets.\n# Change it here, then `ino plan` shows the difference to a firm.\n";

        var written = new List<string>();
        void Put(string rel, string what, object? body)
        {
            var path = Path.Combine(root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, Header(what) + Yaml.Serialize(body), new System.Text.UTF8Encoding(false));
            File.Move(tmp, path, overwrite: true);
            written.Add(rel);
        }

        if (Exported("program") && export["program"] is JsonObject program) Put(Program, "Program settings", Plain(program));
        if (Exported("tiers") && export["tiers"] is JsonObject tiers) Put(Tiers, "Tiers and tier benefits", Plain(tiers));
        if (Exported("channels") && export["channels"] is JsonArray channels) Put(Channels, "Channel settings", Plain(new JsonObject { ["channels"] = channels.DeepClone() }));
        // A section that is no longer exported must not keep an old file (no file = not managed); our fixed files only.
        foreach (var f in Files.Where(f => f != Manifest && !written.Contains(f)))
            if (File.Exists(Path.Combine(root, f))) File.Delete(Path.Combine(root, f));
        Put(Manifest, "Setup folder", Plain(new JsonObject
        {
            ["schemaVersion"] = export["schemaVersion"]?.DeepClone(),
            ["source"] = new JsonObject { ["tenant"] = tenant, ["environment"] = env, ["exportedAt"] = export["exportedAt"]?.DeepClone() },
            ["sections"] = sections.DeepClone(),
            ["references"] = export["references"]?.DeepClone(),
            ["warnings"] = export["warnings"]?.DeepClone(),
        }));
        return written;
    }

    /// <summary>Double-quotes strings a YAML 1.1 reader would take for a boolean, null or number (YamlDotNet quotes only by YAML 1.2 rules).</summary>
    sealed partial class Yaml11Safe(IEventEmitter next) : ChainedEventEmitter(next)
    {
        [GeneratedRegex(@"^(y|Y|yes|Yes|YES|n|N|no|No|NO|true|True|TRUE|false|False|FALSE|on|On|ON|off|Off|OFF|null|Null|NULL|~|[-+]?[0-9_.,:]+([eE][-+]?[0-9]+)?|\.inf|\.nan|0x[0-9a-fA-F_]+|0b[01_]+|0o?[0-7_]+|\d{4}-\d\d?-\d\d?([Tt ].*)?)$")]
        private static partial Regex Ambiguous();

        public override void Emit(ScalarEventInfo eventInfo, IEmitter emitter)
        {
            // Also any control character other than a line feed: a block scalar would turn CRLF into LF and every later `plan` would differ (review P1.2 K2).
            if (eventInfo.Source.Type == typeof(string) && eventInfo.Source.Value is string s
                && (Ambiguous().IsMatch(s) || s.Any(c => char.IsControl(c) && c != '\n'))) eventInfo.Style = ScalarStyle.DoubleQuoted;
            base.Emit(eventInfo, emitter);
        }
    }

    static string Text(JsonNode? n) => n is null ? "" : new string(n.ToString().Select(c => char.IsControl(c) ? '?' : c).ToArray());

    /// <summary>JSON to plain values the YAML writer understands, keeping key order; numbers as invariant decimals or integers, never culture-formatted.</summary>
    internal static object? Plain(JsonNode? n) => n switch
    {
        null => null,
        JsonObject o => o.ToDictionary(kv => kv.Key, kv => Plain(kv.Value)),
        JsonArray a => a.Select(Plain).ToList(),
        JsonValue v => v.GetValueKind() switch
        {
            JsonValueKind.String => v.GetValue<string>(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => long.TryParse(v.ToJsonString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)
                ? l : decimal.Parse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture),
            _ => v.ToJsonString(),
        },
        _ => n.ToJsonString(),
    };
}
