using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ino.Cli;

/// <summary>A profile as stored in the config file: no key, no secret (they live in the OS keychain under the profile name).</summary>
public sealed class Profile
{
    public string BaseUrl { get; set; } = "";
    public string Channel { get; set; } = "";
    public string Environment { get; set; } = "";
    public string? KeyHint { get; set; }
    /// <summary>The user allowed `ino mcp serve --allow-writes` on this profile (`ino profile set --mcp-writes on`). A repository cannot set it.</summary>
    public bool McpWrites { get; set; }
    /// <summary>keychain (default) or file (0600 credentials.json, only when asked for, e.g. Linux without a keyring).</summary>
    public string? Store { get; set; }
}

public sealed class ConfigFile
{
    public int Version { get; set; } = 1;
    public string? DefaultProfile { get; set; }
    public SortedDictionary<string, Profile> Profiles { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>Where a resolved connection came from, for `ino doctor` and error messages.</summary>
public sealed record Resolved(Connection Connection, string Source, string? ProfileName);

/// <summary>
/// Config file (<c>$XDG_CONFIG_HOME/ino/config.json</c>, else <c>~/.config/ino/config.json</c>; Windows <c>%APPDATA%\ino\config.json</c>), written with
/// mode 0600 on Unix. Selection order: environment variables (CI) &gt; --profile &gt; project file .ino/config.json (profile name only) &gt; default profile.
/// </summary>
public sealed class ProfileStore(ISecretStore secrets, string? configPath = null, IDictionary<string, string?>? env = null, string? workingDir = null)
{
    static readonly JsonSerializerOptions Json = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    readonly IDictionary<string, string?> environment = env ?? System.Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
        .ToDictionary(e => (string)e.Key, e => (string?)e.Value);

    public string ConfigPath { get; } = configPath ?? DefaultConfigPath();
    FileSecretStore FileStore => new(Path.Combine(Path.GetDirectoryName(ConfigPath)!, "credentials.json"));
    ISecretStore StoreFor(Profile p) => p.Store == "file" ? FileStore : secrets;

    public CursorStore Cursors => new(Path.Combine(Path.GetDirectoryName(ConfigPath)!, "state"));

    /// <summary>
    /// The local signing secret `listen` and `trigger` use for this profile (whsec_…), created on first use. It is kept with the profile's credentials
    /// (keychain or file store) under "listen:&lt;profile&gt;"; with environment-variable credentials it comes from INO_LISTEN_SECRET or is made per run.
    /// </summary>
    public string DeviceSecret(Resolved r)
    {
        if (r.ProfileName is null) return Env("INO_LISTEN_SECRET") ?? StandardWebhooks.NewSecret();
        var prof = Load().Profiles[r.ProfileName];
        var store = StoreFor(prof);
        var key = ListenKey(r.ProfileName);
        if (store.Read(key) is { } c) return c.Secret;
        var secret = StandardWebhooks.NewSecret();
        store.Write(key, new Credentials("listen", secret));
        return secret;
    }

    /// <summary>Keychain account of a profile's listen secret. ':' cannot appear in a profile name, so it never collides with a profile.</summary>
    static string ListenKey(string profile) => $"listen:{profile}";

    /// <summary>The 401 pause marker for a profile (or for environment-variable credentials).</summary>
    public AuthLock LockFor(Resolved r) => new(Path.Combine(Path.GetDirectoryName(ConfigPath)!, "state", $"401-{r.ProfileName ?? "env"}"));

    public static string DefaultConfigPath()
    {
        if (OperatingSystem.IsWindows())
            return Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), "ino", "config.json");
        var xdg = System.Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var root = !string.IsNullOrEmpty(xdg) ? xdg : Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), ".config");
        return Path.Combine(root, "ino", "config.json");
    }

    public static void ValidateName(string name)
    {
        if (name.Length is 0 or > 40 || !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            throw new CliException(ExitCodes.Usage, "BAD_PROFILE_NAME", "Profile names use letters, digits, - and _ (at most 40).");
    }

    public ConfigFile Load()
    {
        if (!File.Exists(ConfigPath)) return new ConfigFile();
        try { return JsonSerializer.Deserialize<ConfigFile>(File.ReadAllText(ConfigPath), Json) ?? new ConfigFile(); }
        catch (JsonException) { throw new CliException(ExitCodes.Usage, "BAD_CONFIG", $"{ConfigPath} is not valid JSON."); }
    }

    public void Save(ConfigFile cfg)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        var tmp = ConfigPath + ".tmp";
        if (!OperatingSystem.IsWindows())
        {
            using var fs = new FileStream(tmp, new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite });
            JsonSerializer.Serialize(fs, cfg, Json);
        }
        else File.WriteAllText(tmp, JsonSerializer.Serialize(cfg, Json));
        File.Move(tmp, ConfigPath, overwrite: true);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(ConfigPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    public void Add(string name, string baseUrl, string channel, string environmentName, string apiKey, string secret, bool makeDefault, bool fileStore = false)
    {
        ValidateName(name);
        ChannelClient.ValidateBase(baseUrl);
        ChannelClient.ValidateChannel(channel);
        if (environmentName is not ("test" or "live")) throw new CliException(ExitCodes.Usage, "BAD_ENVIRONMENT", "Environment is test or live.");
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(secret))
            throw new CliException(ExitCodes.Usage, "MISSING_CREDENTIALS", "The API key and the signing secret are both needed.");
        var profile = new Profile { BaseUrl = baseUrl.TrimEnd('/'), Channel = channel, Environment = environmentName, KeyHint = Hint(apiKey.Trim()), Store = fileStore ? "file" : null };
        StoreFor(profile).Write(name, new Credentials(apiKey.Trim(), secret.Trim()));
        var cfg = Load();
        if (cfg.Profiles.TryGetValue(name, out var old) && old.Store != profile.Store) StoreFor(old).Delete(name);
        cfg.Profiles[name] = profile;
        if (makeDefault || cfg.DefaultProfile is null) cfg.DefaultProfile = name;
        Save(cfg);
    }

    public bool Remove(string name)
    {
        var cfg = Load();
        var had = cfg.Profiles.Remove(name, out var old);
        var store = old is null ? secrets : StoreFor(old);
        store.Delete(name);
        store.Delete(ListenKey(name));
        Cursors.Delete(name);
        if (cfg.DefaultProfile == name) cfg.DefaultProfile = cfg.Profiles.Keys.FirstOrDefault();
        Save(cfg);
        return had;
    }

    public void SetMcpWrites(string name, bool on)
    {
        var cfg = Load();
        if (!cfg.Profiles.TryGetValue(name, out var p)) throw new CliException(ExitCodes.NotFound, "NO_PROFILE", $"There is no profile named {name}. See `ino profile list`.");
        p.McpWrites = on;
        Save(cfg);
    }

    /// <summary>
    /// For `ino mcp serve`: only a stored profile (--profile or the user's default), never environment-variable credentials or a project's
    /// .ino/config.json, so neither the agent's environment nor a cloned repository can choose what the server talks to.
    /// </summary>
    public (Resolved Resolved, Profile Profile) ResolveStoredOnly(string? name)
    {
        var cfg = Load();
        name ??= cfg.DefaultProfile ?? throw new CliException(ExitCodes.Usage, "NO_PROFILE", "No default profile. Create one with `ino profile add`, or pass --profile.");
        if (!cfg.Profiles.TryGetValue(name, out var prof)) throw new CliException(ExitCodes.NotFound, "NO_PROFILE", $"There is no profile named {name}. See `ino profile list`.");
        var cred = StoreFor(prof).Read(name) ?? throw new CliException(ExitCodes.Auth, "NO_CREDENTIALS", $"The key for profile {name} is not in the keychain.");
        return (new Resolved(new Connection(prof.BaseUrl, prof.Channel, cred.ApiKey, cred.Secret, prof.Environment == "test" ? "test" : "live"), "stored profile", name), prof);
    }

    /// <summary>The profile's existing listen secret, without creating one.</summary>
    public string? ExistingDeviceSecret(Resolved r)
    {
        if (r.ProfileName is null) return null;
        var prof = Load().Profiles.GetValueOrDefault(r.ProfileName);
        return prof is null ? null : StoreFor(prof).Read(ListenKey(r.ProfileName))?.Secret;
    }

    public void Use(string name)
    {
        var cfg = Load();
        if (!cfg.Profiles.ContainsKey(name)) throw new CliException(ExitCodes.NotFound, "NO_PROFILE", $"There is no profile named {name}. See `ino profile list`.");
        cfg.DefaultProfile = name;
        Save(cfg);
    }

    /// <summary>The key's first 11 characters, as the Developers console and the engine's logs show it (the key is not the secret).</summary>
    static string Hint(string key) => key.Length <= 11 ? key[..Math.Min(3, key.Length)] + "…" : key[..11] + "…";

    string? Env(string k) => environment.TryGetValue(k, out var v) && !string.IsNullOrEmpty(v) ? v : null;

    /// <summary>Picks the connection to use. Throws a usage error with the next step when nothing is set up.</summary>
    public Resolved Resolve(string? profileFlag)
    {
        if (Env("INO_BASE_URL") is { } envBase)
        {
            var key = Env("INO_API_KEY"); var secret = Env("INO_HMAC_SECRET"); var ch = Env("INO_CHANNEL");
            if (key is null || secret is null || ch is null)
                throw new CliException(ExitCodes.Usage, "INCOMPLETE_ENV", "INO_BASE_URL is set, so INO_CHANNEL, INO_API_KEY and INO_HMAC_SECRET are needed too.");
            var envName = Env("INO_ENVIRONMENT") ?? throw new CliException(ExitCodes.Usage, "INCOMPLETE_ENV", "Set INO_ENVIRONMENT to test or live as well.");
            if (envName is not ("test" or "live")) throw new CliException(ExitCodes.Usage, "BAD_ENVIRONMENT", "INO_ENVIRONMENT is test or live.");
            return new Resolved(new Connection(envBase.TrimEnd('/'), ch, key, secret, envName), "environment variables", null);
        }
        var cfg = Load();
        string? name = profileFlag, source = "--profile";
        if (name is null && Env("INO_PROFILE") is { } p) { name = p; source = "INO_PROFILE"; }
        if (name is null && ProjectProfile() is { } proj) { name = proj; source = ".ino/config.json"; }
        if (name is null) { name = cfg.DefaultProfile; source = "default profile"; }
        if (name is null)
            throw new CliException(ExitCodes.Usage, "NO_PROFILE", "No profile yet. Create one with `ino profile add <name> --base-url … --channel …` (the key and secret are read from the terminal).");
        if (!cfg.Profiles.TryGetValue(name, out var prof))
            throw new CliException(ExitCodes.NotFound, "NO_PROFILE", $"There is no profile named {name}. See `ino profile list`.");
        var cred = StoreFor(prof).Read(name) ?? throw new CliException(ExitCodes.Auth, "NO_CREDENTIALS",
            $"The key for profile {name} is not in the keychain. Add it again with `ino profile add {name} …`.");
        // INO_ENVIRONMENT can raise a profile to live, never lower it.
        var environmentName = prof.Environment == "live" || Env("INO_ENVIRONMENT") == "live" ? "live" : prof.Environment is "test" ? "test" : "live";
        return new Resolved(new Connection(prof.BaseUrl, prof.Channel, cred.ApiKey, cred.Secret, environmentName), source, name);
    }

    /// <summary>.ino/config.json in the working directory or a parent: <c>{"profile": "name"}</c>. It never holds a key or a secret.</summary>
    string? ProjectProfile()
    {
        for (var dir = new DirectoryInfo(workingDir ?? Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
        {
            var f = Path.Combine(dir.FullName, ".ino", "config.json");
            if (!File.Exists(f)) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(f));
                return doc.RootElement.TryGetProperty("profile", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
            }
            catch (JsonException) { throw new CliException(ExitCodes.Usage, "BAD_CONFIG", $"{f} is not valid JSON."); }
        }
        return null;
    }
}

/// <summary>Opt-in fallback (`profile add --store file`): credentials.json with mode 0600 next to the config. Weaker than a keychain; the CLI says so.</summary>
public sealed class FileSecretStore(string path) : ISecretStore
{
    Dictionary<string, Credentials> Load()
    {
        if (!File.Exists(path)) return new(StringComparer.Ordinal);
        try { return JsonSerializer.Deserialize<Dictionary<string, Credentials>>(File.ReadAllText(path)) ?? new(StringComparer.Ordinal); }
        catch (JsonException) { throw new CliException(ExitCodes.Usage, "BAD_CONFIG", $"{path} is not valid JSON."); }
    }

    void Save(Dictionary<string, Credentials> all)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        if (OperatingSystem.IsWindows()) File.WriteAllText(tmp, JsonSerializer.Serialize(all));
        else
        {
            using var fs = new FileStream(tmp, new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite });
            JsonSerializer.Serialize(fs, all);
        }
        File.Move(tmp, path, overwrite: true);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    public void Write(string profile, Credentials credentials) { var all = Load(); all[profile] = credentials; Save(all); }
    public Credentials? Read(string profile) => Load().GetValueOrDefault(profile);
    public void Delete(string profile) { var all = Load(); if (all.Remove(profile)) Save(all); }
}
