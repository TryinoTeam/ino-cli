import { test } from "node:test";
import assert from "node:assert/strict";
import { createHmac } from "node:crypto";
import { mkdtempSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { signV2, InoClient } from "../src/ino-client.js";
import { verifyWebhook } from "../src/webhook.js";
import { OfflineQueue } from "../src/queue.js";

const cfg = { baseUrl: "https://engine.example.test/t/tabc1234", channel: "MY_CHANNEL", apiKey: "ik_test_key", secret: "test-secret" };

test("signature v2 covers method, path with tenant prefix and the exact body", () => {
  const body = Buffer.from('{"a":1}');
  const want = createHmac("sha256", "test-secret").update("v2.1700000000.n1.POST./t/tabc1234/api/v1/MY_CHANNEL/transaction.create." + '{"a":1}').digest("hex");
  assert.equal(signV2("test-secret", "1700000000", "n1", "post", "/t/tabc1234/api/v1/MY_CHANNEL/transaction.create", body), want);
});

test("a retry sends the same body and requestId with a new nonce; 401 is not retried", async () => {
  const seen = [];
  const replies = [503, 200];
  const client = new InoClient(cfg, { wait: async () => {}, fetchImpl: async (url, init) => {
    seen.push(init); return new Response('{"ResponseCode":"SUCCESS"}', { status: replies.shift() });
  } });
  const r = await client.call("transaction.create", { orderNumber: "A1" });
  assert.equal(r.status, 200);
  assert.equal(seen.length, 2);
  assert.deepEqual(seen[0].body, seen[1].body);
  assert.notEqual(seen[0].headers["X-Nonce"], seen[1].headers["X-Nonce"]);
  assert.match(JSON.parse(seen[0].body.toString()).requestId, /^[0-9a-f-]{36}$/);

  let calls = 0;
  const c401 = new InoClient(cfg, { wait: async () => {}, fetchImpl: async () => { calls++; return new Response("{}", { status: 401 }); } });
  assert.equal((await c401.call("transaction.create", {})).status, 401);
  assert.equal(calls, 1);
});

test("webhook check: valid, tampered, stale", () => {
  const secret = "whsec_" + Buffer.alloc(32, 7).toString("base64");
  const raw = Buffer.from('{"id":"e1","type":"points.earned"}');
  const ts = "1700000000";
  const sig = "v1," + createHmac("sha256", Buffer.alloc(32, 7)).update(`msg_1.${ts}.${raw}`).digest("base64");
  const h = { "webhook-id": "msg_1", "webhook-timestamp": ts, "webhook-signature": sig };
  assert.equal(verifyWebhook(raw, h, secret, 1700000010).ok, true);
  assert.equal(verifyWebhook(Buffer.from('{"id":"e2"}'), h, secret, 1700000010).ok, false);
  assert.equal(verifyWebhook(raw, h, secret, 1700009999).ok, false);
});

test("offline queue keeps order and stops at the first refusal", async () => {
  const q = new OfflineQueue(join(mkdtempSync(join(tmpdir(), "ino-q-")), "q.jsonl"));
  q.add("transaction.create", { orderNumber: "A1", requestId: "r1" });
  q.add("transaction.create", { orderNumber: "A2", requestId: "r2" });
  q.add("transaction.create", { orderNumber: "A3", requestId: "r3" });
  const statuses = [200, 400];
  const client = { call: async () => { const s = statuses.shift() ?? 200; return { status: s, body: { ResponseCode: s === 200 ? "SUCCESS" : "PARAMETER_ERROR" } }; } };
  assert.equal(await q.flush(client), 1);
  assert.deepEqual(q.items().map(i => i.body.orderNumber), ["A2", "A3"]);
});

test("heartbeat is signed and carries no member data", async () => {
  let seen;
  const client = new InoClient(cfg, { fetchImpl: async (url, init) => { seen = { url: String(url), init }; return new Response("{}", { status: 200 }); } });
  assert.equal(await client.heartbeat({ name: "demo", version: "0.1.0", queueDepth: 3 }), 200);
  assert.equal(seen.url, "https://engine.example.test/t/tabc1234/api/v1/MY_CHANNEL/connector/heartbeat");
  const h = seen.init.headers;
  assert.equal(h["X-Signature"], signV2("test-secret", h["X-Timestamp"], h["X-Nonce"], "POST", "/t/tabc1234/api/v1/MY_CHANNEL/connector/heartbeat", seen.init.body));
  assert.deepEqual(Object.keys(JSON.parse(seen.init.body.toString())).sort(), ["name", "queueDepth", "status", "version"]);
});
