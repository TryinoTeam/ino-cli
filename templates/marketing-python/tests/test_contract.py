"""Contract tests for the template: python -m unittest discover -s tests. Each call is signed like Ino signs it."""
import base64, hashlib, hmac, json, os, threading, time, unittest, urllib.error, urllib.request
from http.server import ThreadingHTTPServer
from connector.server import make_handler
from connector.signature import key_of, proof

SECRET = "whsec_" + base64.b64encode(os.urandom(32)).decode()


def sign(body: bytes, key=SECRET, ts=None):
    ts = int(time.time()) if ts is None else ts
    msg = "msg_" + os.urandom(8).hex()
    sig = base64.b64encode(hmac.new(key_of(key), f"{msg}.{ts}.".encode() + body, hashlib.sha256).digest()).decode()
    return {"webhook-id": msg, "webhook-timestamp": str(ts), "webhook-signature": "v1," + sig, "content-type": "application/json"}


class Contract(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.server = ThreadingHTTPServer(("127.0.0.1", 0), make_handler(SECRET))
        threading.Thread(target=cls.server.serve_forever, daemon=True).start()
        cls.url = f"http://127.0.0.1:{cls.server.server_address[1]}/ino"

    @classmethod
    def tearDownClass(cls):
        cls.server.shutdown()

    def call(self, t, data, headers=None, contract=1, body=None, **kw):
        body = body or json.dumps({"type": t, "contract": contract, "connectionId": "c1", "data": data}).encode()
        req = urllib.request.Request(self.url, data=body, headers=headers or sign(body, **kw), method="POST")
        try:
            with urllib.request.urlopen(req) as r:
                return r.status, json.loads(r.read() or b"{}")
        except urllib.error.HTTPError as e:
            return e.code, json.loads(e.read() or b"{}")

    def test_refuses_unsigned_wrong_old_replayed_and_future_contract(self):
        self.assertEqual(self.call("ping", {"challenge": "x"}, headers={"content-type": "application/json"})[0], 401)
        self.assertEqual(self.call("ping", {"challenge": "x"}, key="whsec_" + base64.b64encode(os.urandom(32)).decode())[0], 401)
        self.assertEqual(self.call("ping", {"challenge": "x"}, ts=int(time.time()) - 600)[0], 401)
        body = json.dumps({"type": "ping", "contract": 1, "connectionId": "c1", "data": {"challenge": "r"}}).encode()
        h = sign(body)
        self.assertEqual(self.call("ping", None, headers=h, body=body)[0], 200)
        self.assertEqual(self.call("ping", None, headers=h, body=body)[0], 401)
        self.assertEqual(self.call("ping", {"challenge": "v2"}, contract=2)[0], 400)

    def test_ping_proves_the_secret(self):
        status, body = self.call("ping", {"challenge": "abc"})
        self.assertEqual((status, body["challenge"], body["proof"]), (200, "abc", proof(SECRET, "abc")))

    def test_profile_event_subscription_unsubscribe_delete(self):
        s, b = self.call("profile.upsert", {"externalId": "m1", "profileId": None, "contact": {"email": "avery@example.com"}, "properties": {}, "unset": []})
        self.assertEqual(s, 200)
        pid = b["profileId"]
        self.assertEqual(self.call("event.track", {"profileId": pid, "name": "points.earned", "eventId": "e1", "properties": {}})[0], 202)
        sub = self.call("profile.subscription", {"profileId": pid})[1]
        self.assertEqual((sub["email"]["reachable"], sub["sms"]["reachable"]), (True, False))
        self.assertEqual(self.call("profile.unsubscribe", {"profileId": pid, "channel": "EMAIL"})[0], 202)
        self.assertEqual(self.call("profile.subscription", {"profileId": pid})[1]["email"]["consent"], "UNSUBSCRIBED")
        self.assertEqual(self.call("profile.delete", {"profileId": pid})[0], 202)
        self.assertEqual(self.call("profile.delete", {"profileId": pid})[0], 404)
        self.assertEqual(self.call("event.track", {"profileId": pid, "name": "x", "eventId": "e2", "properties": {}})[0], 404)
        self.assertEqual(self.call("nope", {})[0], 400)


if __name__ == "__main__":
    unittest.main()
