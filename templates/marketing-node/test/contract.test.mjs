// Contract tests for the template (node --test). Each call is signed like Ino signs it.
import { test } from "node:test";
import assert from "node:assert/strict";
import { createHmac, randomBytes } from "node:crypto";
import { app } from "../src/server.js";
import { keyOf, proof } from "../src/signature.js";

const secret = "whsec_" + randomBytes(32).toString("base64");

function sign(body, { key = secret, ts = Math.floor(Date.now() / 1000) } = {}) {
  const id = "msg_" + randomBytes(8).toString("hex");  // a new id per request, as Ino sends
  const sig = createHmac("sha256", keyOf(key)).update(Buffer.concat([Buffer.from(`${id}.${ts}.`), body])).digest("base64");
  return { "webhook-id": id, "webhook-timestamp": String(ts), "webhook-signature": "v1," + sig, "content-type": "application/json" };
}

async function withServer(fn) {
  const server = app(secret).listen(0);
  await new Promise(r => server.once("listening", r));
  const url = `http://127.0.0.1:${server.address().port}/ino`;
  try { await fn(async (type, data, opts) => {
    const body = opts?.body ?? Buffer.from(JSON.stringify({ type, contract: opts?.contract ?? 1, connectionId: "c1", data }));
    const r = await fetch(url, { method: "POST", body, headers: opts?.headers ?? sign(body, opts) });
    return { status: r.status, body: await r.json() };
  }); } finally { server.close(); }
}

test("refuses unsigned, wrongly signed and old requests", () => withServer(async call => {
  assert.equal((await call("ping", { challenge: "x" }, { headers: { "content-type": "application/json" } })).status, 401);
  assert.equal((await call("ping", { challenge: "x" }, { key: "whsec_" + randomBytes(32).toString("base64") })).status, 401);
  assert.equal((await call("ping", { challenge: "x" }, { ts: Math.floor(Date.now() / 1000) - 600 })).status, 401);
}));

test("refuses a replayed message and a future contract version", () => withServer(async call => {
  const body = Buffer.from(JSON.stringify({ type: "ping", contract: 1, connectionId: "c1", data: { challenge: "r" } }));
  const headers = sign(body);
  assert.equal((await call("ping", { challenge: "r" }, { headers, body })).status, 200);
  assert.equal((await call("ping", { challenge: "r" }, { headers, body })).status, 401);
  assert.equal((await call("ping", { challenge: "v2" }, { contract: 2 })).status, 400);
}));

test("ping proves the secret", () => withServer(async call => {
  const r = await call("ping", { challenge: "abc" });
  assert.equal(r.status, 200);
  assert.equal(r.body.challenge, "abc");
  assert.equal(r.body.proof, proof(secret, "abc"));
}));

test("profile, event, subscription, unsubscribe, delete", () => withServer(async call => {
  const up = await call("profile.upsert", { externalId: "m1", profileId: null, contact: { email: "avery@example.com" }, properties: { "Ino Tier": "Gold" }, unset: [] });
  assert.equal(up.status, 200);
  const id = up.body.profileId;
  assert.equal((await call("profile.upsert", { externalId: "m1", profileId: id, contact: { email: "avery@example.com" }, properties: {}, unset: ["Ino Tier"] })).body.profileId, id);
  assert.equal((await call("event.track", { profileId: id, name: "points.earned", eventId: "e1", time: new Date().toISOString(), properties: {} })).status, 202);
  assert.equal((await call("event.track", { profileId: id, name: "points.earned", eventId: "e1", time: new Date().toISOString(), properties: {} })).status, 202);
  const sub = await call("profile.subscription", { profileId: id });
  assert.deepEqual([sub.body.email.reachable, sub.body.sms.reachable], [true, false]);
  assert.equal((await call("profile.unsubscribe", { profileId: id, channel: "EMAIL" })).status, 202);
  assert.equal((await call("profile.subscription", { profileId: id })).body.email.consent, "UNSUBSCRIBED");
  assert.equal((await call("profile.delete", { profileId: id })).status, 202);
  assert.equal((await call("profile.delete", { profileId: id })).status, 404);
  assert.equal((await call("event.track", { profileId: id, name: "x", eventId: "e2", time: new Date().toISOString(), properties: {} })).status, 404);
  assert.equal((await call("profile.upsert", { externalId: "m1", profileId: id, contact: {}, properties: {}, unset: [] })).status, 404);
  assert.equal((await call("nope", {})).status, 400);
}));
