// YOUR MARKETING TOOL. This file is a stand-in that keeps profiles in memory, so the project runs and passes `ino connector test --kind marketing`
// out of the box. Replace each function with calls to {{platform}}'s API; keep the return shapes. Everything else (signature, contract) stays as is.
import { randomUUID } from "node:crypto";

const profiles = new Map();   // profileId -> profile

/**
 * Creates or updates the member's profile. `profileId` is the id this tool gave before (null the first time).
 * Return { profileId } for a profile this tool made or already had for this Ino member (same externalId).
 * If you link the member to a profile that existed before for another reason (same e-mail in a shop), return
 * { profileId, adopted: true, adoptedVia: "EMAIL" | "SMS" | "BOTH" } — the channel whose value matched. Ino then only unsubscribes that channel.
 * Return null when `profileId` is given but no longer exists here (Ino asks again without it).
 */
export async function upsertProfile({ externalId, profileId, contact, properties, unset }) {
  let p = profileId ? profiles.get(profileId) : [...profiles.values()].find(x => x.externalId === externalId);
  if (profileId && !p) return null;
  if (!p) {
    p = { id: "p_" + randomUUID(), externalId, email: null, phone: null, emailConsent: "NEVER_SUBSCRIBED", smsConsent: "NEVER_SUBSCRIBED", properties: {} };
    profiles.set(p.id, p);
  }
  // Ino sends only the contact values the member consented to; a value missing now is removed here.
  p.email = contact?.email ?? null;
  p.phone = contact?.phone ?? null;
  p.firstName = contact?.firstName ?? null;
  p.lastName = contact?.lastName ?? null;
  Object.assign(p.properties, properties ?? {});
  for (const key of unset ?? []) delete p.properties[key];
  return { profileId: p.id };
}

/** Records one event on the profile. Return false when the profile does not exist here. Ino may send the same eventId again: apply it once. */
const seenEvents = new Set();
export async function trackEvent({ profileId, name, eventId, time, properties }) {
  if (!profiles.has(profileId)) return false;
  if (seenEvents.has(eventId)) return true;
  seenEvents.add(eventId);
  // e.g. POST /events to your tool with { profileId, name, time, properties }
  return true;
}

/**
 * The profile's marketing subscription as THIS tool holds it. Return null when the profile does not exist.
 * For each channel: reachable (the tool has an address for it), consent (SUBSCRIBED | UNSUBSCRIBED | NEVER_SUBSCRIBED) and, if you can share it,
 * the address. An adopted profile must return the address.
 */
export async function readSubscription({ profileId }) {
  const p = profiles.get(profileId);
  if (!p) return null;
  return {
    gone: false,
    email: { reachable: !!p.email, consent: p.emailConsent, ...(p.email ? { address: p.email } : {}) },
    sms: { reachable: !!p.phone, consent: p.smsConsent, ...(p.phone ? { address: p.phone } : {}) },
  };
}

/** Stops marketing on one channel ("EMAIL" or "SMS"). Return false when the profile does not exist. */
export async function unsubscribe({ profileId, channel }) {
  const p = profiles.get(profileId);
  if (!p) return false;
  if (channel === "EMAIL") p.emailConsent = "UNSUBSCRIBED"; else p.smsConsent = "UNSUBSCRIBED";
  return true;
}

/** KVKK/GDPR erasure: deletes the profile and everything about it. Return false when it was already gone. */
export async function deleteProfile({ profileId }) {
  return profiles.delete(profileId);
}
