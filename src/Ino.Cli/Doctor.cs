namespace Ino.Cli;

public sealed record Check(string Name, string State, string Detail);

/// <summary>`ino doctor`: profile, keychain, address, clock and key, each ok / warn / fail. It makes one signed GET (events?limit=1) and no writes.</summary>
public static class Doctor
{
    public static async Task<List<Check>> RunAsync(CliContext ctx, string? profileFlag, CancellationToken ct)
    {
        var checks = new List<Check>();
        Resolved r;
        try
        {
            r = ctx.Profiles.Resolve(profileFlag);
            checks.Add(new("profile", "ok", $"{r.ProfileName ?? "(environment)"} from {r.Source}; {r.Connection.Environment}; channel {r.Connection.Channel}; {r.Connection.BaseUrl}"));
            checks.Add(new("keychain", "ok", r.ProfileName is null ? "credentials from environment variables" : "key and secret read from the keychain"));
        }
        catch (CliException e) when (e.Code is not "NO_CREDENTIALS")
        {
            checks.Add(new("profile", "fail", e.Message));
            return checks;
        }
        catch (CliException e)
        {
            checks.Add(new("keychain", "fail", e.Message));
            return checks;
        }
        if (r.Connection.IsLive) checks.Add(new("environment", "warn", "LIVE profile: writes ask for confirmation (--yes)."));

        var client = ctx.ClientFactory(r.Connection, ctx.Profiles.LockFor(r));
        DateTimeOffset? serverDate;
        try { serverDate = await client.HealthAsync(ct); }
        catch (CliException e)
        {
            checks.Add(new("network", "fail", e.Message));
            return checks;
        }
        checks.Add(new("network", "ok", $"reached {new Uri(r.Connection.BaseUrl).Host} over {new Uri(r.Connection.BaseUrl).Scheme}"));

        // Clock first, from the unsigned health check: a wrong clock would make the signed probe fail with 401.
        if (serverDate is { } date)
        {
            var skew = Math.Abs((DateTimeOffset.UtcNow - date).TotalSeconds);
            checks.Add(skew > 240 ? new("clock", skew > 300 ? "fail" : "warn", $"{skew:0} s off the server (signatures are accepted within ±300 s). Fix your system clock.")
                                  : new("clock", "ok", $"{skew:0} s off the server"));
        }
        else checks.Add(new("clock", "warn", "the server sent no Date header; cannot check"));
        if (checks[^1].State == "fail") return checks;

        ChannelResponse res;
        try { res = await client.GetAsync("events?limit=1", ct); }
        catch (CliException e)
        {
            checks.Add(new("key", "fail", e.Message));
            return checks;
        }
        if (res.Status is 200 or 403)
        {
            try
            {
                var who = await client.WhoAmIAsync(ct);
                checks.Add(who is null ? new("environment", "warn", "this engine cannot say whether the key is test or live (no whoami); tools treat it as live")
                    : who.Environment == r.Connection.Environment ? new("environment", "ok", $"the engine says {who.Environment} (tenant {who.Tenant}); matches the profile")
                    : who.Environment == "unknown" ? new("environment", "warn", $"the engine does not know this install's environment; MCP treats it as live")
                    : new("environment", who.Environment == "live" ? "fail" : "warn", $"the engine says {who.Environment} but the profile says {r.Connection.Environment}. Fix the profile (`ino profile add … --environment {who.Environment}`)."));
            }
            catch (CliException e) { checks.Add(new("environment", "warn", $"whoami failed: {e.Message}")); }
        }
        checks.Add(res.Status switch
        {
            200 => new("key", "ok", "signature accepted; the key has the events scope"),
            403 => new("key", "ok", "signature accepted; this key has no events scope (needed for `ino listen` and `ino events`)"),
            401 => new("key", "fail", "signature refused (401): key, secret, channel or clock. Do not retry in a loop: repeated failures block your IP for a minute."),
            404 => new("key", "fail", "404: the base URL or channel is wrong (on Ino Cloud the base URL ends with /t/<tenant code>)."),
            _ => new("key", "fail", $"unexpected HTTP {res.Status}"),
        });
        return checks;
    }
}
