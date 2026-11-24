// Signed calls to the Ino Loyalty channel API (signature version 2).
// Rules that matter:
// - sign exactly the bytes you send, over the path as called (on Ino Cloud it includes /t/<tenant>);
// - every request body carries a GUID requestId; a retry sends the SAME body and requestId, signed again with a NEW nonce and timestamp;
// - never retry a 401 in a loop: 30 failed sign-ins a minute block the caller's IP (and the tills behind it).
import { createHmac, randomUUID, randomBytes } from "node:crypto";

export function signV2(secret, timestamp, nonce, method, pathAndQuery, bodyBytes) {
  return createHmac("sha256", Buffer.from(secret, "utf8"))
    .update(Buffer.concat([Buffer.from(`v2.${timestamp}.${nonce}.${method.toUpperCase()}.${pathAndQuery}.`, "utf8"), bodyBytes]))
    .digest("hex");
}

const RETRYABLE = new Set([429, 502, 503, 504]);
const sleep = ms => new Promise(r => setTimeout(r, ms));

export class InoClient {
  constructor(cfg, { fetchImpl = fetch, now = () => Date.now(), wait = sleep } = {}) {
    this.cfg = cfg; this.fetch = fetchImpl; this.now = now; this.wait = wait;
  }

  /** Reports this connector to Ino (Power Apps → Integrations → Connectors, `ino connector status`). No member data. */
  async heartbeat({ name, version, platform, status = "ok", detail, queueDepth }) {
    const bytes = Buffer.from(JSON.stringify({ name, version, platform, status, detail, queueDepth }), "utf8");
    const url = new URL(`${this.cfg.baseUrl}/api/v1/${this.cfg.channel}/connector/heartbeat`);
    const ts = Math.floor(this.now() / 1000).toString();
    const nonce = randomBytes(16).toString("hex");
    const res = await this.fetch(url, { method: "POST", body: bytes, redirect: "manual", headers: {
      "Content-Type": "application/json", "X-Api-Key": this.cfg.apiKey, "X-Timestamp": ts, "X-Signature-Version": "2",
      "X-Nonce": nonce, "X-Signature": signV2(this.cfg.secret, ts, nonce, "POST", url.pathname + url.search, bytes) } });
    return res.status;
  }

  /** Calls one operation. body.requestId is added when missing and kept across retries. Returns { status, body, requestId }. */
  async call(operation, body = {}, { attempts = 4 } = {}) {
    const payload = { ...body, requestId: body.requestId ?? randomUUID() };
    const bytes = Buffer.from(JSON.stringify(payload), "utf8");           // serialized once: the same bytes are signed and sent on every try
    const url = new URL(`${this.cfg.baseUrl}/api/v1/${this.cfg.channel}/${operation}`);
    let last;
    for (let i = 0; i < attempts; i++) {
      if (i > 0) await this.wait(Math.min(30_000, 1000 * 2 ** (i - 1)) + Math.floor(Math.random() * 250));
      const ts = Math.floor(this.now() / 1000).toString();
      const nonce = randomBytes(16).toString("hex");                     // new for every try: a nonce is accepted once
      let res;
      try {
        res = await this.fetch(url, {
          method: "POST", body: bytes, redirect: "manual",
          headers: {
            "Content-Type": "application/json", "X-Api-Key": this.cfg.apiKey, "X-Timestamp": ts, "X-Signature-Version": "2",
            "X-Nonce": nonce, "X-Signature": signV2(this.cfg.secret, ts, nonce, "POST", url.pathname + url.search, bytes),
          },
        });
      } catch (e) { last = { status: 0, body: { error: String(e?.message ?? e) }, requestId: payload.requestId }; continue; }
      const text = await res.text();
      let parsed; try { parsed = text ? JSON.parse(text) : null; } catch { parsed = text; }
      last = { status: res.status, body: parsed, requestId: payload.requestId };
      if (res.status === 429 && res.headers.get("retry-after")) await this.wait(Number(res.headers.get("retry-after")) * 1000);
      if (!RETRYABLE.has(res.status)) return last;                       // 2xx, 4xx (including 401: fix the key, do not loop) are final
    }
    return last;
  }
}
