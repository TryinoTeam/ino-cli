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
        var doctor = new Command("doctor", "Check the profile, keychain, network, clock and key");
        Bind(doctor, async pr =>
        {
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
                ? $"Created {dir} ({files.Count} files): your marketing tool for Ino Loyalty.\nNext: cd {dir}, `ino marketing connect --name <name> --url https://<host>/ino --env-file ma.env` (or a test secret), copy INO_MA_SIGNING_SECRET into .env, {ConnectorScaffold.MarketingRunHint(pr.GetValue(lang)!)};\nthen `ino connector test --kind marketing --url http://localhost:8080/ino --env-file ma.env` and replace {(pr.GetValue(lang) == "python" ? "connector/tool.py" : "src/tool.js")} with your tool's API."
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
            Write(ctx, pr.GetValue(json), result, $"{(created ? "Saved" : "Already saved")} {data?["id"]} {data?["version"]} (sha256 {data?["sha256"]?.ToString()[..12]}…). " +
                "It is not installed anywhere yet.");
        });
        var packageList = new Command("list", "Your own connector definitions in this tenant, with versions and how many installations use each");
        Bind(packageList, async pr =>
        {
            var (name, prof, _) = ctx.Profiles.ResolveForLogin(pr.GetValue(profile));
            var data = await InoLogin.ManageAsync(ctx, name, InoLogin.TargetOf(prof.BaseUrl), HttpMethod.Get, "connector-packages", null, null, ct);
            var rows = data?["packages"]?.AsArray() ?? [];
            var lines = rows.OfType<JsonObject>().Select(r => $"{r["id"],-30} {r["version"],-10} {r["status"],-12} {r["installations"]} installation(s)  {r["bytes"]} bytes");
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
        var mUrl = new Option<string>("--url") { Description = "Your tool's https address; Ino posts every signed request there", Required = true };
        var mEnv = new Option<string?>("--env-file") { Description = "Write INO_MA_CONNECTION_ID and INO_MA_SIGNING_SECRET to this new file (mode 0600; an existing file is not overwritten)" };
        var mShow = new Option<bool>("--show-secret") { Description = "Print the signing secret here (it ends up in your terminal history or an AI agent's transcript)" };
        var mConnect = new Command("connect", "Add your own marketing tool. Ino signs every call with a new secret, shown once: keep it with --env-file or --show-secret. Then run `ino marketing verify`")
            { mName, mUrl, mEnv, mShow };
        Bind(mConnect, async pr =>
        {
            var (name, prof, _) = ctx.Profiles.ResolveForLogin(pr.GetValue(profile));
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
            Write(ctx, pr.GetValue(json), output, $"Verified: the connection is {data?["status"]?.ToString().ToLowerInvariant()}. Ino now sends profiles, events, unsubscribes and deletions to it.");
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
        marketing.Subcommands.Add(mDisconnect);
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
                ["languages"] = new JsonArray(e.Languages.Select(l => (JsonNode)l).ToArray()), ["version"] = e.Version, ["approvedAt"] = e.ApprovedAt.ToString("yyyy-MM-dd") }).ToArray());
            var head = c.SignedByTryino ? $"Store catalog v{c.Result.Payload!.Version} (signed by Tryino)" : $"TEST catalog v{c.Result.Payload!.Version} (signed with an extra key, not by Tryino)";
            Write(ctx, pr.GetValue(json), new JsonObject { ["version"] = c.Result.Payload!.Version, ["signedByTryino"] = c.SignedByTryino, ["connectors"] = arr },
                entries.Count == 0 ? head + "\nNo approved connectors yet." : head + "\n" + string.Join("\n", entries.Select(e => $"{e.Id,-28} {(e.Kind == "marketing" ? "[marketing] " : "")}{e.Name} · {e.Vendor} · {string.Join("/", e.Languages)} · approved {e.ApprovedAt:yyyy-MM-dd}")));
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
                e.Kind == "marketing" ? $"marketing tool: connect it with `ino marketing connect`, check it with `ino connector test --kind marketing`; approved by Tryino {e.ApprovedAt:yyyy-MM-dd}"
                    : $"compatibility: {e.Suite} suite, self-reported; approved by Tryino {e.ApprovedAt:yyyy-MM-dd}",
                $"repository {e.Repository} · commit {e.Commit}{(e.Tag is null ? "" : $" ({e.Tag})")}", $"get it: ino hub get {e.Id}"));
        });
        var hubGet = new Command("get", "Print the commands that fetch the exact approved commit (runs nothing)") { hubId };
        Bind(hubGet, async pr =>
        {
            var c = await HubTool.ReadCatalogAsync(ctx, ct);
            var e = Find(c, pr.GetValue(hubId)!);
            Write(ctx, pr.GetValue(json), new JsonObject { ["id"] = e.Id, ["mirror"] = e.Mirror, ["commit"] = e.Commit, ["commands"] = HubTool.GetCommands(e) }, HubTool.GetCommands(e));
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
