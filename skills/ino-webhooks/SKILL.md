---
name: ino-webhooks
description: Receive Ino Loyalty events (purchases, points, tiers, campaigns) through webhooks or the event feed, verify their signatures, and test locally with `ino listen` and `ino trigger`. Use when a developer subscribes to Ino events, builds a webhook receiver, or debugs missing or rejected deliveries.
---

# Ino Loyalty webhooks and events

## Tools
- `ino events types` lists the public event types and their versions.
- `ino listen --forward-to http://localhost:3000/webhooks` pulls the profile's event feed (test by default; a live profile needs `--live`) and forwards each event to the local receiver. It sends the same headers as a real delivery, signed with this profile's local secret (`ino listen --print-secret`).
- `ino trigger <type> --forward-to …` sends a synthetic event built from the catalog example. Use `--override data.points=50` or `--dataversion 1` to vary it. Nothing reaches the engine.
- `ino webhooks list|create|update|delete|test|rotate-secret` manage subscriptions. The key needs the `webhooks` permission.
- MCP: `ino_events_types`, `ino_verify_webhook`, `ino_code_sample verify_webhook <language>`, `ino_webhooks_list` (`ino agent setup` turns on both tool sets).

## Verifying a delivery
- **Standard Webhooks** headers are `webhook-id`, `webhook-timestamp` and `webhook-signature: v1,<base64>`. The key is the base64-decoded part of the secret after `whsec_` (older secrets from before 2026-10-29 may be 64 hex characters; use the secret exactly as Ino showed it). The signed content is `"{id}.{timestamp}." + raw body`.
- Subscriptions also send the legacy `X-Ino-Signature`, `X-Timestamp` and `X-Webhook-Id` headers unless they are set to Standard only.
- Verify the **raw** body before parsing JSON.
- Refuse timestamps older than 5 minutes.
- During a secret rotation, accept either of the two signatures in the header.
- `webhook-id` is the **delivery** id: it stays the same across retries. It is not the event's `id`.

## Delivery rules
- Delivery is at least once: deduplicate on `webhook-id`.
- Answer 2xx within 10 seconds and do the work afterwards. Only 2xx counts, and redirects are not followed.
- Failed deliveries are retried for about 6½ days. A subscription is suspended only after 24 hours in which every attempt failed, with at least 10 failed attempts in a row. A `410 Gone` answer suspends it at once.
- Delivery order is best effort: use the event's `time` when strict order matters.
- Read `dataversion` on each event. New versions only add fields; old fields stay.
- The secret is shown **once**, by `ino webhooks create` or `rotate-secret`. Store it in the receiver's configuration straight away.
- `ino webhooks create` with a URL that already exists updates that subscription: its event types are replaced and it becomes active again.

## Local loop
1. Start the receiver on localhost.
2. `ino listen --forward-to http://localhost:<port>/<path>`; configure the receiver with the secret from `ino listen --print-secret`.
3. Fire test events with `ino trigger transaction.completed --forward-to …`, or make a real change on the test tenant.
4. If the receiver refuses an event, `listen` stops there. Fix the receiver, then run `ino listen --resume`.

Event data contains pseudonymous member data. `ino listen` only forwards to this machine; do not copy events into tickets or chats.
