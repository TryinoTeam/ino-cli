namespace Ino.Cli;

public enum OperationClass { Read, Write }

public sealed record OperationInfo(string Name, OperationClass Class, string Purpose);

/// <summary>
/// The channel API operations (engine: OperationRegistry). Reads are an allowlist: an operation that is not listed as a read, including one added to the
/// engine later, is treated as a write. Contract tests keep this list equal to the engine's registry and to DeveloperAgentFacts.
/// </summary>
public static class Catalog
{
    public static readonly IReadOnlyList<OperationInfo> Operations =
    [
        new("campaign.inquiry", OperationClass.Read, "price a cart: which campaign rewards it would get (call on every cart change); with a voucher code it may mark an expired voucher as expired"),
        new("customer.get360", OperationClass.Read, "read a member: profile, point balance, tier, segments, consent"),
        new("customer.create", OperationClass.Write, "register a member directly (no SMS check)"),
        new("customer.register.initiate", OperationClass.Write, "start SMS-verified registration (sends an SMS)"),
        new("customer.register.confirm", OperationClass.Write, "finish SMS-verified registration with the code"),
        new("transaction.create", OperationClass.Write, "commit a completed sale: earns/spends points and applies campaign rewards (call once after checkout)"),
        new("transaction.return.inquiry", OperationClass.Read, "preview what returning items does to points and discounts, nothing saved"),
        new("transaction.return.quote", OperationClass.Read, "dry-run refund quote for a return, nothing saved"),
        new("transaction.return.commit", OperationClass.Write, "commit a return or cancellation"),
        new("transaction.reconcile", OperationClass.Write, "send sales made offline later (reconciliation)"),
        new("transaction.batch", OperationClass.Write, "send up to 500 sales and returns in one call (nightly offline sync); processed in order in the background"),
        new("transaction.batch.status", OperationClass.Read, "read a batch's progress and each row's result"),
        new("member.snapshot", OperationClass.Read, "read the member list for offline tills (full list or changes since a time): codes, cards, short names, tier, balance, phone hash"),
        new("voucher.pack", OperationClass.Read, "read the voucher pack for offline tills (usable vouchers as keyed code hashes, or changes since a time)"),
        new("rules.export", OperationClass.Read, "read the simple rules an offline till can apply (earn rate, tier benefits, simple campaigns) and which campaigns apply only at sync"),
        new("transaction.file.import", OperationClass.Write, "import a POS file (CSV or JSON Lines; sales, payments and returns) as batches; read results with transaction.batch.status"),
        new("order.loyalty", OperationClass.Read, "read the current net loyalty result of an order already sent (for order pages)"),
        new("order.sync", OperationClass.Write, "sync an order's later status changes"),
        new("voucher.validate", OperationClass.Read, "check whether a voucher code can be used; marks an expired voucher as expired"),
        new("voucher.redeem", OperationClass.Write, "redeem a voucher against an order"),
        new("referral.info", OperationClass.Write, "read a member's refer-a-friend code, link and invites (creates the member's code on first call)"),
        new("referral.apply", OperationClass.Write, "attach a friend's referral code to a new member"),
        new("member.session.create", OperationClass.Write, "create a short session token for the member panel on your site"),
        new("game.programs", OperationClass.Read, "list the games a member can play"),
        new("game.session.create", OperationClass.Write, "start a game session for a member"),
    ];

    public static OperationInfo? Find(string name) => Operations.FirstOrDefault(o => o.Name == name);

    /// <summary>Unknown operations count as writes.</summary>
    public static OperationClass ClassOf(string name) => Find(name)?.Class ?? OperationClass.Write;
}
