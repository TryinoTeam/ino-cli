"""Offline queue: when Ino cannot be reached, a sale is written here with its original time and sent later in order.

The engine accepts back-dated sales within the channel's window (default 7 days); transactionDate keeps the real sale time.
"""
import json
import os
import threading


class OfflineQueue:
    def __init__(self, file: str):
        self.file = file
        self._lock = threading.Lock()
        os.makedirs(os.path.dirname(os.path.abspath(file)), exist_ok=True)

    def add(self, operation: str, body: dict) -> None:
        with self._lock, open(self.file, "a", encoding="utf-8") as f:
            f.write(json.dumps({"operation": operation, "body": body}) + "\n")

    def items(self) -> list[dict]:
        if not os.path.exists(self.file):
            return []
        with open(self.file, encoding="utf-8") as f:
            return [json.loads(line) for line in f if line.strip()]

    def flush(self, client) -> int:
        """Sends queued items in order; stops at the first one Ino does not accept so order is kept. Returns how many were sent."""
        with self._lock:
            items = self.items()
            sent = 0
            for it in items:
                r = client.call(it["operation"], it["body"])
                code = r["body"].get("ResponseCode") if isinstance(r["body"], dict) else None
                done = (200 <= r["status"] < 300 and code in (None, "SUCCESS")) or code == "CONFLICT"  # CONFLICT: already recorded
                if not done:
                    break
                sent += 1
            tmp = self.file + ".tmp"
            with open(tmp, "w", encoding="utf-8") as f:
                f.writelines(json.dumps(i) + "\n" for i in items[sent:])
            os.replace(tmp, self.file)
            return sent
