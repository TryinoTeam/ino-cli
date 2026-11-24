using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ino.Cli;

/// <summary>One event as `listen` shows it.</summary>
public sealed record ForwardedEvent(string Id, string Type, string? DataVersion, DateTimeOffset? Time, int? LocalStatus, bool Replayed = false);

/// <summary>
/// `ino listen`: pulls the event feed (GET /api/v1/{channel}/events?after=) and forwards each event to a local URL with the headers of a real
/// delivery. Nothing new is needed on the server. The feed has one cursor per page, so delivery is at least once: the state keeps the page's
/// starting cursor plus the ids already forwarded from that page, a failure stops the loop there, and --resume skips what was already sent.
/// </summary>
public sealed class Listener(ChannelClient client, Uri target, string deviceSecret, CursorStore cursors, string profileKey, HttpMessageHandler? localHandler = null,
    Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan MinRepoll = TimeSpan.FromMilliseconds(250);
    public IReadOnlyCollection<string>? Types { get; init; }
    public bool SkipFailed { get; init; }
    public string Scheme { get; init; } = "both";
    public Action<string>? Notice { get; init; }
    public static readonly TimeSpan[] Retries = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)];

    readonly HttpClient local = new(localHandler ?? StandardWebhooks.LoopbackOnlyHandler(), disposeHandler: localHandler is null) { Timeout = TimeSpan.FromSeconds(10) };
    readonly Func<TimeSpan, CancellationToken, Task> wait = delay ?? Task.Delay;

    string Query(string? after) =>
        "events?limit=100" + (after is null ? "" : "&after=" + Uri.EscapeDataString(after))
        + (Types is { Count: > 0 } ? "&types=" + Uri.EscapeDataString(string.Join(',', Types)) : "");

    /// <summary>Feed page: (events, cursor, hasMore) from the engine's envelope (Data).</summary>
    public static (List<JsonObject> Events, string? Cursor, bool HasMore) Page(ChannelResponse res)
    {
        var data = (res.Body as JsonObject)?["Data"] as JsonObject ?? res.Body as JsonObject;
        var events = (data?["events"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        return (events, data?["cursor"]?.GetValue<string>(), data?["hasMore"]?.GetValue<bool>() == true);
    }

    /// <summary>The position after each event (engine M2), or null from an older engine / when the counts do not match.</summary>
    public static List<string>? EventCursors(ChannelResponse res, int count)
    {
        var data = (res.Body as JsonObject)?["Data"] as JsonObject ?? res.Body as JsonObject;
        var list = (data?["cursors"] as JsonArray)?.Select(c => c?.GetValue<string>()).ToList();
        return list is not null && list.Count == count && list.All(c => !string.IsNullOrEmpty(c)) ? list.Select(c => c!).ToList() : null;
    }

    /// <summary>
    /// Waits out 429/503 (Retry-After), and network errors (2, 4 … 60 s), without moving the cursor. Returns the response once it is final.
    /// </summary>
    async Task<ChannelResponse> GetFeedAsync(string? after, CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(2);
        while (true)
        {
            ChannelResponse res;
            try { res = await client.GetAsync(Query(after), ct); }
            catch (CliException e) when (e.ExitCode == ExitCodes.Network)
            {
                Notice?.Invoke($"{e.Message} Trying again in {backoff.TotalSeconds:0} s.");
                await wait(backoff, ct);
                backoff = TimeSpan.FromSeconds(Math.Min(60, backoff.TotalSeconds * 2));
                continue;
            }
            if (res.Status is 429 or 503)
            {
                var pause = res.RetryAfter is { } ra && ra > TimeSpan.Zero ? ra : TimeSpan.FromSeconds(res.Status == 429 ? 30 : 15);
                Notice?.Invoke($"The engine asked to slow down (HTTP {res.Status}); waiting {pause.TotalSeconds:0} s.");
                await wait(pause, ct);
                continue;
            }
            return res;
        }
    }

    static bool CursorRejected(ChannelResponse res) =>
        res.Status == 410 || (res.Status == 400 && ChannelClient.ResponseCodeOf(res.Body) == "PARAMETER_ERROR"
                              && ((res.Body as JsonObject)?["ResponseMessage"]?.ToString() ?? "").Contains("cursor", StringComparison.OrdinalIgnoreCase));

    static void ThrowFor(ChannelResponse res)
    {
        var code = ChannelClient.ExitCodeOf(res);
        if (code != ExitCodes.Ok)
            throw new CliException(code, "FEED_FAILED", $"The event feed answered HTTP {res.Status} {ChannelClient.ResponseCodeOf(res.Body)}: {(res.Body as JsonObject)?["ResponseMessage"]}",
                ChannelClient.HintFor(res));
    }

    public async Task RunAsync(bool resume, Action<ForwardedEvent> onEvent, Action<string> onReady, CancellationToken ct)
    {
        var state = resume ? cursors.Read(profileKey) : null;
        if (resume && state is null && cursors.Exists(profileKey))
            throw new CliException(ExitCodes.Usage, "BAD_STATE", "The saved position cannot be read. Run listen without --resume to start from now (events since the last run are then not sent).");
        if (resume && state is null)
            throw new CliException(ExitCodes.Usage, "NO_STATE", "There is no saved position for this profile. Run listen without --resume to start from now.");
        if (state is null)
        {
            var head = await GetFeedAsync(null, ct);
            ThrowFor(head);
            state = new ListenState(Page(head).Cursor ?? throw new CliException(ExitCodes.Business, "FEED_FAILED", "The event feed returned no cursor."), []);
            cursors.Write(profileKey, state);
            onReady("from now");
        }
        else onReady($"from the saved position{(state.Done.Count > 0 ? $" ({state.Done.Count} event(s) of that page already sent are skipped)" : "")}");

        while (!ct.IsCancellationRequested)
        {
            var res = await GetFeedAsync(state.Cursor, ct);
            if (CursorRejected(res))
            {
                cursors.Delete(profileKey);
                throw new CliException(ExitCodes.Business, "CURSOR_EXPIRED",
                    "The saved position is no longer valid (older than the 30-day retention, or damaged). Reload your cache, then run listen again without --resume.");
            }
            ThrowFor(res);
            var (events, next, hasMore) = Page(res);
            var perEvent = EventCursors(res, events.Count);
            for (var i = 0; i < events.Count; i++)
            {
                var e = events[i];
                var id = e["id"]?.ToString() ?? "";
                if (state.Done.Contains(id)) continue;
                var status = await ForwardAsync(e, synthetic: false, ct);
                onEvent(Describe(e, status));
                if (status is null or < 200 or >= 300 && !SkipFailed)
                {
                    cursors.Write(profileKey, state);
                    throw new CliException(ExitCodes.Business, "FORWARD_FAILED",
                        $"{target} did not accept event {id} ({(status is null ? "no answer" : "HTTP " + status)}) after {Retries.Length + 1} tries. Stopped; run again with --resume to continue from this event (earlier ones are not sent again).");
                }
                // With per-event positions the state moves exactly past this event; otherwise it remembers the ids sent from this page.
                state = perEvent is not null ? new ListenState(perEvent[i], []) : state with { Done = [.. state.Done, id] };
                cursors.Write(profileKey, state);
            }
            if (next is not null) { state = new ListenState(next, []); cursors.Write(profileKey, state); }
            await wait(hasMore ? MinRepoll : PollInterval, ct);
        }
    }

    static ForwardedEvent Describe(JsonObject e, int? status) => new(
        e["id"]?.ToString() ?? "?", e["type"]?.ToString() ?? "?", e["dataversion"]?.ToString(),
        DateTimeOffset.TryParse(e["time"]?.ToString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var t) ? t : null, status);

    /// <summary>POSTs one event with a real delivery's headers; retries 1, 2, 4 s with the same message id. Returns the last HTTP status, or null.</summary>
    public async Task<int?> ForwardAsync(JsonObject envelope, bool synthetic, CancellationToken ct)
    {
        var body = Encoding.UTF8.GetBytes(envelope.ToJsonString());
        var messageId = StandardWebhooks.DeliveryId(profileKey, envelope["id"]?.ToString() is { Length: > 0 } i ? i : Guid.NewGuid().ToString());
        int? last = null;
        for (var attempt = 0; attempt <= Retries.Length; attempt++)
        {
            if (attempt > 0) await wait(Retries[attempt - 1], ct);
            var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using var req = new HttpRequestMessage(HttpMethod.Post, target) { Content = new ByteArrayContent(body) };
            req.Content.Headers.ContentType = new("application/json") { CharSet = "utf-8" };
            foreach (var (k, v) in StandardWebhooks.Headers(Scheme, deviceSecret, messageId, ts, body)) req.Headers.TryAddWithoutValidation(k, v);
            if (synthetic) req.Headers.TryAddWithoutValidation("X-Ino-Cli-Synthetic", "true");
            req.Headers.TryAddWithoutValidation("User-Agent", StandardWebhooks.EngineUserAgent);
            try
            {
                using var res = await local.SendAsync(req, ct);
                last = (int)res.StatusCode;
                if (last is >= 200 and < 300) return last;
            }
            catch (HttpRequestException) { last = null; }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested) { last = null; }
        }
        return last;
    }
}

public sealed record ListenState(string Cursor, List<string> Done);

/// <summary>Feed positions per profile, next to the config (state/cursor-&lt;profile&gt;): the page's cursor and the ids already sent from it.</summary>
public sealed class CursorStore(string dir)
{
    string PathFor(string key) => Path.Combine(dir, $"cursor-{key}");

    public ListenState? Read(string key)
    {
        try
        {
            var p = PathFor(key);
            var s = File.Exists(p) ? JsonSerializer.Deserialize<ListenState>(File.ReadAllText(p)) : null;
            return s is { Cursor.Length: > 0, Done: not null } ? s : null;
        }
        catch (Exception e) when (e is IOException or JsonException) { return null; }
    }

    public void Write(string key, ListenState state)
    {
        Directory.CreateDirectory(dir);
        var tmp = PathFor(key) + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(state));
        File.Move(tmp, PathFor(key), overwrite: true);
    }

    public bool Exists(string key) => File.Exists(PathFor(key));
    public void Delete(string key) { try { File.Delete(PathFor(key)); } catch (IOException) { } }
}
