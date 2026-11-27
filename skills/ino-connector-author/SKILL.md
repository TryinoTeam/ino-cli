---
name: ino-connector-author
description: Connect a platform Ino Loyalty has no ready connector for (a shop, a POS, a marketing tool such as e-mail, SMS or a CDP). First choice is a definition package (a connector.json the Ino engine runs, no code to host); otherwise a self-hosted connector (an adapter on the customer's or partner's server). Use when someone wants to connect such a platform, write or fix a connector.json, read a POS vendor's file format, or move a marketing connection to a new package version.
---

# Connecting a platform to Ino

## First choice: a definition package (runs in Ino, no code to host)

A definition package is one `connector.json` the Ino engine runs. Consent, deletions, retries, duplicate checks, the credential vault and the audit
trail stay in the engine; the file only says how the platform's calls and events map to Ino. Use it whenever the platform has an HTTP API (or, for a
POS, a sales file), which is most of the time.

1. **Skeleton:** `ino connector definition new <folder> --id local_<name> --kind marketing|ecommerce|pos`. It passes the engine's rules as it is.
   Read the `RULES.md` it writes before changing anything: it lists what a package of that kind may do.
   - `marketing`: Ino sends profiles, events, unsubscribes and deletions to the tool (Marketing Activation).
   - `ecommerce`: the shop's signed webhooks become sales and returns in Ino; the firm enters the shop's API key at install.
   - `pos`: the tills' sales file mapped to Ino POS file v1 (columns, record and payment types, dates, amounts). No calls.
2. **Edit, then check without saving:** `ino connector package check <folder>/connector.json` lists every rule the file breaks, or its kind and
   access summary. Repeat until it passes. Never invent fields: take them from the platform's API documentation and from `ino docs search`.
3. **Save:** `ino connector package save <folder>/connector.json`. A saved version never changes; raise `version` for a new one.
4. **Use it** (a person decides; show them the summary and let them confirm):
   - marketing tool: `ino marketing connect --definition <id> --version <v> --setting k=v … --processor … --country … --key-env <VAR>` prints
     what it sends and where, and needs `--confirm <reviewHash>`; then `ino marketing verify <connection id>`. In Power Apps: Marketing Activation
     → "Your own package".
   - e-commerce: `ino connector package preview <id> <v> --set …` with a credential, then `install` with the access hash it printed (test tenants).
   - POS file format: Power Apps → Integrations → Channels → the channel → "POS file format" (preview, review, pin).
5. **A new version later:** save it, then `ino marketing repin <connection id> --version <new>` shows what changes; with `--confirm <reviewHash>`
   it moves the connection (same account and key). A live connection pauses until `ino marketing verify`.

Rules for every definition package:
- Ids start with `local_`. Addresses come only from the firm's `url` settings (https); the key is entered by the firm, never written in the file.
- On a live tenant, saving needs the firm's "own packages" setting and the Connector Installer role; installing and connecting are done by a person
  who reviews the summary. Do not try to get around a refusal: tell the person what it says.
- Never put keys, secrets or member data in the file, in chat or in logs.

## Otherwise: a self-hosted connector

Use this when the platform needs logic a definition cannot express (an SDK only, a custom protocol, heavy transformation).

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

## Marketing tools (Marketing Activation)

A marketing tool connector is the other direction: Ino sends loyalty profiles and events to the firm's own e-mail, SMS or CDP tool. Ino keeps consent, the erasure ledger, retries and pausing; the tool only applies what it gets and answers with the right status.

1. **Scaffold:** `ino connector new <name> --kind marketing --lang node|python|csharp --platform "<tool>"`. Run the tests it prints. The only file to change is the tool file (`src/tool.js`, `connector/tool.py` or `src/Connector/Tool.cs`): replace each function with the tool's API and keep the return shapes. Leave the signature check and the contract as they are.
2. **Secret:** `ino marketing connect --name "<tool>" --url https://<host>/ino --env-file ma.env` writes the connection id and signing secret to that file. Never show the secret in chat; the server reads it from `INO_MA_SIGNING_SECRET`.
3. **Test locally:** run the server, then `ino connector test --kind marketing --url http://localhost:8080/ino --env-file ma.env`. All 13 checks must pass.
4. **Go live:** deploy behind a public https address and run `ino marketing verify <connection id>`. Ino sends nothing before verify passes.
5. **Store (optional):** `ino connector test --kind marketing --url … --env-file ma.env --json > report.json`, then `ino hub submit --marketing-report report.json`.

Rules for marketing tools:
- **Status codes mean something:** 404 means the profile is not there (Ino rebuilds it; on delete it counts as done). 401/403 stops the whole connection. 408/429/5xx are retried. Never redirect.
- **Linking an existing profile:** when the member is matched to a profile the tool already had (same e-mail from a shop), answer `adopted: true` with `adoptedVia` set to the channel that matched, and return that address from `profile.subscription`.
- **Events:** apply each `eventId` once.
- **Data:** never log request bodies; delete everything on `profile.delete`.
