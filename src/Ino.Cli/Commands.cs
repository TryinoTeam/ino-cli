using Ino.Cli.Hub;
using System.CommandLine;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ino.Cli;

/// <summary>The command tree. Every command supports --json; failures go to stderr with an exit code from <see cref="ExitCodes"/>.</summary>
public static class Commands
{
    public static readonly string Version = typeof(Commands).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";
    static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    // Relaxed escaping so a secret with + < > & ' stays a plain string and the profile-secret filter still finds it.
    static readonly JsonSerializerOptions Compact = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    const int MaxHumanOutput = 64 * 1024;

    public static async Task<int> RunAsync(string[] args, CliContext ctx, CancellationToken ct = default)
    {
        var json = new Option<bool>("--json") { Description = "Print one JSON object (errors as {\"error\":…} on stderr)", Recursive = true };
        var profile = new Option<string?>("--profile", "-p") { Description = "Profile to use (default: INO_PROFILE, .ino/config.json, then the default profile)", Recursive = true };
        var root = new RootCommand($"ino {Version}: the Ino Loyalty command line. Docs: `ino docs search <words>`.");
        root.Options.Add(json);
        root.Options.Add(profile);

        int exit = ExitCodes.Ok;
        void Bind(Command cmd, Func<ParseResult, Task> action) => cmd.SetAction(async (pr, token) =>
        {
            try { await action(pr); }
            catch (CliException e) { exit = e.ExitCode; WriteError(ctx, pr.GetValue(json), e.Code, e.Message, e.Hint); }
            catch (OperationCanceledException) { exit = ExitCodes.Network; WriteError(ctx, pr.GetValue(json), "CANCELLED", "Cancelled."); }
            return exit;
        });

        // ---------------------------------------------------------------- profile
        var profileCmd = new Command("profile", "Connection profiles: base URL, channel, environment; the key and secret are kept in the OS keychain");
        var nameArg = new Argument<string>("name");
        var baseUrl = new Option<string>("--base-url") { Description = "Engine address, e.g. https://api.example.com/t/tabc1234", Required = true };
        var channel = new Option<string>("--channel") { Description = "Channel code", Required = true };
        var envOpt = new Option<string>("--environment") { Description = "test or live: say which environment this channel belongs to (required)", Required = true };
        envOpt.AcceptOnlyFromAmong("test", "live");
        var makeDefault = new Option<bool>("--default") { Description = "Make it the default profile" };
        var store = new Option<string>("--store") { Description = "keychain (default) or file (0600 credentials.json; only where no keychain exists)", DefaultValueFactory = _ => "keychain" };
        store.AcceptOnlyFromAmong("keychain", "file");
        var add = new Command("add", "Add or replace a profile. The API key and signing secret are read from the terminal (or two lines on stdin), never from flags")
            { nameArg, baseUrl, channel, envOpt, makeDefault, store };
        Bind(add, pr =>
        {
            var name = pr.GetValue(nameArg)!;
            ProfileStore.ValidateName(name);
            var key = ReadCredential(ctx, "API key (ik_…): ");
            var secret = ReadCredential(ctx, "Signing secret: ");
            var toFile = pr.GetValue(store) == "file";
            ctx.Profiles.Add(name, pr.GetValue(baseUrl)!, pr.GetValue(channel)!, pr.GetValue(envOpt)!, key ?? "", secret ?? "", pr.GetValue(makeDefault), toFile);
            if (toFile) ctx.Err.WriteLine("warning: the key and secret are in a file (mode 0600), not in a keychain. Anyone who can read your home folder can use them.");
            Write(ctx, pr.GetValue(json), new JsonObject { ["profile"] = name, ["saved"] = true, ["config"] = ctx.Profiles.ConfigPath },
                $"Saved profile {name}. The key and secret are in {(toFile ? "credentials.json (mode 0600)" : "the keychain")}; {ctx.Profiles.ConfigPath} holds only the address and channel.");
            return Task.CompletedTask;
        });
        var list = new Command("list", "List profiles");
        Bind(list, pr =>
        {
            var cfg = ctx.Profiles.Load();
            var arr = new JsonArray(cfg.Profiles.Select(p => (JsonNode)new JsonObject
            {
                ["name"] = p.Key, ["default"] = p.Key == cfg.DefaultProfile, ["baseUrl"] = p.Value.BaseUrl, ["channel"] = p.Value.Channel,
                ["environment"] = p.Value.Environment, ["key"] = p.Value.KeyHint, ["loginOnly"] = p.Value.SignInOnly,
            }).ToArray());
            Write(ctx, pr.GetValue(json), new JsonObject { ["profiles"] = arr }, cfg.Profiles.Count == 0 ? "No profiles yet. See `ino profile add --help`." :
                string.Join("\n", cfg.Profiles.Select(p => $"{(p.Key == cfg.DefaultProfile ? "*" : " ")} {p.Key,-16} {p.Value.Environment,-5} " +
                    (p.Value.SignInOnly ? $"{"login only",-16} {p.Value.BaseUrl}" : $"{p.Value.Channel,-16} {p.Value.BaseUrl}  key {p.Value.KeyHint}"))));
            return Task.CompletedTask;
        });
        var useArg = new Argument<string>("name");
        var use = new Command("use", "Make a profile the default") { useArg };
        Bind(use, pr =>
        {
            ctx.Profiles.Use(pr.GetValue(useArg)!);
            Write(ctx, pr.GetValue(json), new JsonObject { ["default"] = pr.GetValue(useArg) }, $"Default profile: {pr.GetValue(useArg)}");
            return Task.CompletedTask;
        });
        var setArg = new Argument<string>("name");
        var mcpWrites = new Option<string?>("--mcp-writes") { Description = "on or off: allow `ino mcp serve --allow-writes` on this profile (test environments only)" };
        mcpWrites.AcceptOnlyFromAmong("on", "off");
        var set = new Command("set", "Change a profile setting") { setArg, mcpWrites };
        Bind(set, pr =>
        {
            if (pr.GetValue(mcpWrites) is not { } v) throw new CliException(ExitCodes.Usage, "USAGE", "Nothing to set. Use --mcp-writes on|off.");
            ctx.Profiles.SetMcpWrites(pr.GetValue(setArg)!, v == "on");
            Write(ctx, pr.GetValue(json), new JsonObject { ["profile"] = pr.GetValue(setArg), ["mcpWrites"] = v == "on" }, $"MCP writes {(v == "on" ? "allowed" : "not allowed")} on {pr.GetValue(setArg)}.");
            return Task.CompletedTask;
        });
        profileCmd.Subcommands.Add(set);
        var rmArg = new Argument<string>("name");
        var remove = new Command("remove", "Remove a profile and its keychain entry") { rmArg };
        Bind(remove, async pr =>
        {
            var rmName = pr.GetValue(rmArg)!;
            // The profile's sign-in is ended at Ino too (best effort; offline it expires when unused for 14 days).
            if (ctx.Profiles.Load().Profiles.TryGetValue(rmName, out var rmProf))
                try { await InoLogin.LogoutAsync(ctx, rmName, InoLogin.TargetOf(rmProf.BaseUrl), all: false, ct); }
                catch (Exception e) when (e is CliException or HttpRequestException || e is TaskCanceledException && !ct.IsCancellationRequested) { }
            var had = ctx.Profiles.Remove(rmName);
            if (!had) throw new CliException(ExitCodes.NotFound, "NO_PROFILE", $"There is no profile named {pr.GetValue(rmArg)}.");
            Write(ctx, pr.GetValue(json), new JsonObject { ["removed"] = pr.GetValue(rmArg) }, $"Removed {pr.GetValue(rmArg)}.");
        });
        profileCmd.Subcommands.Add(add); profileCmd.Subcommands.Add(list); profileCmd.Subcommands.Add(use); profileCmd.Subcommands.Add(remove);
        root.Subcommands.Add(profileCmd);

        // ---------------------------------------------------------------- api
        var api = new Command("api", "Channel API operations");
        var apiList = new Command("list", "List operations with their class (read = changes nothing; write = changes data or sends something)");
        Bind(apiList, pr =>
        {
            var arr = new JsonArray(Catalog.Operations.Select(o => (JsonNode)new JsonObject { ["operation"] = o.Name, ["class"] = o.Class == OperationClass.Read ? "read" : "write", ["purpose"] = o.Purpose }).ToArray());
            Write(ctx, pr.GetValue(json), new JsonObject { ["operations"] = arr },
                string.Join("\n", Catalog.Operations.Select(o => $"{o.Name,-28} {(o.Class == OperationClass.Read ? "read " : "write")}  {o.Purpose}")));
            return Task.CompletedTask;
        });
        var opArg = new Argument<string>("operation");
        var describe = new Command("describe", "Purpose, class and the reference section of one operation") { opArg };
        Bind(describe, pr =>
        {
            var name = pr.GetValue(opArg)!;
            var info = Catalog.Find(name) ?? throw new CliException(ExitCodes.NotFound, "UNKNOWN_OPERATION", $"{name} is not a channel operation. See `ino api list`.");
            var reference = Docs.SectionFor(name);
            Write(ctx, pr.GetValue(json), new JsonObject
            {
                ["operation"] = info.Name, ["class"] = info.Class == OperationClass.Read ? "read" : "write", ["purpose"] = info.Purpose,
                ["reference"] = reference is null ? null : new JsonObject { ["doc"] = reference.Doc, ["heading"] = reference.Heading, ["text"] = reference.Text },
            }, $"{info.Name}  ({(info.Class == OperationClass.Read ? "read" : "write")})\n{info.Purpose}\n\n{reference?.Text ?? "See `ino docs search " + name + "`."}");
            return Task.CompletedTask;
        });
        var callArg = new Argument<string>("operation");
        var data = new Option<string?>("--data", "-d") { Description = "JSON body, or @file.json, or @- for stdin" };
        var yes = new Option<bool>("--yes", "-y") { Description = "Confirm a write on a live profile (together with --live)" };
        var live = new Option<bool>("--live") { Description = "Say that you mean the live environment (needed with --yes for live writes)" };
        var allowUnknown = new Option<bool>("--allow-unknown") { Description = "Send an operation this CLI version does not know (treated as a write)" };
        var call = new Command("call", "Call one operation (signed with signature v2). The body's field names follow your channel's mapping") { callArg, data, yes, live, allowUnknown };
        Bind(call, async pr =>
        {
            var op = pr.GetValue(callArg)!;
            if (Catalog.Find(op) is null && !pr.GetValue(allowUnknown))
                throw new CliException(ExitCodes.Usage, "UNKNOWN_OPERATION", $"{op} is not a channel operation this CLI knows. See `ino api list`; if the engine is newer, add --allow-unknown.");
            var body = ParseBody(ctx, pr.GetValue(data));
            var resolved = ctx.Profiles.Resolve(pr.GetValue(profile));
            Banner(ctx, resolved);
            LiveGuard.Check(ctx, resolved.Connection, op, pr.GetValue(yes), pr.GetValue(live));
            var res = await ctx.ClientFactory(resolved.Connection, ctx.Profiles.LockFor(resolved)).CallOperationAsync(op, body, ct);
            exit = ChannelClient.ExitCodeOf(res);
            WriteResponse(ctx, pr.GetValue(json), resolved.Connection, res, op, exit);
        });
        api.Subcommands.Add(apiList); api.Subcommands.Add(describe); api.Subcommands.Add(call);
        root.Subcommands.Add(api);

        // ---------------------------------------------------------------- docs
        var docs = new Command("docs", "Partner documentation (bundled; works offline)");
        var words = new Argument<string[]>("words") { Arity = ArgumentArity.OneOrMore };
        var search = new Command("search", "Search the documentation") { words };
        Bind(search, pr =>
        {
            var hits = Docs.Search(string.Join(' ', pr.GetValue(words)!));
            Write(ctx, pr.GetValue(json), new JsonObject { ["results"] = new JsonArray(hits.Select(h => (JsonNode)new JsonObject { ["doc"] = h.Doc, ["heading"] = h.Heading, ["excerpt"] = h.Excerpt }).ToArray()) },
                hits.Count == 0 ? "Nothing found." : string.Join("\n\n", hits.Select(h => $"## {h.Doc} › {h.Heading}\n{h.Excerpt}")));
            return Task.CompletedTask;
        });
        var heading = new Argument<string[]>("heading") { Arity = ArgumentArity.OneOrMore };
        var open = new Command("open", "Print one section by heading") { heading };
        Bind(open, pr =>
        {
            var s = Docs.Section(null, string.Join(' ', pr.GetValue(heading)!)) ?? throw new CliException(ExitCodes.NotFound, "NO_SECTION", "No section with that heading. Try `ino docs search`.");
            Write(ctx, pr.GetValue(json), new JsonObject { ["doc"] = s.Doc, ["heading"] = s.Heading, ["text"] = s.Text }, s.Text);
            return Task.CompletedTask;
        });
        docs.Subcommands.Add(search); docs.Subcommands.Add(open);
        root.Subcommands.Add(docs);

        // ---------------------------------------------------------------- doctor
        var goLiveOpt = new Option<bool>("--go-live") { Description = "Check whether this firm is ready to go live (needs `ino login`); exit 1 if anything blocks or could not be checked" };
        var doctor = new Command("doctor", "Check the profile, keychain, network, clock and key; --go-live checks the firm instead") { goLiveOpt };
        Bind(doctor, async pr =>
        {
            if (pr.GetValue(goLiveOpt))
            {
                // P1.5a: the firm's go-live items from the engine; only they decide the exit code (optional setup steps never block).
                var (lname, lprof, _) = ctx.Profiles.ResolveForLogin(pr.GetValue(profile));
                var data = await InoLogin.ManageAsync(ctx, lname, InoLogin.TargetOf(lprof.BaseUrl), HttpMethod.Get, "setup?goLive=true", null, null, ct) as JsonObject
                    ?? throw new CliException(ExitCodes.Network, "UNEXPECTED", "The engine sent no checklist.");
                var step = (data["steps"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(x => x["key"]?.ToString() == "goLive")
                    ?? throw new CliException(ExitCodes.Usage, "ENGINE_TOO_OLD", "The engine of this firm is too old for `ino doctor --go-live`; it needs the version with P1.5a.");
                var items = (step["items"] as JsonArray ?? []).OfType<JsonObject>().ToList();
                // Green only when the step itself was read and every item is ok, warn or info (review P1.5a K1: an unread step has no items).
                exit = items.Count > 0 && step["status"]?.ToString() is not ("fail" or "unknown")
                    && items.All(i => i["status"]?.ToString() is "ok" or "warn" or "info") ? ExitCodes.Ok : ExitCodes.Business;
                var head = $"Go-live check of {Safe(data["tenant"])} ({Safe(data["environment"])}): {Safe(step["detail"])}";
                Write(ctx, pr.GetValue(json), step.DeepClone(), head + "\n" + string.Join("\n", items.Select(i =>
                    $"{(i["status"]?.ToString() switch { "ok" => "✓", "warn" => "!", "info" => "–", "unknown" => "?", _ => "✗" })} {Safe(i["key"]),-16} {Safe(i["detail"])}"
                    + (i["next"] is { } n ? $"\n    → {Safe(n)}" : "")))
                    + (exit == ExitCodes.Ok ? "\nNothing blocks going live." : "\nNot ready: fix the ✗ items (and recheck the ? ones)."));
                return;
            }
            var report = await Doctor.RunAsync(ctx, pr.GetValue(profile), ct);
            exit = report.FirstOrDefault(c => c.State == "fail") is { } f ? f.Name switch
            {
                "profile" => ExitCodes.Usage,
                "network" => ExitCodes.Network,
                "key" when f.Detail.Contains("401") => ExitCodes.Auth,
                "key" when f.Detail.StartsWith("404", StringComparison.Ordinal) => ExitCodes.NotFound,
                _ => ExitCodes.Business,
            } : ExitCodes.Ok;
            Write(ctx, pr.GetValue(json), new JsonObject { ["version"] = Version, ["checks"] = new JsonArray(report.Select(c => (JsonNode)new JsonObject { ["check"] = c.Name, ["state"] = c.State, ["detail"] = c.Detail }).ToArray()) },
                $"ino {Version}\n" + string.Join("\n", report.Select(c => $"{(c.State switch { "ok" => "✓", "warn" => "!", _ => "✗" })} {c.Name,-12} {c.Detail}")));
        });
        root.Subcommands.Add(doctor);

        // ---------------------------------------------------------------- events, listen, trigger
        var liveFlag = new Option<bool>("--live") { Description = "Say that you mean the live environment (event data can hold personal data)" };
        var typesOpt = new Option<string?>("--events") { Description = "Event types, comma separated (default: all public types)" };
        var events = new Command("events", "The event feed: what happened in the program (same events as webhooks)");
        var evTypes = new Command("types", "Public event types with their versions (from the engine's catalog)");
        Bind(evTypes, async pr =>
        {
            var resolved = ctx.Profiles.Resolve(pr.GetValue(profile));
            var catalog = await ctx.ClientFactory(resolved.Connection, null).CatalogAsync(ct);
            var list = EventCatalogView.Public(catalog);
            Write(ctx, pr.GetValue(json), new JsonObject { ["events"] = new JsonArray(list.Select(x => (JsonNode)new JsonObject { ["type"] = x.Type, ["version"] = x.Version, ["group"] = x.Group, ["title"] = x.Title }).ToArray()) },
                string.Join("\n", list.Select(x => $"{x.Type,-32} v{x.Version,-3} {x.Group,-14} {x.Title}")));
        });
        var after = new Option<string?>("--after") { Description = "Cursor from a previous call; without it the feed starts now (no history)" };
        var limitOpt = new Option<int>("--limit") { Description = "At most this many events (1-500)", DefaultValueFactory = _ => 100 };
        var evList = new Command("list", "One page of events after a cursor") { after, typesOpt, limitOpt, liveFlag };
        Bind(evList, async pr =>
        {
            var resolved = ctx.Profiles.Resolve(pr.GetValue(profile));
            Banner(ctx, resolved);
            RequireLiveFlag(resolved, pr.GetValue(liveFlag), "events list");
            if (pr.GetValue(typesOpt) is { } lt0) await CheckTypes(ctx.ClientFactory(resolved.Connection, null), lt0, ct);
            var q = $"events?limit={Math.Clamp(pr.GetValue(limitOpt), 1, 500)}" + (pr.GetValue(after) is { } a ? "&after=" + Uri.EscapeDataString(a) : "")
                    + (pr.GetValue(typesOpt) is { } t ? "&types=" + Uri.EscapeDataString(t) : "");
            var res = await ctx.ClientFactory(resolved.Connection, ctx.Profiles.LockFor(resolved)).GetAsync(q, ct);
            exit = ChannelClient.ExitCodeOf(res);
            if (exit != ExitCodes.Ok) { WriteResponse(ctx, pr.GetValue(json), resolved.Connection, res, "events", exit); return; }
            var (evs, cursor, more) = Listener.Page(res);
            var redacted = new JsonArray(evs.Select(e => Redaction.Fields(e.DeepClone())).ToArray());
            Write(ctx, pr.GetValue(json), new JsonObject { ["events"] = redacted, ["cursor"] = cursor, ["hasMore"] = more },
                (evs.Count == 0 ? (pr.GetValue(after) is null ? "The feed starts now. Use the cursor below with --after to read what happens next." : "No new events.")
                    : string.Join("\n", evs.Select(e => $"{e["time"]}  {e["type"],-28} v{e["dataversion"]}  {e["id"]}"))) + $"\ncursor: {cursor}" + (more ? " (more waiting)" : ""));
        });
        var tail = new Command("tail", "Follow the feed from now and print each event (NDJSON with --json). Output saved to a file is outside member erasure: do not keep it") { typesOpt, liveFlag };
        Bind(tail, async pr =>
        {
            var resolved = ctx.Profiles.Resolve(pr.GetValue(profile));
            Banner(ctx, resolved);
            RequireLiveFlag(resolved, pr.GetValue(liveFlag), "events tail");
            var client = ctx.ClientFactory(resolved.Connection, ctx.Profiles.LockFor(resolved));
            string? cursor = null;
            var types = pr.GetValue(typesOpt);
            if (types is not null) await CheckTypes(client, types, ct);
            while (!ct.IsCancellationRequested)
            {
                var q = "events?limit=100" + (cursor is null ? "" : "&after=" + Uri.EscapeDataString(cursor)) + (types is null ? "" : "&types=" + Uri.EscapeDataString(types));
                var res = await client.GetAsync(q, ct);
                if (res.Status is 429 or 503)
                {
                    await ctx.Delay(res.RetryAfter is { } ra && ra > TimeSpan.Zero ? ra : TimeSpan.FromSeconds(res.Status == 429 ? 30 : 15), ct);
                    continue;
                }
                var code = ChannelClient.ExitCodeOf(res);
                if (code != ExitCodes.Ok) { exit = code; WriteResponse(ctx, pr.GetValue(json), resolved.Connection, res, "events", code); return; }
                var (evs, next, more) = Listener.Page(res);
                if (cursor is null) ctx.Err.WriteLine("Following the feed from now. Ctrl-C to stop.");
                foreach (var e in evs)
                    ctx.Out.WriteLine(pr.GetValue(json) ? Redaction.Fields(e.DeepClone())!.ToJsonString() : $"{e["time"]}  {e["type"],-28} v{e["dataversion"]}  {e["id"]}");
                cursor = next ?? cursor;
                await ctx.Delay(more ? Listener.MinRepoll : TimeSpan.FromSeconds(2), ct);
            }
        });
        events.Subcommands.Add(evTypes); events.Subcommands.Add(evList); events.Subcommands.Add(tail);
        root.Subcommands.Add(events);

        var forwardTo = new Option<string>("--forward-to") { Description = "Local URL that receives the events, e.g. http://localhost:3000/webhooks", Required = true };
        var printSecret = new Option<bool>("--print-secret") { Description = "Print this profile's local signing secret (whsec_…) and exit" };
        var resume = new Option<bool>("--resume") { Description = "Continue from the saved position instead of from now" };
        var skipFailed = new Option<bool>("--skip-failed") { Description = "Go on when the local endpoint refuses an event (default: stop)" };
        var scheme = new Option<string>("--scheme") { Description = "Signature headers to send: both (default, like new subscriptions), standard or legacy", DefaultValueFactory = _ => "both" };
        scheme.AcceptOnlyFromAmong("both", "standard", "legacy");
        var listen = new Command("listen", "Forward this channel's events to a local URL with the headers of a real webhook delivery. Delivery is at least once: deduplicate by webhook-id")
            { forwardTo, printSecret, resume, skipFailed, typesOpt, liveFlag, scheme };
        forwardTo.Required = false;
        Bind(listen, async pr =>
        {
            var resolved = ctx.Profiles.Resolve(pr.GetValue(profile));
            var secret = ctx.Profiles.DeviceSecret(resolved);
            if (pr.GetValue(printSecret)) { ctx.Out.WriteLine(secret); return; }
            var target = StandardWebhooks.ValidateLocalTarget(pr.GetValue(forwardTo) ?? throw new CliException(ExitCodes.Usage, "USAGE", "--forward-to is required (or --print-secret)."));
            Banner(ctx, resolved);
            RequireLiveFlag(resolved, pr.GetValue(liveFlag), "listen");
            var feedClient = ctx.ClientFactory(resolved.Connection, ctx.Profiles.LockFor(resolved));
            if (pr.GetValue(typesOpt) is { } lt) await CheckTypes(feedClient, lt, ct);
            var listener = new Listener(feedClient, target, secret, ctx.Profiles.Cursors, resolved.ProfileName ?? "env", ctx.LocalHandler, ctx.Delay)
            {
                Types = pr.GetValue(typesOpt)?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), SkipFailed = pr.GetValue(skipFailed),
                Scheme = pr.GetValue(scheme)!, Notice = m => ctx.Err.WriteLine(m),
            };
            var asJson = pr.GetValue(json);
            await listener.RunAsync(pr.GetValue(resume),
                e => ctx.Out.WriteLine(asJson
                    ? new JsonObject { ["id"] = e.Id, ["type"] = e.Type, ["dataversion"] = e.DataVersion, ["time"] = e.Time?.ToString("O"), ["localStatus"] = e.LocalStatus }.ToJsonString()
                    : $"{e.Time?.ToUniversalTime():yyyy-MM-dd HH:mm:ss}Z  {e.Type,-28} v{e.DataVersion}  {e.Id}  → {(e.LocalStatus is { } s ? s.ToString() : "no answer")}"),
                start => ctx.Err.WriteLine($"Listening {start}; forwarding to {target}. Verify with the secret from `ino listen --print-secret`. Ctrl-C to stop."),
                ct);
        });
        root.Subcommands.Add(listen);

        var typeArg = new Argument<string>("event-type");
        var dataversion = new Option<int?>("--dataversion") { Description = "Event version to produce (default: the newest)" };
        var overrides = new Option<string[]>("--override") { Description = "Change a field: data.points=50 (repeatable)", AllowMultipleArgumentsPerToken = false };
        var fixture = new Option<string?>("--fixture") { Description = "A JSON file with the whole event envelope to send" };
        var forwardOpt = new Option<string?>("--forward-to") { Description = "Local URL to send the synthetic event to (signed like `listen`)" };
        var trigger = new Command("trigger", "Send a synthetic event of any public type to a local URL, signed like `listen`. Nothing reaches the engine; to make the engine call a subscription use `ino webhooks test`")
            { typeArg, dataversion, overrides, fixture, forwardOpt, scheme };
        Bind(trigger, async pr =>
        {
            var resolved = ctx.Profiles.Resolve(pr.GetValue(profile));
            var target = StandardWebhooks.ValidateLocalTarget(pr.GetValue(forwardOpt) ?? throw new CliException(ExitCodes.Usage, "USAGE", "Give --forward-to with a local URL, e.g. http://localhost:3000/webhooks."));
            JsonObject envelope;
            if (pr.GetValue(fixture) is { } f)
            {
                if (!File.Exists(f)) throw new CliException(ExitCodes.Usage, "NO_FILE", $"{f} does not exist.");
                envelope = JsonNode.Parse(File.ReadAllText(f)) as JsonObject ?? throw new CliException(ExitCodes.Usage, "BAD_FIXTURE", "The fixture must be a JSON object (an event envelope).");
            }
            else
            {
                var catalog = await ctx.ClientFactory(resolved.Connection, null).CatalogAsync(ct);
                envelope = EventCatalogView.Example(catalog, pr.GetValue(typeArg)!, pr.GetValue(dataversion));
            }
            envelope["id"] = Guid.NewGuid().ToString();
            envelope["time"] = DateTimeOffset.UtcNow.ToString("O");
            foreach (var o in pr.GetValue(overrides) ?? []) EventCatalogView.Override(envelope, o);
            var listener = new Listener(ctx.ClientFactory(resolved.Connection, null), target, ctx.Profiles.DeviceSecret(resolved), ctx.Profiles.Cursors, resolved.ProfileName ?? "env", ctx.LocalHandler, ctx.Delay)
            { Scheme = pr.GetValue(scheme)! };
            var status = await listener.ForwardAsync(envelope, synthetic: true, ct);
            exit = status is >= 200 and < 300 ? ExitCodes.Ok : status is null ? ExitCodes.Network : ExitCodes.Business;
            Write(ctx, pr.GetValue(json), new JsonObject { ["id"] = envelope["id"]!.ToString(), ["type"] = envelope["type"]?.ToString(), ["dataversion"] = envelope["dataversion"]?.DeepClone(), ["localStatus"] = status },
                $"Sent synthetic {envelope["type"]} v{envelope["dataversion"]} ({envelope["id"]}) to {target} → {(status is { } s ? s.ToString() : "no answer")}");
        });
        root.Subcommands.Add(trigger);

        // ---------------------------------------------------------------- webhooks
        var webhooks = new Command("webhooks", "Webhook subscriptions of this channel (the key needs the webhooks permission)");
        void WebhookCall(Command cmd, Func<ParseResult, (HttpMethod Method, string Path, JsonNode? Body, bool Write, bool ShowSecret)> build)
        {
            var whYes = new Option<bool>("--yes", "-y") { Description = "Confirm a change on a live profile (together with --live)" };
            var whLive = new Option<bool>("--live") { Description = "Say that you mean the live environment" };
            cmd.Options.Add(whYes); cmd.Options.Add(whLive);
            Bind(cmd, async pr =>
            {
                var (method, path, body, isWrite, showSecret) = build(pr);
                var resolved = ctx.Profiles.Resolve(pr.GetValue(profile));
                Banner(ctx, resolved);
                if (isWrite)
                {
                    if (cmd.Name == "create" && resolved.Connection.IsLive)
                        ctx.Err.WriteLine("Note: if this URL is already subscribed, create updates that subscription (event types replaced, set ACTIVE again).");
                    if (cmd.Name == "resend" && resolved.Connection.IsLive)
                        ctx.Err.WriteLine("Note: the engine will send this delivery again to the subscription's real (live) receiver.");
                    if (cmd.Name == "test" && resolved.Connection.IsLive)
                        ctx.Err.WriteLine("Note: the engine will POST a webhook.test event to the subscription's real (live) receiver.");
                    LiveGuard.CheckWrite(ctx, resolved.Connection, $"webhooks {cmd.Name}", pr.GetValue(whYes), pr.GetValue(whLive));
                }
                var res = await ctx.ClientFactory(resolved.Connection, ctx.Profiles.LockFor(resolved)).SendAsync(method, path, body, ct);
                exit = ChannelClient.ExitCodeOf(res);
                if (showSecret && exit == ExitCodes.Ok)
                {
                    if (FindSecret(res.Body) is { } sec)
                    {
                        ctx.Err.WriteLine("Signing secret for this subscription (shown once; store it in your receiver's configuration now):");
                        ctx.Err.WriteLine(sec);
                    }
                    else if (cmd.Name == "create")
                        ctx.Err.WriteLine("A subscription for this URL already existed: it was updated (event types replaced, set ACTIVE again) and its secret was not returned. Use `ino webhooks rotate-secret <id>` if you need a new one.");
                }
                WriteResponse(ctx, pr.GetValue(json), resolved.Connection, res, $"webhooks.{cmd.Name}", exit);
            });
        }
        static Guid IdOf(string s) => Guid.TryParse(s, out var g) ? g : throw new CliException(ExitCodes.Usage, "BAD_ID", "Use a subscription id (GUID). See `ino webhooks list`.");
        var whList = new Command("list", "List subscriptions");
        WebhookCall(whList, _ => (HttpMethod.Get, "webhooks/subscriptions", null, false, false));
        var whIdArg = new Argument<string>("id");
        var whGet = new Command("get", "Show one subscription") { whIdArg };
        WebhookCall(whGet, pr => (HttpMethod.Get, $"webhooks/subscriptions/{IdOf(pr.GetValue(whIdArg)!)}", null, false, false));
        var whUrl = new Option<string?>("--url") { Description = "Receiver URL (https)" };
        var whEvents = new Option<string?>("--events") { Description = "Event types, comma separated (default: all)" };
        var whCreate = new Command("create", "Create a subscription; prints its signing secret once") { whUrl, whEvents };
        WebhookCall(whCreate, pr => (HttpMethod.Post, "webhooks/subscriptions", new JsonObject
        {
            ["url"] = pr.GetValue(whUrl) ?? throw new CliException(ExitCodes.Usage, "USAGE", "--url is required."),
            ["eventTypes"] = pr.GetValue(whEvents) is { } ev ? new JsonArray(ev.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => (JsonNode)x).ToArray()) : null,
        }, true, true));
        var whIdArg2 = new Argument<string>("id");
        var whStatus = new Option<string?>("--status") { Description = "ACTIVE or PAUSED" };
        var whUpdate = new Command("update", "Change URL, event types or status") { whIdArg2, whUrl, whEvents, whStatus };
        WebhookCall(whUpdate, pr =>
        {
            var b = new JsonObject();
            if (pr.GetValue(whUrl) is { } u) b["url"] = u;
            if (pr.GetValue(whEvents) is { } ev) b["eventTypes"] = new JsonArray(ev.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => (JsonNode)x).ToArray());
            if (pr.GetValue(whStatus) is { } st) b["status"] = st;
            if (b.Count == 0) throw new CliException(ExitCodes.Usage, "USAGE", "Give --url, --events or --status.");
            return (HttpMethod.Patch, $"webhooks/subscriptions/{IdOf(pr.GetValue(whIdArg2)!)}", b, true, false);
        });
        var whIdArg3 = new Argument<string>("id");
        var whDelete = new Command("delete", "Delete a subscription") { whIdArg3 };
        WebhookCall(whDelete, pr => (HttpMethod.Delete, $"webhooks/subscriptions/{IdOf(pr.GetValue(whIdArg3)!)}", null, true, false));
        var whIdArg4 = new Argument<string>("id");
        var whTestType = new Option<string?>("--event-type") { Description = "Send the catalog example of this public event (marked \"test\": true) instead of webhook.test" };
        var whTestVersion = new Option<int?>("--dataversion") { Description = "Version of --event-type (default: the newest)" };
        var whTest = new Command("test", "Make the engine POST a signed test event to the subscription's receiver") { whIdArg4, whTestType, whTestVersion };
        WebhookCall(whTest, pr =>
        {
            var b = new JsonObject();
            if (pr.GetValue(whTestType) is { } et) b["eventType"] = et;
            if (pr.GetValue(whTestVersion) is { } dv) b["dataversion"] = dv;
            return (HttpMethod.Post, $"webhooks/subscriptions/{IdOf(pr.GetValue(whIdArg4)!)}/test", b, true, false);
        });
        var whIdArg5 = new Argument<string>("id");
        var whRotate = new Command("rotate-secret", "New signing secret (the old one keeps working for 24 hours); prints it once") { whIdArg5 };
        WebhookCall(whRotate, pr => (HttpMethod.Post, $"webhooks/subscriptions/{IdOf(pr.GetValue(whIdArg5)!)}/rotate-secret", new JsonObject(), true, true));
        var whIdArg6 = new Argument<string>("id");
        var whStatusFilter = new Option<string?>("--status") { Description = "ALL, PENDING, FAILED or SENT" };
        var whLimit = new Option<int>("--limit") { Description = "At most this many (1-100)", DefaultValueFactory = _ => 50 };
        var whDeliveries = new Command("deliveries", "Recent deliveries of a subscription: status, attempts, last HTTP status and error (no event data)") { whIdArg6, whStatusFilter, whLimit };
        WebhookCall(whDeliveries, pr => (HttpMethod.Get, $"webhooks/subscriptions/{IdOf(pr.GetValue(whIdArg6)!)}/deliveries?limit={Math.Clamp(pr.GetValue(whLimit), 1, 100)}"
            + (pr.GetValue(whStatusFilter) is { } st ? "&status=" + Uri.EscapeDataString(st) : ""), null, false, false));
        var whIdArg7 = new Argument<string>("id");
        var whDelArg = new Argument<string>("delivery-id");
        var whAttempts = new Command("attempts", "Attempts of one delivery: time, duration, HTTP status, outcome, error (no response bodies)") { whIdArg7, whDelArg };
        WebhookCall(whAttempts, pr => (HttpMethod.Get, $"webhooks/subscriptions/{IdOf(pr.GetValue(whIdArg7)!)}/deliveries/{IdOf(pr.GetValue(whDelArg)!)}/attempts", null, false, false));
        var whIdArg8 = new Argument<string>("id");
        var whDelArg2 = new Argument<string>("delivery-id");
        var whResend = new Command("resend", "Queue a delivery to be sent again (the receiver gets the same webhook-id)") { whIdArg8, whDelArg2 };
        WebhookCall(whResend, pr => (HttpMethod.Post, $"webhooks/subscriptions/{IdOf(pr.GetValue(whIdArg8)!)}/deliveries/{IdOf(pr.GetValue(whDelArg2)!)}/resend", new JsonObject(), true, false));
        foreach (var c in new[] { whList, whGet, whCreate, whUpdate, whDelete, whTest, whRotate, whDeliveries, whAttempts, whResend }) webhooks.Subcommands.Add(c);
        root.Subcommands.Add(webhooks);

        // ---------------------------------------------------------------- jobs
        var jobs = new Command("jobs", "Bulk jobs (the key needs the jobs permission)");
        var jobType = new Argument<string>("job-type");
        var jobData = new Option<string?>("--data", "-d") { Description = "JSON body, @file.json or @-" };
        var jobYes = new Option<bool>("--yes", "-y") { Description = "Confirm on a live profile (together with --live)" };
        var jobLive = new Option<bool>("--live") { Description = "Say that you mean the live environment" };
        var submit = new Command("submit", "Submit a job; prints its id") { jobType, jobData, jobYes, jobLive };
        Bind(submit, async pr =>
        {
            var t = pr.GetValue(jobType)!;
            if (!System.Text.RegularExpressions.Regex.IsMatch(t, "^[a-z][a-z0-9_]{1,63}$")) throw new CliException(ExitCodes.Usage, "BAD_JOB_TYPE", "Job types look like point_load.");
            var body = ParseBody(ctx, pr.GetValue(jobData)) ?? [];
            var resolved = ctx.Profiles.Resolve(pr.GetValue(profile));
            Banner(ctx, resolved);
            LiveGuard.CheckWrite(ctx, resolved.Connection, $"jobs submit {t}", pr.GetValue(jobYes), pr.GetValue(jobLive));
            var res = await ctx.ClientFactory(resolved.Connection, ctx.Profiles.LockFor(resolved)).SendAsync(HttpMethod.Post, $"jobs/{t}", body, ct);
            exit = ChannelClient.ExitCodeOf(res);
            WriteResponse(ctx, pr.GetValue(json), resolved.Connection, res, $"jobs.{t}", exit);
        });
        var jobId = new Argument<string>("job-id");
        var status = new Command("status", "Status and progress of a job") { jobId };
        Bind(status, async pr =>
        {
            if (!Guid.TryParse(pr.GetValue(jobId), out var id)) throw new CliException(ExitCodes.Usage, "BAD_ID", "Use the job id (GUID) that submit printed.");
            var resolved = ctx.Profiles.Resolve(pr.GetValue(profile));
            var res = await ctx.ClientFactory(resolved.Connection, ctx.Profiles.LockFor(resolved)).SendAsync(HttpMethod.Post, $"jobs/{id}/status", new JsonObject(), ct);
            exit = ChannelClient.ExitCodeOf(res);
            WriteResponse(ctx, pr.GetValue(json), resolved.Connection, res, "jobs.status", exit);
        });
        var jobId2 = new Argument<string>("job-id");
        var outFile = new Option<string?>("--out", "-o") { Description = "Write the result to this file (needed when the result is a file, e.g. CSV)" };
        var jobResultLive = new Option<bool>("--live") { Description = "Say that you mean the live environment (results can hold personal data)" };
        var result = new Command("result", "Download a finished job's result") { jobId2, outFile, jobResultLive };
        Bind(result, async pr =>
        {
            if (!Guid.TryParse(pr.GetValue(jobId2), out var id)) throw new CliException(ExitCodes.Usage, "BAD_ID", "Use the job id (GUID) that submit printed.");
            var resolved = ctx.Profiles.Resolve(pr.GetValue(profile));
            RequireLiveFlag(resolved, pr.GetValue(jobResultLive), "jobs result");
            var res = await ctx.ClientFactory(resolved.Connection, ctx.Profiles.LockFor(resolved)).SendAsync(HttpMethod.Post, $"jobs/{id}/result", new JsonObject(), ct);
            exit = ChannelClient.ExitCodeOf(res);
            if (res.Status == 404)
                throw new CliException(ExitCodes.NotFound, "NO_RESULT", "No result: the job id is unknown on this channel, or its result has expired (see expiresAt in `ino jobs status`).");
            if (res.Status == 400)
                throw new CliException(ExitCodes.Business, "NOT_READY", "The job is not READY yet. Check `ino jobs status <id>`.");
            var isJson = res.ContentType is null or "application/json" || res.ContentType.EndsWith("+json", StringComparison.Ordinal);
            if (exit == ExitCodes.Ok && !isJson)
            {
                var path = pr.GetValue(outFile) ?? throw new CliException(ExitCodes.Usage, "OUT_NEEDED", $"The result is a file ({res.ContentType}); give --out <file>. It is not printed to the terminal.");
                File.WriteAllBytes(path, res.Bytes ?? []);
                Write(ctx, pr.GetValue(json), new JsonObject { ["written"] = path, ["bytes"] = res.Bytes?.Length ?? 0, ["contentType"] = res.ContentType },
                    $"Wrote {res.Bytes?.Length ?? 0} bytes ({res.ContentType}) to {path}.");
                return;
            }
            WriteResponse(ctx, pr.GetValue(json), resolved.Connection, res, "jobs.result", exit);
        });
        jobs.Subcommands.Add(submit); jobs.Subcommands.Add(status); jobs.Subcommands.Add(result);
        root.Subcommands.Add(jobs);

        // ---------------------------------------------------------------- connector
        var connector = new Command("connector", "Your own integration (a connector that runs on your server)");
        var suiteOpt = new Option<string>("--suite") { Description = "Scenario set: pos or ecommerce", DefaultValueFactory = _ => "pos" };
        suiteOpt.AcceptOnlyFromAmong("pos", "ecommerce");
        var hoursOpt = new Option<int>("--hours") { Description = "Look back this many hours (1-720)", DefaultValueFactory = _ => 72 };
        var check = new Command("check", "Compatibility report: which required scenarios the engine has seen from this key (run them on your test channel first)") { suiteOpt, hoursOpt };
        Bind(check, async pr =>
        {
            var resolved = ctx.Profiles.Resolve(pr.GetValue(profile));
            Banner(ctx, resolved);
            var q = $"certification?suite={Uri.EscapeDataString(pr.GetValue(suiteOpt)!)}&hours={Math.Clamp(pr.GetValue(hoursOpt), 1, 720)}";
            var res = await ctx.ClientFactory(resolved.Connection, ctx.Profiles.LockFor(resolved)).GetAsync(q, ct);
            exit = ChannelClient.ExitCodeOf(res);
            if (exit != ExitCodes.Ok || ((res.Body as JsonObject)?["Data"] as JsonObject) is not { } d) { WriteResponse(ctx, pr.GetValue(json), resolved.Connection, res, "certification", exit); return; }
            var scenarios = (d["scenarios"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
            var all = scenarios.Count > 0 && scenarios.All(x => x["passed"]?.GetValue<bool>() == true);
            exit = all ? ExitCodes.Ok : ExitCodes.Business;
            var lines = new List<string> { $"{d["suite"]} suite, last {d["hours"]} h, {d["scope"]}: {d["passed"]}/{d["total"]} passed" };
            if (d["requestLogEnabled"]?.GetValue<bool>() == false) lines.Add("note: the engine's request log is off, so scenarios that rely on it cannot pass.");
            foreach (var x in scenarios)
            {
                var ok = x["passed"]?.GetValue<bool>() == true;
                lines.Add($"{(ok ? "✓" : "✗")} {x["code"],-4} {x["title"]}");
                lines.Add($"      {x["evidence"]}");
                if (!ok && x["howTo"] is { } how) lines.Add($"      to do: {how}");
            }
            Write(ctx, pr.GetValue(json), d.DeepClone(), string.Join("\n", lines));
        });
        var cname = new Argument<string>("name") { Description = "Project name, e.g. my-shop-connector" };
        var selfHosted = new Option<bool>("--self-hosted") { Description = "A connector that runs on your own server (the only kind for now)" };
        var lang = new Option<string>("--lang") { Description = "node, python or csharp", DefaultValueFactory = _ => "node" };
        var platformOpt = new Option<string>("--platform") { Description = "The system you connect, e.g. \"My shop\"", DefaultValueFactory = _ => "your platform" };
        var cdir = new Option<string?>("--dir") { Description = "Folder to create (default: ./<name>)" };
        var kindOpt = new Option<string>("--kind") { Description = "sales (orders to Ino) or marketing (your marketing tool receives Ino's signed profiles, events, unsubscribes and deletions)", DefaultValueFactory = _ => "sales" };
        var newCmd = new Command("new", "Create a self-hosted connector project: signed client, retries, webhook receiver, offline queue, health check, Dockerfile") { cname, selfHosted, lang, platformOpt, cdir, kindOpt };
        Bind(newCmd, pr =>
        {
            var name = pr.GetValue(cname)!;
            var channel = ctx.Profiles.Load() is { DefaultProfile: { } dp } cfg && cfg.Profiles.TryGetValue(pr.GetValue(profile) ?? dp, out var p) && p.Channel.Length > 0 ? p.Channel : "YOUR_CHANNEL";
            var dir = pr.GetValue(cdir) ?? Path.Combine(ctx.WorkingDir ?? Directory.GetCurrentDirectory(), name);
            var kind = pr.GetValue(kindOpt)!;
            var files = ConnectorScaffold.Create(dir, name, pr.GetValue(lang)!, pr.GetValue(platformOpt)!, channel, kind);
            Write(ctx, pr.GetValue(json), new JsonObject { ["dir"] = dir, ["kind"] = kind, ["files"] = new JsonArray(files.Select(f => (JsonNode)f).ToArray()) }, kind == "marketing"
                ? $"Created {dir} ({files.Count} files): your marketing tool for Ino Loyalty.\nNext: cd {dir}, `ino marketing connect --name <name> --url https://<host>/ino --env-file ma.env` (or a test secret), copy INO_MA_SIGNING_SECRET into .env, {ConnectorScaffold.MarketingRunHint(pr.GetValue(lang)!)};\nthen `ino connector test --kind marketing --url http://localhost:8080/ino --env-file ma.env` and replace {ConnectorScaffold.MarketingToolFile(pr.GetValue(lang)!)} with your tool's API."
                : $"Created {dir} ({files.Count} files).\nNext: cd {dir} && cp .env.example .env (fill in the test key), {ConnectorScaffold.RunHint(pr.GetValue(lang)!)};\nthen run the scenarios and `ino connector check --suite ecommerce`.");
            return Task.CompletedTask;
        });
        // MCP/CLI P1c.2: the contract compliance of a marketing tool, run from here against the tool (local while developing, or its https address).
        var tUrl = new Option<string>("--url") { Description = "The tool's endpoint, e.g. http://localhost:8080/ino", Required = true };
        var tKind = new Option<string>("--kind") { Description = "marketing", Required = true };
        var tEnv = new Option<string?>("--env-file") { Description = "The file `ino marketing connect --env-file` wrote (INO_MA_SIGNING_SECRET, INO_MA_CONNECTION_ID); else the INO_MA_SIGNING_SECRET variable" };
        var tYes = new Option<bool>("--yes") { Description = "Test a tool that is not on this machine (it receives fictional profiles signed with your secret, deleted at the end)" };
        var ctest = new Command("test", "Check that your marketing tool follows Ino's contract: signatures, ping proof, profiles, events, unsubscribes, deletions") { tUrl, tKind, tEnv, tYes };
        Bind(ctest, async pr =>
        {
            if (pr.GetValue(tKind) != "marketing") throw new CliException(ExitCodes.Usage, "BAD_KIND", "Only --kind marketing is tested here; sales connectors use `ino connector check`.");
            var target = MarketingCompliance.ValidateTarget(pr.GetValue(tUrl)!);
            var local = MarketingCompliance.IsLocal(target);
            if (!local && !pr.GetValue(tYes))
                throw new CliException(ExitCodes.Usage, "CONFIRM", $"{target.Host} is not this machine: it would receive fictional profiles signed with your secret (deleted at the end). Check the address and add --yes.");
            var (secret, connectionId) = ReadMarketingSecret(pr.GetValue(tEnv));
            // As the engine's client: no redirects (Ino never follows one), 30 s per call. A tool on this machine is reached only through loopback.
            var handler = ctx.LocalHandler ?? (local ? StandardWebhooks.LoopbackOnlyHandler() : new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false });
            using var http = new HttpClient(handler, disposeHandler: ctx.LocalHandler is null) { Timeout = TimeSpan.FromSeconds(30) };
            var checks = await new MarketingCompliance(http, target, secret, connectionId).RunAsync(ct);
            var lines = checks.Select(c => $"{(c.Passed ? "✓" : "✗")} {c.Code,-4} {c.Title}\n      {c.Evidence}").ToList();
            var passed = checks.All(c => c.Passed);
            lines.Add(passed ? $"All {checks.Count} checks passed." : $"{checks.Count(c => !c.Passed)} of {checks.Count} checks failed.");
            if (local && target.Scheme == "http") lines.Add("Note: tested over local http. Ino itself calls https on a public address only; run `ino marketing verify` after deploying.");
            Write(ctx, pr.GetValue(json), MarketingCompliance.ToJson(checks), string.Join("\n", lines));
            if (!passed) exit = ExitCodes.Business;
        });
        var cstatus = new Command("status", "What the engine knows about your self-hosted connectors: version, last heartbeat, health, 24-hour requests and errors");
        Bind(cstatus, async pr =>
        {
            var resolved = ctx.Profiles.Resolve(pr.GetValue(profile));
            Banner(ctx, resolved);
            var res = await ctx.ClientFactory(resolved.Connection, ctx.Profiles.LockFor(resolved)).GetAsync("connector/status", ct);
            exit = ChannelClient.ExitCodeOf(res);
            if (exit != ExitCodes.Ok || ((res.Body as JsonObject)?["Data"] as JsonObject)?["connectors"] is not JsonArray list) { WriteResponse(ctx, pr.GetValue(json), resolved.Connection, res, "connector.status", exit); return; }
            var rows = list.OfType<JsonObject>().ToList();
            Write(ctx, pr.GetValue(json), new JsonObject { ["connectors"] = list.DeepClone() }, rows.Count == 0
                ? "No self-hosted connector has reported to this channel with this key yet (POST /api/v1/<channel>/connector/heartbeat; the `ino connector new` project does it every 5 minutes)."
                : string.Join("\n", rows.Select(c => $"{c["health"],-9} {c["name"],-24} v{c["version"]}  last seen {c["lastSeen"]}  requests {c["requests24h"]}  errors {c["errors24h"]}  queue {c["queueDepth"]}"
                    + (c["detail"] is { } d ? $"\n          {d}" : ""))));
        });
        // Ino Hub H1b.2 (docs/INO_HUB.md §19-§22): your own connector definitions (local_*), saved to a test tenant through the Management API.
        var package = new Command("package", "Your own connector definitions (runs in Ino): save, list, delete, preview, install (test tenants)");
        var pFile = new Argument<string>("file") { Description = "connector.json (its id starts with local_)" };
        var packageSave = new Command("save", "Save a version of your own connector definition (the same version with other content is refused: raise the version)") { pFile };
        Bind(packageSave, async pr =>
        {
            var (name, prof, _) = ctx.Profiles.ResolveForLogin(pr.GetValue(profile));
            var path = pr.GetValue(pFile)!;
            if (!File.Exists(path)) throw new CliException(ExitCodes.Usage, "NO_FILE", $"{path} does not exist.");
            if (new FileInfo(path).Length > 512 * 1024) throw new CliException(ExitCodes.Usage, "TOO_LARGE", "A connector definition is at most 512 KB.");
            var raw = await File.ReadAllBytesAsync(path, ct);
            var target = InoLogin.TargetOf(prof.BaseUrl);
            JsonNode? data = null;
            // Saving is idempotent on (id, version, content): a dropped connection is simply retried.
            for (var attempt = 1; ; attempt++)
            {
                try { data = await InoLogin.ManageRawAsync(ctx, name, target, "connector-packages", raw, ct); break; }
                catch (Exception e) when (attempt < 3 && (e is HttpRequestException || e is TaskCanceledException && !ct.IsCancellationRequested || e is CliException { Code: "UNAVAILABLE" or "TEMPORARILY_UNAVAILABLE" }))
                { await ctx.Delay(TimeSpan.FromSeconds(2 * attempt), ct); }
            }
            var created = data?["created"]?.GetValue<bool>() == true;
            var result = new JsonObject { ["id"] = data?["id"]?.DeepClone(), ["version"] = data?["version"]?.DeepClone(), ["sha256"] = data?["sha256"]?.DeepClone(),
                ["created"] = created, ["host"] = data?["access"]?["requestHost"]?["defaultHost"]?.DeepClone() };
            result["origin"] = data?["origin"]?.DeepClone();
            Write(ctx, pr.GetValue(json), result, $"{(created ? "Saved" : "Already saved")} {data?["id"]} {data?["version"]} (sha256 {data?["sha256"]?.ToString()[..12]}…). " +
                "It is not installed anywhere yet." + OriginText(data?["origin"]));
        });
        // IL9a: the engine's package rules on a file, without saving it: every rule it breaks, or the summary a person reviews before installing.
        var cFile = new Argument<string>("file") { Description = "connector.json to check (nothing is saved)" };
        var packageCheck = new Command("check", "Check a definition against the engine's package rules without saving it: every problem at once, or what it would do") { cFile };
        Bind(packageCheck, async pr =>
        {
            var (name, prof, _) = ctx.Profiles.ResolveForLogin(pr.GetValue(profile));
            var path = pr.GetValue(cFile)!;
            if (!File.Exists(path)) throw new CliException(ExitCodes.Usage, "NO_FILE", $"{path} does not exist.");
            if (new FileInfo(path).Length > 512 * 1024) throw new CliException(ExitCodes.Usage, "TOO_LARGE", "A connector definition is at most 512 KB.");
            var data = await InoLogin.ManageRawAsync(ctx, name, InoLogin.TargetOf(prof.BaseUrl), "connector-packages/check", await File.ReadAllBytesAsync(path, ct), ct);
            var ok = data?["ok"]?.GetValue<bool>() == true;
            var errors = data?["errors"] is JsonArray e ? e.Select(x => x?.ToString() ?? "").ToList() : [];
            if (pr.GetValue(json)) Write(ctx, true, data?.DeepClone() as JsonObject ?? new JsonObject(), "");
            else if (ok)
            {
                var hosts = data?["access"]?["requestHost"]?["defaultHost"]?.ToString();
                ctx.Out.WriteLine($"{data?["id"]} {data?["version"]} ({data?["category"]}) passes the engine's package rules." + (hosts is { Length: > 0 } ? $" Calls {hosts}." : ""));
                ctx.Out.WriteLine($"Access hash {data?["accessHash"]}. Save it with: ino connector package save {path}");
            }
            else ctx.Out.WriteLine($"{errors.Count} problem{(errors.Count == 1 ? "" : "s")}:\n" + string.Join("\n", errors.Select(x => "  - " + x)));
            if (!ok) throw new CliException(ExitCodes.Business, "INVALID_DEFINITION", "The definition does not pass the engine's package rules; fix the problems listed and check again.");
        });
        // IL9b-2: what the engine found about a package's origin (an unchanged copy of a Hub definition) and a Hub withdrawal it has seen.
        static string OriginText(JsonNode? o) => o is not JsonObject origin ? ""
            : $"\n    unchanged copy of Hub {origin["hubId"]} {origin["version"]}{(origin["signedByTryino"]?.GetValue<bool>() == false ? " (TEST catalog)" : "")}"
              + (origin["withdrawn"]?.GetValue<bool>() == true ? $"\n    WITHDRAWN from the Hub: {origin["withdrawnReason"]} (it keeps running; review it and replace it)" : "")
              + (origin["hubLatestVersion"] is { } latest ? $"\n    the Hub has version {latest}" : "");
        var packageList = new Command("list", "Your own connector definitions in this tenant, with versions and how many installations use each");
        Bind(packageList, async pr =>
        {
            var (name, prof, _) = ctx.Profiles.ResolveForLogin(pr.GetValue(profile));
            var data = await InoLogin.ManageAsync(ctx, name, InoLogin.TargetOf(prof.BaseUrl), HttpMethod.Get, "connector-packages", null, null, ct);
            var rows = data?["packages"]?.AsArray() ?? [];
            var lines = rows.OfType<JsonObject>().Select(r => $"{r["id"],-30} {r["version"],-10} {r["status"],-12} {r["installations"]} installation(s)  {r["bytes"]} bytes{OriginText(r["origin"])}");
            Write(ctx, pr.GetValue(json), data ?? new JsonObject(), rows.Count == 0 ? "No own connectors saved yet. Save one with `ino connector package save connector.json`." : string.Join("\n", lines));
        });
        var pId = new Argument<string>("id") { Description = "The connector id (local_…)" };
        var pVersion = new Argument<string>("version") { Description = "The version to delete" };
        var pYes = new Option<bool>("--yes", "-y") { Description = "Delete without asking (a deleted version can never be saved again)" };
        var packageDelete = new Command("delete", "Delete a version no installation uses (it can never be saved again with that number)") { pId, pVersion, pYes };
        Bind(packageDelete, async pr =>
        {
            var (name, prof, _) = ctx.Profiles.ResolveForLogin(pr.GetValue(profile));
            var (id, version) = (pr.GetValue(pId)!, pr.GetValue(pVersion)!);
            if (!pr.GetValue(pYes))
                throw new CliException(ExitCodes.Usage, "CONFIRM", $"This deletes {id} {version}; that version number can never be saved again. Run again with --yes to do it.");
            var data = await InoLogin.ManageAsync(ctx, name, InoLogin.TargetOf(prof.BaseUrl), HttpMethod.Post,
                $"connector-packages/{Uri.EscapeDataString(id)}/{Uri.EscapeDataString(version)}/delete", new JsonObject(), null, ct);
            Write(ctx, pr.GetValue(json), data ?? new JsonObject(), data?["alreadyDeleted"]?.GetValue<bool>() == true ? $"{id} {version} was already deleted." : $"Deleted {id} {version}.");
        });
        // Ino Hub H1b.1d (docs/INO_HUB.md §25): preview what an installation of your own connector would touch, then install it. Needs the
        // connector:install scope and the Power Apps "Ino Loyalty - Connector Installer" role (test tenants).
        var piSet = new Option<string[]>("--set") { Description = "A setting: key=value (repeatable; true/false is yes/no, [\"A\",\"B\"] is a list)", AllowMultipleArgumentsPerToken = false };
        var piCredential = new Option<Guid?>("--credential") { Description = "Credential id for the connector's calls (Power Apps → Integrations → Credentials)" };
        var piName = new Option<string?>("--name") { Description = "Installation name (a second installation of the same version needs another name)" };
        JsonObject InstallBody(ParseResult pr)
        {
            var values = new JsonObject();
            foreach (var item in pr.GetValue(piSet) ?? [])
            {
                var eq = item.IndexOf('=');
                if (eq <= 0) throw new CliException(ExitCodes.Usage, "BAD_SETTING", $"'{item}': use --set key=value.");
                var (key, text) = (item[..eq].Trim(), item[(eq + 1)..]);
                JsonNode? value = text switch
                {
                    "true" => true,
                    "false" => false,
                    _ when text.TrimStart().StartsWith('[') => ParseList(key, text),
                    _ => text,
                };
                values[key] = value;
            }
            var body = new JsonObject { ["values"] = values };

            static JsonNode? ParseList(string key, string text)
            {
                try { return JsonNode.Parse(text); }
                catch (JsonException) { throw new CliException(ExitCodes.Usage, "BAD_SETTING", $"'{key}': a list is JSON, e.g. --set {key}='[\"A\",\"B\"]'."); }
            }
            if (pr.GetValue(piCredential) is { } credential) body["credentialId"] = credential.ToString();
            if (pr.GetValue(piName) is { } n) body["name"] = n;
            return body;
        }
        static string AccessText(JsonNode? d)
        {
            var a = d?["access"];
            var lines = new List<string> { $"{d?["id"]} {d?["version"]} (sha256 {d?["sha256"]?.ToString()[..Math.Min(12, d?["sha256"]?.ToString().Length ?? 0)]}…)", $"  calls host      {d?["host"]}" };
            static IEnumerable<string> Items(JsonNode? n) => n?.AsArray().Select(x => x?.ToString() ?? "") ?? [];
            if (a?["events"]?.AsArray() is { Count: > 0 } events) lines.Add($"  receives        {string.Join(", ", Items(events))}");
            foreach (var x in a?["actions"]?.AsArray() ?? []) lines.Add($"  sends           {x?["method"]} {x?["path"]} on {string.Join(", ", Items(x?["events"]))}");
            foreach (var x in a?["writes"]?.AsArray() ?? [])
                lines.Add($"  writes          {x?["operation"]}{(x?["financial"]?.GetValue<bool>() == true ? " (points or money)" : "")}{(x?["profile"]?.GetValue<bool>() == true ? " (member profile)" : "")}");
            if (a?["reads"]?.AsArray() is { Count: > 0 } reads) lines.Add($"  reads           {string.Join(", ", Items(reads))}");
            foreach (var x in a?["memberMatch"]?.AsArray() ?? []) lines.Add($"  finds members   by {string.Join(", ", Items(x?["by"]))} (if missing: {x?["ifMissing"]})");
            if (a?["webhooks"]?.GetValue<bool>() == true) lines.Add("  inbound         webhooks from the platform");
            if (a?["pollMinutes"] is { } poll) lines.Add($"  inbound         polls every {poll} minutes");
            if (d?["channels"]?.AsArray() is { Count: > 0 } ch) lines.Add($"  channels        {string.Join(", ", Items(ch))}");
            lines.Add($"  access hash     {d?["accessHash"]}");
            return string.Join("\n", lines);
        }
        string PackagePath(string id, string version, string verb) => $"connector-packages/{Uri.EscapeDataString(id)}/{Uri.EscapeDataString(version)}/{verb}";

        var piId = new Argument<string>("id") { Description = "The connector id (local_…)" };
        var piVersion = new Argument<string>("version") { Description = "The saved version" };
        var packagePreview = new Command("preview", "What installing a saved version would touch, with your settings (nothing is installed)") { piId, piVersion, piSet, piCredential, piName };
        Bind(packagePreview, async pr =>
        {
            var (name, prof, _) = ctx.Profiles.ResolveForLogin(pr.GetValue(profile));
            var data = await InoLogin.ManageAsync(ctx, name, InoLogin.TargetOf(prof.BaseUrl), HttpMethod.Post, PackagePath(pr.GetValue(piId)!, pr.GetValue(piVersion)!, "preview"),
                InstallBody(pr), null, ct);
            Write(ctx, pr.GetValue(json), data ?? new JsonObject(), AccessText(data) + $"\nInstall it with `ino connector package install {pr.GetValue(piId)} {pr.GetValue(piVersion)} … --yes`.");
        });
        var piYes = new Option<bool>("--yes", "-y") { Description = "Install after showing what it touches (without it, only the summary is shown)" };
        var packageInstall = new Command("install", "Install a saved version of your own connector on this test tenant (shows what it touches; --yes installs)")
            { piId, piVersion, piSet, piCredential, piName, piYes };
        Bind(packageInstall, async pr =>
        {
            var (name, prof, _) = ctx.Profiles.ResolveForLogin(pr.GetValue(profile));
            var target = InoLogin.TargetOf(prof.BaseUrl);
            var (id, version) = (pr.GetValue(piId)!, pr.GetValue(piVersion)!);
            var body = InstallBody(pr);
            var preview = await InoLogin.ManageAsync(ctx, name, target, HttpMethod.Post, PackagePath(id, version, "preview"), body.DeepClone(), null, ct);
            var summary = AccessText(preview);
            if (!pr.GetValue(piYes))
                throw new CliException(ExitCodes.Usage, "CONFIRM", summary + "\nNothing was installed. Run again with --yes to install it with exactly this access.");
            body["accessHash"] = preview?["accessHash"]?.DeepClone() ?? throw new CliException(ExitCodes.Business, "NO_PREVIEW", "Ino returned no access summary.");
            // §25 M5: the same request always has the same Idempotency-Key, so running the command again (after a crash or a dropped connection)
            // returns the first installation instead of making a second one.
            var idempotency = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
                $"ino-package-install|{target.Tenant}|{id}|{version}|{body.ToJsonString()}")));
            JsonNode? data = null;
            for (var attempt = 1; ; attempt++)
            {
                try { data = await InoLogin.ManageAsync(ctx, name, target, HttpMethod.Post, PackagePath(id, version, "install"), body, idempotency, ct); break; }
                catch (Exception e) when (attempt < 3 && (e is HttpRequestException || e is TaskCanceledException && !ct.IsCancellationRequested
                    || e is CliException { Code: "UNAVAILABLE" or "TEMPORARILY_UNAVAILABLE" or "IN_PROGRESS" }))
                { await ctx.Delay(TimeSpan.FromSeconds(2 * attempt), ct); }
            }
            var created = data?["created"]?.GetValue<bool>() == true && data?["replayed"]?.GetValue<bool>() != true;
            Write(ctx, pr.GetValue(json), data ?? new JsonObject(), summary + "\n" + (created
                ? $"Installed {id} {version} as {data?["id"]}."
                : $"Already installed: {data?["id"]} (the same request was installed before)."));
        });
        package.Subcommands.Add(packageSave);
        package.Subcommands.Add(packageList);
        package.Subcommands.Add(packageDelete);
        package.Subcommands.Add(packagePreview);
        package.Subcommands.Add(packageInstall);
        package.Subcommands.Add(packageCheck);
        // IL9a: a package skeleton of each kind (marketing tool, e-commerce platform, POS file format) that passes the package rules as it is.
        var cdDir = new Argument<string>("folder") { Description = "A new folder for the package (connector.json and RULES.md)" };
        var cdId = new Option<string>("--id") { Description = "The package id, local_<name> (lower case, digits and -)", Required = true };
        var cdKind = new Option<string>("--kind") { Description = "marketing (a marketing tool), ecommerce (a shop's orders and refunds), pos (a POS file format)", Required = true };
        cdKind.AcceptOnlyFromAmong(DefinitionScaffold.Kinds);
        var cdNew = new Command("new", "Write a definition package skeleton that passes the engine's package rules; edit it, then `ino connector package check`") { cdDir, cdId, cdKind };
        Bind(cdNew, pr =>
        {
            var dir = pr.GetValue(cdDir)!;
            var files = DefinitionScaffold.Create(dir, pr.GetValue(cdId)!, pr.GetValue(cdKind)!);
            Write(ctx, pr.GetValue(json), new JsonObject { ["dir"] = dir, ["kind"] = pr.GetValue(cdKind), ["files"] = new JsonArray(files.Select(f => (JsonNode)f).ToArray()) },
                $"Created {dir} ({files.Count} files). RULES.md lists what a {pr.GetValue(cdKind)} package may do. Edit connector.json, then:\n" +
                $"  ino connector package check {Path.Combine(dir, "connector.json")}\n  ino connector package save {Path.Combine(dir, "connector.json")}");
            return Task.CompletedTask;
        });
        var cdefinition = new Command("definition", "Definition packages: connectors described by a file the engine runs (no code to host)");
        cdefinition.Subcommands.Add(cdNew);
        connector.Subcommands.Add(cdefinition);
        connector.Subcommands.Add(package);
        connector.Subcommands.Add(check);
        connector.Subcommands.Add(newCmd);
        connector.Subcommands.Add(cstatus);
        connector.Subcommands.Add(ctest);
        root.Subcommands.Add(connector);

        // ---------------------------------------------------------------- login (MCP/CLI P0c.1c: Ino Auth, a person instead of a channel key)
        JsonObject Who(InoLogin.Target target, InoLogin.Session s) => new()
        {
            ["tenant"] = s.Claims["ino_tenant"]?.DeepClone() ?? target.Tenant, ["environment"] = s.Claims["ino_env"]?.DeepClone(),
            ["name"] = s.Claims["name"]?.DeepClone(), ["objectId"] = s.Claims["oid"]?.DeepClone(), ["scopes"] = s.Claims["scope"]?.DeepClone(),
            ["expires"] = s.Claims["exp"] is { } exp ? DateTimeOffset.FromUnixTimeSeconds(exp.GetValue<long>()).ToString("u") : null,
        };
        string Human(JsonObject w) => $"{w["name"]} · tenant {w["tenant"]} ({w["environment"]}) · scopes: {w["scopes"]}";
        var loginScopes = new Option<string>("--scopes") { Description = "Scopes to ask for (space separated)", DefaultValueFactory = _ => InoLogin.DefaultScopes };
        var loginBase = new Option<string?>("--base-url") { Description = "Ino Cloud address, e.g. https://api.inoloyalty.com/t/<tenant>: makes a sign-in-only profile (no channel key) named after the tenant or --profile" };
        var login = new Command("login", "Sign in as yourself (Microsoft account of the tenant) for the profile's tenant; the refresh token stays in the keychain") { loginScopes, loginBase };
        Bind(login, async pr =>
        {
            var address = pr.GetValue(loginBase);
            // Only the flags name a new profile: INO_PROFILE and a repository's .ino/config.json never choose or create one here (M3).
            var name = address is not null ? pr.GetValue(profile) ?? InoLogin.TargetOf(address).Tenant : ctx.Profiles.ResolveForLogin(pr.GetValue(profile)).Name;
            if (address is not null)
                try { address = ctx.Profiles.CheckLoginAddress(name, address); }
                catch (CliException e) when (e.Code == "BAD_PROFILE_NAME" && pr.GetValue(profile) is null)
                {
                    throw new CliException(ExitCodes.Usage, "BAD_PROFILE_NAME", $"The tenant code {name} cannot be a profile name; pass --profile <name> (letters, digits, - and _).");
                }
            var target = InoLogin.TargetOf(address ?? ctx.Profiles.ResolveForLogin(name).Profile.BaseUrl);
            ctx.Profiles.ProbeLoginStore(name);
            var s = await InoLogin.LoginAsync(ctx, target, pr.GetValue(loginScopes)!, ct);
            var created = false;
            try
            {
                if (address is not null) created = ctx.Profiles.EnsureLoginOnly(name, address);
                await InoLogin.SaveAsync(ctx, name, target, s, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                await InoLogin.RevokeAsync(ctx, target, s.RefreshToken, CancellationToken.None);   // not kept anywhere: end it at Ino
                throw;
            }
            var w = Who(target, s);
            Write(ctx, pr.GetValue(json), w, $"Signed in: {Human(w)}" + (created ? $"\nSaved profile {name} (sign-in only; the refresh token is in the keychain)." : ""));
        });
        var loginStatus = new Command("status", "Who you are signed in as for this profile (refreshes the sign-in)");
        Bind(loginStatus, async pr =>
        {
            var (name, prof, _) = ctx.Profiles.ResolveForLogin(pr.GetValue(profile));
            var target = InoLogin.TargetOf(prof.BaseUrl);
            var s = await InoLogin.RefreshAsync(ctx, name, target, ct) ?? throw new CliException(ExitCodes.Auth, "NOT_SIGNED_IN", "Not signed in for this profile. Run `ino login`.");
            var w = Who(target, s);
            Write(ctx, pr.GetValue(json), w, "Signed in: " + Human(w));
        });
        login.Subcommands.Add(loginStatus);
        root.Subcommands.Add(login);
        var logoutAll = new Option<bool>("--all") { Description = "End every session you have in this tenant (other machines and AI agents too)" };
        var logout = new Command("logout", "End your sign-in for this profile (ended at Ino, removed from the keychain)") { logoutAll };
        Bind(logout, async pr =>
        {
            var (name, prof, _) = ctx.Profiles.ResolveForLogin(pr.GetValue(profile));
            var all = pr.GetValue(logoutAll);
            var r = await InoLogin.LogoutAsync(ctx, name, InoLogin.TargetOf(prof.BaseUrl), all, ct);
            var text = !r.WasSignedIn ? "Not signed in for this profile."
                : all ? $"Signed out. {r.Ended ?? 0} session(s) of yours in this tenant ended; access stops within a minute."
                : r.Note is { } note ? "Signed out here. " + note : "Signed out.";
            Write(ctx, pr.GetValue(json), new JsonObject { ["signedOut"] = r.WasSignedIn, ["ended"] = r.Ended, ["note"] = r.Note }, text);
        });
        root.Subcommands.Add(logout);
        var sessions = new Command("sessions", "Your Ino sign-ins in this tenant (this and other machines, AI agents); end them with `ino logout --all`");
        Bind(sessions, async pr =>
        {
            var (name, prof, _) = ctx.Profiles.ResolveForLogin(pr.GetValue(profile));
            var list = await InoLogin.SessionsAsync(ctx, name, InoLogin.TargetOf(prof.BaseUrl), ct);
            static string D(JsonNode? n) => n?.GetValue<DateTimeOffset>().ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture) ?? "-";
            var lines = list.Select(n => $"{(n?["current"]?.GetValue<bool>() == true ? "* " : "  ")}{n?["id"]}  started {D(n?["started"])}  last refresh {D(n?["lastRefresh"])}  " +
                $"expires if unused {D(n?["expiresIfUnused"])}  {string.Join(" ", n?["scopes"]?.AsArray().Select(x => x?.GetValue<string>()) ?? [])}");
            Write(ctx, pr.GetValue(json), new JsonObject { ["sessions"] = list.DeepClone() },
                list.Count == 0 ? "No open sessions." : string.Join("\n", lines) + "\n(* this profile)");
        });
        root.Subcommands.Add(sessions);

        // ---------------------------------------------------------------- channels / keys (MCP/CLI P0c.3: Management API as the signed-in person)
        var channelsCmd = new Command("channels", "Channels of this tenant (as you see them in Power Apps → Developers); needs `ino login`");
        var channelsList = new Command("list", "List channels: mapping, allowed operations, active keys, traffic in the last 24 hours");
        Bind(channelsList, async pr =>
        {
            var (name, prof, _) = ctx.Profiles.ResolveForLogin(pr.GetValue(profile));
            var data = await InoLogin.ManageAsync(ctx, name, InoLogin.TargetOf(prof.BaseUrl), HttpMethod.Get, "channels", null, null, ct);
            var rows = data?["channels"]?.AsArray() ?? [];
            Write(ctx, pr.GetValue(json), new JsonObject { ["channels"] = rows.DeepClone() }, rows.Count == 0 ? "No channels." : string.Join("\n", rows.Select(c =>
                $"{c?["code"],-18} {(c?["hasMapping"]?.GetValue<bool>() == true ? "mapped  " : "no map  ")} keys {c?["activeKeys"],-3} requests 24h {c?["requests24h"]} (failed {c?["failed24h"]})")));
        });
        channelsCmd.Subcommands.Add(channelsList);
        root.Subcommands.Add(channelsCmd);

        // ---------------------------------------------------------------- logs (MC1, docs/MCP_CLI.md §4: `ino logs tail`)
        var logsCmd = new Command("logs", "What the engine saw: requests to this tenant's channel API (no member data, no bodies); needs `ino login`");
        var lgChannel = new Option<string?>("--channel") { Description = "Only this channel" };
        var lgOutcome = new Option<string?>("--outcome") { Description = "failed, success, auth, rate_limited, client_error or server_error" };
        var lgOperation = new Option<string?>("--operation") { Description = "Only this operation, e.g. transaction.create" };
        var lgSearch = new Option<string?>("--search") { Description = "A request id or correlation id" };
        var lgLimit = new Option<int>("--limit") { Description = "How many of the newest requests (1-50)", DefaultValueFactory = _ => 20 };
        var lgFollow = new Option<bool>("--follow", "-f") { Description = "Keep showing new requests until Ctrl-C" };
        var lgInterval = new Option<int>("--interval") { Description = "With --follow: seconds between reads (2-60)", DefaultValueFactory = _ => 5 };
        var logsTail = new Command("tail", "The newest requests, newest last; --follow keeps watching") { lgChannel, lgOutcome, lgOperation, lgSearch, lgLimit, lgFollow, lgInterval };
        Bind(logsTail, async pr =>
        {
            var (name, prof, _) = ctx.Profiles.ResolveForLogin(pr.GetValue(profile));
            var target = InoLogin.TargetOf(prof.BaseUrl);
            var limit = Math.Clamp(pr.GetValue(lgLimit), 1, 50);
            var query = string.Join("&", new (string K, string? V)[] { ("channel", pr.GetValue(lgChannel)), ("outcome", pr.GetValue(lgOutcome)), ("operation", pr.GetValue(lgOperation)),
                ("search", pr.GetValue(lgSearch)), ("limit", limit.ToString(System.Globalization.CultureInfo.InvariantCulture)) }
                .Where(x => !string.IsNullOrWhiteSpace(x.V)).Select(x => $"{x.K}={Uri.EscapeDataString(x.V!.Trim())}"));
            async Task<List<JsonObject>> Read() =>
                ((await InoLogin.ManageAsync(ctx, name, target, HttpMethod.Get, "request-log?" + query, null, null, ct))?["requests"]?.AsArray() ?? [])
                .OfType<JsonObject>().Reverse().ToList();   // oldest first, like a log
            static string Line(JsonObject r) =>
                $"{(DateTimeOffset.TryParse(r["createdAt"]?.ToString(), out var at) ? at.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) : "")} "
                + $"{Safe(r["httpStatus"]),3} {Safe(r["channel"]),-16} {Safe(r["operation"] ?? r["path"]),-28} {Safe(r["durationMs"]),5} ms  {Safe(r["resultCode"])}"
                + (r["requestId"] is { } rid ? $"  request {Safe(rid)}" : "") + (r["error"] is { } err ? $"\n      {Safe(err)}" : "");
            var rows = await Read();
            if (pr.GetValue(json) && !pr.GetValue(lgFollow)) { Write(ctx, true, new JsonObject { ["requests"] = new JsonArray(rows.Select(r => (JsonNode)r.DeepClone()).ToArray()) }, ""); return; }
            if (rows.Count == 0 && !pr.GetValue(lgFollow)) { ctx.Out.WriteLine("No requests match."); return; }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            void Show(IEnumerable<JsonObject> batch)
            {
                foreach (var r in batch.Where(r => seen.Add(r["id"]?.ToString() ?? "")))
                    ctx.Out.WriteLine(pr.GetValue(json) ? r.ToJsonString() : Line(r));   // --json --follow: one JSON object per line
            }
            Show(rows);
            if (!pr.GetValue(lgFollow)) return;
            var wait = TimeSpan.FromSeconds(Math.Clamp(pr.GetValue(lgInterval), 2, 60));
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await ctx.Delay(wait, ct);
                    var batch = await Read();
                    // Review MC1 2: a whole window of new requests means some came and went between two reads; say so.
                    if (batch.Count >= limit && batch.All(r => !seen.Contains(r["id"]?.ToString() ?? "")) && seen.Count > 0)
                        ctx.Err.WriteLine($"… more than {limit} requests since the last read; older ones were skipped (use --limit 50 or a filter).");
                    Show(batch);
                    if (seen.Count > 5000) { seen.Clear(); foreach (var r in batch) seen.Add(r["id"]?.ToString() ?? ""); }
                }
                // Review MC1 3: Ctrl-C ends with 0 wherever it lands; a passing network or server error does not end the watch.
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (CliException e) when (e.ExitCode is ExitCodes.Network or ExitCodes.RateLimited || e.Code is "UNAVAILABLE" or "TEMPORARILY_UNAVAILABLE")
                { ctx.Err.WriteLine($"({e.Message} Trying again.)"); }
                catch (HttpRequestException e) { ctx.Err.WriteLine($"({e.Message} Trying again.)"); }
            }
        });
        logsCmd.Subcommands.Add(logsTail);
        root.Subcommands.Add(logsCmd);

        // ---------------------------------------------------------------- setup (P1.1, docs/MCP_CLI_P1_LOG.md): the firm's setup checklist, read only
        var setupCmd = new Command("setup", "What is set up in this firm and what to do next (program, tiers, catalog, channels, webhooks, campaigns); "
            + "read only, needs `ino login`. To set up your AI client instead, use `ino agent setup`");
        Bind(setupCmd, async pr =>
        {
            var (name, prof, _) = ctx.Profiles.ResolveForLogin(pr.GetValue(profile));
            var data = await InoLogin.ManageAsync(ctx, name, InoLogin.TargetOf(prof.BaseUrl), HttpMethod.Get, "setup", null, null, ct) as JsonObject
                ?? throw new CliException(ExitCodes.Network, "UNEXPECTED", "The engine sent no checklist.");
            if (pr.GetValue(json)) { Write(ctx, true, data.DeepClone(), ""); return; }
            ctx.Out.WriteLine(SetupText(data));
        });
        root.Subcommands.Add(setupCmd);

        // ---------------------------------------------------------------- init (P1.2): the firm's setup as files, no secrets
        var initDir = new Argument<string>("folder") { Description = "Where to write the setup files (default: ./ino-setup)", DefaultValueFactory = _ => "ino-setup" };
        var initForce = new Option<bool>("--force") { Description = "Replace the setup files in a folder that is not empty (other files are kept)" };
        var initCmd = new Command("init", "Write this firm's setup (program, tiers, channel settings) to a folder you can keep in git; no secrets; needs `ino login`")
            { initDir, initForce };
        Bind(initCmd, async pr =>
        {
            var (name, prof, _) = ctx.Profiles.ResolveForLogin(pr.GetValue(profile));
            var export = await InoLogin.ManageAsync(ctx, name, InoLogin.TargetOf(prof.BaseUrl), HttpMethod.Get, "config", null, null, ct) as JsonObject
                ?? throw new CliException(ExitCodes.Network, "UNEXPECTED", "The engine sent no setup.");
            var dir = pr.GetValue(initDir)!;
            var written = SetupFolder.Write(dir, export, pr.GetValue(initForce));
            var skipped = (export["sections"] as JsonObject ?? []).Where(kv => !(kv.Value?["exported"] is JsonValue ev && ev.TryGetValue<bool>(out var on) && on))
                .Select(kv => new JsonObject { ["section"] = kv.Key, ["reason"] = kv.Value?["reason"]?.DeepClone() }).ToList();
            Write(ctx, pr.GetValue(json), new JsonObject
            {
                ["folder"] = Path.GetFullPath(dir), ["files"] = new JsonArray(written.Select(f => (JsonNode)f).ToArray()),
                ["notExported"] = new JsonArray(skipped.ToArray()), ["warnings"] = export["warnings"]?.DeepClone(),
            }, $"Wrote {written.Count} file(s) to {dir}:\n" + string.Join("\n", written.Select(f => "  " + f))
               + (skipped.Count == 0 ? "" : "\nNot exported:\n" + string.Join("\n", skipped.Select(x => $"  {Safe(x["section"])}: {Safe(x["reason"])}")))
               + (export["warnings"] is JsonArray { Count: > 0 } w ? "\nWarnings:\n" + string.Join("\n", w.Select(x => "  " + Safe(x))) : ""));
        });
        root.Subcommands.Add(initCmd);

        // ---------------------------------------------------------------- plan (P1.3): the difference between a setup folder and a firm; writes nothing
        var planDir = new Argument<string>("folder") { Description = "The setup folder (default: ./ino-setup)", DefaultValueFactory = _ => "ino-setup" };
        var planOther = new Option<bool>("--other-firm") { Description = "Compare a folder written from another firm (for example test → live)" };
        var planEnv = new Option<bool>("--include-environment") { Description = "Count fields that differ by environment (program status, URLs, SMS sender) as changes" };
        var planCmd = new Command("plan", "Show what differs between a setup folder and this firm; changes nothing; needs `ino login`") { planDir, planOther, planEnv };
        Bind(planCmd, async pr =>
        {
            var (name, prof, _) = ctx.Profiles.ResolveForLogin(pr.GetValue(profile));
            var dir = pr.GetValue(planDir)!;
            if (!Directory.Exists(dir)) throw new CliException(ExitCodes.Usage, "NOT_A_SETUP_FOLDER", $"{dir} is not a folder. Run `ino init {dir}` first.");
            var target = await InoLogin.ManageAsync(ctx, name, InoLogin.TargetOf(prof.BaseUrl), HttpMethod.Get, "config", null, null, ct) as JsonObject
                ?? throw new CliException(ExitCodes.Network, "UNEXPECTED", "The engine sent no setup.");
            var plan = SetupPlan.Build(dir, target, pr.GetValue(planOther), pr.GetValue(planEnv));
            JsonObject Row(SetupPlan.Change c) => new()
                { ["section"] = c.Section, ["key"] = c.Key, ["field"] = c.Field, ["action"] = c.Action, ["from"] = c.From, ["to"] = c.To };
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Plan: folder from {plan.Source} → {plan.Target}");
            if (target["environment"]?.ToString() == "live") sb.AppendLine("! The target is a LIVE firm.");
            static string Line(SetupPlan.Change c) => c.Action switch
            {
                "change" => $"  ~ {(c.Key.Length > 0 ? Safe(c.Key) + "." : "")}{Safe(c.Field)}: {c.From} → {c.To}",
                "add" => $"  + {Safe(c.Key)}",
                "remove" => $"  - {Safe(c.Key)}",
                "reactivate" => $"  ↻ {Safe(c.Key)} (inactive in the firm: reactivate)",
                _ => $"  {c.Action} {Safe(c.Key)}",
            };
            foreach (var group in plan.Changes.GroupBy(c => c.Section))
            {
                sb.AppendLine(group.Key + ":");
                foreach (var c in group) sb.AppendLine(Line(c));
            }
            if (plan.ByEnvironment.Count > 0)
            {
                sb.AppendLine("Differs by environment (not counted; --include-environment to count):");
                foreach (var c in plan.ByEnvironment) sb.AppendLine(Line(c));
            }
            foreach (var (section, state) in plan.Sections) sb.AppendLine($"  {Safe(section)}: {Safe(state)}");
            foreach (var w in plan.Warnings) sb.AppendLine("! " + Safe(w));
            sb.Append(plan.Changes.Count == 0 ? "No changes." : $"{plan.Changes.Count} change(s). Nothing was changed.");
            Write(ctx, pr.GetValue(json), new JsonObject
            {
                ["source"] = plan.Source, ["target"] = plan.Target,
                ["sections"] = new JsonObject(plan.Sections.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)JsonValue.Create(kv.Value)))),
                ["changes"] = new JsonArray(plan.Changes.Select(c => (JsonNode)Row(c)).ToArray()),
                ["byEnvironment"] = new JsonArray(plan.ByEnvironment.Select(c => (JsonNode)Row(c)).ToArray()),
                ["warnings"] = new JsonArray(plan.Warnings.Select(w => (JsonNode)w).ToArray()),
            }, sb.ToString());
        });
        root.Subcommands.Add(planCmd);

        // ---------------------------------------------------------------- pos file-format (MC1, docs/INTEGRATION_LAYER.md §10.4)
        var posCmd = new Command("pos", "Point of sale: the file format a channel's POS files are read with; needs `ino login`");
        var ffCmd = new Command("file-format", "Which file format each channel's POS files are read with (default: Ino POS file v1)");
        var ffList = new Command("list", "Every channel's pinned file format");
        Bind(ffList, async pr =>
        {
            var (name, prof, _) = ctx.Profiles.ResolveForLogin(pr.GetValue(profile));
            var data = await InoLogin.ManageAsync(ctx, name, InoLogin.TargetOf(prof.BaseUrl), HttpMethod.Get, "channels/file-formats", null, null, ct);
            var rows = data?["fileFormats"]?.AsArray() ?? [];
            Write(ctx, pr.GetValue(json), data as JsonObject ?? new JsonObject(), rows.Count == 0 ? "No channel has a pinned format: every channel reads Ino POS file v1." : string.Join("\n", rows.Select(f =>
                $"{f?["channel"],-18} {f?["fileFormat"],-32} {(f?["allowV1"]?.GetValue<bool>() == true ? "v1 too  " : "        ")}pinned {f?["pinnedAt"]}{(f?["channelConfigured"]?.GetValue<bool>() == false ? "  (channel no longer configured)" : "")}")));
        });
        var ffChannel = new Argument<string>("channel") { Description = "The channel code, e.g. STORE_TILLS" };
        var ffDefinition = new Option<string>("--definition") { Description = "Your saved POS file package (local_…)", Required = true };
        var ffVersion = new Option<string>("--version") { Description = "Its version", Required = true };
        var ffAllowV1 = new Option<bool>("--allow-v1") { Description = "Also accept Ino POS file v1 files (during a vendor change)" };
        var ffConfirm = new Option<string?>("--confirm") { Description = "The review hash the summary printed: pins the format" };
        var ffSet = new Command("set", "Read a channel's files with your saved format package: the column summary first, then --confirm") { ffChannel, ffDefinition, ffVersion, ffAllowV1, ffConfirm };
        Bind(ffSet, async pr =>
        {
            var (name, prof, _) = ctx.Profiles.ResolveForLogin(pr.GetValue(profile));
            var target = InoLogin.TargetOf(prof.BaseUrl);
            var channel = Uri.EscapeDataString(pr.GetValue(ffChannel)!.Trim());
            var body = new JsonObject { ["definition"] = pr.GetValue(ffDefinition), ["version"] = pr.GetValue(ffVersion) };
            var preview = await InoLogin.ManageAsync(ctx, name, target, HttpMethod.Post, $"channels/{channel}/file-format/preview", body.DeepClone(), null, ct);
            var reviewHash = preview?["reviewHash"]?.ToString() ?? throw new CliException(ExitCodes.Business, "NO_REVIEW", "Ino returned no review hash; update the engine.");
            if (pr.GetValue(ffConfirm) != reviewHash)
            {
                if (pr.GetValue(json)) Write(ctx, true, new JsonObject { ["preview"] = preview?.DeepClone(), ["confirmed"] = false }, "");
                else ctx.Out.WriteLine(PosFileText(preview));
                throw new CliException(ExitCodes.Usage, "CONFIRM", pr.GetValue(ffConfirm) is null
                    ? $"Review the columns above. To pin the format, run the same command with --confirm {reviewHash}"
                    : $"The format, or the channel's current pin, changed since that review. Review it above; to pin, use --confirm {reviewHash}");
            }
            body["reviewHash"] = reviewHash;
            body["allowV1"] = pr.GetValue(ffAllowV1);
            var data = await InoLogin.ManageAsync(ctx, name, target, HttpMethod.Put, $"channels/{channel}/file-format", body, null, ct);
            Write(ctx, pr.GetValue(json), data as JsonObject ?? new JsonObject(),
                $"{pr.GetValue(ffChannel)} files are read with {data?["fileFormat"]}{(pr.GetValue(ffAllowV1) ? " (v1 files too)" : "")}" + (data?["previous"] is { } prev ? $"; was {prev}." : "."));
        });
        var ffYes = new Option<bool>("--yes", "-y") { Description = "Go back to v1 without asking" };
        var ffClear = new Command("clear", "Read a channel's files as Ino POS file v1 again") { ffChannel, ffYes };
        Bind(ffClear, async pr =>
        {
            var (name, prof, _) = ctx.Profiles.ResolveForLogin(pr.GetValue(profile));
            if (!pr.GetValue(ffYes)) throw new CliException(ExitCodes.Usage, "CONFIRM", $"Run again with --yes to read {pr.GetValue(ffChannel)} files as Ino POS file v1 again.");
            var data = await InoLogin.ManageAsync(ctx, name, InoLogin.TargetOf(prof.BaseUrl), HttpMethod.Delete, $"channels/{Uri.EscapeDataString(pr.GetValue(ffChannel)!.Trim())}/file-format", null, null, ct);
            Write(ctx, pr.GetValue(json), data as JsonObject ?? new JsonObject(), data?["cleared"]?.GetValue<bool>() == true
                ? $"{pr.GetValue(ffChannel)} files are read as Ino POS file v1 again (was {data?["previous"]})." : $"{pr.GetValue(ffChannel)} had no pinned format.");
        });
        static string PosFileText(JsonNode? d)
        {
            static IEnumerable<string> Items(JsonNode? n) => n is JsonArray arr ? arr.Select(x => x?.ToString() ?? "") : [];
            var a = d?["access"];
            var personal = Items(a?["personalData"] ?? a?["PersonalData"]).ToHashSet(StringComparer.Ordinal);
            var lines = new List<string> { $"{d?["definition"]} {d?["version"]} for channel {d?["channel"]}{(d?["current"] is { } cur ? $" (now {cur})" : " (now Ino POS file v1)")}", "  Ino field = column in the vendor's file" };
            foreach (var c in Items(a?["columns"] ?? a?["Columns"])) lines.Add($"    {c}{(personal.Contains(c.Split(" = ")[0]) ? "   (personal data)" : "")}");
            if (Items(a?["recordTypes"] ?? a?["RecordTypes"]).ToList() is { Count: > 0 } rt) lines.Add($"  record types   {string.Join("; ", rt)}");
            if (Items(a?["paymentTypes"] ?? a?["PaymentTypes"]).ToList() is { Count: > 0 } pt) lines.Add($"  payment types  {string.Join("; ", pt)}");
            var scale = (a?["amountScale"] ?? a?["AmountScale"])?.ToString();
            lines.Add($"  amounts        {(scale == "100" ? "in cents (divided by 100)" : "as written")}{((a?["dateFormat"] ?? a?["DateFormat"]) is { } df ? $" · dates {df}" : "")}");
            lines.Add($"  review hash    {d?["reviewHash"]}");
            return string.Join("\n", lines);
        }
        ffCmd.Subcommands.Add(ffList); ffCmd.Subcommands.Add(ffSet); ffCmd.Subcommands.Add(ffClear);
        posCmd.Subcommands.Add(ffCmd);
        root.Subcommands.Add(posCmd);

        var keysCmd = new Command("keys", "API keys of this tenant's channels; needs `ino login` (creating needs the integration:write scope, test tenants)");
        var keysList = new Command("list", "List API keys (prefix, channel, status, last use); secrets are never shown again");
        Bind(keysList, async pr =>
        {
            var (name, prof, _) = ctx.Profiles.ResolveForLogin(pr.GetValue(profile));
            var data = await InoLogin.ManageAsync(ctx, name, InoLogin.TargetOf(prof.BaseUrl), HttpMethod.Get, "api-keys", null, null, ct);
            var rows = data?["keys"]?.AsArray() ?? [];
            Write(ctx, pr.GetValue(json), new JsonObject { ["keys"] = rows.DeepClone() }, rows.Count == 0 ? "No API keys." : string.Join("\n", rows.Select(k =>
                $"{k?["keyPrefix"],-12} {k?["id"],-36} {k?["channel"],-18} {k?["status"],-8} {k?["name"]}  last used {k?["lastUsedAt"]?.ToString() ?? "never"}")));
        });
        var kChannel = new Option<string>("--channel") { Description = "Channel code (see `ino channels list`)", Required = true };
        var kName = new Option<string>("--name") { Description = "What the key is for, e.g. \"Store POS fleet\"", Required = true };
        var kDescription = new Option<string?>("--description") { Description = "Longer note shown in Power Apps" };
        var kScopes = new Option<string?>("--scopes") { Description = "Operations the key may call, comma separated (e.g. transaction.create,member.get)" };
        var kFull = new Option<bool>("--full-access") { Description = "Every operation of the channel (instead of --scopes)" };
        var kExpires = new Option<DateTimeOffset?>("--expires") { Description = "Expiry date (e.g. 2027-06-30)" };
        var kSave = new Option<string?>("--save-profile") { Description = "Store the key and secret in the keychain as a new channel profile with this name (recommended)" };
        var kEnv = new Option<string?>("--env-file") { Description = "Write INO_BASE_URL, INO_CHANNEL, INO_ENVIRONMENT, INO_API_KEY and INO_HMAC_SECRET to this new file (mode 0600; an existing file is not overwritten), the names the connector templates and the CLI read" };
        var kShow = new Option<bool>("--show-secret") { Description = "Print the key and secret here (they end up in your terminal history or an AI agent's transcript)" };
        var keysCreate = new Command("create", "Create an API key for a channel (test tenants). The secret is shown once: keep it with --save-profile, --env-file or --show-secret")
            { kChannel, kName, kDescription, kScopes, kFull, kExpires, kSave, kEnv, kShow };
        Bind(keysCreate, async pr =>
        {
            var (name, prof, _) = ctx.Profiles.ResolveForLogin(pr.GetValue(profile));
            var dest = new SecretDestinations(pr.GetValue(kSave), pr.GetValue(kEnv), pr.GetValue(kShow), null);
            CheckDestinations(dest, pr.GetValue(kChannel)!, prof, null);
            var scopes = pr.GetValue(kScopes)?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var body = new JsonObject
            {
                ["channel"] = pr.GetValue(kChannel), ["name"] = pr.GetValue(kName), ["description"] = pr.GetValue(kDescription),
                ["scopes"] = scopes is null ? null : new JsonArray(scopes.Select(x => (JsonNode)x).ToArray()), ["fullAccess"] = pr.GetValue(kFull),
                ["expiresAt"] = pr.GetValue(kExpires)?.ToString("O"),
            };
            var data = await KeyWrite(name, InoLogin.TargetOf(prof.BaseUrl), "api-keys", body,
                "Ino did not confirm the key. It may have been issued: check `ino keys list` and revoke a key you did not get in Power Apps.");
            var (apiKey, secret, channelCode) = Issued(data, pr.GetValue(kChannel)!);
            var kept = KeepSecret(dest, prof, channelCode, apiKey, secret);
            var result = new JsonObject { ["id"] = data?["id"]?.DeepClone(), ["channel"] = channelCode, ["keyPrefix"] = data?["keyPrefix"]?.DeepClone(),
                ["savedTo"] = new JsonArray(kept.Select(x => (JsonNode)x).ToArray()) };
            if (dest.Show) { result["apiKey"] = apiKey; result["secret"] = secret; }
            Write(ctx, pr.GetValue(json), result, $"Created API key {data?["keyPrefix"]} for {channelCode}." +
                (kept.Count > 0 ? " Saved to " + string.Join(" and ", kept) + "." : "") +
                (dest.Show ? $"\nAPI key: {apiKey}\nSigning secret: {secret}\nThis is the only time the secret is shown." : ""));
        });

        // ---- keys rotate / revoke (docs/MCP_CLI_P0C.md §2.6)
        var rKey = new Argument<string>("key") { Description = "The key's id or prefix (see `ino keys list`)" };
        var rOverlap = new Option<int>("--overlap-hours") { Description = "How long the old key keeps working: 0 (stops now) to 168; default 24", DefaultValueFactory = _ => 24 };
        var rSave = new Option<string?>("--save-profile") { Description = "Store the new key and secret in the keychain as a new channel profile with this name" };
        var rEnv = new Option<string?>("--env-file") { Description = "Write INO_BASE_URL, INO_CHANNEL, INO_ENVIRONMENT, INO_API_KEY and INO_HMAC_SECRET to this new file (mode 0600)" };
        var rShow = new Option<bool>("--show-secret") { Description = "Print the new key and secret here" };
        var rUpdate = new Option<string?>("--update-profile") { Description = "The channel profile that holds the old key gets the new key and secret (recommended)" };
        var keysRotate = new Command("rotate", "Replace an API key with a new one (test tenants); the old key keeps working for --overlap-hours. The new secret is shown once")
            { rKey, rOverlap, rUpdate, rSave, rEnv, rShow };
        Bind(keysRotate, async pr =>
        {
            var (name, prof, _) = ctx.Profiles.ResolveForLogin(pr.GetValue(profile));
            var target = InoLogin.TargetOf(prof.BaseUrl);
            if (pr.GetValue(rOverlap) is < 0 or > 168) throw new CliException(ExitCodes.Usage, "USAGE", "--overlap-hours is between 0 and 168.");
            var dest = new SecretDestinations(pr.GetValue(rSave), pr.GetValue(rEnv), pr.GetValue(rShow), pr.GetValue(rUpdate));
            if (dest.Save is null && dest.EnvFile is null && !dest.Show && dest.Update is null)
                throw new CliException(ExitCodes.Usage, "NO_SECRET_DESTINATION", "The new secret is shown only once. Choose where it goes: --update-profile <name>, --save-profile <name>, --env-file <path> or --show-secret.");
            var key = await FindKey(name, target, pr.GetValue(rKey)!, revoking: false);
            var channel = key["channel"]!.GetValue<string>();
            CheckDestinations(dest, channel, prof, key["keyPrefix"]!.GetValue<string>());
            var data = await KeyWrite(name, target, $"api-keys/{key["id"]}/rotate", new JsonObject { ["overlapHours"] = pr.GetValue(rOverlap) },
                "Ino did not confirm the rotation. A new key may have been issued: check `ino keys list`.");
            var (apiKey, secret, channelCode) = Issued(data, channel);
            var kept = KeepSecret(dest, prof, channelCode, apiKey, secret);
            var until = data?["previousValidUntil"]?.GetValue<DateTimeOffset?>();
            var result = new JsonObject { ["id"] = data?["id"]?.DeepClone(), ["channel"] = channelCode, ["keyPrefix"] = data?["keyPrefix"]?.DeepClone(),
                ["previousKeyPrefix"] = key["keyPrefix"]?.DeepClone(), ["previousValidUntil"] = until?.ToString("O"), ["savedTo"] = new JsonArray(kept.Select(x => (JsonNode)x).ToArray()) };
            if (dest.Show) { result["apiKey"] = apiKey; result["secret"] = secret; }
            Write(ctx, pr.GetValue(json), result, $"Rotated {key["keyPrefix"]}: the new key is {data?["keyPrefix"]}. " +
                (until is { } u ? $"The old key works until {u.ToLocalTime():yyyy-MM-dd HH:mm}." : "The old key stopped working.") +
                (kept.Count > 0 ? " Saved to " + string.Join(" and ", kept) + "." : "") +
                (dest.Show ? $"\nAPI key: {apiKey}\nSigning secret: {secret}\nThis is the only time the secret is shown." : ""));
        });
        var vReason = new Option<string?>("--reason") { Description = "Why (shown in Power Apps → Developers, at most 200 characters)" };
        var vYes = new Option<bool>("--yes", "-y") { Description = "Revoke without asking (requests with the key are refused from now on)" };
        var keysRevoke = new Command("revoke", "Revoke an API key (test tenants): requests with it are refused at once. Revoking a replacement does not stop the old key during its overlap")
            { rKey, vReason, vYes };
        Bind(keysRevoke, async pr =>
        {
            var (name, prof, _) = ctx.Profiles.ResolveForLogin(pr.GetValue(profile));
            var target = InoLogin.TargetOf(prof.BaseUrl);
            var key = await FindKey(name, target, pr.GetValue(rKey)!, revoking: true);
            var label = $"{key["keyPrefix"]} ({key["name"]}, {key["channel"]})";
            if (!pr.GetValue(vYes))
                throw new CliException(ExitCodes.Usage, "CONFIRM", $"This revokes {label}; requests with it are refused at once. Run again with --yes to do it.");
            await KeyWrite(name, target, $"api-keys/{key["id"]}/revoke", new JsonObject { ["reason"] = pr.GetValue(vReason) },
                "Ino did not confirm the revocation. Check `ino keys list` and try again.");
            Write(ctx, pr.GetValue(json), new JsonObject { ["revoked"] = key["id"]?.DeepClone(), ["keyPrefix"] = key["keyPrefix"]?.DeepClone() }, $"Revoked {label}.");
        });

        // Key lookup for rotate/revoke: an id, an exact prefix or the full key (never part of a prefix); revoke only sees keys not revoked yet.
        async Task<JsonObject> FindKey(string name, InoLogin.Target target, string wanted, bool revoking)
        {
            wanted = wanted.Trim();
            var rows = (await InoLogin.ManageAsync(ctx, name, target, HttpMethod.Get, "api-keys", null, null, ct))?["keys"]?.AsArray() ?? [];
            var prefix = Guid.TryParse(wanted, out _) ? null : wanted.Length > 20 ? ProfileStore.KeyPrefix(wanted) : wanted;
            var matches = rows.OfType<JsonObject>().Where(k => prefix is null
                    ? string.Equals(k["id"]?.ToString(), wanted, StringComparison.OrdinalIgnoreCase)
                    : k["keyPrefix"]?.ToString() == prefix)
                .Where(k => !revoking || k["status"]?.ToString() != "REVOKED").ToList();
            return matches.Count switch
            {
                1 => matches[0],
                0 => throw new CliException(ExitCodes.NotFound, "NO_KEY", $"No {(revoking ? "unrevoked " : "")}API key matches {(prefix is null ? wanted : prefix)}. See `ino keys list`."),
                _ => throw new CliException(ExitCodes.Usage, "AMBIGUOUS", $"{matches.Count} keys match {prefix}; give the id: {string.Join(", ", matches.Select(k => k["id"]))}."),
            };
        }

        // B1/K3: dropped connections, timeouts, 5xx and "still running" are retried with the same Idempotency-Key, which returns the first
        // attempt's result (never a second key).
        async Task<JsonNode?> KeyWrite(string name, InoLogin.Target target, string path, JsonNode body, string uncertain, string? idempotencyKey = null)
        {
            var idempotency = idempotencyKey ?? Guid.NewGuid().ToString("N");
            for (var attempt = 1; ; attempt++)
            {
                try { return await InoLogin.ManageAsync(ctx, name, target, HttpMethod.Post, path, body, idempotency, ct); }
                catch (Exception e) when (attempt < 3 && Retryable(e)) { await ctx.Delay(TimeSpan.FromSeconds(2 * attempt), ct); }
                catch (Exception e) when (Retryable(e)) { throw new CliException(ExitCodes.Network, "UNCERTAIN", uncertain); }
            }
            bool Retryable(Exception e) => e is HttpRequestException || e is TaskCanceledException && !ct.IsCancellationRequested
                || e is CliException { Code: "UNAVAILABLE" or "TEMPORARILY_UNAVAILABLE" or "IN_PROGRESS" };
        }

        static (string ApiKey, string Secret, string Channel) Issued(JsonNode? data, string channel) => (
            data?["apiKey"]?.GetValue<string>() ?? throw new CliException(ExitCodes.Business, "NO_KEY", "Ino returned no key."),
            data?["secret"]?.GetValue<string>() ?? throw new CliException(ExitCodes.Business, "NO_KEY", "Ino returned no secret."),
            data?["channel"]?.GetValue<string>() ?? channel);

        // K4/K5: every check that could fail after the key is issued runs before it is asked for.
        void CheckDestinations(SecretDestinations d, string channel, Profile prof, string? oldPrefix)
        {
            if (d.Save is null && d.EnvFile is null && !d.Show && d.Update is null)
                throw new CliException(ExitCodes.Usage, "NO_SECRET_DESTINATION", "The secret is shown only once. Choose where it goes: --save-profile <name>, --env-file <path> or --show-secret.");
            if (d.Save is not null)
            {
                ProfileStore.ValidateName(d.Save);
                if (ctx.Profiles.Load().Profiles.ContainsKey(d.Save))
                    throw new CliException(ExitCodes.Usage, "PROFILE_EXISTS", $"A profile named {d.Save} exists; choose a new name (its key is not replaced).");
                ChannelClient.ValidateChannel(channel);
            }
            if (d.Update is not null) ctx.Profiles.CheckKeyProfile(d.Update, prof.BaseUrl, channel, oldPrefix!);
            if (d.EnvFile is not null)
            {
                if (File.Exists(d.EnvFile)) throw new CliException(ExitCodes.Usage, "FILE_EXISTS", $"{d.EnvFile} exists; choose a new file.");
                if (Path.GetDirectoryName(Path.GetFullPath(d.EnvFile)) is { } dir && !Directory.Exists(dir))
                    throw new CliException(ExitCodes.Usage, "NO_FOLDER", $"The folder {dir} does not exist.");
            }
        }

        List<string> KeepSecret(SecretDestinations d, Profile prof, string channelCode, string apiKey, string secret)
        {
            var kept = new List<string>();
            var failed = new List<string>();
            if (d.Update is not null)
                try { ctx.Profiles.ReplaceCredentials(d.Update, apiKey, secret); kept.Add($"profile {d.Update}"); }
                catch (Exception e) when (e is not OperationCanceledException) { failed.Add($"profile {d.Update}: {e.Message}"); }
            if (d.Save is not null)
                try
                {
                    ctx.Profiles.Add(d.Save, prof.BaseUrl, channelCode, "test", apiKey, secret, makeDefault: false);
                    ctx.Profiles.PreferAsDefault(d.Save);
                    kept.Add($"profile {d.Save} (keychain)");
                }
                catch (Exception e) when (e is not OperationCanceledException) { failed.Add($"profile {d.Save}: {e.Message}"); }
            if (d.EnvFile is not null)
                try
                {
                    var opts = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
                    if (!OperatingSystem.IsWindows()) opts.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                    using (var fs = new FileStream(d.EnvFile, opts)) using (var f = new StreamWriter(fs, new System.Text.UTF8Encoding(false))) f.Write($"INO_BASE_URL={prof.BaseUrl}\nINO_CHANNEL={channelCode}\nINO_ENVIRONMENT=test\nINO_API_KEY={apiKey}\nINO_HMAC_SECRET={secret}\n");
                    kept.Add($"{d.EnvFile} (mode 0600)");
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { failed.Add($"{d.EnvFile}: {e.Message}"); }
            // K5: a key whose secret reached nowhere is lost; say so loudly and hand the secret over once on stderr.
            if (kept.Count == 0 && !d.Show)
            {
                ctx.Err.WriteLine($"warning: the key was issued but could not be saved ({string.Join("; ", failed)}). Keep these now; they are not shown again:");
                ctx.Err.WriteLine($"INO_API_KEY={apiKey}");
                ctx.Err.WriteLine($"INO_HMAC_SECRET={secret}");
            }
            else foreach (var f in failed) ctx.Err.WriteLine("warning: not saved to " + f);
            return kept;
        }

        keysCmd.Subcommands.Add(keysList);
        keysCmd.Subcommands.Add(keysCreate);
        keysCmd.Subcommands.Add(keysRotate);
        keysCmd.Subcommands.Add(keysRevoke);
        root.Subcommands.Add(keysCmd);

        // ---------------------------------------------------------------- marketing (MCP/CLI P1c.1: your own marketing tool)
        var marketing = new Command("marketing", "Marketing Activation connections; connect a marketing tool you run yourself (test tenants; needs `ino login`)");
        var mList = new Command("list", "Marketing connections of this tenant with their status");
        Bind(mList, async pr =>
        {
            var (name, prof, _) = ctx.Profiles.ResolveForLogin(pr.GetValue(profile));
            var data = await InoLogin.ManageAsync(ctx, name, InoLogin.TargetOf(prof.BaseUrl), HttpMethod.Get, "marketing/connections", null, null, ct);
            var rows = data?["connections"]?.AsArray() ?? [];
            Write(ctx, pr.GetValue(json), new JsonObject { ["connections"] = rows.DeepClone() }, rows.Count == 0 ? "No marketing connections." : string.Join("\n", rows.Select(c =>
                $"{c?["id"],-36} {c?["type"],-8} {c?["status"],-16} {c?["name"]}  {c?["account"]}")));
        });
        var mName = new Option<string>("--name") { Description = "A name for the connection, e.g. \"Our CRM mailer\"", Required = true };
        var mUrl = new Option<string?>("--url") { Description = "Your tool's https address; Ino posts every signed request there (not with --definition)" };
        // IL3b-3 (docs/INTEGRATION_LAYER.md §8): a marketing tool described by the firm's own definition package.
        var mDef = new Option<string?>("--definition") { Description = "Connect through your own marketing tool package (local_…; save it first with `ino connector package save`)" };
        var mDefVersion = new Option<string?>("--version") { Description = "The package version (with --definition)" };
        var mSetting = new Option<string[]>("--setting") { Description = "A package setting as key=value, e.g. --setting apiUrl=https://api.example.com/v2 (repeat for each)", AllowMultipleArgumentsPerToken = false };
        var mProcessor = new Option<string?>("--processor") { Description = "The company that runs the tool (for members' KVKK answers), e.g. \"Acme Mailer Inc.\"" };
        var mCountry = new Option<string?>("--country") { Description = "Where the tool keeps the data, e.g. \"United States\"" };
        var mKeyEnv = new Option<string?>("--key-env") { Description = "The environment variable that holds the tool's API key (the key never goes on the command line)" };
        var mKeyFile = new Option<string?>("--key-file") { Description = "A file (mode 0600) that holds the tool's API key" };
        var mConfirm = new Option<string?>("--confirm") { Description = "The review hash a run without it printed: connects only if what the package sends, and where, is still that" };
        var mEnv = new Option<string?>("--env-file") { Description = "Write INO_MA_CONNECTION_ID and INO_MA_SIGNING_SECRET to this new file (mode 0600; an existing file is not overwritten)" };
        var mShow = new Option<bool>("--show-secret") { Description = "Print the signing secret here (it ends up in your terminal history or an AI agent's transcript)" };
        var mConnect = new Command("connect", "Add your own marketing tool (--url: Ino signs every call with a new secret, shown once: keep it with --env-file or --show-secret), or connect a tool through your own definition package (--definition). Then run `ino marketing verify`")
            { mName, mUrl, mEnv, mShow, mDef, mDefVersion, mSetting, mProcessor, mCountry, mKeyEnv, mKeyFile, mConfirm };
        Bind(mConnect, async pr =>
        {
            var (name, prof, _) = ctx.Profiles.ResolveForLogin(pr.GetValue(profile));
            if (pr.GetValue(mDef) is { } definition)
            {
                if (pr.GetValue(mUrl) is not null || pr.GetValue(mEnv) is not null || pr.GetValue(mShow))
                    throw new CliException(ExitCodes.Usage, "BAD_OPTIONS", "--definition connects through a package: --url, --env-file and --show-secret belong to your own tool.");
                await ConnectDefinitionAsync(pr, name, prof, definition);
                return;
            }
            if (string.IsNullOrWhiteSpace(pr.GetValue(mUrl))) throw new CliException(ExitCodes.Usage, "NO_URL", "Give your tool's address with --url, or a package with --definition.");
            var envFile = pr.GetValue(mEnv);
            var show = pr.GetValue(mShow);
            if (envFile is null && !show)
                throw new CliException(ExitCodes.Usage, "NO_SECRET_DESTINATION", "The signing secret is shown only once. Choose where it goes: --env-file <path> or --show-secret.");
            if (envFile is not null)
            {
                if (File.Exists(envFile)) throw new CliException(ExitCodes.Usage, "FILE_EXISTS", $"{envFile} exists; choose a new file.");
                if (Path.GetDirectoryName(Path.GetFullPath(envFile)) is { } dir && !Directory.Exists(dir))
                    throw new CliException(ExitCodes.Usage, "NO_FOLDER", $"The folder {dir} does not exist.");
            }
            var body = new JsonObject { ["name"] = pr.GetValue(mName), ["url"] = pr.GetValue(mUrl) };
            // The same command always sends the same Idempotency-Key (Gate K 3): running it again after an uncertain answer returns the first connection
            // and its secret (to the same person) instead of opening a second one.
            var stableKey = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
                $"ino-marketing-connect|{prof.BaseUrl}|{pr.GetValue(mName)}|{pr.GetValue(mUrl)}")));
            var data = await KeyWrite(name, InoLogin.TargetOf(prof.BaseUrl), "marketing/connections", body,
                "Ino did not confirm the connection. Run the same command again: it returns the same connection and secret.", stableKey);
            var id = data?["connectionId"]?.ToString() ?? throw new CliException(ExitCodes.Business, "NO_CONNECTION", "Ino returned no connection.");
            var secret = data?["secret"]?.GetValue<string>() ?? throw new CliException(ExitCodes.Business, "NO_SECRET", "Ino returned no signing secret.");
            var kept = new List<string>();
            if (envFile is not null)
                try
                {
                    var opts = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
                    if (!OperatingSystem.IsWindows()) opts.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                    using (var fs = new FileStream(envFile, opts)) using (var f = new StreamWriter(fs, new System.Text.UTF8Encoding(false)))
                        f.Write($"INO_MA_CONNECTION_ID={id}\nINO_MA_SIGNING_SECRET={secret}\n");
                    kept.Add($"{envFile} (mode 0600)");
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // The connection exists; a secret that reached nowhere is lost, so it is handed over once on stderr.
                    ctx.Err.WriteLine($"warning: the secret could not be written to {envFile} ({e.Message}). Keep it now; it is not shown again:");
                    ctx.Err.WriteLine($"INO_MA_SIGNING_SECRET={secret}");
                }
            var result = new JsonObject { ["connectionId"] = id, ["name"] = data?["name"]?.DeepClone(), ["host"] = data?["host"]?.DeepClone(), ["status"] = data?["status"]?.DeepClone(),
                ["savedTo"] = new JsonArray(kept.Select(x => (JsonNode)x).ToArray()) };
            if (show) result["secret"] = secret;
            Write(ctx, pr.GetValue(json), result, $"Added {data?["name"]} ({data?["host"]}) as {id}; nothing is sent until it is verified." +
                (kept.Count > 0 ? " Secret saved to " + string.Join(" and ", kept) + "." : "") +
                (show ? $"\nSigning secret: {secret}\nThis is the only time it is shown." : "") +
                $"\nWhen your tool answers the ping, run: ino marketing verify {id}");
        });
        async Task ConnectDefinitionAsync(ParseResult pr, string name, Profile prof, string definition)
        {
            var version = pr.GetValue(mDefVersion) ?? throw new CliException(ExitCodes.Usage, "NO_VERSION", "Give the package version with --version.");
            var settings = new JsonObject();
            foreach (var kv in pr.GetValue(mSetting) ?? [])
            {
                var at = kv.IndexOf('=');
                if (at <= 0) throw new CliException(ExitCodes.Usage, "BAD_SETTING", $"--setting {kv}: write it as key=value.");
                settings[kv[..at].Trim()] = kv[(at + 1)..].Trim();
            }
            var target = InoLogin.TargetOf(prof.BaseUrl);
            var path = $"marketing/definitions/{Uri.EscapeDataString(definition)}/{Uri.EscapeDataString(version)}";
            // T20 (Gate T M2): the summary first. Connecting needs --confirm with the review hash this summary printed, so the approval is of what
            // was shown: the package's access and the hosts with these settings (an AI agent shows it to the person and asks).
            var preview = await InoLogin.ManageAsync(ctx, name, target, HttpMethod.Post, path + "/preview", new JsonObject { ["settings"] = settings.DeepClone() }, null, ct);
            var reviewHash = preview?["reviewHash"]?.ToString() ?? throw new CliException(ExitCodes.Business, "NO_REVIEW", "Ino returned no review hash; update the engine.");
            if (pr.GetValue(mConfirm) != reviewHash)
            {
                if (pr.GetValue(json)) Write(ctx, true, new JsonObject { ["preview"] = preview?.DeepClone(), ["confirmed"] = false }, "");
                else ctx.Out.WriteLine(MarketingAccessText(preview));
                throw new CliException(ExitCodes.Usage, "CONFIRM", pr.GetValue(mConfirm) is null
                    ? $"Review what the package sends above. To connect, run the same command with --confirm {reviewHash}"
                    : $"What the package sends, or where, changed since that review. Review it above; to connect, use --confirm {reviewHash}");
            }
            if (string.IsNullOrWhiteSpace(pr.GetValue(mName))) throw new CliException(ExitCodes.Usage, "NO_NAME", "Give the connection a name with --name.");
            if (string.IsNullOrWhiteSpace(pr.GetValue(mProcessor)) || string.IsNullOrWhiteSpace(pr.GetValue(mCountry)))
                throw new CliException(ExitCodes.Usage, "NO_PROCESSOR", "Give --processor (the company that runs the tool) and --country (where it keeps the data): members' KVKK answers name them.");
            var key = TransportSeal.ReadKey(pr.GetValue(mKeyEnv), pr.GetValue(mKeyFile), ctx.Err, KeyEnv);
            var sealedKey = await SealAsync(name, target, key);
            var body = new JsonObject
            {
                ["name"] = pr.GetValue(mName), ["definition"] = definition, ["version"] = version, ["settings"] = settings, ["key"] = sealedKey,
                ["processor"] = pr.GetValue(mProcessor), ["country"] = pr.GetValue(mCountry), ["reviewHash"] = reviewHash,
            };
            var data = await KeyWrite(name, target, "marketing/connections", body,
                "Ino did not confirm the connection. Run `ino marketing list`: if it is not there, run the same command again.");
            var id = data?["connectionId"]?.ToString() ?? throw new CliException(ExitCodes.Business, "NO_CONNECTION", "Ino returned no connection.");
            var existing = data?["existing"]?.GetValue<bool>() == true;
            Write(ctx, pr.GetValue(json), new JsonObject { ["connectionId"] = id, ["definition"] = definition, ["version"] = version, ["hosts"] = data?["hosts"]?.DeepClone(),
                ["status"] = data?["status"]?.DeepClone(), ["existing"] = existing },
                existing
                    ? $"This tool account is already connected through {definition} {version} as {id} ({data?["status"]?.ToString().ToLowerInvariant()}); its key was not changed.\nTo replace the key: ino marketing key {id} --key-env <VARIABLE>; then ino marketing verify {id}"
                    : $"Added {pr.GetValue(mName)} through {definition} {version} as {id}; nothing is sent until it is verified.\nRun: ino marketing verify {id}");
        }

        string? KeyEnv(string n) => ctx.Env.TryGetValue(n, out var v) && !string.IsNullOrEmpty(v) ? v : Environment.GetEnvironmentVariable(n);

        async Task<JsonObject> SealAsync(string name, InoLogin.Target target, string secret)
        {
            var tk = await InoLogin.ManageAsync(ctx, name, target, HttpMethod.Get, "transport-key", null, null, ct);
            var kid = tk?["kid"]?.ToString();
            var publicKey = tk?["publicKey"]?.ToString();
            if (string.IsNullOrEmpty(kid) || string.IsNullOrEmpty(publicKey)) throw new CliException(ExitCodes.Business, "NO_TRANSPORT_KEY", "Ino returned no transport key.");
            try { return TransportSeal.Seal(kid, publicKey, secret); }
            catch (System.Security.Cryptography.CryptographicException) { throw new CliException(ExitCodes.Business, "BAD_TRANSPORT_KEY", "Ino's transport key could not be used."); }
            catch (FormatException) { throw new CliException(ExitCodes.Business, "BAD_TRANSPORT_KEY", "Ino's transport key could not be used."); }
        }

        static string MarketingAccessText(JsonNode? d)
        {
            var a = d?["access"];
            static IEnumerable<string> Items(JsonNode? n) => n is JsonArray arr ? arr.Select(x => x?.ToString() ?? "") : [];
            var sha = d?["sha256"]?.ToString() ?? "";
            var lines = new List<string> { $"{d?["definition"]} {d?["version"]} (sha256 {sha[..Math.Min(12, sha.Length)]}…)", $"  sends to        {string.Join(", ", Items(d?["hosts"]))}" };
            if (a?["contact"] is JsonArray { Count: > 0 } contact) lines.Add($"  contact fields  {string.Join(", ", Items(contact))}");
            if (a?["attributes"]?.GetValue<bool>() == true) lines.Add("  profile fields  the loyalty fields the firm allows (tier, points, …)");
            // Gate T m5: each body field is marked: [checked] = a declared subscription field Ino checks; every other field carries Ino's data as the
            // package maps it and Ino cannot check what the tool does with it, so a subscription field missing from the consent list shows here.
            var checkedPaths = Items(a?["consent"]).Select(x => x.Split(' ')[0].Replace("[*]", "").Replace("[0]", "")).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var o in a?["operations"]?.AsArray() ?? [])
            {
                lines.Add($"  {o?["operation"],-15} {o?["method"]} {o?["path"]}{(o?["query"] is JsonArray { Count: > 0 } q ? " ?" + string.Join("&", Items(q)) : "")}");
                foreach (var bp in Items(o?["bodyPaths"]))
                    lines.Add($"                  {(checkedPaths.Contains(bp.Replace("[*]", "")) ? "[checked] " : "          ")}{bp}");
            }
            if (a?["constants"] is JsonArray { Count: > 0 } k) lines.Add($"  constants       {string.Join(", ", Items(k))}");
            lines.Add(a?["consent"] is JsonArray { Count: > 0 } c ? $"  consent fields  {string.Join(", ", Items(c))}" : "  consent fields  NONE DECLARED");
            if (a?["events"] is JsonArray { Count: > 0 } ev) lines.Add($"  events          {string.Join(", ", Items(ev))}");
            lines.Add($"  review hash     {d?["reviewHash"]}");
            if (d?["warning"]?.ToString() is { Length: > 0 } w) lines.Add($"  NOTE            {w}");
            return string.Join("\n", lines);
        }

        var mId = new Argument<Guid>("connection-id") { Description = "The connection's id (see `ino marketing list`)" };
        var mVerify = new Command("verify", "Send a signed ping to your tool; it connects when the tool answers with the challenge and its proof (also reconnects a disconnected one)") { mId };
        Bind(mVerify, async pr =>
        {
            var (name, prof, _) = ctx.Profiles.ResolveForLogin(pr.GetValue(profile));
            var id = pr.GetValue(mId);
            var data = await InoLogin.ManageAsync(ctx, name, InoLogin.TargetOf(prof.BaseUrl), HttpMethod.Post, $"marketing/connections/{id:D}/verify", new JsonObject(), null, ct);
            var ok = data?["connected"]?.GetValue<bool>() == true;
            var output = new JsonObject { ["connectionId"] = id.ToString("D"), ["connected"] = ok, ["status"] = data?["status"]?.DeepClone(), ["error"] = data?["error"]?.DeepClone(),
                ["capabilities"] = data?["capabilities"]?.DeepClone() };
            if (!ok)
                throw new CliException(ExitCodes.Business, "NOT_VERIFIED", $"Your tool did not pass the check ({data?["error"]}); nothing is sent to it.",
                    VerifyHint(data?["error"]?.ToString()));
            if (data?["onPreviousSecret"]?.GetValue<bool>() == true) output["onPreviousSecret"] = true;
            Write(ctx, pr.GetValue(json), output, data?["onPreviousSecret"]?.GetValue<bool>() == true
                ? $"Your tool answered, but still with the previous signing secret: Ino keeps signing with both until the overlap ends. Put the new secret in the tool and run `ino marketing verify {id:D}` again."
                : $"Verified: the connection is {data?["status"]?.ToString().ToLowerInvariant()}. Ino now sends profiles, events, unsubscribes and deletions to it.");
        });
        var mOverlap = new Option<int>("--overlap-hours") { Description = "How long the previous secret keeps signing next to the new one (0 = stop it now, for a leaked secret; at most 168)", DefaultValueFactory = _ => 24 };
        var mRotEnv = new Option<string?>("--env-file") { Description = "Put the new INO_MA_SIGNING_SECRET in this file (created with mode 0600, or the line replaced in an existing file)" };
        var mRotate = new Command("rotate-secret", "Renew the signing secret of your own tool. Nothing stops: Ino signs with both secrets until the tool is verified with the new one or the overlap ends")
            { mId, mOverlap, mRotEnv, mShow };
        Bind(mRotate, async pr =>
        {
            var (name, prof, _) = ctx.Profiles.ResolveForLogin(pr.GetValue(profile));
            var id = pr.GetValue(mId);
            var envFile = pr.GetValue(mRotEnv);
            var show = pr.GetValue(mShow);
            var overlap = pr.GetValue(mOverlap);
            if (overlap is < 0 or > 168) throw new CliException(ExitCodes.Usage, "BAD_OVERLAP", "--overlap-hours is between 0 and 168.");
            if (envFile is null && !show)
                throw new CliException(ExitCodes.Usage, "NO_SECRET_DESTINATION", "The new signing secret is shown only once. Choose where it goes: --env-file <path> or --show-secret.");
            if (envFile is not null && Path.GetDirectoryName(Path.GetFullPath(envFile)) is { } dir && !Directory.Exists(dir))
                throw new CliException(ExitCodes.Usage, "NO_FOLDER", $"The folder {dir} does not exist.");
            // A new Idempotency-Key per run (Gate T M4): this run's own retries get the same secret; running it again renews again.
            var data = await KeyWrite(name, InoLogin.TargetOf(prof.BaseUrl), $"marketing/connections/{id:D}/rotate-secret", new JsonObject { ["overlapHours"] = overlap },
                "Ino did not confirm the renewal. Run `ino marketing list` and renew again if needed: the tool keeps working with the previous secret meanwhile.");
            var secret = data?["secret"]?.GetValue<string>() ?? throw new CliException(ExitCodes.Business, "NO_SECRET", "Ino returned no signing secret.");
            var kept = new List<string>();
            if (envFile is not null)
                try { ReplaceEnvValue(envFile, "INO_MA_SIGNING_SECRET", secret); kept.Add($"{envFile} (mode 0600)"); }
                catch (Exception e)     // any failure: the renewal is done, so the secret must reach the person here (Gate K 6)
                {
                    ctx.Err.WriteLine($"warning: the secret could not be written to {envFile} ({e.Message}). Keep it now; it is not shown again:");
                    ctx.Err.WriteLine($"INO_MA_SIGNING_SECRET={secret}");
                }
            var until = data?["previousValidUntil"]?.ToString();
            var result = new JsonObject { ["connectionId"] = id.ToString("D"), ["previousValidUntil"] = until, ["savedTo"] = new JsonArray(kept.Select(x => (JsonNode)x).ToArray()) };
            if (show) result["secret"] = secret;
            Write(ctx, pr.GetValue(json), result, "Signing secret renewed." +
                (string.IsNullOrEmpty(until) ? " The previous secret stopped now: your tool refuses Ino's calls until it has the new one." : $" The previous secret also signs until {until}.") +
                (kept.Count > 0 ? " Saved to " + string.Join(" and ", kept) + "." : "") +
                (show ? $"\nSigning secret: {secret}\nThis is the only time it is shown." : "") +
                $"\nPut it in your tool, then run: ino marketing verify {id:D}");
        });
        var mYes = new Option<bool>("--yes") { Description = "Disconnect without asking" };
        var mDisconnect = new Command("disconnect", "Stop sending to a marketing connection (an own tool keeps its address and secret; `verify` reconnects it)") { mId, mYes };
        Bind(mDisconnect, async pr =>
        {
            if (!pr.GetValue(mYes)) throw new CliException(ExitCodes.Usage, "CONFIRM", "Add --yes to disconnect: nothing more is sent to the tool.");
            var (name, prof, _) = ctx.Profiles.ResolveForLogin(pr.GetValue(profile));
            var id = pr.GetValue(mId);
            var data = await InoLogin.ManageAsync(ctx, name, InoLogin.TargetOf(prof.BaseUrl), HttpMethod.Post, $"marketing/connections/{id:D}/disconnect", new JsonObject(), null, ct);
            Write(ctx, pr.GetValue(json), new JsonObject { ["connectionId"] = id.ToString("D"), ["alreadyDisconnected"] = data?["alreadyDisconnected"]?.DeepClone() },
                data?["alreadyDisconnected"]?.GetValue<bool>() == true ? "It was already disconnected." : "Disconnected; nothing more is sent to it.");
        });

        /// <summary>
        /// Sets KEY=value in an env file: every line that sets the key (also <c>export KEY=</c>) becomes the one new line, other lines kept; written to a
        /// temporary file (0600) next to it and moved over it in one step (Gate K 5). Never through a symbolic link.
        /// </summary>
        static void ReplaceEnvValue(string envFile, string key, string value)
        {
            // A symlink is refused (Gate S note 3): a planted link must not make the CLI rewrite another file.
            if (new FileInfo(envFile).LinkTarget is not null) throw new IOException($"{envFile} is a symbolic link; give the file itself");
            var lines = File.Exists(envFile) ? File.ReadAllLines(envFile).ToList() : [];
            static bool Sets(string line, string key)
            {
                var l = line.TrimStart();
                if (l.StartsWith("export ", StringComparison.Ordinal)) l = l[7..].TrimStart();
                return l.StartsWith(key + "=", StringComparison.Ordinal) || l.StartsWith(key + " =", StringComparison.Ordinal);
            }
            var at = lines.FindIndex(l => Sets(l, key));
            lines.RemoveAll(l => Sets(l, key));
            lines.Insert(at >= 0 ? at : lines.Count, $"{key}={value}");
            var tmp = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(envFile))!, $".{Path.GetFileName(envFile)}.{Guid.NewGuid():N}.tmp");
            var opts = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) opts.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            try
            {
                using (var fs = new FileStream(tmp, opts))
                {
                    using (var f = new StreamWriter(fs, new System.Text.UTF8Encoding(false), leaveOpen: true)) f.Write(string.Join("\n", lines) + "\n");
                    fs.Flush(flushToDisk: true);
                }
                File.Move(tmp, envFile, overwrite: true);
            }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
        }

        // MA5d1 (docs/MARKETING_ACTIVATION.md §33): actions a marketing tool reports back (e.g. a Klaviyo flow's webhook step) become `marketing.action` events.
        var mActions = new Command("actions", "Actions a marketing tool may report back to Ino, today's counts, and where the tool calls") { mId };
        Bind(mActions, async pr =>
        {
            var (name, prof, _) = ctx.Profiles.ResolveForLogin(pr.GetValue(profile));
            var id = pr.GetValue(mId);
            var data = await InoLogin.ManageAsync(ctx, name, InoLogin.TargetOf(prof.BaseUrl), HttpMethod.Get, $"marketing/connections/{id:D}/actions", null, null, ct);
            var rows = data?["actions"]?.AsArray() ?? [];
            var token = data?["token"];
            Write(ctx, pr.GetValue(json), data?.DeepClone() ?? new JsonObject(),
                $"Endpoint: POST {data?["endpoint"]}  (Authorization: Bearer <action token>; body: profile_id, dedupe)\n" +
                (token is null ? "No action token yet: run `ino marketing action-token " + id.ToString("D") + " --env-file <file>`.\n"
                    : $"Action token since {token["createdAt"]}; caps {token["dailyCap"]}/day, {token["memberDailyCap"]}/member/day. Today: {data?["today"]?["accepted"]} accepted, {data?["today"]?["refused"]} refused.\n") +
                (rows.Count == 0 ? "No actions yet: add one with `ino marketing action-set " + id.ToString("D") + " <key> --name \"…\" --on`."
                    : string.Join("\n", rows.Select(a => $"{a?["key"],-24} {(a?["enabled"]?.GetValue<bool>() == true ? "on " : "off")} v{a?["version"]}  {a?["name"]}  " +
                        $"caps {a?["memberDailyCap"]}/member {a?["connectionDailyCap"]}/day  today {a?["today"]?["accepted"]}✓ {a?["today"]?["refused"]}✗"))));
        });
        var aKey = new Argument<string>("key") { Description = "The action key, e.g. survey.completed (lower case, digits, . - _), or * for the connection's own caps" };
        var aName = new Option<string?>("--name") { Description = "What the action means, shown in Power Apps and in the event" };
        var aOn = new Option<bool>("--on") { Description = "Accept this action" };
        var aOff = new Option<bool>("--off") { Description = "Refuse this action from now on" };
        var aMember = new Option<int?>("--member-daily-cap") { Description = "At most this many per member per day (1-100)" };
        var aDaily = new Option<int?>("--daily-cap") { Description = "At most this many per day for the whole connection" };
        var aVersion = new Option<int?>("--version") { Description = "The version `ino marketing actions` shows (0 or leave out for a new action); a change made meanwhile is not overwritten" };
        var mActionSet = new Command("action-set", "Add or change an action a marketing tool may report") { mId, aKey, aName, aOn, aOff, aMember, aDaily, aVersion };
        Bind(mActionSet, async pr =>
        {
            if (pr.GetValue(aOn) && pr.GetValue(aOff)) throw new CliException(ExitCodes.Usage, "BAD_FLAGS", "Choose --on or --off, not both.");
            var (name, prof, _) = ctx.Profiles.ResolveForLogin(pr.GetValue(profile));
            var id = pr.GetValue(mId);
            var key = pr.GetValue(aKey)!;
            var body = new JsonObject { ["name"] = pr.GetValue(aName), ["memberDailyCap"] = pr.GetValue(aMember), ["connectionDailyCap"] = pr.GetValue(aDaily), ["version"] = pr.GetValue(aVersion) ?? 0 };
            if (pr.GetValue(aOn)) body["enabled"] = true;
            if (pr.GetValue(aOff)) body["enabled"] = false;
            var data = await InoLogin.ManageAsync(ctx, name, InoLogin.TargetOf(prof.BaseUrl), HttpMethod.Put,
                $"marketing/connections/{id:D}/actions/{Uri.EscapeDataString(key)}", body, null, ct);
            Write(ctx, pr.GetValue(json), data?.DeepClone() ?? new JsonObject(), data?["changed"]?.GetValue<bool>() == false
                ? "Nothing changed: someone changed this action meanwhile. Run `ino marketing actions` and use the version it shows."
                : key == "*" ? "Connection caps saved." : $"Saved {key} (version {data?["version"]}).");
        });
        var tEnvFile = new Option<string?>("--env-file") { Description = "Put INO_MA_ACTION_TOKEN in this file (created with mode 0600, or the line replaced)" };
        var mActionToken = new Command("action-token", "A new action token for the connection (the previous one stops). Shown once: --env-file or --show-secret")
            { mId, tEnvFile, mShow };
        Bind(mActionToken, async pr =>
        {
            var envFile = pr.GetValue(tEnvFile);
            var show = pr.GetValue(mShow);
            if (envFile is null && !show)
                throw new CliException(ExitCodes.Usage, "NO_SECRET_DESTINATION", "The action token is shown only once. Choose where it goes: --env-file <path> or --show-secret.");
            if (envFile is not null && Path.GetDirectoryName(Path.GetFullPath(envFile)) is { } dir && !Directory.Exists(dir))
                throw new CliException(ExitCodes.Usage, "NO_FOLDER", $"The folder {dir} does not exist.");
            var (name, prof, _) = ctx.Profiles.ResolveForLogin(pr.GetValue(profile));
            var id = pr.GetValue(mId);
            // A new Idempotency-Key per run: this run's retries get the same token; running it again makes a new one.
            var data = await KeyWrite(name, InoLogin.TargetOf(prof.BaseUrl), $"marketing/connections/{id:D}/action-token", new JsonObject(),
                "Ino did not confirm the new token. Run the command again: it makes a new one and the earlier one stops.");
            var token = data?["token"]?.GetValue<string>() ?? throw new CliException(ExitCodes.Business, "NO_TOKEN", "Ino returned no token.");
            var kept = new List<string>();
            if (envFile is not null)
                try { ReplaceEnvValue(envFile, "INO_MA_ACTION_TOKEN", token); kept.Add($"{envFile} (mode 0600)"); }
                catch (Exception e)
                {
                    ctx.Err.WriteLine($"warning: the token could not be written to {envFile} ({e.Message}). Keep it now; it is not shown again:");
                    ctx.Err.WriteLine($"INO_MA_ACTION_TOKEN={token}");
                }
            var result = new JsonObject { ["connectionId"] = id.ToString("D"), ["tokenId"] = data?["tokenId"]?.DeepClone(), ["savedTo"] = new JsonArray(kept.Select(x => (JsonNode)x).ToArray()) };
            if (show) result["token"] = token;
            Write(ctx, pr.GetValue(json), result, "New action token; the previous one stopped." +
                (kept.Count > 0 ? " Saved to " + string.Join(" and ", kept) + "." : "") +
                (show ? $"\nAction token: {token}\nThis is the only time it is shown." : "") +
                $"\nYour tool calls POST …/ma/actions/{id:D}/<key> with Authorization: Bearer <token>; see `ino marketing actions {id:D}`.");
        });

        static (string Secret, string? ConnectionId) ReadMarketingSecret(string? envFile)
        {
            string? secret = null, connection = null;
            if (envFile is not null)
            {
                if (!File.Exists(envFile)) throw new CliException(ExitCodes.Usage, "NO_FILE", $"{envFile} does not exist.");
                foreach (var line in File.ReadAllLines(envFile))
                {
                    var i = line.IndexOf('=');
                    if (i <= 0) continue;
                    var (k, v) = (line[..i].Trim(), line[(i + 1)..].Trim());
                    if (k == "INO_MA_SIGNING_SECRET") secret = v;
                    if (k == "INO_MA_CONNECTION_ID") connection = v;
                }
            }
            secret ??= Environment.GetEnvironmentVariable("INO_MA_SIGNING_SECRET");
            if (string.IsNullOrEmpty(secret) || !secret.StartsWith("whsec_", StringComparison.Ordinal))
                throw new CliException(ExitCodes.Usage, "NO_SECRET", "Give the signing secret: --env-file <the file from `ino marketing connect`> or INO_MA_SIGNING_SECRET=whsec_….");
            return (secret, connection);
        }

        static string VerifyHint(string? error) => error switch
        {
            "PROOF_MISMATCH" => "Answer with proof = base64(HMAC-SHA256(key, challenge)), the key being your secret without \"whsec_\", base64-decoded.",
            "CHALLENGE_MISMATCH" => "Answer the ping with the same challenge Ino sent.",
            "EGRESS_BLOCKED" => "The address resolves to a private network; Ino calls public addresses only.",
            "HTTP_401" or "HTTP_403" => "Your tool refused the request: check that it verifies the signature with the secret from `ino marketing connect`.",
            "CHANGED_MEANWHILE" => "The connection changed while it was checked; run the command again.",
            _ => "Check that your tool is reachable over https and answers the ping (see `ino docs marketing`).",
        };

        marketing.Subcommands.Add(mList);
        marketing.Subcommands.Add(mConnect);
        marketing.Subcommands.Add(mVerify);
        marketing.Subcommands.Add(mRotate);
        marketing.Subcommands.Add(mActions);
        marketing.Subcommands.Add(mActionSet);
        marketing.Subcommands.Add(mActionToken);
        marketing.Subcommands.Add(mDisconnect);
        var mKeyKeyEnv = new Option<string?>("--key-env") { Description = "The environment variable that holds the new API key" };
        var mKeyKeyFile = new Option<string?>("--key-file") { Description = "A file (mode 0600) that holds the new API key" };
        var mKey = new Command("key", "Replace the API key of a connection made through your own package; it pauses until `ino marketing verify` proves the same account") { mId, mKeyKeyEnv, mKeyKeyFile };
        Bind(mKey, async pr =>
        {
            var (name, prof, _) = ctx.Profiles.ResolveForLogin(pr.GetValue(profile));
            var id = pr.GetValue(mId);
            var target = InoLogin.TargetOf(prof.BaseUrl);
            var sealedKey = await SealAsync(name, target, TransportSeal.ReadKey(pr.GetValue(mKeyKeyEnv), pr.GetValue(mKeyKeyFile), ctx.Err, KeyEnv));
            var data = await InoLogin.ManageAsync(ctx, name, target, HttpMethod.Post, $"marketing/connections/{id:D}/key", new JsonObject { ["key"] = sealedKey }, null, ct);
            Write(ctx, pr.GetValue(json), new JsonObject { ["connectionId"] = id.ToString("D"), ["status"] = data?["status"]?.DeepClone() },
                $"Key replaced; the connection is {data?["status"]?.ToString().ToLowerInvariant()}. Run: ino marketing verify {id:D}");
        });
        marketing.Subcommands.Add(mKey);
        // IL3b-4: move a connection made through your own package to another saved version of it (the summary first, then --confirm).
        var rpVersion = new Option<string>("--version") { Description = "The saved package version to move to", Required = true };
        var rpSetting = new Option<string[]>("--setting") { Description = "key=value for a setting the new version adds (saved values never change)", AllowMultipleArgumentsPerToken = false };
        var rpConfirm = new Option<string?>("--confirm") { Description = "The review hash the summary printed: moves the connection" };
        var mRepin = new Command("repin", "Move a connection made through your own package to another saved version of it; a live one pauses until `ino marketing verify`") { mId, rpVersion, rpSetting, rpConfirm };
        Bind(mRepin, async pr =>
        {
            var (name, prof, _) = ctx.Profiles.ResolveForLogin(pr.GetValue(profile));
            var id = pr.GetValue(mId);
            var target = InoLogin.TargetOf(prof.BaseUrl);
            var settings = new JsonObject();
            foreach (var kv in pr.GetValue(rpSetting) ?? [])
            {
                var at = kv.IndexOf('=');
                if (at <= 0) throw new CliException(ExitCodes.Usage, "BAD_SETTING", $"--setting {kv}: write it as key=value.");
                settings[kv[..at].Trim()] = kv[(at + 1)..].Trim();
            }
            var body = new JsonObject { ["version"] = pr.GetValue(rpVersion), ["settings"] = settings };
            var preview = await InoLogin.ManageAsync(ctx, name, target, HttpMethod.Post, $"marketing/connections/{id:D}/repin/preview", body.DeepClone(), null, ct);
            var reviewHash = preview?["reviewHash"]?.ToString() ?? throw new CliException(ExitCodes.Business, "NO_REVIEW", "Ino returned no review hash; update the engine.");
            if (pr.GetValue(rpConfirm) != reviewHash)
            {
                if (pr.GetValue(json)) Write(ctx, true, new JsonObject { ["preview"] = preview?.DeepClone(), ["confirmed"] = false }, "");
                else ctx.Out.WriteLine(RepinText(preview));
                throw new CliException(ExitCodes.Usage, "CONFIRM", pr.GetValue(rpConfirm) is null
                    ? $"Review what changes above. To move the connection, run the same command with --confirm {reviewHash}"
                    : $"What the new version sends, or where, or the version it moves from, changed since that review. Review it above; to move, use --confirm {reviewHash}");
            }
            body["reviewHash"] = reviewHash;
            // One Idempotency-Key for this move: a retry after a lost answer gets the same answer (and no second audit row).
            var data = await KeyWrite(name, target, $"marketing/connections/{id:D}/repin", body,
                $"Ino did not confirm the move. Run `ino marketing list`: if {id:D} still runs with its old version, run the same command again.");
            var status = data?["status"]?.ToString() ?? "";
            Write(ctx, pr.GetValue(json), new JsonObject { ["connectionId"] = id.ToString("D"), ["from"] = data?["from"]?.DeepClone(), ["version"] = data?["version"]?.DeepClone(),
                ["status"] = status, ["unchanged"] = data?["unchanged"]?.DeepClone() },
                data?["unchanged"]?.GetValue<bool>() == true ? $"Nothing changed: the connection already runs with {data?["version"]}."
                    : $"Moved from {data?["from"]} to {data?["version"]}; the connection is {status.ToLowerInvariant()}." + (status == "PAUSED" || status == "PENDING_CONFIRM"
                        ? $" Profiles and events wait until: ino marketing verify {id:D}" : ""));
        });
        marketing.Subcommands.Add(mRepin);

        static string RepinText(JsonNode? d)
        {
            static IEnumerable<string> Items(JsonNode? n) => n is JsonArray arr ? arr.Select(x => x?.ToString() ?? "") : [];
            var x = d?["diff"];
            var lines = new List<string> { $"{d?["definition"]} {d?["from"]} → {d?["version"]} (after the move: {d?["statusAfter"]?.ToString().ToLowerInvariant()})" };
            if (x?["previousUnavailable"]?.GetValue<bool>() == true) lines.Add("  the current version can no longer be read: no comparison is possible, review the whole summary");
            void Section(string title, string key) { if (x?[key] is JsonArray { Count: > 0 } a) lines.Add($"  {title,-22} {string.Join("; ", Items(a))}"); }
            Section("operations added", "operationsAdded"); Section("operations removed", "operationsRemoved");
            Section("fields added", "fieldsAdded"); Section("fields removed", "fieldsRemoved");
            Section("events newly sent", "eventsNewlySent"); Section("events no longer sent", "eventsNoLongerSent");
            Section("settings added", "settingsAdded"); Section("settings dropped", "settingsDropped");
            if (x?["unsubscribeReadingLost"]?.GetValue<bool>() == true) lines.Add("  NOTE                   the new version no longer reads unsubscribes made in the tool");
            if (x?["unsubscribeReadingAdded"]?.GetValue<bool>() == true) lines.Add("  NOTE                   the new version reads unsubscribes made in the tool");
            lines.Add($"  sends to               {string.Join(", ", Items(d?["hosts"]))}");
            lines.Add($"  review hash            {d?["reviewHash"]}");
            return string.Join("\n", lines);
        }
        var defDir = new Argument<string>("folder") { Description = "A new folder for the package (connector.json and RULES.md)" };
        var defName = new Option<string>("--id") { Description = "The package id, local_<name> (lower case, digits and -)", Required = true };
        var defNew = new Command("new", "Write a marketing tool package skeleton that follows the engine's package rules; edit it, then `ino connector package save`") { defDir, defName };
        Bind(defNew, pr =>
        {
            var files = MarketingDefinitionScaffold.Create(pr.GetValue(defDir)!, pr.GetValue(defName)!);
            Write(ctx, pr.GetValue(json), new JsonObject { ["dir"] = pr.GetValue(defDir), ["files"] = new JsonArray(files.Select(f => (JsonNode)f).ToArray()) },
                $"Created {pr.GetValue(defDir)} ({files.Count} files). Edit connector.json for your tool (RULES.md lists what a package may do), then:\n" +
                "  ino connector package save connector.json\n  ino marketing connect --definition <id> --version 1.0.0 --setting apiUrl=https://… (shows what it sends)");
            return Task.CompletedTask;
        });
        var mDefinition = new Command("definition", "Your own marketing tool packages (definition files the engine runs)");
        mDefinition.Subcommands.Add(defNew);
        marketing.Subcommands.Add(mDefinition);
        root.Subcommands.Add(marketing);

        // ---------------------------------------------------------------- hub (MCP/CLI P6a: connector store)
        var hub = new Command("hub", "Connector store: approved connectors, and sending yours to Tryino for review");
        var hubList = new Command("list", "Connectors Tryino approved (signed catalog, verified here)");
        Bind(hubList, async pr =>
        {
            var c = await HubTool.ReadCatalogAsync(ctx, ct);
            if (!c.Result.Ok) throw new CliException(ExitCodes.Business, "STORE_NOT_TRUSTED", $"The store catalog was refused: {c.Result.Status}. Nothing from it is shown.");
            var entries = HubTool.Listed(c.Result.Payload!).ToList();
            var arr = new JsonArray(entries.Select(e => (JsonNode)new JsonObject { ["id"] = e.Id, ["name"] = e.Name, ["kind"] = e.Kind, ["category"] = e.Category, ["vendor"] = e.Vendor,
                ["languages"] = new JsonArray(e.Languages.Select(l => (JsonNode)l).ToArray()), ["version"] = e.Version, ["approvedAt"] = e.ApprovedAt.ToString("yyyy-MM-dd"),
                ["runtime"] = e.Runtime, ["shape"] = e.Definition?.Shape }).ToArray());
            var head = c.SignedByTryino ? $"Store catalog v{c.Result.Payload!.Version} (signed by Tryino)" : $"TEST catalog v{c.Result.Payload!.Version} (signed with an extra key, not by Tryino)";
            Write(ctx, pr.GetValue(json), new JsonObject { ["version"] = c.Result.Payload!.Version, ["signedByTryino"] = c.SignedByTryino, ["connectors"] = arr },
                entries.Count == 0 ? head + "\nNo approved connectors yet." : head + "\n" + string.Join("\n", entries.Select(e => $"{e.Id,-28} {(e.Definition is { } df ? $"[definition: {df.Shape}] " : e.Kind == "marketing" ? "[marketing] " : "")}{e.Name} · {e.Vendor} · {string.Join("/", e.Languages)} · approved {e.ApprovedAt:yyyy-MM-dd}")));
        });
        var hubId = new Argument<string>("id") { Description = "Connector id from `ino hub list`" };
        HubEntry Find(HubTool.Catalog c, string id) => !c.Result.Ok
            ? throw new CliException(ExitCodes.Business, "STORE_NOT_TRUSTED", $"The store catalog was refused: {c.Result.Status}.")
            : HubTool.Listed(c.Result.Payload!).FirstOrDefault(e => e.Id == id) ?? throw new CliException(ExitCodes.NotFound, "NOT_IN_STORE", $"{id} is not an approved connector.");
        var hubShow = new Command("show", "Details of an approved connector") { hubId };
        Bind(hubShow, async pr =>
        {
            var c = await HubTool.ReadCatalogAsync(ctx, ct);
            var e = Find(c, pr.GetValue(hubId)!);
            var node = JsonSerializer.SerializeToNode(e)!;
            Write(ctx, pr.GetValue(json), node, string.Join("\n",
                $"{e.Name} ({e.Id}) {e.Version} · {e.Vendor}", e.Description, $"platform {e.Platform} · {e.Category} · languages {string.Join(", ", e.Languages)} · licence {e.License}",
                e.Definition is { } dd ? $"definition package ({dd.Shape}, runs in Ino): {dd.Path} · {dd.Bytes} bytes · SHA-256 {dd.Sha256[..16]}…{(dd.Requires.Count > 0 ? " · requires " + string.Join(", ", dd.Requires) : "")}; approved by Tryino {e.ApprovedAt:yyyy-MM-dd}"
                : e.Kind == "marketing" ? $"marketing tool: connect it with `ino marketing connect`, check it with `ino connector test --kind marketing`; approved by Tryino {e.ApprovedAt:yyyy-MM-dd}"
                    : $"compatibility: {e.Suite} suite, self-reported; approved by Tryino {e.ApprovedAt:yyyy-MM-dd}",
                $"repository {e.Repository} · commit {e.Commit}{(e.Tag is null ? "" : $" ({e.Tag})")}",
                e.Definition is null ? $"get it: ino hub get {e.Id}" : $"get it: ino hub get {e.Id} --as-own local_<name>"));
        });
        var getOut = new Option<string?>("--out") { Description = "A definition package: the file to write (default <id>.connector.json; never overwritten)" };
        var getAsOwn = new Option<string?>("--as-own") { Description = "A definition package: write it as your own package with this id (local_<name>) so you can save it" };
        var hubGet = new Command("get", "A connector you run yourself: print the commands that fetch the approved commit (runs nothing). A definition package: download the approved file and check it")
            { hubId, getOut, getAsOwn };
        Bind(hubGet, async pr =>
        {
            var c = await HubTool.ReadCatalogAsync(ctx, ct);
            var e = Find(c, pr.GetValue(hubId)!);
            if (e.Runtime != "definition")
            {
                if (pr.GetValue(getOut) is not null || pr.GetValue(getAsOwn) is not null)
                    throw new CliException(ExitCodes.Usage, "NOT_A_DEFINITION", $"{e.Id} is a connector you run yourself: --out and --as-own are for definition packages.");
                Write(ctx, pr.GetValue(json), new JsonObject { ["id"] = e.Id, ["runtime"] = e.Runtime, ["mirror"] = e.Mirror, ["commit"] = e.Commit, ["commands"] = HubTool.GetCommands(e) },
                    HubTool.GetCommands(e));
                return;
            }
            // IL9b: the file the signed catalog names, checked before anything is written.
            var got = await HubTool.GetDefinitionAsync(ctx, e, pr.GetValue(getOut), pr.GetValue(getAsOwn), ct);
            var test = c.SignedByTryino ? "" : "TEST catalog, not signed by Tryino. ";
            var next = got.AsOwn
                ? $"It is your own package now ({got.Id}): Tryino's approval and later versions of {e.Id} do not follow it. If {got.Id} is already one of your packages, choose another id. Next:\n  ino connector package check {got.File}\n  ino connector package save {got.File}"
                : $"To save it in your environment it needs your own id: run again with --as-own local_<name> (or change \"id\" in the file).";
            var where = got.Shape switch
            {
                "marketing" => "Then connect it: ino marketing connect --definition <id> --version <v> … (or Power Apps → Marketing Activation → Your own package).",
                "posFile" => "Then pin it to a channel: Power Apps → Integrations → Channels → the channel → POS file format.",
                _ => "Then: ino connector package preview <id> <v> --set … and install (test tenants).",
            };
            Write(ctx, pr.GetValue(json), new JsonObject { ["id"] = e.Id, ["runtime"] = e.Runtime, ["file"] = got.File, ["savedAs"] = got.Id, ["version"] = got.Version, ["shape"] = got.Shape,
                ["bytes"] = got.Bytes, ["sha256"] = e.Definition!.Sha256, ["asOwn"] = got.AsOwn, ["signedByTryino"] = c.SignedByTryino },
                $"{test}Wrote {got.File} ({e.Id} {got.Version}, SHA-256 checked against the catalog).\n{next}\n{where}");
        });
        var subDir = new Option<string?>("--dir") { Description = "Connector project folder (default: current folder)" };
        var subCompany = new Option<string>("--company") { Description = "Your company", Required = true };
        var subName = new Option<string>("--contact-name") { Description = "Who Tryino should contact", Required = true };
        var subEmail = new Option<string>("--contact-email") { Description = "Where Tryino replies (approved, or rejected with the reason)", Required = true };
        var subNote = new Option<string?>("--note") { Description = "Anything the reviewer should know (no keys, no customer data)" };
        var subSuite = new Option<string?>("--suite") { Description = "Compatibility suite to attach: pos or ecommerce (default from the manifest's category)" };
        var subNoCheck = new Option<bool>("--no-check") { Description = "Send without the compatibility result (the review takes longer)" };
        var subYes = new Option<bool>("--yes") { Description = "Send without asking (after reading the preview with --dry-run)" };
        var subDry = new Option<bool>("--dry-run") { Description = "Show exactly what would be sent, send nothing" };
        var subReport = new Option<string?>("--marketing-report") { Description = "A marketing tool: the result of `ino connector test --kind marketing --json` saved to a file" };
        var hubSubmit = new Command("submit", "Send your connector to Tryino for review. Private: only Tryino sees it; nothing is published until Tryino approves")
        { subDir, subCompany, subName, subEmail, subNote, subSuite, subNoCheck, subYes, subDry, subReport };
        Bind(hubSubmit, async pr =>
        {
            var dir = Path.GetFullPath(pr.GetValue(subDir) ?? ctx.WorkingDir ?? Directory.GetCurrentDirectory());
            var manifest = HubTool.LoadManifest(dir);
            var git = HubTool.ReadGit(ctx, dir);
            JsonObject? check = null;
            if (!pr.GetValue(subNoCheck) && manifest.Kind == "marketing")
            {
                // P1c.2: a marketing tool is checked from the CLI against the tool (not by the engine): attach that report.
                var file = pr.GetValue(subReport) ?? throw new CliException(ExitCodes.Usage, "NO_REPORT",
                    "A marketing tool sends its contract test: `ino connector test --kind marketing --url … --json > report.json`, then --marketing-report report.json (or --no-check).");
                check = HubTool.MarketingCheck(file);
            }
            else if (!pr.GetValue(subNoCheck))
            {
                var resolved = ctx.Profiles.Resolve(pr.GetValue(profile));
                Banner(ctx, resolved);
                var client = ctx.ClientFactory(resolved.Connection, ctx.Profiles.LockFor(resolved));
                var suite = pr.GetValue(subSuite) ?? (manifest.Category.Equals("POS", StringComparison.OrdinalIgnoreCase) ? "pos" : "ecommerce");
                if (suite is not ("pos" or "ecommerce")) throw new CliException(ExitCodes.Usage, "BAD_SUITE", "--suite: pos or ecommerce.");
                var res = await client.GetAsync($"certification?suite={suite}&hours=720", ct);
                if (ChannelClient.ExitCodeOf(res) != ExitCodes.Ok || ((res.Body as JsonObject)?["Data"] as JsonObject) is not { } d)
                    throw new CliException(ExitCodes.Business, "CHECK_FAILED", "Could not read the compatibility result. Run `ino connector check` first, or send with --no-check.");
                check = d.DeepClone().AsObject();
                check["environment"] = (await client.WhoAmIAsync(ct))?.Environment;
            }
            var submission = HubTool.BuildSubmission(manifest, git, check, pr.GetValue(subCompany)!, pr.GetValue(subName)!, pr.GetValue(subEmail)!, pr.GetValue(subNote));
            var preview = submission.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            if (pr.GetValue(subDry)) { Write(ctx, pr.GetValue(json), submission, "Would send (nothing sent):\n" + preview); return; }
            if (!pr.GetValue(subYes))
            {
                if (!ctx.InputIsTerminal || ctx.IsCi) throw new CliException(ExitCodes.Usage, "CONFIRM", "Check the preview with --dry-run, then send with --yes.");
                ctx.Err.WriteLine("This goes to Tryino only (not public):\n" + preview + "\nSend it? [y/N] ");
                if ((ctx.In.ReadLine() ?? "").Trim().ToLowerInvariant() is not ("y" or "yes")) { ctx.Err.WriteLine("Not sent."); exit = ExitCodes.Business; return; }
            }
            var intake = ctx.Env.TryGetValue("INO_HUB_INTAKE_URL", out var iu) && !string.IsNullOrWhiteSpace(iu) ? iu! : HubTool.DefaultIntakeUrl;
            if (!Uri.TryCreate(intake, UriKind.Absolute, out var intakeUri) || intakeUri.Scheme != Uri.UriSchemeHttps)
                throw new CliException(ExitCodes.Usage, "BAD_URL", "INO_HUB_INTAKE_URL must be an https URL.");
            using var http = new HttpClient(ctx.HubHandler ?? new SocketsHttpHandler { AllowAutoRedirect = false }, disposeHandler: ctx.HubHandler is null) { Timeout = TimeSpan.FromSeconds(30) };
            HttpResponseMessage sent;
            try { sent = await http.PostAsync(intakeUri, new StringContent(submission.ToJsonString(), System.Text.Encoding.UTF8, "application/json"), ct); }
            catch (HttpRequestException e) { throw new CliException(ExitCodes.Network, "NOT_SENT", $"Not sent: {e.Message}. Try again later."); }
            using (sent)
            {
                var code = (int)sent.StatusCode;
                if (code == 429) throw new CliException(ExitCodes.RateLimited, "NOT_SENT", "Too many submissions from this network in the last hour. Try again later.");
                if (code is < 200 or >= 300) throw new CliException(ExitCodes.Network, "NOT_SENT", $"Tryino's intake answered {code}; nothing was recorded. Try again later.");
                string? id = null;
                try { id = JsonNode.Parse(await sent.Content.ReadAsStringAsync(ct))?["submissionId"]?.GetValue<string>(); } catch (JsonException) { }
                Write(ctx, pr.GetValue(json), new JsonObject { ["submitted"] = true, ["submissionId"] = id, ["id"] = manifest.Id, ["commit"] = git.Commit },
                    $"Sent to Tryino{(id is null ? "" : $" (submission {id})")}. A reviewer replies to {pr.GetValue(subEmail)}." +
                    (manifest.Kind == "marketing" ? " Marketing connectors are reviewed once the marketing kit (P1c) is out." : ""));
            }
        });
        hub.Subcommands.Add(hubList); hub.Subcommands.Add(hubShow); hub.Subcommands.Add(hubGet); hub.Subcommands.Add(hubSubmit);
        root.Subcommands.Add(hub);

        // ---------------------------------------------------------------- mcp
        var mcp = new Command("mcp", "Model Context Protocol");
        var toolsets = new Option<string>("--toolsets") { Description = "Tool sets: dev, integration (comma separated)", DefaultValueFactory = _ => "dev" };
        var allowWrites = new Option<bool>("--allow-writes") { Description = "Offer ino_api_write (TEST profiles only; the client asks before every call)" };
        var serve = new Command("serve", "Run a local MCP server over stdio with this profile (for Claude Code, Claude Desktop, Cursor, VS Code)") { toolsets, allowWrites };
        Bind(serve, async pr =>
        {
            var (resolved, stored) = ctx.Profiles.ResolveStoredOnly(pr.GetValue(profile));
            var sets = pr.GetValue(toolsets)!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);
            if (sets.Count == 0 || sets.Any(x => x is not ("dev" or "integration")))
                throw new CliException(ExitCodes.Usage, "BAD_TOOLSETS", "--toolsets takes dev and/or integration.");
            if (pr.GetValue(allowWrites) && resolved.Connection.IsLive)
                throw new CliException(ExitCodes.Usage, "LIVE_WRITES", "--allow-writes is only for test profiles; the MCP server never writes to live.");
            if (pr.GetValue(allowWrites) && !stored.McpWrites)
                throw new CliException(ExitCodes.Usage, "MCP_WRITES_OFF", $"Profile {resolved.ProfileName} does not allow MCP writes. If you want them, run `ino profile set {resolved.ProfileName} --mcp-writes on` yourself.");
            string? engineEnv = null;
            try { engineEnv = (await ctx.ClientFactory(resolved.Connection, ctx.Profiles.LockFor(resolved)).WhoAmIAsync(ct))?.Environment; }
            catch (CliException e) { ctx.Err.WriteLine($"ino mcp serve: the engine did not confirm the environment ({e.Message}); live rules apply."); }
            var settings = new McpSettings { Ctx = ctx, Profile = resolved, AllowWrites = pr.GetValue(allowWrites) && stored.McpWrites, Toolsets = sets, EngineEnvironment = engineEnv };
            ctx.Err.WriteLine($"ino mcp serve: config {ctx.Profiles.ConfigPath}; profile {resolved.ProfileName}, environment {settings.Environment} (profile {resolved.Connection.Environment}, engine {engineEnv ?? "unconfirmed"}), tool sets {string.Join(",", sets)}{(McpServe.WritesAvailable(settings) ? ", writes offered" : "")}.");
            exit = await McpServe.RunAsync(settings, ctx.McpInput, ctx.McpOutput, ct);
        });
        mcp.Subcommands.Add(serve);
        root.Subcommands.Add(mcp);

        // ---------------------------------------------------------------- agent
        var agent = new Command("agent", "Set up AI clients for Ino work");
        var client = new Option<string>("--client") { Description = "claude-code, claude-desktop, cursor or vscode", Required = true };
        client.AcceptOnlyFromAmong(AgentSetup.Clients);
        var skillsScope = new Option<string>("--skills-scope") { Description = "global (~/.claude/skills) or local (<project>/.claude/skills); Claude Code only", DefaultValueFactory = _ => "global" };
        skillsScope.AcceptOnlyFromAmong("global", "local");
        var dryRun = new Option<bool>("--dry-run") { Description = "Show what would change; change nothing" };
        var setup = new Command("setup", "Register `ino mcp serve` with a client and install the Ino skills. Existing settings are kept and backed up (*.bak)") { client, skillsScope, dryRun };
        Bind(setup, pr =>
        {
            var home = ctx.Env.TryGetValue("HOME", out var hm) && !string.IsNullOrEmpty(hm) ? hm : System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
            var name = pr.GetValue(profile) ?? ctx.Profiles.Load().DefaultProfile
                ?? throw new CliException(ExitCodes.Usage, "NO_PROFILE", "No profile yet. Create one with `ino profile add`, then run setup again.");
            var (stored, _) = ctx.Profiles.ResolveStoredOnly(name);
            if (stored.Connection.IsLive)
                ctx.Err.WriteLine($"Note: profile {name} is live. The MCP server will allow only three side-effect-free queries there; for integration work use a test profile.");
            var s = new AgentSetup(home, ctx.WorkingDir ?? Directory.GetCurrentDirectory(), ctx.RunProcess);
            var c = pr.GetValue(client)!;
            var steps = new List<SetupStep> { s.RegisterServer(c, name, pr.GetValue(dryRun)) };
            if (c == "claude-code") steps.AddRange(s.InstallSkills(pr.GetValue(skillsScope)!, pr.GetValue(dryRun)));
            if (c == "claude-desktop") ctx.Err.WriteLine("Skills: Claude Desktop does not read ~/.claude/skills; upload them in Settings → Capabilities if you want them there.");
            var verb = pr.GetValue(dryRun) ? "would change" : "changed";
            Write(ctx, pr.GetValue(json), new JsonObject
            {
                ["dryRun"] = pr.GetValue(dryRun), ["client"] = c, ["profile"] = name,
                ["steps"] = new JsonArray(steps.Select(x => (JsonNode)new JsonObject { ["kind"] = x.Kind, ["target"] = x.Target, ["detail"] = x.Detail, ["changed"] = x.Changed }).ToArray()),
            }, string.Join("\n", steps.Select(x => $"{(x.Changed ? verb : "unchanged"),-13} {x.Kind,-10} {x.Target}" + (x.Kind == "mcp-server" ? $"\n              {x.Detail}" : "")))
               + "\nWrites stay off. To allow them on a test profile: `ino profile set " + name + " --mcp-writes on`, then add --allow-writes to the server's args.");
            return Task.CompletedTask;
        });
        agent.Subcommands.Add(setup);
        root.Subcommands.Add(agent);

        var version = new Command("version", "Print the version");
        Bind(version, pr => { Write(ctx, pr.GetValue(json), new JsonObject { ["version"] = Version }, Version); return Task.CompletedTask; });
        root.Subcommands.Add(version);

        var parse = root.Parse(args);
        if (parse.Errors.Count > 0)
        {
            WriteError(ctx, args.Contains("--json"), "USAGE", string.Join("; ", parse.Errors.Select(e => e.Message)), "See `ino --help`.");
            return ExitCodes.Usage;
        }
        var config = new InvocationConfiguration { Output = ctx.Out, Error = ctx.Err };
        var code = await parse.InvokeAsync(config, ct);
        return code != 0 && exit == ExitCodes.Ok ? code : exit;
    }

    static string? ReadCredential(CliContext ctx, string prompt)
    {
        if (ctx.InputIsTerminal) { ctx.Err.Write(prompt); return ctx.ReadHidden(); }
        return ctx.In.ReadLine();
    }

    static JsonObject? ParseBody(CliContext ctx, string? data)
    {
        if (data is null) return null;
        string text;
        if (data == "@-") text = ctx.In.ReadToEnd();
        else if (data.StartsWith('@'))
        {
            var path = data[1..];
            if (!File.Exists(path)) throw new CliException(ExitCodes.Usage, "NO_FILE", $"{path} does not exist.");
            text = File.ReadAllText(path);
        }
        else text = data;
        try
        {
            return JsonNode.Parse(text) as JsonObject ?? throw new CliException(ExitCodes.Usage, "BAD_BODY", "The body must be a JSON object.");
        }
        catch (JsonException e) { throw new CliException(ExitCodes.Usage, "BAD_BODY", $"The body is not valid JSON: {e.Message}"); }
    }

    /// <summary>--events must name public types: the feed refuses others with 400, so check against the catalog first.</summary>
    static async Task CheckTypes(ChannelClient client, string types, CancellationToken ct)
    {
        var known = EventCatalogView.Public(await client.CatalogAsync(ct)).Select(t => t.Type).ToHashSet(StringComparer.Ordinal);
        var bad = types.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(t => !known.Contains(t)).ToList();
        if (bad.Count > 0) throw new CliException(ExitCodes.Usage, "UNKNOWN_EVENT", $"Not public event types: {string.Join(", ", bad)}. See `ino events types`.");
    }

    /// <summary>Event data can hold personal data: on a live profile, reading the feed needs --live.</summary>
    static void RequireLiveFlag(Resolved r, bool live, string what)
    {
        if (r.Connection.IsLive && !live)
            throw new CliException(ExitCodes.Usage, "LIVE_FLAG_NEEDED", $"{what} reads LIVE events, which can hold personal data. Add --live if you mean it.");
    }

    /// <summary>The one-time subscription secret in a create/rotate response (Data.secret or secret).</summary>
    static string? FindSecret(JsonNode? body) =>
        ((body as JsonObject)?["Data"] as JsonObject)?["secret"]?.GetValue<string>() ?? (body as JsonObject)?["secret"]?.GetValue<string>();

    /// <summary>Which profile and environment a call uses, on stderr, so a person or an agent always sees where it is going.</summary>
    static void Banner(CliContext ctx, Resolved r) =>
        ctx.Err.WriteLine($"[{r.ProfileName ?? "env"} · {(r.Connection.IsLive ? "LIVE" : "test")} · {r.Connection.Channel}]");

    static void WriteResponse(CliContext ctx, bool asJson, Connection conn, ChannelResponse res, string operation, int exitCode)
    {
        var body = Redaction.Fields(res.Body?.DeepClone());
        var hint = ChannelClient.HintFor(res);
        var ok = exitCode == ExitCodes.Ok;
        if (asJson)
        {
            var o = new JsonObject { ["status"] = res.Status, ["operation"] = operation, ["path"] = res.Path, ["responseCode"] = ChannelClient.ResponseCodeOf(res.Body), ["body"] = body ?? (JsonNode?)JsonValue.Create(res.RawBody) };
            if (hint is not null) o["hint"] = hint;
            (ok ? ctx.Out : ctx.Err).WriteLine(Redaction.Strings(o.ToJsonString(Compact), [conn.ApiKey, conn.Secret]));
            return;
        }
        var text = $"HTTP {res.Status} {res.Method} {res.Path}\n\n{(body is null ? res.RawBody : body.ToJsonString(Pretty))}";
        if (hint is not null) text += "\n\nHint: " + hint;
        text = Redaction.Strings(text, [conn.ApiKey, conn.Secret]);
        if (text.Length > MaxHumanOutput) text = text[..MaxHumanOutput] + "\n…(cut at 64 KB; use --json for the full response)";
        (ok ? ctx.Out : ctx.Err).WriteLine(text);
    }

    static void Write(CliContext ctx, bool asJson, JsonNode node, string human) => ctx.Out.WriteLine(asJson ? node.ToJsonString() : human);

    static string Mark(string? status) => status switch { "ok" => "✓", "todo" => "○", "warn" => "!", "info" => "–", _ => "?" };

    /// <summary>The checklist for a person: one line per step, channel lines under it, the next step indented.</summary>
    internal static string SetupText(JsonObject data)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Setup of {Safe(data["tenant"])} ({Safe(data["environment"])})");
        foreach (var step in data["steps"]?.AsArray().OfType<JsonObject>() ?? [])
        {
            sb.AppendLine($"{Mark(step["status"]?.ToString())} {Safe(step["title"]),-20} {Safe(step["detail"])}");
            if (step["next"] is { } next) sb.AppendLine($"    → {Safe(next)}");
            foreach (var item in step["items"]?.AsArray().OfType<JsonObject>() ?? [])
            {
                sb.AppendLine($"    {Mark(item["status"]?.ToString())} {Safe(item["key"]),-18} {Safe(item["detail"])}");
                if (item["next"] is { } n) sb.AppendLine($"        → {Safe(n)}");
            }
        }
        var sum = data["summary"];
        sb.Append($"{sum?["todo"]} to do, {sum?["warn"]} to check, {sum?["ok"]} done.");
        if (sum?["unknown"]?.GetValue<int>() is > 0 and var unknown) sb.Append($" {unknown} could not be read right now.");
        return sb.ToString();
    }

    /// <summary>Text from the engine that callers or firm admins wrote (paths, channels, errors): control characters never reach the terminal (review MC1 1).</summary>
    internal static string Safe(JsonNode? n) => n is null ? "" : new string(n.ToString().Select(c => char.IsControl(c) ? '?' : c).ToArray());

    static void WriteError(CliContext ctx, bool asJson, string code, string message, string? hint = null)
    {
        if (asJson) ctx.Err.WriteLine(new JsonObject { ["error"] = new JsonObject { ["code"] = code, ["message"] = message, ["hint"] = hint } }.ToJsonString());
        else ctx.Err.WriteLine($"error: {message}" + (hint is null ? "" : $"\nhint: {hint}"));
    }
}

/// <summary>
/// Live profiles: reads are allowed; a write needs --live and --yes together, or a typed "yes" on a terminal; CI never writes to live (agent keys come
/// in P0c). The environment is what the profile says, and environment variables can only raise it to live.
/// </summary>
public static class LiveGuard
{
    public static void Check(CliContext ctx, Connection conn, string operation, bool yes, bool live)
    {
        if (Catalog.ClassOf(operation) == OperationClass.Read) return;
        CheckWrite(ctx, conn, operation, yes, live);
    }

    /// <summary>The same rule for anything that changes data (webhook subscriptions, job submissions).</summary>
    public static void CheckWrite(CliContext ctx, Connection conn, string operation, bool yes, bool live)
    {
        if (!conn.IsLive) return;
        if (ctx.IsCi)
            throw new CliException(ExitCodes.Usage, "LIVE_WRITE_IN_CI", $"{operation} changes live data and CI=true. Live writes from CI are not allowed yet.");
        if (yes && live) return;
        if (yes) throw new CliException(ExitCodes.Usage, "CONFIRMATION_NEEDED", $"{operation} changes live data. Use --live together with --yes.");
        if (!ctx.InputIsTerminal)
            throw new CliException(ExitCodes.Usage, "CONFIRMATION_NEEDED", $"{operation} changes live data. Add --live --yes to confirm.");
        ctx.Err.Write($"{operation} changes LIVE data on channel {conn.Channel}. Type yes to continue: ");
        if (!string.Equals(ctx.In.ReadLine()?.Trim(), "yes", StringComparison.Ordinal))
            throw new CliException(ExitCodes.Usage, "NOT_CONFIRMED", "Not confirmed; nothing was sent.");
    }
}

/// <summary>Where a newly issued secret goes (`keys create` / `keys rotate`).</summary>
sealed record SecretDestinations(string? Save, string? EnvFile, bool Show, string? Update);
