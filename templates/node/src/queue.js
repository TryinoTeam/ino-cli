// Offline queue: when Ino cannot be reached, a sale is written here with its original time and sent later in order.
// The engine accepts back-dated sales within the channel's window (default 7 days); transactionDate keeps the real sale time.
import { appendFileSync, readFileSync, writeFileSync, existsSync, mkdirSync, renameSync } from "node:fs";
import { dirname } from "node:path";

export class OfflineQueue {
  constructor(file) { this.file = file; mkdirSync(dirname(file), { recursive: true }); }

  add(operation, body) { appendFileSync(this.file, JSON.stringify({ operation, body }) + "\n"); }

  items() {
    if (!existsSync(this.file)) return [];
    return readFileSync(this.file, "utf8").split("\n").filter(Boolean).map(l => JSON.parse(l));
  }

  /** Sends queued items in order; stops at the first one Ino does not accept so order is kept. Returns how many were sent. */
  async flush(client) {
    const items = this.items();
    let sent = 0;
    for (const it of items) {
      const r = await client.call(it.operation, it.body);
      const code = r.body?.ResponseCode;
      const done = (r.status >= 200 && r.status < 300 && (!code || code === "SUCCESS")) || code === "CONFLICT";   // CONFLICT: already recorded
      if (!done) break;
      sent++;
    }
    const rest = items.slice(sent).map(i => JSON.stringify(i) + "\n").join("");
    const tmp = this.file + ".tmp";
    writeFileSync(tmp, rest);
    renameSync(tmp, this.file);
    return sent;
  }
}
