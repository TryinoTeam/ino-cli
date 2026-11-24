"""{{platform}} <-> Ino Loyalty connector. Replace the /platform/* handlers with your platform's events.

Run: python -m connector.server
"""
import json
import threading
from datetime import datetime, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

from . import __version__
from .config import config, load_env
from .ino_client import InoClient
from .offline_queue import OfflineQueue
from .webhook import verify_webhook

NAME, PLATFORM = "{{name}}", "{{platform}}"


class App:
    def __init__(self, cfg, client=None):
        self.cfg = cfg
        self.ino = client or InoClient(cfg)
        self.queue = OfflineQueue(cfg.queue_file)
        self.seen: set[str] = set()  # webhook-ids already handled (use a database in production)

    def order_paid(self, order: dict) -> dict:
        """A paid order on the platform -> one transaction.create. Network trouble -> offline queue with the original time."""
        body = {
            "requestId": order.get("requestId"),  # keep it with the order, so a retry of the same order is safe
            "orderNumber": order.get("orderNumber"),
            "customerId": order.get("customerId"),
            "transactionDate": order.get("paidAt") or datetime.now(timezone.utc).isoformat(),
            "cart": order.get("cart"),
            "payments": order.get("payments"),
        }
        body = {k: v for k, v in body.items() if v is not None}
        r = self.ino.call("transaction.create", body)
        if r["status"] == 0 or r["status"] >= 500:
            self.queue.add("transaction.create", {**body, "requestId": r["requestId"]})
            return {"queued": True, "requestId": r["requestId"]}
        return {"status": r["status"], "result": r["body"], "requestId": r["requestId"]}

    def beat(self) -> None:
        # Ino shows the connector as healthy, degraded (queue growing) or silent (no heartbeat for 15 minutes).
        depth = len(self.queue.items())
        try:
            self.ino.heartbeat(NAME, __version__, PLATFORM, status="degraded" if depth > 100 else "ok",
                               detail=f"{depth} sales waiting in the offline queue" if depth > 100 else None, queue_depth=depth)
        except Exception:  # noqa: BLE001 - a missed heartbeat must not stop the connector
            pass


def handler(app: App):
    class Handler(BaseHTTPRequestHandler):
        def _send(self, status: int, obj) -> None:
            data = json.dumps(obj).encode("utf-8")
            self.send_response(status)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(data)))
            self.end_headers()
            self.wfile.write(data)

        def _raw(self) -> bytes:
            return self.rfile.read(int(self.headers.get("Content-Length") or 0))

        def do_GET(self):
            if self.path == "/health":
                return self._send(200, {"ok": True, "queued": len(app.queue.items())})
            self._send(404, {"error": "not found"})

        def do_POST(self):
            try:
                if self.path == "/webhooks/ino":
                    raw = self._raw()
                    if not app.cfg.webhook_secret:
                        return self._send(500, {"error": "INO_WEBHOOK_SECRET is not set"})
                    v = verify_webhook(raw, dict(self.headers), app.cfg.webhook_secret)
                    if not v["ok"]:
                        return self._send(401, {"error": v["reason"]})
                    if v["id"] in app.seen:
                        return self._send(200, {"duplicate": True})
                    app.seen.add(v["id"])
                    event = json.loads(raw)
                    if event.get("test") is True or self.headers.get("X-Ino-Test") == "true":
                        return self._send(200, {"test": True})  # a test event: do not act on it
                    # TODO: apply the event in {{platform}} (points balance, tier, campaign changed ...), after answering.
                    return self._send(200, {"received": event.get("type")})
                if self.path == "/platform/order-paid":
                    return self._send(200, app.order_paid(json.loads(self._raw())))
                if self.path == "/platform/flush-queue":
                    return self._send(200, {"sent": app.queue.flush(app.ino)})
                self._send(404, {"error": "not found"})
            except Exception as e:  # noqa: BLE001
                self._send(500, {"error": str(e)})

        def log_message(self, *args):  # requests may carry member data: do not write them to stdout
            pass

    return Handler


def _every(seconds: float, fn) -> None:
    def loop():
        while not stop.wait(seconds):
            fn()
    stop = threading.Event()
    threading.Thread(target=loop, daemon=True).start()


def main() -> None:
    load_env()
    app = App(config())
    _every(60, lambda: app.queue.flush(app.ino))  # retry the offline queue every minute
    app.beat()
    _every(300, app.beat)                          # heartbeat every 5 minutes
    server = ThreadingHTTPServer(("0.0.0.0", app.cfg.port), handler(app))
    print(f"{NAME} listening on :{app.cfg.port} (channel {app.cfg.channel})", flush=True)
    server.serve_forever()


if __name__ == "__main__":
    main()
