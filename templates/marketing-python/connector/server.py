"""The tool's endpoint for Ino Loyalty: POST /ino (signed contract), GET /health. Register its public https address with
`ino marketing connect --url https://…/ino`, then `ino marketing verify <id>`. Locally: `ino connector test --kind marketing --url http://localhost:8080/ino`."""
import json, os, sys
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from .contract import handle
from .signature import key_of, verify

MAX_BODY = 64 * 1024


def make_handler(secret: str):
    key_of(secret)   # a malformed secret stops the server at start
    class Handler(BaseHTTPRequestHandler):
        def _send(self, status, body):
            data = json.dumps(body).encode()
            self.send_response(status)
            self.send_header("content-type", "application/json")
            self.send_header("content-length", str(len(data)))
            self.end_headers()
            self.wfile.write(data)

        def do_GET(self):
            self._send(200, {"status": "ok"}) if self.path == "/health" else self._send(404, {"error": "not found"})

        def do_POST(self):
            if self.path != "/ino":
                return self._send(404, {"error": "not found"})
            length = int(self.headers.get("content-length") or 0)
            if length > MAX_BODY:
                return self._send(413, {"error": "too large"})
            raw = self.rfile.read(length)
            ok, reason = verify(raw, self.headers, secret)
            if not ok:
                return self._send(401, {"error": reason})
            try:
                request = json.loads(raw)
            except ValueError:
                return self._send(400, {"error": "not JSON"})
            try:
                status, body = handle(request, secret)
            except Exception as e:   # never log the request body: it holds personal data
                print(f"ino request failed: {type(e).__name__}", file=sys.stderr)
                return self._send(503, {"error": "try again later"})
            self._send(status, body)

        def log_message(self, *args):   # no request lines in the log
            pass
    return Handler


def main():
    secret = os.environ.get("INO_MA_SIGNING_SECRET", "")
    if not secret.startswith("whsec_"):
        sys.exit("INO_MA_SIGNING_SECRET (whsec_…) is required.")
    port = int(os.environ.get("PORT", "8080"))
    print(f"listening on :{port} (POST /ino, GET /health)")
    ThreadingHTTPServer(("0.0.0.0", port), make_handler(secret)).serve_forever()


if __name__ == "__main__":
    main()
