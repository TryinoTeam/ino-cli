# Real-time Channel API — partner reference

This is the reference for the `POST /api/v1/{channelCode}/{operation}` endpoint — the real-time
request/response API that point-of-sale, e-commerce and call-center channels use to earn/spend points,
run campaigns, manage customers and vouchers.

**Out of scope:** `/partner/v1/*` (used by loyalty program *partners* redeeming vouchers issued by Ino
Loyalty) is a separate surface with its own auth (`X-Partner-Key`) and error envelope (`{"error":{"code",
"message"}}`). It is not covered by this document.

## 1 · Base URL and versioning

```
POST https://<your-host>/api/v1/{channelCode}/{operation}
```

- `channelCode` identifies your integration (e.g. `MODERN_ECOM`); it is provisioned by Ino Loyalty and
  tied to your credentials and field mapping.
- `v1` is the current, stable version. We do not make breaking changes within `v1` — only additive ones
  (new optional fields, new operations). A breaking change ships as `/api/v2/...` while `v1` keeps
  working; we will tell you the sunset timeline before retiring a version. There is no `v2` yet, so there
  are no `Deprecation`/`Sunset` response headers today — they will be added when `v2` ships.
- Every channel gets its own field mapping (request/response field names, units, auth strategy). The
  examples below use the **standard mapping** (`src/InoLoyalty.Api/mappings/example_modern_ecom.json`),
  which is also the template new integrations should copy and adapt. A channel that needs different field
  names (e.g. a legacy POS protocol) gets a custom mapping — see §6 for how that changes error handling.

## 2 · Authentication

Two strategies, chosen per channel at provisioning time.

### API_KEY_HMAC

Every request carries three headers:

| Header | Meaning |
|---|---|
| `X-Api-Key` | Your channel's API key |
| `X-Timestamp` | Unix seconds, must be within 300s of server time (replay protection) |
| `X-Signature` | `hex(HMAC-SHA256(hmacSecret, "{X-Timestamp}." + rawRequestBody))`, lowercase |

```
signature = HMACSHA256(hmac_secret, timestamp + "." + raw_body)
```

#### Signature version 2 (recommended)

Version 1 above signs only the timestamp and the body. Version 2 also signs **what you are calling** and a **one-time value**, so a captured
request cannot be sent again or pointed at another operation. Send two more headers and sign a longer string:

| Header | Meaning |
|---|---|
| `X-Signature-Version` | `2` |
| `X-Nonce` | 16 to 128 letters, digits, `-` or `_`; a new random value for every request (a UUID without dashes is fine) |
| `X-Signature` | `hex(HMAC-SHA256(hmacSecret, "v2.{X-Timestamp}.{X-Nonce}.{METHOD}.{path-and-query}." + rawRequestBody))`, lowercase |

```
signature = HMACSHA256(hmac_secret, "v2." + timestamp + "." + nonce + "." + "POST" + "." + "/api/v1/MY_CHANNEL/transaction.create" + "." + raw_body)
```

`METHOD` is upper case. `path-and-query` is the path exactly as you call it, with the query string if there is one (for example
`/t/tabc1234/api/v1/MY_CHANNEL/events?after=10`). A nonce is accepted once; when you retry a request, sign it again with a new nonce and
timestamp (the `requestId` in the body keeps the retry idempotent). Version 1 keeps working until your channel is switched to
"version 2 only" (`Channels:<code>:RequireSignatureV2`), which we do together with you.

Sign the **exact raw bytes** you send — not a re-serialized version of your JSON object, since whitespace
differences change the signature.

#### Managed API keys

Your administrator issues keys in **Settings → Developers → API keys**. A managed key starts with `ik_`
and comes with its own secret; the headers and the signature above are exactly the same. Each calling
system (a POS fleet, the online shop, a partner) should get its own key, so one can be rotated or revoked
without touching the others. A key may be limited to:

- **operations** — e.g. only `campaign.inquiry` and `transaction.create`; the webhook subscription endpoints
  need the `webhooks` permission and the bulk job endpoints the `jobs` permission. A call outside the key's
  permissions returns `ResponseCode: "FORBIDDEN"` with HTTP **403**;
- **IP addresses or ranges** — calls from any other address are rejected;
- **an expiry date**.

**POS chains: one key per central server.** Give every central server (for example "Istanbul server – 55 tills" and "Ankara server – 70
tills") its own key. Each key can carry its own **request limit per minute** (10–6000; empty = the default 600 per minute per address and key) —
set it in the key editor. Integrations → Channels → *channel* → **Connected central servers** then shows per server the last call, the last
batch (OK / failed rows), rows still pending and rows failed in the last 24 hours, and marks a server that has not called for more than 26 hours.

When a key is rotated, the old key keeps working for the overlap the administrator chose (24 hours, 1 hour or
not at all). Rejections say why: `Invalid api_key.`, `This API key has been revoked.`, `This API key has expired.`,
`This API key cannot be used from this IP address.` Keys set in the server configuration before managed keys
existed keep working unchanged.

### OAUTH2_CLIENT_CREDENTIALS

Send `Authorization: Bearer <token>`. Tokens must be signed with **RS256 or ES256** (HS256 is rejected).
We validate issuer, audience, expiry and the token's client id (`azp` or `appid` claim) against what was
provisioned for your channel.

Either way, auth failure returns `ResponseCode: "AUTHENTICATION_ERROR"` with HTTP **401**.

## 3 · Request envelope

Every operation's inbound mapping reads a `requestId` field from your JSON body — a **GUID you generate**.
It is your idempotency key (see §7) and is echoed back in every response as `RequestId`. If you omit it or
send something that isn't a valid GUID, we generate one for you — but that silently disables idempotency
for that call, so always send your own.

## 4 · Operations

22 operations are available. Field names in the examples follow the standard mapping; your channel's mapping may use other
names if you asked for custom field names. "Read" means the operation changes nothing you asked for. A read can still write an audit row,
or mark an expired voucher as expired. `ino api list` shows the same list with the same classes. The local MCP server (`ino mcp serve`)
is stricter: it reads only `order.loyalty`, `transaction.return.inquiry`, `transaction.return.quote` and `game.programs`, does not offer
`customer.get360`, and treats everything else as a write.

| Operation | Class | Purpose |
|---|---|---|
| `campaign.inquiry` | read | Price a cart: which campaign rewards it would get (call on every cart change); with a voucher code it may mark an expired voucher as expired |
| `customer.get360` | read | Read a member: profile, point balance, tier, segments, consent |
| `customer.create` | changes data | Register a member directly (no SMS check) |
| `customer.register.initiate` | changes data | Start SMS-verified registration (sends an SMS) |
| `customer.register.confirm` | changes data | Finish SMS-verified registration with the code |
| `transaction.create` | changes data | Commit a completed sale: earns/spends points and applies campaign rewards (call once after checkout) |
| `transaction.return.inquiry` | read | Preview what returning items does to points and discounts, nothing saved |
| `transaction.return.quote` | read | Dry-run refund quote for a return, nothing saved |
| `transaction.return.commit` | changes data | Commit a return or cancellation |
| `transaction.reconcile` | changes data | Send sales made offline later (reconciliation) |
| `transaction.batch` | changes data | Send up to 500 sales and returns in one call (nightly offline sync); processed in order in the background |
| `transaction.batch.status` | read | Read a batch's progress and each row's result |
| `transaction.file.import` | changes data | Import a POS file (CSV or JSON Lines; sales, payments and returns) as batches; read results with transaction.batch.status |
| `order.loyalty` | read | Read the current net loyalty result of an order already sent (for order pages) |
| `order.sync` | changes data | Sync an order's later status changes |
| `voucher.validate` | read | Check whether a voucher code can be used; marks an expired voucher as expired |
| `voucher.redeem` | changes data | Redeem a voucher against an order |
| `referral.info` | changes data | Read a member's refer-a-friend code, link and invites (creates the member's code on first call) |
| `referral.apply` | changes data | Attach a friend's referral code to a new member |
| `member.session.create` | changes data | Create a short session token for the member panel on your site |
| `game.programs` | read | List the games a member can play |
| `game.session.create` | changes data | Start a game session for a member |

### `transaction.create`

```json
POST /api/v1/MODERN_ECOM/transaction.create
{
  "requestId": "6a5d1f2e-0b3a-4c1d-9e8f-1a2b3c4d5e6f",
  "orderNumber": "ORD-10045",
  "customerId": "a1b2c3d4-...",
  "cart": {
    "currentTotal": "100.00", "originalTotal": "100.00", "totalDiscount": "0",
    "lineItems": [
      { "lineNumber": 1, "sku": "SKU-1", "qty": 1, "unitPrice": "100.00", "originalUnitPrice": "100.00" }
    ]
  },
  "payments": [ { "type": "CASH", "amount": "100.00" } ]
}
```

**Benefits always come from the loyalty engine.** On `transaction.create` Ino evaluates the campaigns itself, once, for the sale's date and basket, and records
points, discounts, vouchers and campaign webhook calls from that result. A channel never grants its own loyalty benefit: an `awards` array sent by older
integrations is accepted for compatibility but **ignored** (an `awards.channel_ignored` audit note is written). Campaign usage limits and budgets are counted
again under a lock when the sale is committed, so two simultaneous sales cannot exceed a limit. Discounts the POS applies on its own (price markdowns) are
recorded as external discounts (`externalTotalDiscount`), not as loyalty benefits.

```json
200 OK
{ "ResponseCode": "SUCCESS", "httpStatus": 200, "message": "İşlem başarılı", "transactionId": "..." }
```

### `campaign.inquiry`

Same cart shape as `transaction.create` (minus `orderNumber`/`payments`/`awards` — this is a dry run).
Response adds `spendablePoints`; each `awards[]` row also carries `CampaignName` (display name; "Program base earn" for the program's base earning) so the cart can show a readable label instead of the code. For a recognised (non-guest) customer it adds a `redemption` object (points payment, see §12). Optional request field `pointsToSpend` (the points the customer wants to use, e.g. from a slider).

### `transaction.return.inquiry` / `transaction.return.commit`

```json
{
  "requestId": "...", "orderNumber": "ORD-10045",
  "returnedItems": [ { "lineNumber": 1, "sku": "SKU-1", "qty": 1 } ],
  "returnType": "RETURN"
}
```

### `customer.get360`

```json
{ "requestId": "...", "customerCode": "C-000123" }
```
```json
{
  "ResponseCode": "SUCCESS", "httpStatus": 200,
  "customerId": "...", "fullName": "...", "pointBalance": 150.0,
  "lifetimePointEarned": 400.0, "segments": ["VIP"],
  "smsPermission": true, "emailPermission": false
}
```

### `customer.create` / `customer.register.initiate` / `.confirm`

```json
{ "requestId": "...", "fullName": "Jane Doe", "mobilePhone": "+15550001234", "email": "jane@example.com" }
```
Response includes `customerId`, `cardNumber`, and — for the register flow — `needsVerification` +
`verificationId` (used in `.confirm` with the SMS code).

**Existing members (since 2026-11-19).** If the phone or email already belongs to a member, the channel does not get that
member directly. When SMS verification is required for registration, the code goes to the phone **on the membership** (not the phone you sent):
the response has `alreadyExisted: true`, `needsVerification: true`, `maskedPhone` of that number and no `customerId`;
`.confirm` with the right code returns the same member with `alreadyExisted: true` (no new member, no welcome or friend
reward). With verification off (or not required for registration), only a request whose phone matches the member returns it; an email that belongs to a
member with another phone is refused with `CONFLICT`. Always check `needsVerification` before `customerId`.

### `voucher.validate` / `voucher.redeem`

```json
{ "requestId": "...", "code": "INOL-ABC12345", "orderNumber": "ORD-10045" }
```
Response includes `isValid`/`voucherMessage` (validate) or `status` (redeem).

## 5 · Standard error catalog

Every response carries `ResponseCode` (string), `ResponseCodeValue` (its numeric form), `ResponseMessage`,
`RequestId`, `Data`, `Warnings`. The HTTP status is chosen the same way for every channel using the
standard mapping:

| `ResponseCode` | HTTP status | Meaning | Retryable? |
|---|---|---|---|
| `SUCCESS` | 200 | Request succeeded | — |
| `PARAMETER_ERROR` | 400 | Validation failed | No — fix the request |
| `AUTHENTICATION_ERROR` | 401 | Bad/missing signature or token | No — fix credentials |
| `FORBIDDEN` | 403 | Authenticated but not allowed to call this operation | No |
| `NOT_FOUND` | 404 | Channel, operation, or referenced record not found | No |
| `CONFLICT` | 409 | Duplicate order number, concurrent update, etc. | No |
| `PROVISION_EXPIRED` | 410 | A point provision/reservation expired | No — re-reserve |
| `IDEMPOTENCY_REPLAY` | 409 | Same `requestId`, different payload | No — use a new `requestId` |
| `GENERAL_ERROR` | 500 | Unexpected server error | Yes, after a delay |
| `UPSTREAM_UNAVAILABLE` | 503 | A dependency (database, Dataverse) is temporarily down | Yes — honor backoff |

This table is the **default**; a channel's mapping may override the HTTP status per operation (and, for
some legacy protocols, may carry a protocol-specific numeric code in the response body that is *not* the
HTTP status — see §6).

## 6 · Note on custom (non-standard) field mappings

If your channel was onboarded with its own field names (common for legacy POS protocols), your response
body may carry a protocol-specific status field instead of (or in addition to) the standard envelope. In
that case the field's meaning is documented in your channel's own integration sheet, not here — but the
**HTTP status code** you receive always follows §5's table, so you can always branch on it generically
before looking at the body.

## 7 · Idempotency

`requestId` is your idempotency key. Replaying the exact same request body with the same `requestId`
within **24 hours** returns the original response without re-executing the operation. Sending the same
`requestId` with a **different** body returns `IDEMPOTENCY_REPLAY` (HTTP 409) — treat this as a bug in your
retry logic (you should only retry with the *same* body, or generate a new `requestId` for a genuinely new
request).

## 8 · Rate limiting

Requests are limited per caller address and API key — 600 requests/minute by default. Exceeding the limit returns HTTP `429` with a
`Retry-After` header (seconds to wait).

Failed sign-ins are counted per caller address: after 30 requests in a minute that end in `401`, that address gets `429` for the rest of the
minute, whatever it sends. A correctly configured integration never reaches this; if you see it, check your key, secret and clock.

### Rules that changed on 2026-11-15 (security hardening)

- `customerId` is the member's id (a GUID) or is left out for a guest sale. Any other value is refused with `400 PARAMETER_ERROR`; it used to be
  recorded as a guest sale.
- `orderNumber` and the other identifiers (voucher and coupon codes, provision id, sequence number) are at most 100 characters and plain text.
- `returnedPointAmount` on `transaction.return.commit` can lower the points given back, never raise them above what the order spent.
- The asynchronous endpoints (`/jobs`, `/webhooks`, `/events`) need the matching scope on a managed key, or `jobs` / `webhooks` / `events` in the
  channel's allowed operations. Without it the answer is `403`.
- One `point_load` row moves at most 100,000 points.

## 9 · Correlation

Send an `X-Correlation-Id` header to tie a request to your own logs/traces; it is echoed back in the
response `X-Correlation-Id` header. If you don't send one, we generate one and still echo it back — always
log the response header's value for support requests.

## 11 · Integration notes (learned from the Try Retail reference integration)
- **Real-time cart pricing:** call `campaign.inquiry` with the **whole cart on every change**; Ino keeps no cart state. Send list prices (`currentTotal = originalTotal`, `totalDiscount = 0`)
  and `sku` = the product code held in Ino's catalog (for catalogs synced at colour level this is *not* the size-level variant code).
- **Per-line awards:** the response `awards[]` carries `RowNumber` (the cart `lineNumber`; null = basket-wide, e.g. points), `DiscountAmount`, `Point`, `CampaignCode`, `PointValidDays`.
  Basket-level discount rewards (`BASKET_FIXED_DISCOUNT` / `BASKET_PERCENTAGE_DISCOUNT`) are spread across **all** lines proportionally to the remaining line value (after other discounts). Points campaigns are evaluated **after** all discount campaigns,
  so percent-of-amount points are based on the discounted basket total. A **basket total condition** (`BASKET_TOTAL`, e.g. "double points on baskets of $100+") compares the basket
  value **after the discounts applied before that campaign** (campaign, tier and voucher discounts; paying with points is a payment, not a discount): a $103 basket with a
  $10.30 discount is a $92.70 basket for a later points campaign. A campaign's channel list (Web/Mobile/POS) is enforced. The set of response fields is defined by the channel mapping (see §6); the `TRY_RETAIL` mapping returns all of the above.
- **Failure policy (recommended for channels):** use a short timeout (≈1 s) for inquiries; on failure show list prices and never block checkout. Save the order locally **before** calling `transaction.create` and retry with the **same `requestId`** —
  duplicate sends return the original transaction (no double points).
- **`transaction.create` prices:** send list prices in `cart`/`lineItems` (`current = original`, no external discount). Do not send campaign awards: Ino re-evaluates the basket and records the benefits itself (see `transaction.create`); charge the customer the amounts `campaign.inquiry` returned.
  `payments[].type` must be one of the seeded codes: `CASH`, `CREDIT_CARD`, `LOYALTY_POINT`, `GIFT_CARD` (an unknown code returns 404).
- **`customer.create` requires a mobile phone** (`mobilePhone`, e.g. `+15551234567`) — omitting it returns `400 Phone number is required`. If SMS verification is enabled in program settings the call does not create the member and returns `needsVerification: true`
  (use the `customer.register.initiate/confirm` pair, or disable verification for the channel's program).
- **`walletPassUrl`** (when Apple Wallet is enabled) is a **relative** path (`/wallet/apple/pass/{customerId}`); prefix it with the API base URL. Opening it on an iPhone in Safari adds the card to Wallet.

## 11b · Order loyalty summary (`order.loyalty`)
`POST /api/v1/{channel}/order.loyalty` with `{ "requestId": "...", "orderNumber": "WEB-100245" }` returns the order's **current, net** loyalty result —
the sale plus every return so far: `Status` (COMPLETED, PARTIALLY_RETURNED, REFUNDED, CANCELLED), `PointsEarned`, `PointsSpent`, `LoyaltyDiscount`,
`VoucherUsed`, `Campaigns[]` (net points, discount and voucher value per campaign), `VouchersIssued[]` (code, type, value, status, expiry),
`Returns[]`, `PointsPending` / `PointsPendingUntil` (when the program holds points until the return period ends) and a `Version` that only grows —
ignore a summary whose version is lower than one you already stored. It is the same summary Ino's connectors write back to e-commerce platforms.
Add `order.loyalty` to the channel mapping's `allowed_operations` (TRY_RETAIL and MODERN_ECOM have it).

## 11c · Offline reconciliation (`transaction.reconcile`)

Body: `requestId`, `orderNumbers` (1–500). Answer per order: `found`, `transactionId`, `status`, `transactionDate`, `recordedAt`,
`returns`; plus `recorded` (count) and `missing` (order numbers not recorded). Read-only; matched within the request's channel
type. Use it after a till was offline — see [OFFLINE_POS.md](OFFLINE_POS.md) §5. Event feed for store caches: §4 there.

## 11d · Bulk sales and returns (`transaction.batch`, `transaction.batch.status`)

For a central POS server that syncs its stores once a night (offline tills). One signed call carries **1–500 rows**; each row is
`{ "operation": "transaction.create" | "transaction.return.commit" | "customer.create", "requestId": "<GUID>", "body": { … } }` where `body` is exactly the
body you would send to that operation in real time (multi-line sales, payments, returns) and carries the same `requestId`.

```
POST /api/v1/{channel}/transaction.batch
{ "requestId": "<batch GUID>", "items": [ { "operation": "transaction.create", "requestId": "…", "body": { "requestId": "…", "orderNumber": "NYC05-0001", "transactionDate": "…", "cart": { … }, "payments": [ … ] } },
                                          { "operation": "transaction.return.commit", "requestId": "…", "body": { "requestId": "…", "orderNumber": "NYC05-0001", "returnedItems": [ … ] } } ] }
→ Data: { "batchId", "status": "QUEUED", "totalRows", "pendingRows", … }

POST /api/v1/{channel}/transaction.batch.status   { "requestId": "<new GUID>", "batchId": "…" }
→ Data: { "status": "QUEUED|PROCESSING|DONE", "okRows", "alreadyRecordedRows", "failedRows", "pendingRows", "lastError", "nextAttemptAt",
          "rows": [ { "index", "operation", "requestId", "status": "PENDING|OK|ALREADY_RECORDED|FAILED", "code", "message", "data" } ] }
```

- The call answers at once; rows are processed by the server **in the order sent**, one batch of a channel at a time (a channel's next
  batch waits for the previous one). Send each store's sales in the order they happened.
- Every sale row needs `transactionDate`. The channel's offline window (see [OFFLINE_POS.md](OFFLINE_POS.md) §2) is measured from the
  moment the batch was **received**, not from when the row is processed.
- Safe to resend: the same batch `requestId` returns the same `batchId` (with different rows → 409); a row whose `requestId` was
  already recorded (in any earlier batch) is reported `ALREADY_RECORDED` and not recorded twice — returns included.
- A bad row (validation, unknown order, too old) is `FAILED` with its code and message; the other rows continue. A temporary server-side
  problem does not fail rows: the batch waits and retries (`lastError`, `nextAttemptAt`); after 24 hours the waiting row fails.
- The API key needs `transaction.batch`, `transaction.batch.status` **and** every operation used in the rows. Points spending in a row
  works only as online (needs a provision); see OFFLINE_POS.md.
- Batches are kept 30 days after they finish (row idempotency lasts as long). Each batch counts as one call in Developers → Inbound;
  the rows are counted as transactions in usage like real-time sales.
- **Members who joined offline.** A `customer.create` row also carries `"memberRef": "<the till's own id for the new member>"` (required,
  up to 64 characters). Sales of that member, in the same batch after the registration row or in a later batch, send
  `"customerId": "ref:<memberRef>"`; the server puts the recorded member's id in its place. If the registration row has not been
  recorded (it failed, or it comes later), those sales fail with `NOT_FOUND` and can be resent once it is. A registration row needs the
  member's consent captured at the till, as in real time. The link between `memberRef` and the member is kept 60 days. If the
  program requires phone verification for new members (Settings → SMS verification), a member cannot be created offline: the
  registration row fails with `VERIFICATION_REQUIRED`; register that member in real time (`customer.register.initiate` / `confirm`) when
  the till is back online.
- Body limit 1 MB per call: split larger nights into several batches. `data` of a row is the canonical answer of the operation (not the
  channel's custom outbound mapping).

## 11e · POS files (`transaction.file.import`)

A day's receipts as one file (CSV "Ino POS file v1" or JSON Lines; UTF-8 or Windows-1254), base64 in the body; parsed at once and sent as
`transaction.batch` batches. Columns, examples and re-send rules: [BATCH_FILE_IMPORT.md](BATCH_FILE_IMPORT.md) §5.

**Markdowns.** A line with `originalUnitPrice` above `unitPrice` counts the difference as that line's own discount; send
`totalDiscount` = the sum of those differences (and `currentTotal` = `originalTotal` − `totalDiscount`). If no `businessDayDate` is sent,
the business day is the day of `transactionDate`. A `transactionDate` without an offset is read as UTC in the API and in JSON Lines;
only local times in a CSV POS file are read as Turkey time (+03:00) — always send the offset.

## 11f · Member list for offline tills (`member.snapshot`)

Read-only, paged list of the program's members for a till that must recognise members without a connection. Data is kept to the minimum:
`memberId`, `status` (`ACTIVE` / `REMOVED`), `code`, `cardNumber` (current active card), `displayName` (first name + last initial, e.g.
"Ayşe K."), `tier`, `balance`, `offlineSpendable` (points the till may let the member spend offline — see OFFLINE_POS.md), `phoneHash`.
No plain phone number, e-mail, full name or birth date is sent.

```
POST /api/v1/{channel}/member.snapshot   { "requestId": "…", "limit": 500 }                          → first page of the full list
POST /api/v1/{channel}/member.snapshot   { "requestId": "…", "cursor": "<nextCursor>" }              → next page
POST /api/v1/{channel}/member.snapshot   { "requestId": "…", "since": "<asOf of the last full run>" } → only members changed since then
→ Data: { "mode": "FULL" | "CHANGES", "members": [ … ], "hasMore", "nextCursor", "asOf" }
```

- Keep `asOf` from the first page of a run and ask `since=asOf` next time (`asOf` is two minutes before the run, so runs overlap; a member
  may come twice — treat rows as upserts). `REMOVED` rows (membership closed, record deactivated, member erased on request) must be
  deleted from the till. Take a **full list at least once a week** and replace the till's list with it (a record deleted directly in the CRM
  is only caught by a full run). Only loyalty members (with a member code) are listed. Card changes (lost, blocked, new card) appear in the
  next change run.
- **Phone lookup:** `phoneHash` = hex(HMAC-SHA256(UTF-8 bytes of the channel's phone key, normalized number)). Normalize like this: keep
  digits only, drop a leading `00`, and write Turkish numbers with `90` in front (`0532 111 22 33` → `905321112233`, `5321112233` →
  `905321112233`). The phone key is shown once per request in Integrations → Channels → *channel* → Offline member list (recorded in the
  audit log); keep it in the POS server's configuration, not on receipts or logs.
- The list is personal data: it may be stored only on the retailer's own store servers, for the till's use, and replaced by each run.
  The phone hash protects a copy of the list that leaks **without** the key; with the key, Turkish mobile numbers can be recovered by trying
  all of them. Keep the key and the list apart (key in the POS server's configuration, list in its database) and treat both as personal
  data.

## 11g · Voucher pack and rules for offline tills (`voucher.pack`, `rules.export`)

- `voucher.pack` — same paging as `member.snapshot` (`cursor`, `since`, `limit`). Each row: `codeHash`, `status` (`USABLE` / `REMOVED`),
  `type`, `value`, `expiresAt`, `memberId` (member vouchers), `freeItemProductCode`. The code itself is never sent: the till computes
  hex(HMAC-SHA256(UTF-8 bytes of the channel's voucher key, the code in upper case)) for the code it is given and looks it up. The voucher key
  comes with the phone key (Integrations → Channels → Offline member list). A member voucher used offline in two stores on the same night:
  the second sale is recorded without the voucher at sync and the case is written to the audit log.
- `rules.export` — what a till can apply itself while offline: `program` (currency, point value and rounding, base earn rate with category
  exceptions, redemption limits, discount cap, conflict mode), `tiers` (earn/spend multipliers and basket percentage per tier) and
  `campaigns` — campaigns running within the next 8 days whose conditions and rewards a till can evaluate (product, category, brand,
  family, tier, store, sales channel, basket/line amount or quantity, payment type; percentage or amount discounts, bonus points, points
  multiplier) and that use no member segments, hold-out group, usage limits or budget. Only campaigns for the calling channel are listed.
  Each entry is `{ "version", "campaign": { … } }`: `campaign` has the engine's own rule tree, `evaluationOrder` (the engine's order:
  item discounts, then basket discounts, then points; higher priority first) and `comparisonPool` (`null`: applied in order, and a
  campaign that cannot be combined stops the ones after it; `BEST_OFFER` or a group name: of the campaigns in the same pool only the one
  giving the customer the most applies). `version` changes whenever the campaign's rules change. Take a new export before `refreshBy`
  (24 hours) and do not apply an older one: a campaign stopped in between disappears from the next export. Schedules use the campaign's
  `timeZone` (an IANA or Windows time zone id; empty = UTC). Campaigns in `notExported` (code and reason only) are applied only when the sale is
  synced, as points. Send offline sales with the lines **before**
  loyalty discounts, as in real time: the engine re-evaluates at sync and records the result.

## 12 · Paying with points (redemption)
Points can be spent on **any channel** that is online. The flow is stateless — there is no reservation to manage:
1. **Inquire.** Send `campaign.inquiry` as usual. For a recognised customer the response contains `redemption` (PascalCase keys): `MaxPoints` / `MaxAmount` (the most the customer may spend on this basket today: balance, program minimum and
   "max share of the purchase" limits applied — the top of your slider), and — when you sent `pointsToSpend` — `Points` / `Amount` (what was applied) and `Payable` (the cash still due), `LineAmounts` (row → amount paid with points) and `Note` (why it was reduced).
2. **Value.** Points are always whole numbers; a point is worth the program's point value (e.g. 50 points = $5). A *point value multiplier* campaign ("spend points on denim: 2×") makes points worth more on the matching lines;
   points go to the most valuable lines first. Spending is calculated on the **discounted** basket. Points paid with do **not** earn points: base earning uses the cash actually paid.
3. **Pay.** Send `transaction.create` with `pointsToSpend` and a `LOYALTY_POINT` payment whose **`amount` is the money value** (`redemption.Amount`) next to your cash payment(s). Ino **re-quotes the basket** (campaigns, multipliers, balance, limits) and rejects with
   `409` if it no longer matches what you were shown (e.g. a campaign ended) — inquire again. The balance is checked under a customer lock, so concurrent sales cannot spend the same points twice; points are taken from the lots that expire first.
4. **Returns** restore the points like any other return — only for the returned lines: points are recorded per line at sale time, so returning a line gives back exactly the points that paid for it (a full return restores all). The cash refund for a line is its price minus discounts minus the money value of the points paid on it. Offline POS: redemption needs the API, so a sale made offline is simply completed without points.

## 13 · Vouchers in the basket, return quotes
- **Voucher:** send `voucherCode` with `campaign.inquiry` and `transaction.create`. Ino validates it (amount or percentage vouchers; free-item vouchers cannot be applied to a basket total), applies it **after campaign discounts and before points**, and returns
  `voucher: { Code, IsValid, Message, Amount }`. `transaction.create` re-prices the basket and rejects with `409` if the voucher is no longer valid; once the sale is saved Ino marks the voucher used. Points and base earning are calculated on what is left after the voucher.
- **Return quote** (`transaction.return.quote`, dry run) and the `refund` block of `transaction.return.commit`: `cashRefund`, `returnedGross`, `discountReversal` (discount that stops being valid when the remaining basket is re-priced), `pointsValue` (paid with points — returned as points, not cash),
  `voucherValue`, `spendPointReversal`, `earnPointReversal` and per-line `lines[]`. Which rewards survive a partial return is decided by re-pricing the remaining basket, so spend thresholds stay correct.

## 13b · Refer a friend (`referral.info`, `referral.apply`)
For point-of-sale systems and your own app. Add both to the channel mapping's `allowed_operations`. The program itself is designed in Power Apps ([`REFERRAL`](REFERRAL.md)).

`referral.info` with `{ "crm_customer_id": "...", "language": "en" }` returns the member's code, share link and invites (masked names):

```json
{ "enabled": true, "code": "7K2MQ9XT", "shareUrl": "https://shop.example.com/?ref=7K2MQ9XT", "title": "Invite a friend", "description": "...", "shareMessage": "...",
  "joined": 1, "waiting": 1, "rewarded": 2, "invites": [{ "name": "M***", "status": "WAITING", "joinedAt": "2026-11-03T10:00:00Z", "rewardDueAt": "2026-11-17T10:00:00Z", "rewardedAt": null }] }
```

`enabled: false` means the member cannot invite now (no live program, or they do not qualify yet).

When the program has milestone bonuses, `milestones` lists them: `[{ "invites": 3, "rewardType": "POINTS", "points": 1000, "label": null, "reached": false }]` (`rewardType` is `POINTS` or `VOUCHER`; `reached` is true once the member has the bonus). An empty list means the program has none.

`referral.apply` with `{ "crm_customer_id": "<the new member>", "referral_code": "7K2MQ9XT", "phone_verified": true }` right after the member is created. The result is `{ "ok": true, "held": false, "voucherCode": "REF-8QK2" }` or `{ "ok": false, "reason": "..." }`. Reasons: `INVALID_CODE`, `SELF`, `NOT_NEW`, `ALREADY_INVITED`, `NOT_ELIGIBLE`, `PROGRAM_OFF`, `PROGRAM_FULL`. A rejected code never blocks the membership. Send `phone_verified: true` only if you verified the phone yourself.

Calling it again with the same member and code returns the same result. Only members created in the last 24 hours with no orders count as new.

## 13c · Member panel on your own site (`member.session.create`, `<ino-loyalty>`)
A ready-made rewards panel for any site: balance, tier and progress, coupons, offers, recent activity, the invitation link and games. It is one script and one element; it draws inside a shadow root, so your styles and the panel's do not touch each other. English and Turkish.

**1. Turn it on.** The member API is off until the installation sets `Member:Enabled=true` (in Ino Cloud: the tenant setting `Member--Enabled`, with `ops/azure/tenant_settings.py`). Add `member.session.create` to the channel mapping's `allowed_operations`.

**2. Your server asks for a member token** when it renders a page for a signed-in member (a normal signed channel call):

```json
{ "customerId": "5b0c1c5e-…", "allowGames": true }
```
```json
{ "member": true, "token": "imt1.eyJj….hR2…", "expiresAt": "2026-11-16T10:30:00Z", "reason": null }
```

- The token names that one member, lasts 30 minutes and only reads. With `allowGames: true` it can also open the games you have published (a paid extra play spends the member's points); leave it out if the page should not offer games.
- `member: false` with `reason` `NOT_MEMBER` (not an active member), `NOT_ENABLED` or `NOT_CONFIGURED`: show nothing.
- Never put the channel key or secret in a page. The token is the only thing the browser gets.

**3. Put the panel in the page.**

```html
<script src="https://<engine>/widget/v1/ino-loyalty.js" async></script>
<ino-loyalty token="imt1.eyJj…" lang="en" accent="#1d5c4b"></ino-loyalty>
```

| Attribute | Meaning |
|---|---|
| `token` | The member token your server wrote into the page. |
| `token-url` | Instead of `token`: an address on **your own site** (same origin) that answers `POST` with `{ "token": "…" }` for the signed-in member. The panel calls it when it starts and again when the token has expired. |
| `lang` | `en`, `tr` or `auto` (the page's language). |
| `accent` | Your brand colour as `#hex`. |
| `view` | `panel` (default) or `balance` (a small pill for the header). |
| `api` | The engine address; by default the origin the script was loaded from. |

From code: `element.getToken = async () => "<token>"` (instead of `token-url`), `element.refresh()` after you know an order was placed. Events: `ino:ready` (`detail: { points, tier }`), `ino:error` (`detail: { status }`; 401 = the session ended), `ino:game` (messages of an open game, for example `game.completed`).

Without the element: `InoLoyalty.client({ api, token, getToken })` gives `summary()`, `referral(lang)`, `games()`, `gameSession(programCode)` and `logo()`; they call `GET /member/v1/summary`, `/referral`, `/games`, `POST /member/v1/games/{program}/session` and `GET /member/v1/logo` with `Authorization: Bearer <token>`.

**What the browser can read.** Only that member's balance, tier, coupons, offers, recent point activity, invitation code and games. No name, phone, e-mail, member id or consent record; no wallet link. Coupon codes are shown to their owner, as on your account page.

**Pinning the script.** `GET /widget/v1/manifest.json` returns the current file name (`ino-loyalty.<hash>.js`, never changes) and its `integrity` value, for sites that load scripts with `integrity` + `crossorigin="anonymous"`. The plain name follows fixes by itself.

**If your site has a Content-Security-Policy,** allow the engine in `script-src` and `connect-src`, `blob:` in `img-src` (the logo), and the game address in `frame-src` if you offer games. No `unsafe-inline` is needed.

**Limits.** 600 requests a minute per client address; member traffic is counted apart from your channel calls and cannot slow them down. An answer for one member is reused for 30 seconds.

## 10 · OpenAPI

A machine-readable OpenAPI document is published at `/openapi/v1.json`. It lists only the partner contract
under `/api/v1/`:

- `POST /api/v1/{channelCode}/{operation}` — real-time operations (this document)
- `/api/v1/{channelCode}/webhooks/*` — webhook subscription management
- `GET /api/v1/{channelCode}/events` — event feed
- `GET /api/v1/meta/events` — event catalog
- `/api/v1/{channelCode}/jobs/*` — bulk jobs

Other routes the server answers (health checks, data feeds, platform connectors, wallet passes, the
`/partner/v1` surface) are not part of this contract and are not listed. The document has a `servers`
entry only when the server is configured with a public `https` base URL; otherwise use the base URL
you were given (§1). Operators can turn the document off entirely with `OpenApi:Enabled=false`.

Because the operation endpoint accepts a per-channel JSON body shape (see §1), the generated schema is
intentionally generic for `/api/v1/...` — this document, not the OpenAPI schema, is the source of truth
for request/response shapes.
