namespace Connector;

/// <summary>Settings come only from the environment (or a .env file in the working folder); nothing secret is written in code.</summary>
public sealed record Settings(string BaseUrl, string Channel, string ApiKey, string Secret, string? WebhookSecret = null, int Port = 8080,
    string QueueFile = "./data/queue.jsonl")
{
    public static void LoadEnv(string path = ".env")
    {
        if (!File.Exists(path)) return;
        foreach (var line in File.ReadAllLines(path))
        {
            var m = System.Text.RegularExpressions.Regex.Match(line, @"^\s*([A-Z0-9_]+)\s*=\s*(.*?)\s*$");
            if (m.Success && Environment.GetEnvironmentVariable(m.Groups[1].Value) is null) Environment.SetEnvironmentVariable(m.Groups[1].Value, m.Groups[2].Value);
        }
    }

    public static Settings FromEnvironment(Func<string, string?>? get = null)
    {
        get ??= Environment.GetEnvironmentVariable;
        string[] need = ["INO_BASE_URL", "INO_CHANNEL", "INO_API_KEY", "INO_HMAC_SECRET"];
        var missing = need.Where(k => string.IsNullOrEmpty(get(k))).ToArray();
        if (missing.Length > 0) throw new InvalidOperationException($"Missing settings: {string.Join(", ", missing)} (see .env.example).");
        return new Settings(get("INO_BASE_URL")!.TrimEnd('/'), get("INO_CHANNEL")!, get("INO_API_KEY")!, get("INO_HMAC_SECRET")!,
            string.IsNullOrEmpty(get("INO_WEBHOOK_SECRET")) ? null : get("INO_WEBHOOK_SECRET"),
            int.TryParse(get("PORT"), out var port) ? port : 8080, get("QUEUE_FILE") ?? "./data/queue.jsonl");
    }
}
