using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ino.Cli;

/// <summary>
/// `ino connector test --kind marketing` (MCP/CLI P1c.2, docs/MCP_CLI_P1C_LOG.md): sends Ino's signed activation contract v1 to a firm's own marketing
/// tool, the way the engine's CustomMarketingConnector does, and checks every answer. The tool is called at an https address or on this machine
/// only; the signing secret is used for signatures and never printed or sent. Test data is fictional and removed by the last steps.
/// </summary>
public sealed class MarketingCompliance(HttpClient http, Uri target, string secret, string? connectionId = null)
{
    public sealed record Check(string Code, string Title, bool Passed, string Evidence);

    /// <summary>Https anywhere, or http(s) on this machine (a tool under development). Never user info, never a fragment.</summary>
    public static Uri ValidateTarget(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https") || !string.IsNullOrEmpty(u.UserInfo) || !string.IsNullOrEmpty(u.Fragment))
            throw new CliException(ExitCodes.Usage, "BAD_URL", "--url must be your tool's address, e.g. http://localhost:8080/ino or https://tool.example.com/ino.");
        if (u.Scheme == "http" && !IsLocal(u))
            throw new CliException(ExitCodes.Usage, "NOT_HTTPS", "Use https for a tool that is not on this machine (Ino calls https only).");
        return u;
    }

    public static bool IsLocal(Uri u)
    {
        var host = u.IdnHost.TrimEnd('.').ToLowerInvariant();
        return host == "localhost" || host.EndsWith(".localhost", StringComparison.Ordinal) || (IPAddress.TryParse(host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));
    }

    public static string Proof(string secret, string challenge) =>
        Convert.ToBase64String(HMACSHA256.HashData(Key(secret), Encoding.UTF8.GetBytes(challenge)));

    /// <summary>As the engine (WebhookSigner.StandardKey): base64 after "whsec_", or the UTF-8 bytes when it is not valid base64.</summary>
    static byte[] Key(string secret)
    {
        var raw = secret.StartsWith("whsec_", StringComparison.Ordinal) ? secret[6..] : secret;
        var buffer = new byte[raw.Length];
        return Convert.TryFromBase64String(raw, buffer, out var written) ? buffer[..written] : Encoding.UTF8.GetBytes(raw);
    }

    public async Task<List<Check>> RunAsync(CancellationToken ct)
    {
        var checks = new List<Check>();
        void Add(string code, string title, bool ok, string evidence) => checks.Add(new(code, title, ok, evidence));
        void Skip(string code, string title) => checks.Add(new(code, title, false, "skipped: no profile to test with"));
        var run = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();

        // 1-3: the tool refuses what Ino would never send.
        var (s1, _) = await SendAsync("ping", new { challenge = "unsigned" }, sign: false, ct: ct);
        Add("M1", "Refuses an unsigned request", s1 is 401 or 403, $"HTTP {Show(s1)}");
        var other = "whsec_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var (s2, _) = await SendAsync("ping", new { challenge = "other-secret" }, key: other, ct: ct);
        Add("M2", "Refuses a request signed with another secret", s2 is 401 or 403, $"HTTP {Show(s2)}");
        var (s3a, _) = await SendAsync("ping", new { challenge = "old" }, at: DateTimeOffset.UtcNow.AddMinutes(-10), ct: ct);
        var (s3b, _) = await SendAsync("ping", new { challenge = "future" }, at: DateTimeOffset.UtcNow.AddMinutes(10), ct: ct);
        Add("M3", "Refuses a request signed 10 minutes ago or ahead", s3a is 401 or 403 && s3b is 401 or 403, $"HTTP {Show(s3a)}, ahead {Show(s3b)}");

        // 4: the verification Ino does before sending anything.
        var challenge = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var (s4, b4) = await SendAsync("ping", new { challenge }, ct: ct);
        var proofOk = Str(b4, "challenge") == challenge && Str(b4, "proof") is { } p && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(p), Encoding.UTF8.GetBytes(Proof(secret, challenge)));
        Add("M4", "Answers the ping with the challenge and the proof of the secret", Ok(s4) && proofOk,
            Ok(s4) ? (proofOk ? "proof matches" : "challenge or proof does not match (proof = base64(HMAC-SHA256(key, challenge)))") : $"HTTP {Show(s4)}");

        // 5-6: profiles; every successful answer carries the profile id (the engine refuses one without it).
        var externalId = $"ino-compliance-{run}";
        var email = $"avery.quinlan+ino-{run}@example.com";
        var contact = new { email, phone = (string?)null, firstName = "Avery", lastName = "Quinlan" };
        var (s5, b5) = await SendAsync("profile.upsert", new { externalId, profileId = (string?)null, contact, properties = new Dictionary<string, object?> { ["Ino Points Balance"] = 120, ["Ino Tier"] = "Gold" }, unset = Array.Empty<string>() }, ct: ct);
        var profileId = Id(b5);
        var adoption = Adoption(b5);
        Add("M5", "Creates a profile and answers its profileId", Ok(s5) && profileId is not null && adoption.Valid,
            !Ok(s5) ? $"HTTP {Show(s5)}{(s5 is 404 or 405 or 410 ? " (Ino reads this as: the address takes no requests)" : "")}" : profileId is null ? "no profileId (1-200 characters) in the answer" : !adoption.Valid ? adoption.Problem! : $"profileId {Trim(profileId)}");
        if (profileId is null)
        {
            foreach (var (c, t) in new[] { ("M6", "Updates the profile by its id"), ("M7", "Takes events once per eventId"), ("M8", "Reads the subscription"), ("M9", "Unsubscribes e-mail"),
                         ("M10", "Deletes the profile"), ("M11", "Treats a deleted profile as gone"), ("M12", "Says when it links to a profile it already had") }) Skip(c, t);
            await UnknownTypeAsync(Add, ct);
            return checks;
        }
        string? second = null;
        try
        {
            var (s6, b6) = await SendAsync("profile.upsert", new { externalId, profileId, contact, properties = new Dictionary<string, object?> { ["Ino Points Balance"] = 140 }, unset = new[] { "Ino Tier" } }, ct: ct);
            Add("M6", "Updates the profile by its id and answers the profileId", Ok(s6) && Id(b6) is not null, Ok(s6) ? (Id(b6) is { } id6 ? $"profileId {Trim(id6)}" : "no profileId in the answer") : $"HTTP {Show(s6)}");

            // 7: two events of one Ino event (same outboxId, e.g. points redeemed and a purchase) and a repeat; the key is eventId (as the engine computes it).
            var outbox = Guid.NewGuid();
            object Evt(string name) => new { profileId, name, eventId = EventId(outbox, name), outboxId = outbox, time = DateTimeOffset.UtcNow, properties = new { points = 20, transactionId = $"tx-{run}" } };
            var (s7a, _) = await SendAsync("event.track", Evt("points.redeemed"), ct: ct);
            var (s7b, _) = await SendAsync("event.track", Evt("purchase.completed"), ct: ct);
            var (s7c, _) = await SendAsync("event.track", Evt("points.redeemed"), ct: ct);
            Add("M7", "Takes two events of one Ino event, and a repeated eventId without failing", Ok(s7a) && Ok(s7b) && Ok(s7c), $"HTTP {Show(s7a)}, second event {Show(s7b)}, repeat {Show(s7c)}");

            // 8: Ino just sent an e-mail, so the tool must say it can reach it; "not reachable" would turn every unsubscribe into a no-op.
            var (s8, b8) = await SendAsync("profile.subscription", new { profileId }, ct: ct);
            var mail = Channel(b8, "email");
            var shapeOk = Ok(s8) && mail is { Reachable: true } && (mail.Address is null || string.Equals(mail.Address, email, StringComparison.OrdinalIgnoreCase)) && Channel(b8, "sms") is not null;
            Add("M8", "Reads the subscription: e-mail reachable, consent per channel", shapeOk,
                !Ok(s8) ? $"HTTP {Show(s8)}" : mail is null || Channel(b8, "sms") is null ? "email and sms need reachable (true/false) and consent (SUBSCRIBED, UNSUBSCRIBED, NEVER_SUBSCRIBED)"
                : !mail.Reachable ? "e-mail reads as not reachable although Ino just sent one" : mail.Address is not null && !shapeOk ? "the e-mail address read back differs from the one sent" : $"email {mail.Consent}, sms {Channel(b8, "sms")!.Consent}");

            // 9: unsubscribe; a tool may apply it a moment later, so the read is retried for up to 10 seconds.
            var (s9, _) = await SendAsync("profile.unsubscribe", new { profileId, channel = "EMAIL", contact = email }, ct: ct);
            var consent9 = await ReadUntilAsync(profileId, b => Channel(b, "email")?.Consent == "UNSUBSCRIBED", b => Channel(b, "email")?.Consent ?? "nothing", ct);
            Add("M9", "Unsubscribes e-mail and then reads it as UNSUBSCRIBED", Ok(s9) && consent9 == "UNSUBSCRIBED", Ok(s9) ? $"read back as {consent9}" : $"HTTP {Show(s9)}");

            // 12: a second Ino member with the same e-mail; a tool that links it to the first profile must say so and share the address.
            var (s12, b12) = await SendAsync("profile.upsert", new { externalId = externalId + "-b", profileId = (string?)null, contact = contact with { firstName = "Jordan", lastName = "Ellis" }, properties = new Dictionary<string, object?>(), unset = Array.Empty<string>() }, ct: ct);
            second = Id(b12);
            var adopt12 = Adoption(b12);
            var m12 = Ok(s12) && second is not null && adopt12.Valid;
            var evidence12 = !Ok(s12) ? $"HTTP {Show(s12)}" : second is null ? "no profileId in the answer" : !adopt12.Valid ? adopt12.Problem! : adopt12.Adopted ? $"linked via {adopt12.Via}" : "a profile of its own";
            if (m12 && adopt12.Adopted)
            {
                var (_, b12r) = await SendAsync("profile.subscription", new { profileId = second }, ct: ct);
                var via = adopt12.Via == "SMS" ? "sms" : "email";
                if (Channel(b12r, via) is not { Reachable: true, Address: not null }) { m12 = false; evidence12 = $"linked via {adopt12.Via} but the subscription read gives no {via} address (required for a linked profile)"; }
            }
            Add("M12", "Says when it links to a profile it already had (adopted + adoptedVia + address)", m12, evidence12);

            // 10-11: erasure; a profile already gone counts as deleted, and every later call about it reads as gone (Ino then rebuilds it).
            var (s10a, _) = await SendAsync("profile.delete", new { profileId }, ct: ct);
            var gone = await ReadUntilAsync(profileId, b => b is null, _ => "still there", ct, goneIsDone: true);
            var (s10c, _) = await SendAsync("profile.delete", new { profileId }, ct: ct);
            Add("M10", "Deletes the profile; a second delete is not an error", Ok(s10a) && (Ok(s10c) || s10c is 404 or 410), $"delete HTTP {Show(s10a)}, again {Show(s10c)}");
            var (s11u, b11u) = await SendAsync("profile.upsert", new { externalId, profileId, contact, properties = new Dictionary<string, object?>(), unset = Array.Empty<string>() }, ct: ct);
            var (s11e, _) = await SendAsync("event.track", Evt("points.earned"), ct: ct);
            var upsertOk = s11u is 404 or 410 || (Ok(s11u) && Id(b11u) is { } fresh && fresh != profileId);
            var m11 = gone == "gone" && upsertOk && s11e is 404 or 410;
            Add("M11", "Treats the deleted profile as gone (404/410) for reads, updates and events", m11,
                $"read {gone}, update HTTP {Show(s11u)}, event HTTP {Show(s11e)} (anything but 404/410 for a gone profile stops Ino from rebuilding it)");
            if (Ok(s11u) && Id(b11u) is { } again2 && again2 != profileId) await SendAsync("profile.delete", new { profileId = again2 }, ct: ct);
            profileId = null;   // deleted
        }
        finally
        {
            // The fictional profiles never stay in the firm's tool, whatever happened above.
            if (profileId is not null) await SendAsync("profile.delete", new { profileId }, ct: CancellationToken.None);
            if (second is not null) await SendAsync("profile.delete", new { profileId = second }, ct: CancellationToken.None);
        }
        await UnknownTypeAsync(Add, ct);
        return checks;
    }

    async Task UnknownTypeAsync(Action<string, string, bool, string> add, CancellationToken ct)
    {
        // An unknown request or a future contract version is refused, never taken as done and never a server error.
        var (s1, _) = await SendAsync("ino.unknown_type", new { x = 1 }, ct: ct);
        var (s2, _) = await SendAsync("ping", new { challenge = "v2" }, contract: 2, ct: ct);
        add("M13", "Refuses an unknown type and a future contract version with 4xx", s1 is >= 400 and < 500 and not (401 or 403) && s2 is >= 400 and < 500 and not (401 or 403),
            $"unknown type HTTP {Show(s1)}, contract 2 HTTP {Show(s2)}");
    }

    /// <summary>Reads the subscription until <paramref name="done"/> (or the profile is gone when <paramref name="goneIsDone"/>), for up to 10 seconds.</summary>
    async Task<string> ReadUntilAsync(string profileId, Func<JsonElement?, bool> done, Func<JsonElement?, string> describe, CancellationToken ct, bool goneIsDone = false)
    {
        var last = "no answer";
        for (var i = 0; i < 6; i++)
        {
            if (i > 0) await Task.Delay(TimeSpan.FromSeconds(2), ct);
            var (status, body) = await SendAsync("profile.subscription", new { profileId }, ct: ct);
            var isGone = status is 404 or 410 || (Ok(status) && body is { ValueKind: JsonValueKind.Object } b && b.TryGetProperty("gone", out var g) && g.ValueKind == JsonValueKind.True);
            if (goneIsDone && isGone) return "gone";
            if (!goneIsDone && Ok(status) && done(body)) return describe(body);
            last = Ok(status) ? describe(body) : $"HTTP {Show(status)}";
        }
        return last;
    }

    /// <summary>The engine's event key: the first 32 hex of sha256("{outboxId}|{name}") (CustomMarketingConnector.EventId).</summary>
    public static string EventId(Guid outboxId, string name) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{outboxId:D}|{name}")))[..32].ToLowerInvariant();

    sealed record AdoptionState(bool Valid, bool Adopted, string? Via, string? Problem);

    static AdoptionState Adoption(JsonElement? body)
    {
        if (body is not { ValueKind: JsonValueKind.Object } b || !b.TryGetProperty("adopted", out var a)) return new(true, false, null, null);
        if (a.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return new(false, false, null, "adopted must be true or false");
        if (a.ValueKind == JsonValueKind.False) return new(true, false, null, null);
        var via = Str(body, "adoptedVia");
        return via is "EMAIL" or "SMS" or "BOTH" ? new(true, true, via, null) : new(false, true, null, "adopted without adoptedVia (EMAIL, SMS or BOTH)");
    }

    static string? Id(JsonElement? body) => Str(body, "profileId") is { Length: > 0 and <= 200 } id ? id : null;

    async Task<(int Status, JsonElement? Body)> SendAsync(string type, object data, bool sign = true, string? key = null, DateTimeOffset? at = null, CancellationToken ct = default,
        int contract = 1)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new { type, contract, connectionId = connectionId ?? "ino-compliance", data }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var request = new HttpRequestMessage(HttpMethod.Post, target) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        request.Headers.TryAddWithoutValidation("Ino-Contract", "1");
        if (sign)
        {
            var id = "msg_" + Guid.NewGuid().ToString("N");
            var ts = (at ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
            request.Headers.TryAddWithoutValidation("webhook-id", id);
            request.Headers.TryAddWithoutValidation("webhook-timestamp", ts.ToString(System.Globalization.CultureInfo.InvariantCulture));
            request.Headers.TryAddWithoutValidation("webhook-signature", StandardWebhooks.Sign(key ?? secret, id, ts, body));
        }
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            var status = (int)response.StatusCode;
            // As the engine: at most 64 KB of answer is read.
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[64 * 1024 + 1];
            var read = 0;
            while (read < buffer.Length && await stream.ReadAsync(buffer.AsMemory(read), ct) is var n and > 0) read += n;
            var bytes = buffer.AsSpan(0, read).ToArray();
            if (bytes.Length is 0 or > 64 * 1024) return (status, null);
            try { using var doc = JsonDocument.Parse(bytes); return (status, doc.RootElement.Clone()); }
            catch (JsonException) { return (status, null); }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested) { return (0, null); }
    }

    sealed record ChannelState(bool Reachable, string Consent, string? Address);

    static ChannelState? Channel(JsonElement? body, string name)
    {
        if (body is not { ValueKind: JsonValueKind.Object } b || !b.TryGetProperty(name, out var ch) || ch.ValueKind != JsonValueKind.Object) return null;
        if (!ch.TryGetProperty("reachable", out var r) || r.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return null;
        var consent = ch.TryGetProperty("consent", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
        var address = ch.TryGetProperty("address", out var ad) && ad.ValueKind == JsonValueKind.String ? ad.GetString() : null;
        return consent is "SUBSCRIBED" or "UNSUBSCRIBED" or "NEVER_SUBSCRIBED" ? new ChannelState(r.ValueKind == JsonValueKind.True, consent, address) : null;
    }

    static string? Str(JsonElement? body, string name) =>
        body is { ValueKind: JsonValueKind.Object } b && b.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static bool Ok(int status) => status is >= 200 and < 300;
    static string Show(int status) => status == 0 ? "no answer" : status.ToString(System.Globalization.CultureInfo.InvariantCulture);
    static string Trim(string? s) => s is null ? "" : s.Length > 40 ? s[..40] + "…" : s;

    public static JsonObject ToJson(IEnumerable<Check> checks) => new()
    {
        ["passed"] = checks.All(c => c.Passed),
        ["checks"] = new JsonArray(checks.Select(c => (JsonNode)new JsonObject { ["code"] = c.Code, ["title"] = c.Title, ["passed"] = c.Passed, ["evidence"] = c.Evidence }).ToArray()),
    };
}
