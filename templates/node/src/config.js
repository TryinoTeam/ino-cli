// Settings come only from the environment (or a .env file next to package.json); nothing secret is written in code.
import { readFileSync, existsSync } from "node:fs";

export function loadEnv(path = ".env") {
  if (!existsSync(path)) return;
  for (const line of readFileSync(path, "utf8").split(/\r?\n/)) {
    const m = /^\s*([A-Z0-9_]+)\s*=\s*(.*)\s*$/.exec(line);
    if (m && process.env[m[1]] === undefined) process.env[m[1]] = m[2];
  }
}

export function config(env = process.env) {
  const need = ["INO_BASE_URL", "INO_CHANNEL", "INO_API_KEY", "INO_HMAC_SECRET"];
  const missing = need.filter(k => !env[k]);
  if (missing.length) throw new Error(`Missing settings: ${missing.join(", ")} (see .env.example).`);
  return {
    baseUrl: env.INO_BASE_URL.replace(/\/+$/, ""),
    channel: env.INO_CHANNEL,
    apiKey: env.INO_API_KEY,
    secret: env.INO_HMAC_SECRET,
    webhookSecret: env.INO_WEBHOOK_SECRET ?? null,
    port: Number(env.PORT ?? 8080),
    queueFile: env.QUEUE_FILE ?? "./data/queue.jsonl",
  };
}
