# {{name}}: your marketing tool for Ino Loyalty

Ino Loyalty's Marketing Activation sends loyalty profiles, events, unsubscribes and deletions to this server. The server applies them to {{platform}}. Ino keeps the hard parts: who may receive what (consent), the erasure ledger, retries and pausing. This server only has to apply what it gets and answer with the right status.

## Run it

```bash
cp .env.example .env                    # put the secret from `ino marketing connect --env-file` into INO_MA_SIGNING_SECRET
dotnet test tests/Connector.Tests       # the contract tests
dotnet run --project src/Connector      # POST /ino, GET /health (reads .env)
ino connector test --kind marketing --url http://localhost:8080/ino --env-file <the file from ino marketing connect>
```

Then deploy it behind a public https address and connect it:

```bash
ino marketing connect --name "{{platform}}" --url https://<your host>/ino --env-file ma.env
ino marketing verify <connection id>
```

Ino sends nothing until `verify` passes.

## What to change

Only `src/Connector/Tool.cs`: replace each method with calls to {{platform}}'s API and keep the return shapes. The signature check (`Signature.cs`) and the contract (`Contract.cs`) stay as they are.

## The contract (v1)

Every request is one `POST` with JSON `{"type", "contract": 1, "connectionId", "data"}`. It is signed with Standard Webhooks (`webhook-id`, `webhook-timestamp`, `webhook-signature`) using your connection's secret. Refuse anything unsigned or older than 5 minutes with 401.

| type | data | answer |
|---|---|---|
| `ping` | `challenge` | `{"challenge", "proof": base64(HMAC-SHA256(key, challenge))}`. The key is the secret without `whsec_`, base64-decoded |
| `profile.upsert` | `externalId`, `profileId` (null the first time), `contact {email, phone, firstName, lastName}`, `properties`, `unset[]` | `{"profileId"}`; 404 if the `profileId` given no longer exists |
| `event.track` | `profileId`, `name`, `eventId`, `outboxId`, `time`, `properties` | 2xx; apply each `eventId` once; 404 if the profile is gone |
| `profile.subscription` | `profileId` | `{"email": {"reachable", "consent", "address"?}, "sms": {…}}`; consent is SUBSCRIBED, UNSUBSCRIBED or NEVER_SUBSCRIBED; 404 if gone |
| `profile.unsubscribe` | `profileId`, `channel` (EMAIL/SMS) | 2xx; 404 if gone |
| `profile.delete` | `profileId` | 2xx; 404 if it was already gone (counts as done) |

Status codes mean something to Ino:

| Status | Meaning |
|---|---|
| 2xx | Done |
| 404 | The profile is not here: Ino rebuilds it, or a deletion counts as done |
| Other 4xx | Refused for this member |
| 401 / 403 | Refused for the whole connection |
| 408 / 429 / 5xx | Try again later |

Never redirect.

**Linking to a profile you already had.** Sometimes the member already has a profile in your tool, for example from a shop, and you link the Ino member to it. In that case answer `{"profileId", "adopted": true, "adoptedVia": "EMAIL" | "SMS" | "BOTH"}` with the channel whose value matched. From then on, `profile.subscription` must return the address for that profile. Ino unsubscribes such a profile only on the matched channel, so it never stops marketing to someone else who shares a phone number.

**Personal data.** Requests carry personal data: never log request bodies. Delete everything about a profile on `profile.delete`.
