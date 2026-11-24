using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ino.Cli;

/// <summary>One change `ino agent setup` makes (or would make with --dry-run).</summary>
public sealed record SetupStep(string Kind, string Target, string Detail, bool Changed);

/// <summary>
/// `ino agent setup`: registers `ino mcp serve` with an AI client and installs the Ino skills for Claude Code. Rules:
/// - other servers and settings in a client's file are kept, and an existing "ino" entry keeps its own keys (env, extra args are replaced only in
///   command/args); a file with comments (JSONC) is not rewritten: the entry to add is printed instead;
/// - the first backup (*.bak) is never overwritten, so the user's original survives repeated runs;
/// - nothing is written through a symbolic link inside a project (a repository could point it at a file elsewhere); for user-level files a link is
///   followed to its target so dotfile managers keep working;
/// - the server entry uses an absolute, stable path (Claude Desktop starts servers with a minimal PATH in /);
/// - writes are never switched on here.
/// </summary>
public sealed class AgentSetup(string home, string workingDir, Func<string, string[], int>? run = null)
{
    public static readonly string[] Clients = ["claude-code", "claude-desktop", "cursor", "vscode"];
    static readonly JsonSerializerOptions Out = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    readonly Func<string, string[], int> exec = run ?? Run;

    /// <summary>The command that starts this CLI. Package-manager paths that change on upgrade (Homebrew Cellar) are replaced by their stable link.</summary>
    public static (string Command, List<string> Args, string? Warning) SelfCommand()
    {
        var process = Environment.ProcessPath ?? "ino";
        if (Path.GetFileNameWithoutExtension(process).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            return (process, [Path.Combine(AppContext.BaseDirectory, "ino.dll")], "running from a build (dotnet ino.dll); run setup again from the installed ino");
        var cellar = process.IndexOf("/Cellar/", StringComparison.Ordinal);
        if (cellar > 0 && File.Exists(Path.Combine(process[..cellar], "bin", "ino"))) return (Path.Combine(process[..cellar], "bin", "ino"), [], null);
        return (process, [], process.Contains("/_npx/", StringComparison.Ordinal) ? "ino runs from the npx cache; install it (Homebrew, winget or npm -g) so the path stays valid" : null);
    }

    public JsonObject ServerEntry(string profile, bool vscodeStyle, out string? warning)
    {
        var (cmd, args, w) = SelfCommand();
        warning = w;
        args.AddRange(["mcp", "serve", "--profile", profile, "--toolsets", "dev,integration"]);
        var o = new JsonObject { ["command"] = cmd, ["args"] = new JsonArray(args.Select(a => (JsonNode)a).ToArray()) };
        if (vscodeStyle) o["type"] = "stdio";
        return o;
    }

    /// <summary>Project root: the nearest folder with .git above the working directory (else the working directory).</summary>
    string ProjectRoot()
    {
        for (var d = new DirectoryInfo(workingDir); d is not null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, ".git")) || File.Exists(Path.Combine(d.FullName, ".git"))) return d.FullName;
        return workingDir;
    }

    public string ConfigPath(string client) => client switch
    {
        "claude-desktop" when OperatingSystem.IsWindows() => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "claude_desktop_config.json"),
        "claude-desktop" when OperatingSystem.IsMacOS() => Path.Combine(home, "Library", "Application Support", "Claude", "claude_desktop_config.json"),
        "claude-desktop" => Path.Combine(home, ".config", "Claude", "claude_desktop_config.json"),
        "cursor" => Path.Combine(home, ".cursor", "mcp.json"),
        "vscode" => Path.Combine(ProjectRoot(), ".vscode", "mcp.json"),
        "claude-code" => Path.Combine(ProjectRoot(), ".mcp.json"),
        _ => throw new CliException(ExitCodes.Usage, "BAD_CLIENT", $"Clients: {string.Join(", ", Clients)}."),
    };

    bool IsProjectFile(string path) => Path.GetFullPath(path).StartsWith(Path.GetFullPath(ProjectRoot()) + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    /// <summary>Refuses a symbolic link anywhere between the project root and the file (project files), or resolves a user file's own link.</summary>
    string SafeTarget(string path)
    {
        if (IsProjectFile(path))
        {
            var root = Path.GetFullPath(ProjectRoot());
            var rel = Path.GetRelativePath(root, Path.GetFullPath(path));
            var cur = root;
            foreach (var part in rel.Split(Path.DirectorySeparatorChar))
            {
                cur = Path.Combine(cur, part);
                FileSystemInfo fi = Directory.Exists(cur) ? new DirectoryInfo(cur) : new FileInfo(cur);
                if (fi.Exists && fi.LinkTarget is not null)
                    throw new CliException(ExitCodes.Usage, "SYMLINK_REFUSED", $"{cur} is a symbolic link; setup does not write through links inside a project. Remove it or add the entry by hand.");
            }
            return path;
        }
        var f = new FileInfo(path);
        return f.Exists && f.LinkTarget is not null ? f.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? path : path;
    }

    static void WriteAtomically(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path) && !File.Exists(path + ".bak")) File.Copy(path, path + ".bak");   // the first backup keeps the user's original
        var tmp = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Adds or updates the "ino" server in a client's JSON file, keeping everything else.</summary>
    public SetupStep RegisterServer(string client, string profile, bool dryRun)
    {
        var vscode = client == "vscode";
        var entry = ServerEntry(profile, vscode, out var warning);
        if (client == "claude-code") return RegisterClaudeCode(entry, dryRun, warning);
        var path = SafeTarget(ConfigPath(client));
        var section = vscode ? "servers" : "mcpServers";
        JsonObject doc = [];
        if (File.Exists(path))
        {
            var text = File.ReadAllText(path);
            try { doc = JsonNode.Parse(text) as JsonObject ?? throw new JsonException(); _ = doc.Count; }   // Count builds the object now: duplicate keys throw here
            catch (JsonException)
            {
                var hasComments = false;
                try { hasComments = JsonNode.Parse(text, documentOptions: new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) is JsonObject; }
                catch (JsonException) { }
                throw new CliException(ExitCodes.Usage, hasComments ? "CLIENT_CONFIG_HAS_COMMENTS" : "BAD_CLIENT_CONFIG",
                    hasComments ? $"{path} has comments; setup does not rewrite it (they would be lost). Add this under \"{section}\" by hand: \"ino\": {entry.ToJsonString(Out)}"
                                : $"{path} is not a JSON object; fix it by hand, then run setup again.");
            }
            catch (ArgumentException) { throw new CliException(ExitCodes.Usage, "BAD_CLIENT_CONFIG", $"{path} has duplicate keys; fix it by hand, then run setup again."); }
        }
        if (doc[section] is { } existing && existing is not JsonObject)
            throw new CliException(ExitCodes.Usage, "BAD_CLIENT_CONFIG", $"\"{section}\" in {path} is not an object; fix it by hand.");
        var servers = doc[section] as JsonObject ?? [];
        // Keep the user's own keys on an existing entry (env such as INO_WEBHOOK_SECRET); command, args and type are ours.
        var merged = servers["ino"] is JsonObject old ? (JsonObject)old.DeepClone() : [];
        foreach (var (k, v) in entry) merged[k] = v?.DeepClone();
        var changed = servers["ino"]?.ToJsonString() != merged.ToJsonString();
        if (changed && !dryRun)
        {
            servers["ino"] = merged;
            doc[section] = servers;
            WriteAtomically(path, doc.ToJsonString(Out));
        }
        var note = client == "claude-desktop" ? "restart Claude Desktop" : "reload the editor's MCP servers";
        return new SetupStep("mcp-server", path, note + (warning is null ? "" : $"; note: {warning}"), changed);
    }

    /// <summary>Claude Code: `claude mcp add-json --scope user`, so no machine-specific path lands in a project's committed .mcp.json.</summary>
    SetupStep RegisterClaudeCode(JsonObject entry, bool dryRun, string? warning)
    {
        var json = entry.ToJsonString();
        var shown = $"claude mcp add-json --scope user ino '{json}'";
        if (dryRun) return new SetupStep("mcp-server", "claude (user scope)", shown, true);
        int code;
        try { code = exec("claude", ["mcp", "add-json", "--scope", "user", "ino", json]); }
        catch (System.ComponentModel.Win32Exception)
        { throw new CliException(ExitCodes.Usage, "NO_CLAUDE", $"The claude command is not on PATH. Run this yourself: {shown}"); }
        if (code != 0) throw new CliException(ExitCodes.Business, "CLAUDE_FAILED", $"`claude mcp add-json` failed ({code}). If an ino server exists already, run `claude mcp remove --scope user ino` first.");
        return new SetupStep("mcp-server", "claude (user scope)", "restart Claude Code" + (warning is null ? "" : $"; note: {warning}"), true);
    }

    static int Run(string file, string[] args)
    {
        var psi = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode;
    }

    /// <summary>Copies the bundled skills to Claude Code's skills folder: ~/.claude/skills (global) or &lt;project&gt;/.claude/skills (local).</summary>
    public List<SetupStep> InstallSkills(string scope, bool dryRun)
    {
        var root = scope == "global" ? Path.Combine(home, ".claude", "skills") : Path.Combine(ProjectRoot(), ".claude", "skills");
        var steps = new List<SetupStep>();
        foreach (var (name, text) in Skills.All())
        {
            if (name.Contains('/') || name.Contains('\\') || name.Contains("..")) continue;
            var path = SafeTarget(Path.Combine(root, name, "SKILL.md"));
            var changed = !File.Exists(path) || File.ReadAllText(path) != text;
            if (changed && !dryRun) WriteAtomically(path, text);
            steps.Add(new SetupStep("skill", path, name, changed));
        }
        return steps;
    }
}

/// <summary>The skills bundled in the CLI (tools/ino-cli/skills/*/SKILL.md, embedded).</summary>
public static class Skills
{
    public static IEnumerable<(string Name, string Text)> All()
    {
        var asm = typeof(Skills).Assembly;
        foreach (var res in asm.GetManifestResourceNames().Where(n => n.StartsWith("skills/", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using var s = asm.GetManifestResourceStream(res)!;
            using var r = new StreamReader(s);
            yield return (res["skills/".Length..].Split('/')[0], r.ReadToEnd());
        }
    }
}
