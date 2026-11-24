"""Settings come only from the environment (or a .env file in the working folder); nothing secret is written in code."""
import os
import re
from dataclasses import dataclass


def load_env(path: str = ".env") -> None:
    if not os.path.exists(path):
        return
    with open(path, encoding="utf-8") as f:
        for line in f:
            m = re.match(r"^\s*([A-Z0-9_]+)\s*=\s*(.*?)\s*$", line)
            if m and m.group(1) not in os.environ:
                os.environ[m.group(1)] = m.group(2)


@dataclass(frozen=True)
class Config:
    base_url: str
    channel: str
    api_key: str
    secret: str
    webhook_secret: str | None = None
    port: int = 8080
    queue_file: str = "./data/queue.jsonl"


def config(env=os.environ) -> Config:
    need = ["INO_BASE_URL", "INO_CHANNEL", "INO_API_KEY", "INO_HMAC_SECRET"]
    missing = [k for k in need if not env.get(k)]
    if missing:
        raise RuntimeError(f"Missing settings: {', '.join(missing)} (see .env.example).")
    return Config(
        base_url=env["INO_BASE_URL"].rstrip("/"),
        channel=env["INO_CHANNEL"],
        api_key=env["INO_API_KEY"],
        secret=env["INO_HMAC_SECRET"],
        webhook_secret=env.get("INO_WEBHOOK_SECRET") or None,
        port=int(env.get("PORT", "8080")),
        queue_file=env.get("QUEUE_FILE", "./data/queue.jsonl"),
    )
