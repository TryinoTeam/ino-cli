"""Verifies an Ino request (Standard Webhooks) on the RAW body, before JSON parsing. Ino signs every call with the connection's secret.
A request older or newer than 5 minutes is refused, and each message id is accepted once (a captured request cannot be played again)."""
import base64, binascii, hashlib, hmac, re, threading, time

_seen: dict[str, float] = {}
_lock = threading.Lock()


def key_of(secret: str) -> bytes:
    raw = secret.removeprefix("whsec_")
    if not re.fullmatch(r"[A-Za-z0-9+/]+={0,2}", raw) or len(raw) % 4:
        raise ValueError("INO_MA_SIGNING_SECRET is not a valid whsec_ secret.")
    try:
        return base64.b64decode(raw, validate=True)
    except binascii.Error as e:
        raise ValueError("INO_MA_SIGNING_SECRET is not a valid whsec_ secret.") from e


def _first_time(msg_id: str, now: float) -> bool:
    with _lock:
        for k in [k for k, until in _seen.items() if until < now]:
            del _seen[k]
        if msg_id in _seen:
            return False
        _seen[msg_id] = now + 600
        return True


def verify(raw: bytes, headers, secret: str, now: float | None = None) -> tuple[bool, str]:
    h = {k.lower(): v for k, v in headers.items()}
    msg_id, ts, sigs = h.get("webhook-id"), h.get("webhook-timestamp"), h.get("webhook-signature", "")
    if not msg_id or not ts or not sigs:
        return False, "missing signature headers"
    now = time.time() if now is None else now
    if not ts.isdigit() or abs(now - int(ts)) > 300:
        return False, "timestamp outside 5 minutes"
    expected = base64.b64encode(hmac.new(key_of(secret), f"{msg_id}.{ts}.".encode() + raw, hashlib.sha256).digest()).decode()
    # The header may carry several signatures (space separated) while a secret is renewed; each needs the v1, prefix.
    if not any(s.startswith("v1,") and hmac.compare_digest(s[3:], expected) for s in sigs.split(" ")):
        return False, "signature does not match"
    if not _first_time(msg_id, now):
        return False, "message already received"
    return True, msg_id


def proof(secret: str, challenge: str) -> str:
    """The ping answer that proves this tool holds the secret: base64(HMAC-SHA256(key, challenge))."""
    return base64.b64encode(hmac.new(key_of(secret), challenge.encode(), hashlib.sha256).digest()).decode()
