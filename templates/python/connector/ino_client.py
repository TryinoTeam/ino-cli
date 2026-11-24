"""Signed calls to the Ino Loyalty channel API (signature version 2).

Rules that matter:
- sign exactly the bytes you send, over the path as called (on Ino Cloud it includes /t/<tenant>);
- every request body carries a GUID requestId; a retry sends the SAME body and requestId, signed again with a NEW nonce and timestamp;
- never retry a 401 in a loop: 30 failed sign-ins a minute block the caller's IP (and the tills behind it).
"""
import hashlib
import hmac
import json
import random
import secrets
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid

RETRYABLE = {429, 502, 503, 504}


def sign_v2(secret: str, timestamp: str, nonce: str, method: str, path_and_query: str, body: bytes) -> str:
    message = f"v2.{timestamp}.{nonce}.{method.upper()}.{path_and_query}.".encode("utf-8") + body
    return hmac.new(secret.encode("utf-8"), message, hashlib.sha256).hexdigest()


class _NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):  # a redirect would resend the signature elsewhere: refuse it
        return None


_opener = urllib.request.build_opener(_NoRedirect)


def http_send(method: str, url: str, body: bytes, headers: dict) -> tuple[int, dict, str]:
    """Default transport. Returns (status, headers, text); status 0 means the network failed."""
    req = urllib.request.Request(url, data=body, method=method, headers=headers)
    try:
        with _opener.open(req, timeout=30) as res:
            return res.status, dict(res.headers), res.read().decode("utf-8")
    except urllib.error.HTTPError as e:
        return e.code, dict(e.headers or {}), e.read().decode("utf-8", "replace")
    except (urllib.error.URLError, TimeoutError, OSError) as e:
        return 0, {}, json.dumps({"error": str(e)})


class InoClient:
    def __init__(self, cfg, send=http_send, now=time.time, wait=time.sleep):
        self.cfg, self.send, self.now, self.wait = cfg, send, now, wait

    def _headers(self, path: str, body: bytes) -> dict:
        ts = str(int(self.now()))
        nonce = secrets.token_hex(16)  # new for every try: a nonce is accepted once
        return {
            "Content-Type": "application/json", "X-Api-Key": self.cfg.api_key, "X-Timestamp": ts, "X-Signature-Version": "2",
            "X-Nonce": nonce, "X-Signature": sign_v2(self.cfg.secret, ts, nonce, "POST", path, body),
        }

    def heartbeat(self, name: str, version: str, platform: str | None = None, status: str = "ok",
                  detail: str | None = None, queue_depth: int | None = None) -> int:
        """Reports this connector to Ino (Power Apps -> Integrations -> Connectors, `ino connector status`). No member data."""
        payload = {k: v for k, v in {"name": name, "version": version, "platform": platform, "status": status,
                                     "detail": detail, "queueDepth": queue_depth}.items() if v is not None}
        body = json.dumps(payload).encode("utf-8")
        url = f"{self.cfg.base_url}/api/v1/{self.cfg.channel}/connector/heartbeat"
        status_code, _, _ = self.send("POST", url, body, self._headers(urllib.parse.urlsplit(url).path, body))
        return status_code

    def call(self, operation: str, body: dict | None = None, attempts: int = 4) -> dict:
        """Calls one operation. body['requestId'] is added when missing and kept across retries. Returns {status, body, requestId}."""
        payload = dict(body or {})
        payload.setdefault("requestId", str(uuid.uuid4()))
        raw = json.dumps(payload).encode("utf-8")  # serialized once: the same bytes are signed and sent on every try
        url = f"{self.cfg.base_url}/api/v1/{self.cfg.channel}/{operation}"
        path = urllib.parse.urlsplit(url).path
        last = None
        for i in range(attempts):
            if i > 0:
                self.wait(min(30.0, 2 ** (i - 1)) + random.random() / 4)
            status, headers, text = self.send("POST", url, raw, self._headers(path, raw))
            try:
                parsed = json.loads(text) if text else None
            except ValueError:
                parsed = text
            last = {"status": status, "body": parsed, "requestId": payload["requestId"]}
            retry_after = {k.lower(): v for k, v in headers.items()}.get("retry-after")
            if status == 429 and retry_after and retry_after.isdigit():
                self.wait(float(retry_after))
            if status != 0 and status not in RETRYABLE:
                return last  # 2xx and 4xx (including 401: fix the key, do not loop) are final
        return last
