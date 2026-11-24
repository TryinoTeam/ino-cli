using System.Text.Json.Nodes;
using Ino.Cli;

namespace Ino.Cli.Tests;

public class AgentSetupTests
{
    static string Temp(string name) => Directory.CreateTempSubdirectory(name).FullName;

    [Fact]
    public void Cursor_keeps_other_servers_and_the_users_env_and_keeps_the_first_backup()
    {
        var home = Temp("ino-home");
        var path = Path.Combine(home, ".cursor", "mcp.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var original = "{\"mcpServers\":{\"other\":{\"command\":\"x\"},\"ino\":{\"command\":\"old\",\"env\":{\"INO_WEBHOOK_SECRET\":\"keep-me\"}}},\"theme\":\"dark\"}";
        File.WriteAllText(path, original);
        var s = new AgentSetup(home, home);
        Assert.True(s.RegisterServer("cursor", "dev", dryRun: false).Changed);
        var doc = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Equal("x", doc["mcpServers"]!["other"]!["command"]!.ToString());
        Assert.Equal("dark", doc["theme"]!.ToString());
        Assert.Equal("keep-me", doc["mcpServers"]!["ino"]!["env"]!["INO_WEBHOOK_SECRET"]!.ToString());
        var args = doc["mcpServers"]!["ino"]!["args"]!.AsArray().Select(a => a!.ToString()).ToList();
        Assert.Equal(["mcp", "serve", "--profile", "dev", "--toolsets", "dev,integration"], args.TakeLast(6));
        Assert.DoesNotContain("--allow-writes", args);
        Assert.True(Path.IsPathRooted(doc["mcpServers"]!["ino"]!["command"]!.ToString()));
        Assert.False(s.RegisterServer("cursor", "dev", dryRun: false).Changed);
        s.RegisterServer("cursor", "other-profile", dryRun: false);
        Assert.Equal(original, File.ReadAllText(path + ".bak"));            // the user's original survives repeated runs
    }

    [Fact]
    public void A_file_with_comments_is_not_rewritten()
    {
        var home = Temp("ino-home");
        var path = Path.Combine(home, ".cursor", "mcp.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ // my servers\n \"mcpServers\": {} }");
        var e = Assert.Throws<CliException>(() => new AgentSetup(home, home).RegisterServer("cursor", "dev", false));
        Assert.Equal("CLIENT_CONFIG_HAS_COMMENTS", e.Code);
        Assert.Contains("\"ino\"", e.Message);
        Assert.Equal("{ // my servers\n \"mcpServers\": {} }", File.ReadAllText(path));
    }

    [Fact]
    public void Symbolic_links_inside_a_project_are_refused()
    {
        var proj = Temp("ino-proj");
        Directory.CreateDirectory(Path.Combine(proj, ".git"));
        var outside = Path.Combine(Temp("ino-outside"), "victim.txt");
        File.WriteAllText(outside, "precious");
        Directory.CreateDirectory(Path.Combine(proj, ".claude", "skills", "ino-webhooks"));
        File.CreateSymbolicLink(Path.Combine(proj, ".claude", "skills", "ino-webhooks", "SKILL.md"), outside);
        var e = Assert.Throws<CliException>(() => new AgentSetup(proj, proj).InstallSkills("local", dryRun: false));
        Assert.Equal("SYMLINK_REFUSED", e.Code);
        Assert.Equal("precious", File.ReadAllText(outside));

        var proj2 = Temp("ino-proj");
        Directory.CreateDirectory(Path.Combine(proj2, ".git"));
        Directory.CreateSymbolicLink(Path.Combine(proj2, ".vscode"), Path.GetDirectoryName(outside)!);
        Assert.Equal("SYMLINK_REFUSED", Assert.Throws<CliException>(() => new AgentSetup(proj2, proj2).RegisterServer("vscode", "dev", false)).Code);
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(outside)!, "mcp.json")));
    }

    [Fact]
    public void Dry_run_changes_nothing()
    {
        var home = Temp("ino-home");
        var s = new AgentSetup(home, home);
        Assert.True(s.RegisterServer("claude-desktop", "dev", dryRun: true).Changed);
        Assert.All(s.InstallSkills("global", dryRun: true), x => Assert.True(x.Changed));
        Assert.Contains("add-json --scope user ino", s.RegisterServer("claude-code", "dev", dryRun: true).Detail);
        Assert.Empty(Directory.EnumerateFileSystemEntries(home));
    }

    [Fact]
    public void Claude_code_is_registered_at_user_scope_with_claude_itself()
    {
        var dir = Temp("ino-proj");
        var calls = new List<string[]>();
        var s = new AgentSetup(dir, dir, (f, a) => { calls.Add([f, .. a]); return 0; });
        s.RegisterServer("claude-code", "dev", dryRun: false);
        var call = Assert.Single(calls);
        Assert.Equal(["claude", "mcp", "add-json", "--scope", "user", "ino"], call.Take(6));
        Assert.Contains("\"--profile\",\"dev\"", call[6]);
        Assert.False(File.Exists(Path.Combine(dir, ".mcp.json")));
    }

    [Fact]
    public void Vscode_shape_project_root_and_skills_with_front_matter()
    {
        var proj = Temp("ino-proj");
        Directory.CreateDirectory(Path.Combine(proj, ".git"));
        var sub = Directory.CreateDirectory(Path.Combine(proj, "src", "shop")).FullName;
        var s = new AgentSetup(proj, sub);
        s.RegisterServer("vscode", "dev", dryRun: false);
        var doc = JsonNode.Parse(File.ReadAllText(Path.Combine(proj, ".vscode", "mcp.json")))!;   // at the project root, not in the subfolder
        Assert.Equal("stdio", doc["servers"]!["ino"]!["type"]!.ToString());
        var skills = s.InstallSkills("local", dryRun: false);
        Assert.Equal(["ino-connector-author", "ino-debug-integration", "ino-integrate-ecommerce", "ino-webhooks"], skills.Select(x => x.Detail).Order(StringComparer.Ordinal));
        foreach (var x in skills)
        {
            Assert.StartsWith(Path.Combine(proj, ".claude", "skills"), x.Target);
            var text = File.ReadAllText(x.Target);
            Assert.StartsWith("---\nname: " + x.Detail + "\ndescription: ", text);
            Assert.DoesNotMatch(@"\bik_[A-Za-z0-9_]{8,}|whsec_[A-Za-z0-9+/=]{16,}", text);
        }
    }

    [Fact]
    public void Broken_or_odd_client_files_are_not_overwritten()
    {
        var home = Temp("ino-home");
        var path = Path.Combine(home, ".cursor", "mcp.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        foreach (var text in new[] { "not json", "{\"mcpServers\": []}", "{\"a\":1,\"a\":2}" })
        {
            File.WriteAllText(path, text);
            Assert.Equal(ExitCodes.Usage, Assert.Throws<CliException>(() => new AgentSetup(home, home).RegisterServer("cursor", "dev", false)).ExitCode);
            Assert.Equal(text, File.ReadAllText(path));
        }
    }
}
