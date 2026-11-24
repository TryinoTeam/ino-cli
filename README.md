# ino: the Ino Loyalty command line

`ino` connects you, your scripts and your AI assistant to an [Ino Loyalty](https://inoloyalty.com) engine: call the API, follow events, build and check your own connectors, and share them in the connector store.

## Install

- **macOS and Linux (Homebrew):** `brew install tryinoteam/tap/ino`
- **Any platform:** download the archive for your platform from [Releases](https://github.com/TryinoTeam/ino-cli/releases), check it against `SHA256SUMS`, and put `ino` (or `ino.exe`) on your PATH.

The macOS build is not notarized yet; if macOS blocks it, run `xattr -d com.apple.quarantine ./ino` once. Windows builds are not signed yet.

## Start

```
ino profile add dev --base-url https://api.inoloyalty.com/t/<tenant> --channel <CHANNEL> --environment test
ino doctor
ino api list
```

The key and secret come from Power Apps → Settings → Developers → API keys. `ino` keeps them in the system keychain, never in a file. Use a test channel first: on live profiles, writing calls ask for `--live --yes`.

## What it does

| Command | |
|---|---|
| `ino api list / describe / call` | The channel API, with the engine's own rules (signature v2, request ids, safe retries) |
| `ino events`, `ino listen`, `ino trigger` | Follow the event feed, forward events to a local endpoint, send test events |
| `ino webhooks …`, `ino jobs …` | Webhook subscriptions, deliveries and resends; bulk jobs |
| `ino connector new --self-hosted --lang node\|python\|csharp` | A connector project for your own server: signed client, webhook receiver, offline queue, heartbeat, Dockerfile |
| `ino connector check`, `ino connector status` | Which required scenarios the engine has seen from your key; your connectors' health |
| `ino hub list / show / get / submit` | The connector store: approved connectors (signed by Tryino), and sending yours for review |
| `ino mcp serve` | A local MCP server for Claude Code, Claude Desktop, Cursor and VS Code |
| `ino agent setup` | Installs the Ino skills for your AI assistant |
| `ino docs search` | The API documentation, offline |

Add `--json` to any command for machine-readable output; exit codes are stable (0 ok, 1 business, 2 usage, 3 auth, 4 not found, 5 conflict, 6 rate limited, 7 network).

## Security

- Keys live in the keychain; `ino` never prints them.
- Live is decided by the engine (`whoami`), not only by the profile name.
- The connector store catalog is accepted only with Tryino's signature (ECDSA P-256); `ino hub get` prints commands, it never runs downloaded code.
- Report a vulnerability privately: Security → Report a vulnerability on this repository.

## Licence

Apache-2.0. © Tryino.
