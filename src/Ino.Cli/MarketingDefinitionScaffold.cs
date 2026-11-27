using System.Text.RegularExpressions;

namespace Ino.Cli;

/// <summary>IL3b-3: a marketing tool package skeleton (templates/marketing-definition) that the engine's package rules accept as it is.</summary>
public static partial class MarketingDefinitionScaffold
{
    [GeneratedRegex("^local_[a-z0-9][a-z0-9-]{1,40}$")] private static partial Regex Id();

    public static List<string> Create(string dir, string id)
    {
        if (!Id().IsMatch(id)) throw new CliException(ExitCodes.Usage, "BAD_ID", "Use an id like local_acme-mailer (local_, then lower case, digits and -).");
        if (Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any()) throw new CliException(ExitCodes.Usage, "DIR_NOT_EMPTY", $"{dir} is not empty.");
        Directory.CreateDirectory(dir);
        var name = string.Join(' ', id["local_".Length..].Split('-', StringSplitOptions.RemoveEmptyEntries).Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
        var files = new List<string>();
        foreach (var (path, text) in ConnectorScaffold.Template("marketing-definition"))
        {
            var target = Path.Combine(dir, path);
            File.WriteAllText(target, text.Replace("__ID__", id).Replace("__NAME__", name));
            files.Add(path);
        }
        return files;
    }
}
