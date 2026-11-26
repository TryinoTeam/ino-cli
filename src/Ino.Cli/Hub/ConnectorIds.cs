using System.Text.RegularExpressions;

// The CLI compiles this file too (linked source, like HubEnvelope.cs); there it gets its own namespace.
#if INO_CLI
namespace Ino.Cli.Hub;
#else
namespace InoLoyalty.Api.Hub;
#endif

/// <summary>
/// Connector id grammar (docs/INO_HUB.md §17, H1a.2). Two forms that can never collide:
/// <list type="bullet">
/// <item><b>Plain</b> <c>ikas</c>, <c>sap-commerce-cloud</c>: Tryino's built-in (image) definitions, and self-hosted Hub entries.</item>
/// <item><b>Package</b> <c>publisher_name</c>, e.g. <c>acme_netsuite</c>: every connector published by someone else. Plain ids never contain
/// <c>_</c>, so a package can never take a built-in id; <c>_</c> also keeps ids from looking like host names (<c>acme.com</c>).</item>
/// </list>
/// Publishers <c>ino</c>, <c>tryino</c> and look-alikes (<see cref="IsReservedPublisher"/>) are Tryino's only. The same rules are mirrored in
/// ops/hub/sign_catalog.py, ops/hub/intake/hub-submission.js and ops/hub/mirror.sh (test vectors: tests ConnectorIdTests).
/// </summary>
public static partial class ConnectorIds
{
    public const int MaxLength = 60;
    // One segment: starts with a letter, hyphens only between letters/digits (no trailing "-", no "--", so no "xn--" look-alikes).
    const string Segment = "[a-z][a-z0-9]*(?:-[a-z0-9]+)*";

    [GeneratedRegex(@"\A[a-z][a-z0-9-]{1,59}\z")] private static partial Regex PlainRe();
    [GeneratedRegex(@"\A(" + Segment + ")_(" + Segment + @")\z")] private static partial Regex PackageRe();

    /// <summary>Built-in or self-hosted id (the grammar every engine release accepted before H1a.2).</summary>
    public static bool IsPlain(string? id) => id is not null && PlainRe().IsMatch(id);

    /// <summary><c>publisher_name</c>: publisher 2–20 characters, name 2–39, at most 60 in all.</summary>
    public static bool IsPackage(string? id)
    {
        if (id is null || id.Length > MaxLength || PackageRe().Match(id) is not { Success: true } m) return false;
        return m.Groups[1].Length is >= 2 and <= 20 && m.Groups[2].Length is >= 2 and <= 39;
    }

    public static bool IsValid(string? id) => IsPlain(id) || IsPackage(id);

    /// <summary>The publisher of a package id, or null for a plain id.</summary>
    public static string? Publisher(string id) => IsPackage(id) ? id[..id.IndexOf('_')] : null;

    /// <summary>Publisher of a firm's own definitions installed without publishing (H1b, Kapı T M8): <c>local_*</c> ids never appear in the Hub, so a
    /// firm's local package can't squat or shadow a published connector.</summary>
    public const string LocalPublisher = "local";

    /// <summary>A Hub entry or submission id: valid, and not a local package id.</summary>
    public static bool IsHubId(string? id) => IsValid(id) && Publisher(id!) != LocalPublisher;

    /// <summary>Tryino's publishers and look-alikes: <c>ino</c>, <c>tryino</c>, anything starting with <c>ino-</c> or <c>tryino</c>, or containing
    /// <c>inoloyalty</c>/<c>tryino</c>. Only trusted (Tryino-signed or image) definitions may use them.</summary>
    public static bool IsReservedPublisher(string publisher)
        => publisher is "ino" or "tryino" || publisher.StartsWith("ino-", StringComparison.Ordinal) || publisher.StartsWith("tryino", StringComparison.Ordinal)
           || publisher.Contains("inoloyalty", StringComparison.Ordinal) || publisher.Contains("tryino", StringComparison.Ordinal);
}
