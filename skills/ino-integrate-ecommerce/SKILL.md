---
name: ino-integrate-ecommerce
description: Connect an online shop or POS to Ino Loyalty's channel API (cart pricing, order commit, returns, member lookup) with signed, idempotent calls. Use when building or fixing an Ino Loyalty integration for an e-commerce site, a checkout, or a till.
---

# Integrating a shop with Ino Loyalty

You are helping a developer connect their shop to the Ino Loyalty engine through its channel API. Work against a **test** environment. Never ask for API keys or secrets in the chat: they live in the `ino` CLI profile (OS keychain) or in the developer's own environment variables.

## Tools
- `ino` CLI: `ino doctor`, `ino api list`, `ino api describe <operation>`, `ino api call <operation> --data @body.json`.
- Ino MCP server (`ino mcp serve`): `ino_docs_search`, `ino_docs_read`, `ino_api_list`, `ino_api_describe`, `ino_code_sample`.
  - `ino_api_read` calls only `order.loyalty`, `transaction.return.inquiry`, `transaction.return.quote` and `game.programs`.
  - Everything else, including cart pricing (`campaign.inquiry`) and `voucher.validate`, goes through `ino_api_write`. That tool exists only on a test environment the engine has confirmed, and only when the user allowed writes for the profile.
  - `customer.get360` is not offered through MCP. Use `ino api call` in the terminal for it.
- Run `ino doctor` first. It checks the clock (signatures expire after ±300 s), the key and which environment the engine says the key belongs to.

## The flow
1. **Cart changes:** call `campaign.inquiry` with the cart. It returns the rewards the cart would earn and `spendablePoints`. Call it on every cart change; nothing is committed, but with a voucher code an expired voucher is marked expired.
2. **Checkout:** call `transaction.create` **once** after payment, with the order number, the customer and the cart. The engine works out the benefits itself; never send your own rewards.
3. **Order page:** `order.loyalty` returns the net loyalty result of an order already sent.
4. **Returns:** preview with `transaction.return.inquiry` (or `transaction.return.quote` for a refund quote), then commit with `transaction.return.commit`.
5. **Members:** `customer.create` registers directly. `customer.register.initiate` and `customer.register.confirm` register with an SMS check. If the phone or email already belongs to a member, check `needsVerification` before `customerId`.

## Rules that matter
- **Sign exactly the bytes you send.** Signature version 2: `hex(HMAC-SHA256(secret, "v2.{ts}.{nonce}.{METHOD}.{path-and-query}." + body))`. The path includes the `/t/<tenant>` prefix on Ino Cloud. Get working code with `ino_code_sample sign_call <language>`.
- **Idempotency:** put a GUID `requestId` in every body. A retry must send the **same body and the same requestId**, signed again with a **new nonce and timestamp** (a nonce is accepted once). Changing the body with the same id returns `IDEMPOTENCY_REPLAY` (409).
- **Field names belong to the channel's mapping.** The examples in the docs use the standard mapping; a channel with a custom mapping (for example a legacy POS) uses its own names. `ino api describe` shows the reference; ask the developer for their mapping if calls fail with `PARAMETER_ERROR`.
- **Do not retry a 401 in a loop.** 30 failures a minute block the caller's IP, which can include the shop's tills. Fix the key, secret or clock first.
- **Live is not for experiments.** On a live profile the CLI asks for `--live --yes` before any write, and the MCP server never writes.

## Done means
- `ino doctor` is green.
- A test order goes through `campaign.inquiry` → `transaction.create` → `order.loyalty`, and a return through `transaction.return.inquiry` → `transaction.return.commit`.
- Retrying the same `transaction.create` returns the original response, not a second sale.
