"""YOUR MARKETING TOOL. A stand-in that keeps profiles in memory, so the project runs and passes `ino connector test --kind marketing` out of
the box. Replace each function with calls to {{platform}}'s API and keep the return shapes. Signature and contract stay as they are."""
import uuid

_profiles: dict[str, dict] = {}
_seen_events: set[str] = set()


def upsert_profile(d: dict) -> dict | None:
    """Create or update the member's profile. Return {"profileId"}; if you link the member to a profile you already had (same e-mail in a shop),
    return {"profileId", "adopted": True, "adoptedVia": "EMAIL" | "SMS" | "BOTH"}. Return None when the given profileId no longer exists."""
    pid = d.get("profileId")
    p = _profiles.get(pid) if pid else next((x for x in _profiles.values() if x["externalId"] == d.get("externalId")), None)
    if pid and p is None:
        return None
    if p is None:
        p = {"id": "p_" + str(uuid.uuid4()), "externalId": d.get("externalId"), "emailConsent": "NEVER_SUBSCRIBED", "smsConsent": "NEVER_SUBSCRIBED", "properties": {}}
        _profiles[p["id"]] = p
    contact = d.get("contact") or {}
    p["email"], p["phone"] = contact.get("email"), contact.get("phone")   # only consented values come; a missing one is removed here
    p["properties"].update(d.get("properties") or {})
    for k in d.get("unset") or []:
        p["properties"].pop(k, None)
    return {"profileId": p["id"]}


def track_event(d: dict) -> bool:
    """Record one event; apply each eventId once. Return False when the profile does not exist."""
    if d.get("profileId") not in _profiles:
        return False
    _seen_events.add(d.get("eventId"))
    return True


def read_subscription(d: dict) -> dict | None:
    """The subscription as THIS tool holds it; None when the profile does not exist. An adopted profile must return the address."""
    p = _profiles.get(d.get("profileId"))
    if p is None:
        return None
    def channel(addr, consent):
        return {"reachable": bool(addr), "consent": consent, **({"address": addr} if addr else {})}
    return {"gone": False, "email": channel(p["email"], p["emailConsent"]), "sms": channel(p["phone"], p["smsConsent"])}


def unsubscribe(d: dict) -> bool:
    p = _profiles.get(d.get("profileId"))
    if p is None:
        return False
    p["emailConsent" if d.get("channel") == "EMAIL" else "smsConsent"] = "UNSUBSCRIBED"
    return True


def delete_profile(d: dict) -> bool:
    """KVKK/GDPR erasure: delete the profile and everything about it. Return False when it was already gone."""
    return _profiles.pop(d.get("profileId"), None) is not None
