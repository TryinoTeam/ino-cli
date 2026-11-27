using System.Text.RegularExpressions;

namespace Ino.Cli;

/// <summary>
/// IL3b-3, IL9a: package skeletons (templates/&lt;kind&gt;-definition) that the engine's package rules accept as they are: a marketing tool, an
/// e-commerce platform (incoming webhooks), a POS file format. Each comes with RULES.md, what a package of that kind may do.
/// </summary>
public static partial class DefinitionScaffold
{
    public static readonly string[] Kinds = ["marketing", "ecommerce", "pos"];

    // The engine's package id grammar (Hub.ConnectorIds): the name starts with a letter, hyphens only between letters and digits, 2-39 characters.
    [GeneratedRegex("^local_([a-z][a-z0-9]*(?:-[a-z0-9]+)*)$")] private static partial Regex Id();

    public static List<string> Create(string dir, string id, string kind = "marketing")
    {
        if (!Kinds.Contains(kind)) throw new CliException(ExitCodes.Usage, "BAD_KIND", $"--kind is one of {string.Join(", ", Kinds)}.");
        if (Id().Match(id) is not { Success: true } m || m.Groups[1].Length is < 2 or > 39)
            throw new CliException(ExitCodes.Usage, "BAD_ID", "Use an id like local_acme-mailer: local_, then 2-39 lower-case letters, digits and single hyphens, starting with a letter.");
        if (Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any()) throw new CliException(ExitCodes.Usage, "DIR_NOT_EMPTY", $"{dir} is not empty.");
        Directory.CreateDirectory(dir);
        var name = string.Join(' ', id["local_".Length..].Split('-', StringSplitOptions.RemoveEmptyEntries).Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
        var files = new List<string>();
        foreach (var (path, text) in ConnectorScaffold.Template(kind + "-definition"))
        {
            var target = Path.Combine(dir, path);
            File.WriteAllText(target, text.Replace("__ID__", id).Replace("__NAME__", name));
            files.Add(path);
        }
        return files;
    }
}

/// <summary>IL3b-3: kept for `ino marketing definition new` and its callers.</summary>
public static class MarketingDefinitionScaffold
{
    public static List<string> Create(string dir, string id) => DefinitionScaffold.Create(dir, id, "marketing");
}
