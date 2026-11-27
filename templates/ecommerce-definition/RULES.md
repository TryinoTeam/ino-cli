# E-commerce package: what the engine allows

Ino runs this file inside its engine. Points, rewards, duplicate checks, retries, the credential vault and the audit trail stay in the engine;
the file only says how the shop's events become Ino operations and how Ino calls the shop back. A package that breaks a rule is refused when
you save it (`ino connector package save connector.json`); `ino connector package check connector.json` tells you why without saving it.

- **Id:** `local_<name>` (lower case, digits, `-`).
- **Address:** from the firm's `url` setting (https). Every call stays under that address. Leave out a default value: the firm enters its own.
- **Incoming webhooks:** `inbound.verify` is required (HMAC_SHA256, STANDARD_WEBHOOKS, TOKEN or BASIC). Ino answers only signed requests.
- **Each order once:** `requestKey` names the order (`commit:{{order.code}}`), so a webhook sent twice records one sale. `CONFLICT` means it is
  already recorded and counts as done.
- **Benefits come from Ino:** send the cart (lines, prices, payments). Never compute points or discounts in the package.
- **Members:** `transaction.customer_id` is the member's Ino id as the shop keeps it; leave it `null` when the order has none.
- **Credential:** `auth.kinds` is required (HEADER, BEARER or BASIC); the firm enters the key when it installs the package. Calls back to the
  shop (`actions`) use it.

Install it on a test channel first. `ino connector package preview <id> 1.0.0 --set baseUrl=https://… --set channel=<CHANNEL>` with a credential
shows the access summary (the operations it writes, the events it calls the shop on, the host); `install` then needs the reviewed access hash.
