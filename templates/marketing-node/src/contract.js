// Ino Loyalty activation contract v1: one signed POST, `type` says what to do. Status codes carry meaning for Ino:
//   2xx done · 404 the profile is not here (Ino rebuilds it, or a deletion counts as done) · 4xx refused for this member
//   401/403 refused for the whole connection · 408/429/5xx try again later. Never redirect.
import * as tool from "./tool.js";
import { proof } from "./signature.js";

const reply = (status, body) => ({ status, body });

export async function handle(request, secret) {
  const { type, contract, data } = request ?? {};
  if (contract !== 1) return reply(400, { error: "unsupported contract version" });   // a future version is refused, never half-applied
  if (!data || typeof data !== "object") return reply(400, { error: "missing data" });
  switch (type) {
    case "ping":
      if (typeof data.challenge !== "string") return reply(400, { error: "missing challenge" });
      return reply(200, { challenge: data.challenge, proof: proof(secret, data.challenge), capabilities: [] });
    case "profile.upsert": {
      const r = await tool.upsertProfile(data);
      return r ? reply(200, r) : reply(404, { error: "profile not found" });
    }
    case "event.track":
      return (await tool.trackEvent(data)) ? reply(202, {}) : reply(404, { error: "profile not found" });
    case "profile.subscription": {
      const s = await tool.readSubscription(data);
      return s ? reply(200, s) : reply(404, { error: "profile not found" });
    }
    case "profile.unsubscribe":
      if (data.channel !== "EMAIL" && data.channel !== "SMS") return reply(400, { error: "unknown channel" });
      return (await tool.unsubscribe(data)) ? reply(202, {}) : reply(404, { error: "profile not found" });
    case "profile.delete":
      return (await tool.deleteProfile(data)) ? reply(202, {}) : reply(404, { error: "profile not found" });
    default:
      return reply(400, { error: "unknown type" });
  }
}
