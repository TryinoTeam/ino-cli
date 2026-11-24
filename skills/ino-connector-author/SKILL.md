---
name: ino-connector-author
description: Build a self-hosted Ino Loyalty connector (an adapter that runs on the customer's or partner's server and links a shop, POS or other platform to the Ino channel API), from scaffold to a passing compatibility report. Use when someone wants to connect a platform Ino has no ready connector for.
---

# Writing a self-hosted Ino connector

A self-hosted connector is the customer's or partner's own code. It turns their platform's events into signed Ino channel API calls, and Ino events (webhooks) into platform changes. It never touches the engine itself.

## Steps
1. **Profile and environment:**
   - Run `ino doctor`. Work only against a **test** channel; doctor shows the environment the engine reports.
   - Never ask for or paste the key or secret in chat: they belong in the project's `.env` file (not committed) or the `ino` profile.
2. **Scaffold:** `ino connector new <name> --self-hosted --lang node|python|csharp --platform "<platform>"`. Pick the language the customer's team already runs; then run the tests the command prints (`npm test`, `python -m unittest discover -s tests` or `dotnet test tests/Connector.Tests`). Every language gets the same four parts (Node file names shown):
   - `src/ino-client.js`: signed calls, `requestId`, safe retries; it never retries a 401.
   - `src/webhook.js`: signature check on the raw body.
   - `src/queue.js`: an offline queue that keeps the sale time.
   - `src/server.js`: `/health`, `/webhooks/ino`, an example `/platform/order-paid`, and a heartbeat every 5 minutes.
3. **Map the platform:**
   - Replace `/platform/*` with the platform's real events (order paid, refund, cancel, customer created).
   - Keep the order's `requestId` with the order, so a retry of the same order is the same request.
   - Field names follow the **channel's mapping**. Check them with `ino api describe <operation>`, and look at the channel in Power Apps if a call returns `PARAMETER_ERROR`.
4. **Events back:**
   - Locally: `ino listen --forward-to http://localhost:8080/webhooks/ino` and `ino trigger <type> --forward-to …`.
   - In a deployment: `ino webhooks create --url https://<host>/webhooks/ino`. Store the secret it prints once in `INO_WEBHOOK_SECRET`.
5. **Run the scenarios** on the test channel (`ino connector check --suite ecommerce` or `--suite pos` lists what each one needs). Repeat until every scenario passes.

## Rules
- **Benefits come from the engine.** Send the cart and let Ino work out points and discounts; never send your own rewards.
- **Retries:**
  - A retry sends the same body with the same `requestId`, signed again with a new nonce and timestamp.
  - `CONFLICT` for an order already recorded means it is done.
  - A 401 is never retried in a loop: 30 failures a minute block the IP.
- **Events:**
  - Answer webhooks 2xx within 10 seconds and do the work afterwards.
  - Deduplicate on `webhook-id`.
  - Ignore events marked `"test": true` or with the header `X-Ino-Test: true`.
- **Data:** Do not log member data, keys or secrets. Event data is personal data.

## Done means
`ino connector check` passes every scenario of its suite on the test channel, and `ino webhooks deliveries <id>` shows the receiver's deliveries as `SENT`.
