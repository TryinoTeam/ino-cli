using System.Net;
using System.Text;
using Ino.Cli;

namespace Ino.Cli.Tests;

sealed class MemorySecrets : ISecretStore
{
    public readonly Dictionary<string, Credentials> Items = new();
    public void Write(string profile, Credentials credentials) => Items[profile] = credentials;
    public Credentials? Read(string profile) => Items.GetValueOrDefault(profile);
    public void Delete(string profile) => Items.Remove(profile);
}

/// <summary>Records every request (with its body bytes) and answers with a fixed response.</summary>
/// <summary>Answers by a function of the request; records every request with its body.</summary>
sealed class RouteHandler(Func<HttpRequestMessage, byte[], HttpResponseMessage> route) : HttpMessageHandler
{
    public readonly List<(HttpRequestMessage Request, byte[] Body)> Seen = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var bytes = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(ct);
        Seen.Add((request, bytes));
        return route(request, bytes);
    }

    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}

sealed class FakeHandler(HttpStatusCode status = HttpStatusCode.OK, string body = "{\"ResponseCode\":\"SUCCESS\"}", Action<HttpResponseMessage>? shape = null) : HttpMessageHandler
{
    public readonly List<(HttpRequestMessage Request, byte[] Body)> Seen = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var bytes = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(ct);
        Seen.Add((request, bytes));
        var res = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        res.Headers.Date = DateTimeOffset.UtcNow;
        shape?.Invoke(res);
        return res;
    }
}

/// <summary>A CLI run in-process: temp config folder, in-memory keychain, fake HTTP, captured stdout/stderr.</summary>
sealed class Harness : IDisposable
{
    public readonly string Dir = Directory.CreateTempSubdirectory("ino-cli-test").FullName;
    public readonly MemorySecrets Secrets = new();
    public readonly Dictionary<string, string?> Env = new();
    public FakeHandler Http = new();
    /// <summary>When set, the engine side answers through this instead of <see cref="Http"/>.</summary>
    public HttpMessageHandler? Api;
    public HttpMessageHandler? Local;
    /// <summary>Fake browser and Ino Auth for `ino login`.</summary>
    public Func<Uri, Task>? Browser;
    public HttpMessageHandler? Auth;
    /// <summary>The store catalog and Tryino's intake (`ino hub`).</summary>
    public HttpMessageHandler? Hub;
    /// <summary>git and other captured commands (`ino hub submit`).</summary>
    public Func<string, string[], string, (int Code, string Out)>? Capture;
    public Func<TimeSpan, CancellationToken, Task> Delay = (_, _) => Task.CompletedTask;
    public CancellationToken Token;
    public string ConfigPath => Path.Combine(Dir, "ino", "config.json");
    public ProfileStore Store => new(Secrets, ConfigPath, Env, Dir);

    public async Task<(int Code, string Out, string Err)> RunWithInput(string stdin, params string[] args)
    {
        var o = new StringWriter(); var e = new StringWriter();
        var ctx = new CliContext
        {
            Out = o, Err = e, In = new StringReader(stdin), InputIsTerminal = false, Env = Env, Profiles = Store,
            ClientFactory = (c, l) => new ChannelClient(c, Api ?? Http) { Lock = l },
            LocalHandler = Local, Delay = Delay, HubHandler = Hub, Capture = Capture, OpenBrowser = Browser, AuthHandler = Auth,
        };
        var code = await Commands.RunAsync(args, ctx, Token);
        return (code, o.ToString(), e.ToString());
    }

    public Task<(int Code, string Out, string Err)> Run(params string[] args) => RunWithInput("", args);

    public Task AddProfile(string name = "dev", string env = "test", string key = "ik_tabc1234_9f8e7d6c5b4a", string secret = "s3cr3t-value-123") =>
        RunWithInput($"{key}\n{secret}\n", "profile", "add", name, "--base-url", "https://engine.example.test/t/tabc1234", "--channel", "MODERN_ECOM", "--environment", env);

    public void Dispose() { try { Directory.Delete(Dir, true); } catch (IOException) { } }
}
