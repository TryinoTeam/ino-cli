namespace Ino.Cli;

/// <summary>Everything a command touches outside itself, so tests can run commands in-process with fakes.</summary>
public sealed class CliContext
{
    public required TextWriter Out { get; init; }
    public required TextWriter Err { get; init; }
    public required TextReader In { get; init; }
    public bool InputIsTerminal { get; init; }
    public required IDictionary<string, string?> Env { get; init; }
    public required ProfileStore Profiles { get; init; }
    public Func<Connection, AuthLock?, ChannelClient> ClientFactory { get; init; } = (c, l) => new ChannelClient(c) { Lock = l };
    public Func<string?> ReadHidden { get; init; } = () => null;
    /// <summary>Handler for forwarding to the local endpoint (listen, trigger); tests replace it.</summary>
    public HttpMessageHandler? LocalHandler { get; init; }
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;
    /// <summary>MCP transport streams for tests; null = the process's stdin/stdout.</summary>
    public Stream? McpInput { get; init; }
    public Stream? McpOutput { get; init; }
    /// <summary>Working directory for project-level files (tests set it).</summary>
    public string? WorkingDir { get; init; }
    /// <summary>Runs a command and captures its output (git, for `ino hub submit`); tests replace it.</summary>
    public Func<string, string[], string, (int Code, string Out)>? Capture { get; init; }
    /// <summary>HTTP handler for the store catalog and Tryino's intake (`ino hub`); tests replace it.</summary>
    public HttpMessageHandler? HubHandler { get; init; }
    /// <summary>Runs an external command (claude mcp add-json); tests replace it.</summary>
    public Func<string, string[], int>? RunProcess { get; init; }

    public bool IsCi => Env.TryGetValue("CI", out var v) && v is not null && (v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1");
    public bool NoColor => Env.TryGetValue("NO_COLOR", out var v) && !string.IsNullOrEmpty(v);

    public static CliContext FromProcess()
    {
        var env = System.Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string?)e.Value);
        return new CliContext
        {
            Out = Console.Out, Err = Console.Error, In = Console.In, InputIsTerminal = !Console.IsInputRedirected, Env = env,
            Profiles = new ProfileStore(SecretStores.ForPlatform(), env: env),
            ReadHidden = ReadHiddenLine,
        };
    }

    /// <summary>Reads one line from the terminal without echo.</summary>
    static string? ReadHiddenLine()
    {
        var sb = new System.Text.StringBuilder();
        while (true)
        {
            var k = Console.ReadKey(intercept: true);
            if (k.Key == ConsoleKey.Enter) { Console.Error.WriteLine(); return sb.ToString(); }
            if (k.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; continue; }
            if (!char.IsControl(k.KeyChar)) sb.Append(k.KeyChar);
        }
    }
}
