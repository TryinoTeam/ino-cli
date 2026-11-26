# Webhooks

Webhooks let you receive loyalty events (points earned, vouchers issued, transactions returned, ...) as
signed HTTP POSTs to your own endpoint — no polling required. This document is the companion to
[`REALTIME_API.md`](REALTIME_API.md); read that first for auth, the base URL, and the standard error catalog.

**Out of scope:** `/partner/v1/*` is unrelated (voucher partner redemptions, different surface entirely).

## 1 · Managing subscriptions

All management is programmatic, under your channel's existing authentication — no separate credentials, no
mapping-file changes, no deploy. Endpoints:

| Endpoint | Purpose |
|---|---|
| `POST /api/v1/{channelCode}/webhooks/subscribe` | Create or update a subscription |
| `POST /api/v1/{channelCode}/webhooks/unsubscribe` | Remove a subscription |
| `POST /api/v1/{channelCode}/webhooks/list` | List your channel's subscriptions |
| `POST /api/v1/{channelCode}/webhooks/{id}/test` | Send a one-off test event to one subscription |

**REST resource (recommended for new integrations, and what Power Automate / Zapier-style platforms use):**

| Endpoint | Purpose |
|---|---|
| `POST /api/v1/{channelCode}/webhooks/subscriptions` | Create — **201** with an absolute `Location` header and the secret (once). Same URL again → 200 with the existing subscription, no secret |
| `GET /api/v1/{channelCode}/webhooks/subscriptions` | List (never includes the secret) |
| `GET /api/v1/{channelCode}/webhooks/subscriptions/{id}` | One subscription |
| `PATCH /api/v1/{channelCode}/webhooks/subscriptions/{id}` | Change `url`, `eventTypes` and/or `status` (`ACTIVE` to resume, `PAUSED` to pause — events keep queuing while paused) |
| `DELETE /api/v1/{channelCode}/webhooks/subscriptions/{id}` | Delete — **204**, idempotent (this is the call an automation platform makes when its flow is removed) |
| `POST /api/v1/{channelCode}/webhooks/subscriptions/{id}/test` | Send a test event |
| `POST /api/v1/{channelCode}/webhooks/subscriptions/{id}/rotate-secret` | New secret; the previous one stays valid for 24 h |

`GET`/`DELETE` requests are signed over an empty body (`"{X-Timestamp}."`). Unknown event types are accepted (to keep
older integrations working) but reported back in `warnings` — a misspelled type would never fire.
Set `Api:PublicBaseUrl` on the server when it runs behind a proxy that hides the public host, so `Location` is correct.

All four are signed exactly like any other `/api/v1/{channelCode}/...` call (HMAC or OAuth2 — see
`REALTIME_API.md` §2). They're all POST, including `list`, so every call signs a body (empty `{}` is fine) —
one signing rule, no exceptions.

**Important:** `channelCode` identifies *who may manage* a subscription, not *which channel's events it
receives*. A subscription set up from your `ECOM` channel's credentials still receives events from `POS`,
`MOBILE`, etc. — `eventTypes` (below) is the only filter on *what* you get.

### Subscribe

```json
POST /api/v1/MODERN_ECOM/webhooks/subscribe
{ "url": "https://partner.example.com/ino-webhook", "eventTypes": ["points.earned", "voucher.issued"] }
```
- `eventTypes` omitted or `[]` → **all** event types (see the catalog in §4).
- Calling `subscribe` again with the **same `url`** updates `eventTypes` and reactivates a `SUSPENDED`
  subscription (see §3) — this is intentional, not accidental; it's also how you resume delivery after
  fixing your endpoint.
- `url` must use **https** (plain http is accepted only for `localhost` during development). Addresses that
  resolve to private, loopback, link-local or cloud-metadata networks are refused at delivery time (see §3).
- The response includes a `secret` **only** on first creation, or when you pass `"rotateSecret": true`.
  **Store it immediately — it is never shown again.** Losing it means calling `subscribe` again with
  `rotateSecret: true` to get a new one (which invalidates the old one). New secrets look like
  `whsec_<base64>`; secrets issued before 2026-10-29 are 64 hex characters — both work with every
  verification method in §2.

```json
{ "ResponseCode": "SUCCESS", "Data": {
  "subscriptionId": "...", "url": "...", "eventTypes": [...], "secretRotated": true, "secret": "..." } }
```

### Unsubscribe

```json
POST /api/v1/MODERN_ECOM/webhooks/unsubscribe
{ "url": "https://partner.example.com/ino-webhook" }
```
Idempotent — calling it again (or on an unknown URL) still returns success.

### List

```json
POST /api/v1/MODERN_ECOM/webhooks/list
```
Returns your subscriptions (`id`, `url`, `eventTypes`, `status`, `state`, `createdAt`) — **the secret is never
included**, even here. `status` is `ACTIVE` or `SUSPENDED` (unchanged contract); `state` adds detail:
`ACTIVE`, `PAUSED` (paused by an administrator) or `SUSPENDED` (paused automatically, see §3).

### Test

```json
POST /api/v1/MODERN_ECOM/webhooks/{subscriptionId}/test
```
Sends a `webhook.test` event straight to that one subscription (bypassing the normal queue) and returns
whether it was delivered (`sent`, `httpStatus`, `error`) — use this right after subscribing to confirm your
endpoint and signature verification work before relying on real events.

`POST /api/v1/MODERN_ECOM/webhooks/subscriptions/{subscriptionId}/test` accepts an optional body
`{ "eventType": "transaction.completed", "dataversion": 2 }`. It then sends the catalog's example of that public event
(the newest version unless you name one) instead of `webhook.test`, with a new `id` and `time` and the CloudEvents
attribute `"test": true` and the header `X-Ino-Test: true`. **Check one of them before you act on the event**: a receiver that routes on
`type` alone would treat the test as a real sale. The test is sent even if the subscription does not list that type, and even while it is paused.
An unknown or non-public type returns `PARAMETER_ERROR` and nothing is sent. With the `ino` CLI:
`ino webhooks test <id> --event-type transaction.completed`.

### Deliveries and resending

These need the `webhooks` permission and work only on your channel's own subscriptions.

```
GET  /api/v1/MODERN_ECOM/webhooks/subscriptions/{subscriptionId}/deliveries?status=FAILED&limit=50
GET  /api/v1/MODERN_ECOM/webhooks/subscriptions/{subscriptionId}/deliveries/{deliveryId}/attempts
POST /api/v1/MODERN_ECOM/webhooks/subscriptions/{subscriptionId}/deliveries/{deliveryId}/resend
```

- **Deliveries** list the most recent deliveries first. Each row has `deliveryId`, `eventId`, `eventType`, `status`
  (`PENDING`, `SENDING`, `SENT`, `FAILED`, `DEAD`, `EXPIRED`), `attempts`, `lastHttpStatus`, `lastError` and timestamps.
  `status` filters on `ALL`, `PENDING`, `FAILED` (failed, dead or expired) or `SENT`. `limit` is 1–100 (default 50).
- **Attempts** list each try, newest first, with its time, duration, HTTP status and outcome. Errors are short categories
  (`HTTP 500`, `Timed out`, `Could not connect`, `Blocked by the endpoint's credential or network policy`). The details of a blocked
  attempt are shown only to administrators in Settings → Developers.
- **Resend** queues a finished delivery (`SENT`, `DEAD` or `EXPIRED`) again. Your endpoint receives it with the same
  `webhook-id`, and the backlog age limit counts from the resend. A delivery that is still being retried (`PENDING`, `FAILED`)
  keeps its retry schedule and cannot be resent. If the subscription is paused or suspended, the answer says so, and the
  delivery waits until the subscription is resumed.
- Event data and your endpoint's response bodies are not returned here, because they can hold member data. Use the event feed,
  or Settings → Developers in Power Apps.
- With the `ino` CLI: `ino webhooks deliveries <id> --status FAILED`, `ino webhooks attempts <id> <deliveryId>`,
  `ino webhooks resend <id> <deliveryId>`.

## 2 · Verifying the signature

Every delivery (including the test event) is signed twice, so you can use either method:

**Standard Webhooks (recommended).** Ino follows the [Standard Webhooks](https://www.standardwebhooks.com)
specification, so the official libraries for every language verify our requests with one call:

| Header | Meaning |
|---|---|
| `webhook-id` | Delivery id — **the same on every retry of the same delivery** (use it to deduplicate) |
| `webhook-timestamp` | Unix seconds the request was signed at |
| `webhook-signature` | `v1,<base64(HMACSHA256(key, "{webhook-id}.{webhook-timestamp}." + rawBody))>`; the key is your secret with the `whsec_` prefix removed, base64-decoded. During a secret rotation the header carries two space-separated signatures (new and previous secret) — accept the request if **any** of them matches |

```python
from standardwebhooks import Webhook          # pip install standardwebhooks  (also: npm, NuGet, Go, Java, Ruby, PHP…)
payload = Webhook(secret).verify(raw_body, request.headers)   # raises if the signature or timestamp is invalid
```


**Legacy Ino signature (deprecated, still sent).** Kept for integrations built before 2026-10-29; it will be
retired on **2027-05-01** (Standard Webhooks headers stay):

| Header | Meaning |
|---|---|
| `X-Ino-Signature` | `hex(HMACSHA256(UTF8(secret), "{X-Timestamp}." + rawBody))`, lowercase — always computed with the **current** secret |
| `X-Timestamp` | Unix seconds the request was signed at |
| `X-Webhook-Id` | Same value as `webhook-id` (stable across retries of one delivery) |

```python
import hashlib, hmac
expected = hmac.new(secret.encode(), f"{timestamp}.".encode() + raw_body, hashlib.sha256).hexdigest()
assert hmac.compare_digest(expected, received_signature)
```

Either way: verify against the **raw bytes** you received, before any JSON parsing/re-serialization —
whitespace differences change the signature — and reject requests whose timestamp is more than 5 minutes old.
Every request also carries `User-Agent: InoLoyalty-Webhooks/1.0`. Test events additionally carry `X-Correlation-Id`.

## 3 · Delivery guarantees

- **At-least-once.** A delivery may be retried after your endpoint already processed it but the response
  was lost. Deduplicate with `webhook-id` (one per subscription × event, stable across retries) or with the
  CloudEvents `id` in the body (one per event, the same for all your subscriptions).
- **No events are lost while a subscription is paused.** Events keep queuing while a subscription is paused
  (by an administrator) or suspended (automatically); when it is resumed they are sent in order. The queue is
  bounded: 10,000 waiting events and 7 days per subscription. Older events are marked `EXPIRED`, and events
  beyond the limit are not queued (an administrator can re-queue them with *Recover* for up to 7 days back).
- **Ordering:** within a subscription, events are sent in the order they occurred, up to 4 in parallel per
  endpoint, so ordering is best-effort — use the event's `time` field if you need strict order.
- **Timeout:** 10 seconds per attempt. Only `2xx` counts as success. Redirects (`3xx`) are **not** followed.
- **Retry schedule** (non-2xx, timeout, connection error): about 30 s, 2 min, 10 min, 30 min, 1 h, 2 h, 4 h, 6 h,
  8 h, then every 12 h (±10% jitter) — up to 20 attempts over roughly 6½ days, then the delivery is `DEAD`.
  `Retry-After` on `429`/`503` is honoured (up to 24 h).
- **`410 Gone`** means "this endpoint no longer exists": the delivery stops and the subscription is suspended
  immediately.
- **Auto-suspend:** a subscription is suspended only when every attempt has failed for **24 hours** and at least
  10 attempts in a row — short outages never switch it off. Administrators see the reason in *Settings →
  Webhooks* and an *Integration Log* entry is written. Resume with `subscribe` on the same URL (or *Resume* in the
  admin screen); everything queued meanwhile is delivered.
- **Network policy:** the delivery service connects only to public internet addresses. Hosts that resolve to
  private (10/8, 172.16/12, 192.168/16), loopback, link-local (incl. cloud metadata 169.254.169.254), CGNAT,
  multicast or reserved ranges are refused (the attempt is logged as *blocked*). On-premises installations can
  allow specific ranges (`Webhooks:Egress:AllowedCidrs`).

## 4 · Event catalog

The machine-readable catalog — every type with its description, an example body and the JSON Schema of `data` — is at
**`GET /api/v1/meta/events`** (no authentication needed). The table below is generated from the same source.
An empty `eventTypes` filter means "all public event types"; subscriptions created before 2026-10-29 with an empty
filter were pinned to the types that existed then, so new types never reach an integration unannounced.

<!-- event-catalog:start (generated by EventCatalogTests; do not edit by hand) -->
| Type | Group | Fired when |
|---|---|---|
| `transaction.completed` (v2) | Transactions | **Purchase completed.** A purchase was recorded and its points were earned or spent. Fired once per order. Version 2 adds the loyalty discount, store, tier, the voucher used, the value of points spent and the award of each campaign. |
| `transaction.returned` (v3) | Transactions | **Return processed.** Items of a purchase were returned: points earned on them are taken back and points spent on them are given back. Version 3 adds the order's net loyalty result after this return (status, points earned and spent, loyalty discount). |
| `points.earned` (v1) | Points | **Points earned.** A campaign gave points to a member. One event per campaign award. |
| `points.adjusted` (v1) | Points | **Points adjusted.** Points were added or removed by an administrator or a bulk load. A negative amount means points were removed. |
| `points.expiring` (v1) | Points | **Points about to expire.** Some of a member's points will expire soon. Sent once per reminder window (by default 30, 7 and 1 days before) — use it to remind the member. |
| `points.released` (v1) | Points | **Pending points released.** Points earned on a purchase were held until the return period ended and can now be spent. Sent only when the program holds points (Points Pending Days). |
| `points.expired` (v1) | Points | **Points expired.** Points reached the end of their validity and were removed from the member's balance. |
| `voucher.issued` (v1) | Vouchers | **Voucher issued.** A voucher was given to a member (campaign reward, batch or manual). |
| `voucher.redeemed` (v1) | Vouchers | **Voucher used.** A voucher was used — in a channel order or at a partner. Update any copy of the voucher you keep so it cannot be used twice. |
| `voucher.redemption_reversed` (v1) | Vouchers | **Voucher use reversed.** A partner reversed a voucher use; the voucher can be used again. |
| `voucher.cancelled` (v1) | Vouchers | **Voucher cancelled.** An unused voucher was cancelled (the order that earned it was returned, or its batch was revoked). Remove any copy you keep, such as a store coupon. |
| `voucher.expired` (v1) | Vouchers | **Voucher expired.** An unused voucher reached its expiry date. Remove any copy you keep, such as a store coupon. |
| `member.registered` (v1) | Members | **Member joined.** A new member joined the loyalty program (through a channel, in the app or by an import). The event carries ids only; read the profile with customer.get360 if you need it. |
| `member.updated` (v1) | Members | **Member profile updated.** A member's profile changed — through the API, in the Ino Loyalty app, in another Dynamics app or by an import. Sent within about two minutes; only the names of the changed fields are sent, not their values. |
| `consent.changed` (v1) | Members | **Contact permission changed.** A member gave or withdrew a contact permission (email, SMS, call or data sharing). Respect it in your own systems right away. The channel says where: POS, ECOM, MOBILE, CALL_CENTER, IYS_PORTAL, or MARKETING_TOOL when the member unsubscribed in a connected marketing tool. |
| `referral.joined` (v1) | Referral | **Friend joined with an invite.** Someone became a member with another member's referral code. Use it to thank the inviting member. No reward yet: rewards follow the friend's first order. |
| `referral.rewarded` (v1) | Referral | **Referral reward given.** The friend's first order passed the return period and the rewards were given. Use it to tell the members about their reward. |
| `referral.milestone_reached` (v1) | Referral | **Referral milestone reached.** A member's rewarded invites reached a milestone of the program and the bonus was given. Use it to congratulate the member. |
| `marketing.action` (v1) | Marketing | **Marketing tool action.** A connected marketing tool (for example a Klaviyo flow) reported an action of the member, such as a completed survey. Ino records it; it gives no points by itself. Never sent back to the marketing tools. |
| `game.completed` (v1) | Games | **Game completed.** A member finished a game and the server verified the score, or completed a garden or a mission. Fires once per play (once per garden or mission). |
| `game.reward_given` (v1) | Games | **Game reward given.** A game rule gave the member points, a voucher or a badge. |
| `game.reward_reversed` (v1) | Games | **Game reward reversed.** A game reward was taken back: staff voided the play, or a return took the member below the reward's score. |
| `game.result_rejected` (v1) | Games | **Game result rejected.** A result failed the server's replay check. No reward was given. |
| `game.sticker_earned` (v1) | Games | **Album sticker earned.** A purchase gave the member an album sticker. |
| `game.sticker_lost` (v1) | Games | **Album sticker lost.** A return took back an album sticker: nothing bought from that category is kept. |
| `tier.changed` (v1) | Tiers | **Tier changed.** A member moved to another tier (joined a tier, moved up, moved down or lost the tier). |
| `tier.downgrade_scheduled` (v1) | Tiers | **Tier downgrade scheduled.** A member no longer qualifies for their tier. They keep it until the downgrade date and will move down then unless they qualify again. |
| `tier.downgrade_cancelled` (v1) | Tiers | **Tier downgrade cancelled.** A member qualified again during the grace period; the scheduled downgrade will not happen. |
| `segment.membership_changed` (v1) | Segments & campaigns | **Segment membership changed.** A member joined or left a segment. Only for segments where "Publish membership events" is turned on. |
| `campaign.changed` (v1) | Segments & campaigns | **Campaign changed.** A campaign was created or changed (dates, status, rules). Use it to refresh campaign data cached in tills and apps. |
| `job.completed` (v1) | Bulk jobs | **Bulk job completed.** A bulk job you submitted (file import, point load, segment export) finished. Row counts tell how many rows succeeded or failed. |
| `job.failed` (v1) | Bulk jobs | **Bulk job failed.** A bulk job you submitted could not be processed. |

Notes:
- `member.registered` and `voucher.redeemed` are sent immediately when the member/voucher is saved through Ino Loyalty. A
  catch-up check also looks at members and used vouchers older than two minutes and sends the event for any that were not
  reported (for example members created by an import, or the rare case the service stopped between saving and sending) — each
  event is sent once either way. `voucher.redemption_reversed` is sent immediately only.
- `member.updated` reports profile changes made anywhere — through the API, in the Ino Loyalty app, in another Dynamics app or
  by an import — within about two minutes. It compares a fingerprint of the profile (no values are stored) and lists exactly
  the fields that changed: `firstName`, `lastName`, `email`, `mobilePhone`, `phone`, `jobTitle`, `preferredContactMethod`,
  `address`, `city`, `birthDate`, `gender`. Changes Ino Loyalty makes itself (point summary, tier) never produce this event.
- `consent.changed` and `campaign.changed` come from changes made in the Ino Loyalty app (Dataverse) and are picked up
  within about a minute. Deleted campaigns are not reported (campaigns are normally completed or cancelled, not deleted).
- `points.adjusted` is sent per member and adjustment, also for bulk point loads (one event per row; the job itself is
  reported once by `job.completed`).
- `segment.membership_changed` is sent only for segments with **Publish membership events** turned on.
- Events caused by a channel request carry the CloudEvents extension attributes `originchannel` (the channel code, e.g.
  `TRY_RETAIL`) and, when known, `storeid`.

Each event body is a [CloudEvents 1.0](https://cloudevents.io/) envelope:

```json
{
  "id": "...", "source": "ino-loyalty-api", "type": "points.earned", "specversion": "1.0",
  "time": "2026-01-01T00:00:00Z", "subject": "<customerId or null>", "dataversion": 1,
  "data": { "...": "event-specific fields, see InoLoyalty.Events.EventTypes" }
}
```

## 7 · Campaign webhooks ("Call webhook" reward)
A campaign can call one of your systems when its rule matches a completed purchase — e.g. open a CRM task for a VIP order, send a Braze custom event,
post a Teams/Slack message. The benefit itself is still designed in Ino Loyalty; the webhook only tells another system that it happened.

**Templates (Settings → Developers → Templates).** An integration administrator defines a reusable *template*: method (POST, PUT, PATCH, GET, DELETE),
address, headers, body (JSON, form, text or none), an optional credential from the vault, the idempotency header name (default `Idempotency-Key`), a
timeout (1–30 s) and typed *parameters* (text, number, true/false, choice, date) that campaign designers fill in. Values are inserted with placeholders:

| Group | Variables |
|---|---|
| Call | `{{call.id}}` (also sent as the idempotency key; it stays the same on retries) |
| Transaction | `{{transaction.id}}`, `order_number`, `date` (ISO 8601 UTC), `channel`, `store_code`, `gross_amount`, `discount`, `net_amount`, `points_earned`, `points_spent` |
| Campaign | `{{campaign.id}}`, `code`, `name` |
| Member | `{{member.id}}`, `code`, `tier` — and, only if personal data is allowed (below): `first_name`, `last_name`, `full_name`, `email`, `phone` |
| Program | `{{program.name}}`, `{{program.currency}}` |
| Parameters | `{{param.KEY}}` |

Rules that keep calls safe: the variable list is closed (a template with an unknown variable cannot be published); the **host is fixed** (variables only in
the path or query, URL-encoded); in a JSON body a value that is exactly `"{{x}}"` keeps the variable's type (numbers stay numbers) and escaping is automatic,
so a value can never break the body or add fields; line breaks are removed from header values; `Authorization`, cookies and API-key headers cannot be
written into a template — attach a credential instead, so the secret is stored encrypted and is sent only to its allowed hosts. Outgoing calls pass the same
network guard as webhooks (no private/internal addresses, no redirects).

**Versions.** Templates are edited as a draft. *Publish* validates the draft and creates a new, immutable version. A campaign reward stores the template
code **and the version it was approved with**; publishing a new version never changes a live campaign. Moving a campaign to the new version is a campaign
change and needs approval again. A template used by a live or pending campaign cannot be archived. *Preview* renders the draft with sample values; *Send
test* sends the saved draft with sample values (header `X-Ino-Test: true`).

**When calls happen.** Only when the purchase is committed (`transaction.create`) — never for `campaign.inquiry`, simulations or return calculations — and
only for campaigns the engine applied to that sale. The call is queued in the same database transaction as the sale: if the sale is recorded, the call is
not lost; the same sale cannot produce the same call twice. A campaign whose only rewards are calls does not compete in best-offer and is not blocked by
non-stackable campaigns; the campaign's usage limits and budget apply (a call counts as a use). Not called: sales that reach Ino more than 72 hours after
they happened (late offline sync), campaigns whose limit was reached at commit (logged as *Not sent*), and sales fully returned before the call was sent
(*Cancelled*). A call already sent is not reversed on return.

**Delivery.** The request is rendered once, on the first attempt, encrypted and frozen; retries and manual resends send exactly the same request with
the same idempotency key — make your receiver deduplicate on it. Transient failures (timeouts, network errors, 408, 425, 429, 5xx, and 401 when a
credential is attached — the OAuth token is renewed) are retried with back-off for up to **24 hours**; other 4xx/3xx answers fail at once. Repeated
transient failures on one receiving host pause calls to that host for a minute (circuit breaker). After 25 permanent failures in a row the template is
**paused automatically**, an entry is written to Integration Logs, and new calls wait until it is resumed (calls older than 24 hours then fail).

**Personal data.** By default templates can use identifiers only. An administrator can allow name, e-mail and phone (Templates → Settings); such templates
are marked *Personal data*, their requests and the receiver's responses are kept encrypted and deleted after **7 days**, and a member's erasure removes them
and cancels the member's unsent calls. Turning the setting off stops calls of templates that use personal data.

**Logs → Campaign calls** lists every call (status, template and version, campaign, order, attempts) with the frozen request, each attempt (status code,
duration, first 4 KB of the response) and *Resend* for sent or failed calls. Retention: 30 days (`Webhooks:Calls:RetentionDays`).

Technical: tables `hook.template`, `hook.template_version`, `hook.call`, `hook.call_attempt`, `hook.setting` (migration 0041); reward fields
`try_actionreward.try_webhooktemplate`, `try_webhooktemplateversion`, `try_webhookparameters`, reward type `CALL_WEBHOOK` (100000014); engine
`CallTemplateEngine` (InoLoyalty.Webhooks), `CampaignCallWorker`, `CampaignCallAdmin` (actions TEMPLATES, TEMPLATE, TEMPLATE_CREATE/UPDATE/PUBLISH/
ARCHIVE/RESTORE/PAUSE/RESUME/PREVIEW/TEST, CALL_LOG, CALL, CALL_STATS, CALL_RESEND, CALLS_SETTINGS on `try_webhookaction`); the Campaign Wizard reads
published templates through `try_campaignsimulation` (action `WEBHOOK_TEMPLATES`, no addresses or secrets). Settings: `Webhooks:Calls:{Enabled,
IntervalSeconds,BatchSize,MaxParallel,PerTemplate,PauseAfterFailures,BreakerFailures,BreakerSeconds,RetentionDays}`; metric `ino.webhook.campaign_calls`.
