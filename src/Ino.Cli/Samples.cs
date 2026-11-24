namespace Ino.Cli;

/// <summary>Code samples for ino_code_sample (ported from tools/ino-mcp/samples.mjs). Secrets come from environment variables in every sample.</summary>
public static class Samples
{
    public static readonly string[] Kinds = ["sign_call", "verify_webhook"];
    public static readonly string[] Languages = ["node", "python", "csharp"];

    static readonly Dictionary<(string, string), string> All = new()
    {
        [("sign_call", "node")] = """
// Signed call to the Ino real-time channel API (signature version 2). Node.js 18+.
import { createHmac, randomUUID } from "node:crypto";

export async function inoCall(operation, body) {
  const base = process.env.INO_BASE_URL;            // e.g. https://<your-test-host>
  const path = `/api/v1/${process.env.INO_CHANNEL}/${operation}`;
  const raw = JSON.stringify(body);                 // sign exactly the bytes you send
  const ts = Math.floor(Date.now() / 1000).toString();
  const nonce = randomUUID().replace(/-/g, "");
  const sig = createHmac("sha256", process.env.INO_HMAC_SECRET).update(`v2.${ts}.${nonce}.POST.${path}.${raw}`).digest("hex");
  const res = await fetch(base + path, { method: "POST", body: raw, headers: {
    "Content-Type": "application/json", "X-Api-Key": process.env.INO_API_KEY, "X-Timestamp": ts,
    "X-Signature-Version": "2", "X-Nonce": nonce, "X-Signature": sig } });
  return res.json();
}
""",
        [("sign_call", "python")] = """
# Signed call to the Ino real-time channel API (signature version 2). Python 3.9+, requests.
import hashlib, hmac, json, os, time, uuid
import requests

def ino_call(operation: str, body: dict) -> dict:
    path = f"/api/v1/{os.environ['INO_CHANNEL']}/{operation}"
    raw = json.dumps(body, separators=(",", ":"))     # sign exactly the bytes you send
    ts = str(int(time.time()))
    nonce = uuid.uuid4().hex
    msg = f"v2.{ts}.{nonce}.POST.{path}.".encode() + raw.encode()
    sig = hmac.new(os.environ["INO_HMAC_SECRET"].encode(), msg, hashlib.sha256).hexdigest()
    r = requests.post(os.environ["INO_BASE_URL"] + path, data=raw, timeout=20, headers={
        "Content-Type": "application/json", "X-Api-Key": os.environ["INO_API_KEY"], "X-Timestamp": ts,
        "X-Signature-Version": "2", "X-Nonce": nonce, "X-Signature": sig})
    return r.json()
""",
        [("sign_call", "csharp")] = """
// Signed call to the Ino real-time channel API (signature version 2). .NET 8+.
using System.Security.Cryptography;
using System.Text;

static async Task<string> InoCall(HttpClient http, string operation, string rawJsonBody)
{
    var path = $"/api/v1/{Environment.GetEnvironmentVariable("INO_CHANNEL")}/{operation}";
    var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
    var nonce = Guid.NewGuid().ToString("N");
    var secret = Encoding.UTF8.GetBytes(Environment.GetEnvironmentVariable("INO_HMAC_SECRET")!);
    var sig = Convert.ToHexStringLower(HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes($"v2.{ts}.{nonce}.POST.{path}.{rawJsonBody}")));
    using var req = new HttpRequestMessage(HttpMethod.Post, Environment.GetEnvironmentVariable("INO_BASE_URL") + path)
        { Content = new StringContent(rawJsonBody, Encoding.UTF8, "application/json") };   // send exactly the signed bytes
    req.Headers.Add("X-Api-Key", Environment.GetEnvironmentVariable("INO_API_KEY"));
    req.Headers.Add("X-Timestamp", ts); req.Headers.Add("X-Signature-Version", "2"); req.Headers.Add("X-Nonce", nonce); req.Headers.Add("X-Signature", sig);
    var res = await http.SendAsync(req);
    return await res.Content.ReadAsStringAsync();
}
""",
        [("verify_webhook", "node")] = """
// Verify an Ino webhook (Standard Webhooks). Use the raw body, before JSON parsing. npm i standardwebhooks — or by hand:
import { createHmac, timingSafeEqual } from "node:crypto";

export function verifyIno(rawBody, headers, secret = process.env.INO_WEBHOOK_SECRET) {
  const id = headers["webhook-id"], ts = headers["webhook-timestamp"], sigs = headers["webhook-signature"] ?? "";
  if (Math.abs(Date.now() / 1000 - Number(ts)) > 300) return false;                    // older than 5 minutes
  const key = Buffer.from(secret.replace(/^whsec_/, ""), "base64");
  const expected = Buffer.from(createHmac("sha256", key).update(`${id}.${ts}.${rawBody}`).digest("base64"));
  return sigs.split(" ").some(s => { const got = Buffer.from(s.replace(/^v1,/, "")); return got.length === expected.length && timingSafeEqual(got, expected); });
}
""",
        [("verify_webhook", "python")] = """
# Verify an Ino webhook (Standard Webhooks). pip install standardwebhooks
import os
from standardwebhooks import Webhook

def verify_ino(raw_body: bytes, headers: dict) -> dict:
    # raises if the signature or the timestamp (5 minutes) is not valid; deduplicate on headers["webhook-id"]
    return Webhook(os.environ["INO_WEBHOOK_SECRET"]).verify(raw_body, headers)
""",
        [("verify_webhook", "csharp")] = """
// Verify an Ino webhook (Standard Webhooks). dotnet add package StandardWebhooks — or by hand:
using System.Security.Cryptography;
using System.Text;

static bool VerifyIno(string rawBody, string id, string timestamp, string signatureHeader)
{
    if (Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - long.Parse(timestamp)) > 300) return false;   // older than 5 minutes
    var key = Convert.FromBase64String(Environment.GetEnvironmentVariable("INO_WEBHOOK_SECRET")!.Replace("whsec_", ""));
    var expected = Convert.ToBase64String(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes($"{id}.{timestamp}.{rawBody}")));
    return signatureHeader.Split(' ').Any(s => s.StartsWith("v1,") &&
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(s[3..]), Encoding.UTF8.GetBytes(expected)));
}
""",
    };

    public static string? Get(string kind, string language) => All.GetValueOrDefault((kind, language));
}
