// Verifies an Ino request (Standard Webhooks) on the RAW body, before JSON parsing. Ino signs every call with the connection's secret.
// A request older or newer than 5 minutes is refused (a recorded request cannot be played again later).
import { createHmac, timingSafeEqual } from "node:crypto";

export function keyOf(secret) {
  const raw = secret.replace(/^whsec_/, "");
  // A malformed secret is refused instead of guessed (Buffer.from would silently skip bad characters).
  if (!/^[A-Za-z0-9+/]+={0,2}$/.test(raw) || raw.length % 4 !== 0) throw new Error("INO_MA_SIGNING_SECRET is not a valid whsec_ secret.");
  return Buffer.from(raw, "base64");
}

// Each message id is accepted once within the 5-minute window: a captured request cannot be played again (it could roll back newer data).
const seen = new Map();   // webhook-id -> expiry (ms)
function firstTime(id, now) {
  for (const [k, until] of seen) if (until < now) seen.delete(k);
  if (seen.has(id)) return false;
  seen.set(id, now + 10 * 60 * 1000);
  return true;
}

export function verify(rawBody, headers, secret, nowSeconds = Math.floor(Date.now() / 1000)) {
  const h = Object.fromEntries(Object.entries(headers).map(([k, v]) => [k.toLowerCase(), String(v)]));
  const id = h["webhook-id"], ts = h["webhook-timestamp"], sigs = h["webhook-signature"] ?? "";
  if (!id || !ts || !sigs) return { ok: false, reason: "missing signature headers" };
  if (!/^\d+$/.test(ts) || Math.abs(nowSeconds - Number(ts)) > 300) return { ok: false, reason: "timestamp outside 5 minutes" };
  const expected = Buffer.from(createHmac("sha256", keyOf(secret)).update(Buffer.concat([Buffer.from(`${id}.${ts}.`, "utf8"), rawBody])).digest("base64"));
  // The header may carry more than one signature (space separated) while a secret is being renewed.
  const ok = sigs.split(" ").some(s => {
    if (!s.startsWith("v1,")) return false;
    const got = Buffer.from(s.slice(3));
    return got.length === expected.length && timingSafeEqual(got, expected);
  });
  if (!ok) return { ok: false, reason: "signature does not match" };
  if (!firstTime(id, nowSeconds * 1000)) return { ok: false, reason: "message already received" };
  return { ok: true, id };
}

/** The ping answer that proves this tool holds the secret: base64(HMAC-SHA256(key, challenge)). */
export function proof(secret, challenge) {
  return createHmac("sha256", keyOf(secret)).update(challenge, "utf8").digest("base64");
}
