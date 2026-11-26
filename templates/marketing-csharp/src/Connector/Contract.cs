using System.Text.Json.Nodes;

namespace Connector;

/// <summary>
/// Ino Loyalty activation contract v1: one signed POST, `type` says what to do. Status codes carry meaning for Ino:
///   2xx done · 404 the profile is not here (Ino rebuilds it, or a deletion counts as done) · 4xx refused for this member
///   401/403 refused for the whole connection · 408/429/5xx try again later. Never redirect.
/// </summary>
public sealed class Contract(Tool tool, Signature signature)
{
    public sealed record Reply(int Status, JsonObject Body);

    static Reply Error(int status, string message) => new(status, new JsonObject { ["error"] = message });
    static readonly Reply NotFound = Error(404, "profile not found");

    public async Task<Reply> HandleAsync(JsonObject? request)
    {
        if (request?["contract"] is not JsonValue v || !v.TryGetValue<int>(out var contract) || contract != 1)
            return Error(400, "unsupported contract version");   // a future version is refused, never half-applied
        if (request["data"] is not JsonObject data) return Error(400, "missing data");
        switch (request["type"]?.GetValue<string>())
        {
            case "ping":
                if (data["challenge"] is not JsonValue c || !c.TryGetValue<string>(out var challenge)) return Error(400, "missing challenge");
                return new(200, new JsonObject { ["challenge"] = challenge, ["proof"] = signature.Proof(challenge), ["capabilities"] = new JsonArray() });
            case "profile.upsert":
                return await tool.UpsertProfileAsync(data) is { } r ? new(200, r) : NotFound;
            case "event.track":
                return await tool.TrackEventAsync(data) ? new(202, []) : NotFound;
            case "profile.subscription":
                return await tool.ReadSubscriptionAsync(data) is { } s ? new(200, s) : NotFound;
            case "profile.unsubscribe":
                if (data["channel"]?.GetValue<string>() is not ("EMAIL" or "SMS")) return Error(400, "unknown channel");
                return await tool.UnsubscribeAsync(data) ? new(202, []) : NotFound;
            case "profile.delete":
                return await tool.DeleteProfileAsync(data) ? new(202, []) : NotFound;
            default:
                return Error(400, "unknown type");
        }
    }
}
