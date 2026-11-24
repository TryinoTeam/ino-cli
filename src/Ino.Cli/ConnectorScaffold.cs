using System.Text.RegularExpressions;

namespace Ino.Cli;

/// <summary>
/// `ino connector new`: writes a self-hosted connector project from the templates bundled in the CLI (tools/ino-cli/templates/&lt;lang&gt;). Only into a
/// new or empty folder; names are checked; no secret is written (the project reads its key from .env, which the template's .gitignore excludes).
/// </summary>
public static partial class ConnectorScaffold
{
    public static readonly string[] Languages = ["node", "python", "csharp"];

    /// <summary>How to test and start a generated project, per language (shown after `ino connector new`).</summary>
    public static string RunHint(string lang) => lang switch
    {
        "python" => "python -m unittest discover -s tests, python -m connector.server",
        "csharp" => "dotnet test tests/Connector.Tests, dotnet run --project src/Connector",
        _ => "npm test, npm start",
    };

    [GeneratedRegex("^[a-z][a-z0-9-]{1,49}$")] private static partial Regex Name();
    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")] private static partial Regex Channel();

    public static IEnumerable<(string Path, string Text)> Template(string lang)
    {
        var asm = typeof(ConnectorScaffold).Assembly;
        var prefix = $"templates/{lang}/";
        foreach (var res in asm.GetManifestResourceNames().Where(n => n.StartsWith(prefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using var s = asm.GetManifestResourceStream(res)!;
            using var r = new StreamReader(s);
            yield return (res[prefix.Length..], r.ReadToEnd());
        }
    }

    public static List<string> Create(string dir, string name, string lang, string platform, string channel)
    {
        if (!Name().IsMatch(name)) throw new CliException(ExitCodes.Usage, "BAD_NAME", "Use a name like my-shop-connector (lower case, digits, -).");
        if (!Channel().IsMatch(channel)) throw new CliException(ExitCodes.Usage, "BAD_CHANNEL", "The channel code is not valid.");
        if (!Languages.Contains(lang)) throw new CliException(ExitCodes.Usage, "BAD_LANG", $"Languages: {string.Join(", ", Languages)}.");
        if (platform.IndexOfAny(['\n', '\r', '"', '`', '{', '}']) >= 0 || platform.Length > 60)
            throw new CliException(ExitCodes.Usage, "BAD_PLATFORM", "Use a plain platform name, e.g. \"My shop\".");
        if (Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any())
            throw new CliException(ExitCodes.Usage, "NOT_EMPTY", $"{dir} is not empty; choose a new folder.");
        var root = Path.GetFullPath(dir);
        var written = new List<string>();
        foreach (var (rel, text) in Template(lang))
        {
            var target = Path.GetFullPath(Path.Combine(root, rel));
            if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, text.Replace("{{name}}", name).Replace("{{platform}}", platform).Replace("{{channel}}", channel));
            written.Add(rel);
        }
        if (written.Count == 0) throw new CliException(ExitCodes.Business, "NO_TEMPLATE", $"This CLI has no {lang} template.");
        return written;
    }
}
