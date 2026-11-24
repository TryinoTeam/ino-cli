using System.Text.RegularExpressions;

namespace Ino.Cli;

public sealed record DocSection(string Doc, string Heading, string Text);
public sealed record DocHit(string Doc, string Heading, string Excerpt);

/// <summary>Bundled partner documents (copies made for partners: tools/ino-mcp/docs), split at ## and ### headings.</summary>
public static partial class Docs
{
    static readonly Lazy<IReadOnlyList<DocSection>> All = new(Load);

    static IReadOnlyList<DocSection> Load()
    {
        var asm = typeof(Docs).Assembly;
        var list = new List<DocSection>();
        foreach (var res in asm.GetManifestResourceNames().Where(n => n.StartsWith("docs/", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using var s = asm.GetManifestResourceStream(res)!;
            using var r = new StreamReader(s);
            var name = res["docs/".Length..];
            var heading = "(top)";
            var lines = new List<string>();
            foreach (var line in r.ReadToEnd().Split('\n'))
            {
                if (HeadingLine().IsMatch(line))
                {
                    if (lines.Count > 0) list.Add(new DocSection(name, heading, string.Join('\n', lines).TrimEnd()));
                    heading = line.TrimStart('#').Trim();
                    lines = [];
                }
                lines.Add(line);
            }
            if (lines.Count > 0) list.Add(new DocSection(name, heading, string.Join('\n', lines).TrimEnd()));
        }
        return list;
    }

    [GeneratedRegex("^#{2,3} ")] private static partial Regex HeadingLine();
    [GeneratedRegex("[^a-z0-9_.\\-]+")] private static partial Regex WordSplit();

    public static IReadOnlyList<DocSection> Sections => All.Value;

    public static IReadOnlyList<DocHit> Search(string query, int limit = 6)
    {
        var words = WordSplit().Split(query.ToLowerInvariant()).Where(w => w.Length > 1).ToArray();
        if (words.Length == 0) return [];
        return Sections.Select(s =>
        {
            var body = s.Text.ToLowerInvariant(); var head = s.Heading.ToLowerInvariant();
            var score = words.Sum(w => (head.Contains(w) ? 5 : 0) + Math.Min(5, Count(body, w)));
            return (s, score);
        }).Where(x => x.score > 0).OrderByDescending(x => x.score).Take(limit)
          .Select(x => new DocHit(x.s.Doc, x.s.Heading, x.s.Text.Length > 900 ? x.s.Text[..900] + "…" : x.s.Text)).ToList();
    }

    static int Count(string text, string word)
    {
        var n = 0;
        for (var i = text.IndexOf(word, StringComparison.Ordinal); i >= 0; i = text.IndexOf(word, i + word.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    public static DocSection? Section(string? doc, string heading)
    {
        var h = heading.Trim().ToLowerInvariant();
        var all = Sections.Where(s => doc is null || s.Doc.Equals(doc, StringComparison.OrdinalIgnoreCase)).ToList();
        return all.FirstOrDefault(s => s.Heading.Equals(h, StringComparison.OrdinalIgnoreCase)) ?? all.FirstOrDefault(s => s.Heading.Contains(h, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The reference section that documents an operation: a heading in REALTIME_API.md that names it, else the first section mentioning it in backticks.</summary>
    public static DocSection? SectionFor(string operation)
    {
        var rt = Sections.Where(s => s.Doc == "REALTIME_API.md").ToList();
        return rt.FirstOrDefault(s => s.Heading.Contains(operation, StringComparison.Ordinal))
            ?? rt.FirstOrDefault(s => s.Text.Contains($"`{operation}`", StringComparison.Ordinal));
    }
}
