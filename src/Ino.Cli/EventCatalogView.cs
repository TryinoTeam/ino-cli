using System.Globalization;
using System.Text.Json.Nodes;

namespace Ino.Cli;

public sealed record EventTypeInfo(string Type, int Version, string Group, string Title);

/// <summary>Reads the engine's catalog document ({events:[{type, version, group, title, visibility, example, dataSchema}]}).</summary>
public static class EventCatalogView
{
    static IEnumerable<JsonObject> Entries(JsonNode catalog) => ((catalog as JsonObject)?["events"] as JsonArray)?.OfType<JsonObject>() ?? [];

    static int VersionOf(JsonObject e) => int.TryParse(e["version"]?.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 1;

    public static List<EventTypeInfo> Public(JsonNode catalog) =>
        Entries(catalog).Where(e => e["visibility"]?.ToString() == "public")
            .Select(e => new EventTypeInfo(e["type"]!.ToString(), VersionOf(e), e["group"]?.ToString() ?? "", e["title"]?.ToString() ?? ""))
            .OrderBy(e => e.Type, StringComparer.Ordinal).ThenBy(e => e.Version).ToList();

    /// <summary>The catalog's example envelope for a public type (newest version unless one is asked for).</summary>
    public static JsonObject Example(JsonNode catalog, string type, int? version)
    {
        var matches = Entries(catalog).Where(e => e["type"]?.ToString() == type && e["visibility"]?.ToString() == "public").ToList();
        if (matches.Count == 0) throw new CliException(ExitCodes.NotFound, "UNKNOWN_EVENT", $"{type} is not a public event type. See `ino events types`.");
        var pick = version is { } v ? matches.FirstOrDefault(e => VersionOf(e) == v)
                   ?? throw new CliException(ExitCodes.NotFound, "UNKNOWN_VERSION", $"{type} has no version {v} (has {string.Join(", ", matches.Select(VersionOf))}).")
                 : matches.OrderByDescending(VersionOf).First();
        return pick["example"]?.DeepClone() as JsonObject ?? throw new CliException(ExitCodes.Business, "NO_EXAMPLE", $"The catalog has no example for {type}.");
    }

    /// <summary>--override data.points=50: dotted path below the envelope; the value is JSON when it parses (50, true, "x", {…}), else a string.</summary>
    public static void Override(JsonObject envelope, string assignment)
    {
        var eq = assignment.IndexOf('=');
        if (eq <= 0) throw new CliException(ExitCodes.Usage, "BAD_OVERRIDE", "Use --override path=value, e.g. data.points=50.");
        var path = assignment[..eq].Split('.');
        if (path.Any(p => p.Length == 0) || path[0] is "id" or "time")
            throw new CliException(ExitCodes.Usage, "BAD_OVERRIDE", $"Cannot override {assignment[..eq]}.");
        JsonNode value;
        try { value = JsonNode.Parse(assignment[(eq + 1)..]) ?? JsonValue.Create(assignment[(eq + 1)..])!; }
        catch (System.Text.Json.JsonException) { value = JsonValue.Create(assignment[(eq + 1)..])!; }
        var node = envelope;
        foreach (var part in path[..^1])
        {
            if (node[part] is not JsonObject next) { next = new JsonObject(); node[part] = next; }
            node = next;
        }
        node[path[^1]] = value;
    }
}
