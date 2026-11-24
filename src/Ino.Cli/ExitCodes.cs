namespace Ino.Cli;

/// <summary>Exit codes (docs/MCP_CLI.md §6). Scripts and agents rely on them, so they never change meaning.</summary>
public static class ExitCodes
{
    public const int Ok = 0, Business = 1, Usage = 2, Auth = 3, NotFound = 4, Conflict = 5, RateLimited = 6, Network = 7;

    public static int FromStatus(int status) => status switch
    {
        >= 200 and < 300 => Ok,
        401 or 403 => Auth,
        404 => NotFound,
        409 => Conflict,
        429 => RateLimited,
        _ => Business,
    };
}

/// <summary>A failure the CLI reports with a code, a message and an optional hint (never a secret).</summary>
public sealed class CliException(int exitCode, string code, string message, string? hint = null) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
    public string Code { get; } = code;
    public string? Hint { get; } = hint;
}
