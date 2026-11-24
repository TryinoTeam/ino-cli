"""Verifies an Ino webhook (Standard Webhooks) on the RAW body, before JSON parsing.

Deduplicate on webhook-id: delivery is at least once. Answer 2xx within 10 seconds and do the work afterwards.
"""
import base64
import hashlib
import hmac
import time


def verify_webhook(raw_body: bytes, headers: dict, secret: str, now_seconds: int | None = None) -> dict:
    h = {str(k).lower(): str(v) for k, v in headers.items()}
    msg_id, ts, sigs = h.get("webhook-id"), h.get("webhook-timestamp"), h.get("webhook-signature", "")
    if not msg_id or not ts or not sigs:
        return {"ok": False, "reason": "missing Standard Webhooks headers"}
    now = int(time.time()) if now_seconds is None else now_seconds
    if not ts.isdigit() or abs(now - int(ts)) > 300:
        return {"ok": False, "reason": "timestamp older than 5 minutes"}
    key = base64.b64decode(secret.removeprefix("whsec_"))
    expected = base64.b64encode(hmac.new(key, f"{msg_id}.{ts}.".encode("utf-8") + raw_body, hashlib.sha256).digest())
    for s in sigs.split(" "):
        if hmac.compare_digest(s.removeprefix("v1,").encode("utf-8"), expected):
            return {"ok": True, "id": msg_id}
    return {"ok": False, "reason": "signature does not match"}
