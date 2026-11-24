// {{platform}} ↔ Ino Loyalty connector. Replace the /platform/* handlers with your platform's events.
import { createServer } from "node:http";
import { readFileSync } from "node:fs";
import { loadEnv, config } from "./config.js";
import { InoClient } from "./ino-client.js";
import { verifyWebhook } from "./webhook.js";
import { OfflineQueue } from "./queue.js";

loadEnv();
const cfg = config();
const ino = new InoClient(cfg);
const queue = new OfflineQueue(cfg.queueFile);
const seen = new Set();                       // webhook-ids already handled (use a database in production)

const readBody = req => new Promise((ok, fail) => { const parts = []; req.on("data", c => parts.push(c)); req.on("end", () => ok(Buffer.concat(parts))); req.on("error", fail); });
const send = (res, status, obj) => { res.writeHead(status, { "Content-Type": "application/json" }); res.end(JSON.stringify(obj)); };

/** A paid order on the platform → one transaction.create. Network trouble → offline queue with the original time. */
async function orderPaid(order) {
  const body = {
    requestId: order.requestId,                // keep it with the order, so a retry of the same order is safe
    orderNumber: order.orderNumber,
    customerId: order.customerId,
    transactionDate: order.paidAt ?? new Date().toISOString(),
    cart: order.cart,
    payments: order.payments,
  };
  const r = await ino.call("transaction.create", body);
  if (r.status === 0 || r.status >= 500) { queue.add("transaction.create", { ...body, requestId: r.requestId }); return { queued: true, requestId: r.requestId }; }
  return { status: r.status, result: r.body, requestId: r.requestId };
}

const server = createServer(async (req, res) => {
  try {
    if (req.method === "GET" && req.url === "/health") return send(res, 200, { ok: true, queued: queue.items().length });
    if (req.method === "POST" && req.url === "/webhooks/ino") {
      const raw = await readBody(req);
      if (!cfg.webhookSecret) return send(res, 500, { error: "INO_WEBHOOK_SECRET is not set" });
      const v = verifyWebhook(raw, req.headers, cfg.webhookSecret);
      if (!v.ok) return send(res, 401, { error: v.reason });
      if (seen.has(v.id)) return send(res, 200, { duplicate: true });
      seen.add(v.id);
      const event = JSON.parse(raw.toString("utf8"));
      if (event.test === true || req.headers["x-ino-test"] === "true") return send(res, 200, { test: true });   // a test event: do not act on it
      // TODO: apply the event in {{platform}} (points balance, tier, campaign changed …), after answering.
      send(res, 200, { received: event.type });
      return;
    }
    if (req.method === "POST" && req.url === "/platform/order-paid") return send(res, 200, await orderPaid(JSON.parse((await readBody(req)).toString("utf8"))));
    if (req.method === "POST" && req.url === "/platform/flush-queue") return send(res, 200, { sent: await queue.flush(ino) });
    send(res, 404, { error: "not found" });
  } catch (e) { send(res, 500, { error: String(e?.message ?? e) }); }
});

server.listen(cfg.port, () => console.error(`{{name}} listening on :${cfg.port} (channel ${cfg.channel})`));
setInterval(() => queue.flush(ino).catch(() => {}), 60_000).unref();   // retry the offline queue every minute

// Heartbeat every 5 minutes: Ino shows the connector as healthy, degraded (queue growing) or silent (no heartbeat for 15 minutes).
const version = JSON.parse(readFileSync(new URL("../package.json", import.meta.url), "utf8")).version;
const beat = () => {
  const depth = queue.items().length;
  return ino.heartbeat({ name: "{{name}}", version, platform: "{{platform}}", status: depth > 100 ? "degraded" : "ok",
    detail: depth > 100 ? `${depth} sales waiting in the offline queue` : undefined, queueDepth: depth }).catch(() => {});
};
beat();
setInterval(beat, 5 * 60_000).unref();
