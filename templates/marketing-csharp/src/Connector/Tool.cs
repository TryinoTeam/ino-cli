using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace Connector;

/// <summary>
/// YOUR MARKETING TOOL. This class is a stand-in that keeps profiles in memory, so the project runs and passes `ino connector test --kind marketing`
/// out of the box. Replace each method with calls to {{platform}}'s API; keep the return shapes. Everything else (Signature, Contract) stays as is.
/// </summary>
public class Tool
{
    sealed class Profile
    {
        public required string Id { get; init; }
        public required string ExternalId { get; init; }
        public string? Email, Phone, FirstName, LastName;
        public string EmailConsent = "NEVER_SUBSCRIBED", SmsConsent = "NEVER_SUBSCRIBED";
        public JsonObject Properties { get; } = [];
    }

    readonly ConcurrentDictionary<string, Profile> _profiles = new();
    readonly ConcurrentDictionary<string, bool> _seenEvents = new();

    /// <summary>
    /// Creates or updates the member's profile. `profileId` is the id this tool gave before (null the first time).
    /// Return { profileId } for a profile this tool made or already had for this Ino member (same externalId).
    /// If you link the member to a profile that existed before for another reason (same e-mail in a shop), return
    /// { profileId, adopted: true, adoptedVia: "EMAIL" | "SMS" | "BOTH" }: the channel whose value matched. Ino then only unsubscribes that channel.
    /// Return null when `profileId` is given but no longer exists here (Ino asks again without it).
    /// </summary>
    public virtual Task<JsonObject?> UpsertProfileAsync(JsonObject data)
    {
        var externalId = data["externalId"]?.GetValue<string>() ?? "";
        var profileId = data["profileId"]?.GetValue<string>();
        Profile? p;
        if (profileId is not null) { if (!_profiles.TryGetValue(profileId, out p)) return Task.FromResult<JsonObject?>(null); }
        else p = _profiles.Values.FirstOrDefault(x => x.ExternalId == externalId);
        if (p is null) { p = new Profile { Id = "p_" + Guid.NewGuid().ToString("N"), ExternalId = externalId }; _profiles[p.Id] = p; }
        // Ino sends only the contact values the member consented to; a value missing now is removed here.
        var contact = data["contact"] as JsonObject;
        p.Email = contact?["email"]?.GetValue<string>();
        p.Phone = contact?["phone"]?.GetValue<string>();
        p.FirstName = contact?["firstName"]?.GetValue<string>();
        p.LastName = contact?["lastName"]?.GetValue<string>();
        if (data["properties"] is JsonObject props) foreach (var (k, v) in props) p.Properties[k] = v?.DeepClone();
        if (data["unset"] is JsonArray unset) foreach (var k in unset) p.Properties.Remove(k!.GetValue<string>());
        return Task.FromResult<JsonObject?>(new JsonObject { ["profileId"] = p.Id });
    }

    /// <summary>Records one event on the profile. Return false when the profile does not exist here. Ino may send the same eventId again: apply it once.</summary>
    public virtual Task<bool> TrackEventAsync(JsonObject data)
    {
        if (!_profiles.ContainsKey(data["profileId"]?.GetValue<string>() ?? "")) return Task.FromResult(false);
        if (!_seenEvents.TryAdd(data["eventId"]?.GetValue<string>() ?? "", true)) return Task.FromResult(true);
        // e.g. POST /events to your tool with { profileId, name, time, properties }
        return Task.FromResult(true);
    }

    /// <summary>
    /// The profile's marketing subscription as THIS tool holds it. Return null when the profile does not exist.
    /// For each channel: reachable (the tool has an address for it), consent (SUBSCRIBED | UNSUBSCRIBED | NEVER_SUBSCRIBED) and, if you can share it,
    /// the address. An adopted profile must return the address.
    /// </summary>
    public virtual Task<JsonObject?> ReadSubscriptionAsync(JsonObject data)
    {
        if (!_profiles.TryGetValue(data["profileId"]?.GetValue<string>() ?? "", out var p)) return Task.FromResult<JsonObject?>(null);
        static JsonObject Channel(string? address, string consent)
        {
            var c = new JsonObject { ["reachable"] = address is not null, ["consent"] = consent };
            if (address is not null) c["address"] = address;
            return c;
        }
        return Task.FromResult<JsonObject?>(new JsonObject { ["gone"] = false, ["email"] = Channel(p.Email, p.EmailConsent), ["sms"] = Channel(p.Phone, p.SmsConsent) });
    }

    /// <summary>Stops marketing on one channel ("EMAIL" or "SMS"). Return false when the profile does not exist.</summary>
    public virtual Task<bool> UnsubscribeAsync(JsonObject data)
    {
        if (!_profiles.TryGetValue(data["profileId"]?.GetValue<string>() ?? "", out var p)) return Task.FromResult(false);
        if (data["channel"]?.GetValue<string>() == "EMAIL") p.EmailConsent = "UNSUBSCRIBED"; else p.SmsConsent = "UNSUBSCRIBED";
        return Task.FromResult(true);
    }

    /// <summary>KVKK/GDPR erasure: deletes the profile and everything about it. Return false when it was already gone.</summary>
    public virtual Task<bool> DeleteProfileAsync(JsonObject data) =>
        Task.FromResult(_profiles.TryRemove(data["profileId"]?.GetValue<string>() ?? "", out _));
}
