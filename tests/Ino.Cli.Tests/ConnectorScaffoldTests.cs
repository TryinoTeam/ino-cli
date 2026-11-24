using Ino.Cli;

namespace Ino.Cli.Tests;

public class ConnectorScaffoldTests
{
    [Theory]
    [InlineData("node", "src/ino-client.js")]
    [InlineData("python", "connector/ino_client.py")]
    [InlineData("csharp", "src/Connector/InoClient.cs")]
    public void Writes_the_project_with_names_filled_in_and_no_secret(string lang, string client)
    {
        var dir = Path.Combine(Directory.CreateTempSubdirectory("ino-new").FullName, "my-shop");
        var files = ConnectorScaffold.Create(dir, "my-shop", lang, "My Shop", "MODERN_ECOM");
        Assert.Contains(client, files);
        Assert.Contains(".gitignore", files);
        Assert.Contains("Dockerfile", files);
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(f);
            Assert.DoesNotContain("{{", text);
            Assert.DoesNotMatch(@"\bik_[A-Za-z0-9_]{12,}|whsec_[A-Za-z0-9+/=]{16,}", text);
        }
        Assert.Contains("INO_CHANNEL=MODERN_ECOM", File.ReadAllText(Path.Combine(dir, ".env.example")));
        Assert.Contains(".env", File.ReadAllText(Path.Combine(dir, ".gitignore")));
        Assert.StartsWith("# my-shop", File.ReadAllText(Path.Combine(dir, "README.md")));
    }

    [Theory]
    [InlineData("My Shop", "C", "node")]
    [InlineData("../evil", "C", "node")]
    [InlineData("ok-name", "C;rm", "node")]
    [InlineData("ok-name", "C", "cobol")]
    public void Bad_names_are_refused(string name, string channel, string lang)
    {
        var dir = Path.Combine(Directory.CreateTempSubdirectory("ino-new").FullName, "x");
        Assert.Equal(ExitCodes.Usage, Assert.Throws<CliException>(() => ConnectorScaffold.Create(dir, name, lang, "p", channel)).ExitCode);
        Assert.False(Directory.Exists(dir));
    }

    [Theory]
    [InlineData("node", "npm test")]
    [InlineData("python", "python -m unittest")]
    [InlineData("csharp", "dotnet test")]
    public void The_next_step_hint_matches_the_language(string lang, string expected) => Assert.Contains(expected, ConnectorScaffold.RunHint(lang));

    [Fact]
    public void Never_writes_into_a_folder_that_has_files()
    {
        var dir = Directory.CreateTempSubdirectory("ino-new").FullName;
        File.WriteAllText(Path.Combine(dir, "keep.txt"), "mine");
        Assert.Equal("NOT_EMPTY", Assert.Throws<CliException>(() => ConnectorScaffold.Create(dir, "my-shop", "node", "p", "C")).Code);
        Assert.Single(Directory.EnumerateFileSystemEntries(dir));
    }
}
