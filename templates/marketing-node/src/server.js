// The tool's endpoint for Ino Loyalty. Register its public https address with `ino marketing connect --url https://…/ino`, then run
// `ino marketing verify <id>`. Locally: `ino connector test --kind marketing --url http://localhost:8080/ino --env-file <file>`.
import { createServer } from "node:http";
import { pathToFileURL } from "node:url";
import { verify, keyOf } from "./signature.js";
import { handle } from "./contract.js";

const MAX_BODY = 64 * 1024;

export function app(secret) {
  if (!secret || !secret.startsWith("whsec_")) throw new Error("INO_MA_SIGNING_SECRET (whsec_…) is required.");
  keyOf(secret);   // a malformed secret stops the server at start, not at the first request
  return createServer((req, res) => {
    const send = (status, body) => { res.writeHead(status, { "content-type": "application/json" }); res.end(JSON.stringify(body)); };
    if (req.method === "GET" && req.url === "/health") return send(200, { status: "ok" });
    if (req.method !== "POST" || req.url !== "/ino") return send(404, { error: "not found" });
    const chunks = []; let size = 0;
    req.on("data", c => { size += c.length; if (size > MAX_BODY) { send(413, { error: "too large" }); req.destroy(); } else chunks.push(c); });
    req.on("end", async () => {
      if (res.writableEnded) return;
      const raw = Buffer.concat(chunks);
      const check = verify(raw, req.headers, secret);
      if (!check.ok) return send(401, { error: check.reason });
      let request;
      try { request = JSON.parse(raw.toString("utf8")); } catch { return send(400, { error: "not JSON" }); }
      try {
        const { status, body } = await handle(request, secret);
        send(status, body);
      } catch (e) {
        console.error(`ino request failed: ${e.message}`);   // never log the request body: it holds personal data
        send(503, { error: "try again later" });
      }
    });
  });
}

// Started directly (node src/server.js), also from a folder whose path has spaces.
if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  const port = Number(process.env.PORT ?? 8080);
  app(process.env.INO_MA_SIGNING_SECRET).listen(port, () => console.log(`listening on :${port} (POST /ino, GET /health)`));
}
