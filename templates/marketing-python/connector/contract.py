"""Ino Loyalty activation contract v1: one signed POST, `type` says what to do. Status codes carry meaning for Ino:
2xx done · 404 the profile is not here (Ino rebuilds it, or a deletion counts as done) · 4xx refused for this member ·
401/403 refused for the whole connection · 408/429/5xx try again later. Never redirect."""
from . import tool
from .signature import proof


def handle(request: dict, secret: str) -> tuple[int, dict]:
    if not isinstance(request, dict) or request.get("contract") != 1:
        return 400, {"error": "unsupported contract version"}
    t, d = request.get("type"), request.get("data")
    if not isinstance(d, dict):
        return 400, {"error": "missing data"}
    if t == "ping":
        if not isinstance(d.get("challenge"), str):
            return 400, {"error": "missing challenge"}
        return 200, {"challenge": d["challenge"], "proof": proof(secret, d["challenge"]), "capabilities": []}
    if t == "profile.upsert":
        r = tool.upsert_profile(d)
        return (200, r) if r else (404, {"error": "profile not found"})
    if t == "event.track":
        return (202, {}) if tool.track_event(d) else (404, {"error": "profile not found"})
    if t == "profile.subscription":
        s = tool.read_subscription(d)
        return (200, s) if s else (404, {"error": "profile not found"})
    if t == "profile.unsubscribe":
        if d.get("channel") not in ("EMAIL", "SMS"):
            return 400, {"error": "unknown channel"}
        return (202, {}) if tool.unsubscribe(d) else (404, {"error": "profile not found"})
    if t == "profile.delete":
        return (202, {}) if tool.delete_profile(d) else (404, {"error": "profile not found"})
    return 400, {"error": "unknown type"}
