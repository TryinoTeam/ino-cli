import base64
import hashlib
import hmac
import json
import os
import tempfile
import unittest
import urllib.parse

from connector.config import Config
from connector.ino_client import InoClient, sign_v2
from connector.offline_queue import OfflineQueue
from connector.webhook import verify_webhook

CFG = Config(base_url="https://engine.example.test/t/tabc1234", channel="MY_CHANNEL", api_key="ik_test_key", secret="test-secret")


class Recorder:
    def __init__(self, statuses):
        self.statuses, self.seen = list(statuses), []

    def __call__(self, method, url, body, headers):
        self.seen.append((method, url, body, headers))
        s = self.statuses.pop(0) if self.statuses else 200
        return s, {}, json.dumps({"ResponseCode": "SUCCESS"})


class ConnectorTests(unittest.TestCase):
    def test_signature_v2_covers_method_path_and_exact_body(self):
        want = hmac.new(b"test-secret", b"v2.1700000000.n1.POST./t/tabc1234/api/v1/MY_CHANNEL/transaction.create." + b'{"a":1}',
                        hashlib.sha256).hexdigest()
        self.assertEqual(sign_v2("test-secret", "1700000000", "n1", "post", "/t/tabc1234/api/v1/MY_CHANNEL/transaction.create", b'{"a":1}'), want)

    def test_retry_sends_same_body_new_nonce_and_401_is_final(self):
        rec = Recorder([503, 200])
        r = InoClient(CFG, send=rec, wait=lambda s: None).call("transaction.create", {"orderNumber": "A1"})
        self.assertEqual(r["status"], 200)
        self.assertEqual(len(rec.seen), 2)
        self.assertEqual(rec.seen[0][2], rec.seen[1][2])
        self.assertNotEqual(rec.seen[0][3]["X-Nonce"], rec.seen[1][3]["X-Nonce"])
        self.assertRegex(json.loads(rec.seen[0][2])["requestId"], r"^[0-9a-f-]{36}$")
        rec401 = Recorder([401, 401, 401])
        self.assertEqual(InoClient(CFG, send=rec401, wait=lambda s: None).call("transaction.create", {})["status"], 401)
        self.assertEqual(len(rec401.seen), 1)

    def test_webhook_valid_tampered_stale(self):
        key = bytes([7]) * 32
        secret = "whsec_" + base64.b64encode(key).decode()
        raw, ts = b'{"id":"e1","type":"points.earned"}', "1700000000"
        sig = "v1," + base64.b64encode(hmac.new(key, f"msg_1.{ts}.".encode() + raw, hashlib.sha256).digest()).decode()
        h = {"webhook-id": "msg_1", "webhook-timestamp": ts, "webhook-signature": sig}
        self.assertTrue(verify_webhook(raw, h, secret, 1700000010)["ok"])
        self.assertFalse(verify_webhook(b'{"id":"e2"}', h, secret, 1700000010)["ok"])
        self.assertFalse(verify_webhook(raw, h, secret, 1700009999)["ok"])

    def test_offline_queue_keeps_order_and_stops_at_first_refusal(self):
        q = OfflineQueue(os.path.join(tempfile.mkdtemp(prefix="ino-q-"), "q.jsonl"))
        for n in ("A1", "A2", "A3"):
            q.add("transaction.create", {"orderNumber": n, "requestId": "r" + n})
        statuses = [200, 400]

        class Fake:
            def call(self, op, body):
                s = statuses.pop(0) if statuses else 200
                return {"status": s, "body": {"ResponseCode": "SUCCESS" if s == 200 else "PARAMETER_ERROR"}}
        self.assertEqual(q.flush(Fake()), 1)
        self.assertEqual([i["body"]["orderNumber"] for i in q.items()], ["A2", "A3"])

    def test_heartbeat_is_signed_and_carries_no_member_data(self):
        rec = Recorder([200])
        self.assertEqual(InoClient(CFG, send=rec).heartbeat("demo", "0.1.0", queue_depth=3), 200)
        method, url, body, h = rec.seen[0]
        self.assertEqual(url, "https://engine.example.test/t/tabc1234/api/v1/MY_CHANNEL/connector/heartbeat")
        self.assertEqual(h["X-Signature"], sign_v2("test-secret", h["X-Timestamp"], h["X-Nonce"], "POST", urllib.parse.urlsplit(url).path, body))
        self.assertEqual(sorted(json.loads(body)), ["name", "queueDepth", "status", "version"])


if __name__ == "__main__":
    unittest.main()
