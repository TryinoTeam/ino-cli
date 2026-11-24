// Verifies an Ino webhook (Standard Webhooks) on the RAW body, before JSON parsing.
// Deduplicate on webhook-id: delivery is at least once. Answer 2xx within 10 seconds and do the work afterwards.
import { createHmac, timingSafeEqual } from "node:crypto";

export function verifyWebhook(rawBody, headers, secret, nowSeconds = Math.floor(Date.now() / 1000)) {
  const h = Object.fromEntries(Object.entries(headers).map(([k, v]) => [k.toLowerCase(), String(v)]));
  const id = h["webhook-id"], ts = h["webhook-timestamp"], sigs = h["webhook-signature"] ?? "";
  if (!id || !ts || !sigs) return { ok: false, reason: "missing Standard Webhooks headers" };
  if (!/^\d+$/.test(ts) || Math.abs(nowSeconds - Number(ts)) > 300) return { ok: false, reason: "timestamp older than 5 minutes" };
  const key = Buffer.from(secret.replace(/^whsec_/, ""), "base64");
  const expected = Buffer.from(createHmac("sha256", key).update(Buffer.concat([Buffer.from(`${id}.${ts}.`, "utf8"), Buffer.from(rawBody)])).digest("base64"));
  const ok = sigs.split(" ").some(s => {
    const got = Buffer.from(s.replace(/^v1,/, ""));
    return got.length === expected.length && timingSafeEqual(got, expected);
  });
  return ok ? { ok: true, id } : { ok: false, reason: "signature does not match" };
}
