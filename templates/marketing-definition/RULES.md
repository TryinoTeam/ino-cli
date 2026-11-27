# Marketing tool package: what the engine allows

Ino runs this file inside its engine. Consent, deletion, retries, the credential vault and the audit trail stay in the engine; the file only says how
each call to your tool is made. A package that breaks a rule is refused when you save it (`ino connector package save connector.json`).

- **Id:** `local_<name>` (lower case, digits, `-`). Published packages follow with Hub signing.
- **Address:** only from the firm's `url` setting, https, no default value. Every call stays under that address (no `..`, `//`, `@`, `%25`).
- **Credential:** one, of kind HEADER, BEARER or BASIC; the firm's key is sealed by `ino marketing connect` and kept in Ino's vault.
- **Values:** every value written comes from Ino's data (`$root.profile…`, `$root.event…`, `$root.remoteProfileId`), the firm's settings
  (`$settings.x`) or a constant you name in `marketing.constants` (`{"from": "$vars.name"}`). No `const`, `default`, `value_map`, `lookup`, literal
  text in templates, headers or the query; no constants in headers, the query or the path.
- **Transforms:** only ones that reshape a value: to_str, to_decimal, to_int, round, abs, upper, lower, trim, to_iso8601, to_unix_ms,
  cents_to_decimal, decimal_to_cents, first, flatten, slug. A date `format` only after `to_iso8601`.
- **Consent:** declare every subscription field of your tool in `marketing.consent`. Ino checks each request at those paths: a value is sent only
  when the member rejected that channel, or the call is that channel's unsubscribe. Ino cannot check a field you leave out; the person who installs the
  package sees every field and address it sends.
- **Channels:** the skeleton unsubscribes e-mail only. If your tool has an SMS subscription field, declare it in `marketing.consent` with
  `"channel": "SMS"` and write it in `unsubscribe` with a `when` on `$root.unsubscribe.channel` = `SMS`, like the e-mail one.
- **Deletion:** `deleteProfile` counts as done only on 2xx, or on 404/410 marked `already_gone`.

Run `ino marketing connect --definition <id> --version 1.0.0 --setting apiUrl=https://…` to see exactly what Ino will send before connecting.
