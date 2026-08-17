# `landbridge-acp-bridge`

Stdio ↔ WebSocket pipe for ACP. `landbridged` still owns a local process and
writes NDJSON to its stdin. This binary is that process, or the far-side
listener that wraps a real agent.

```
landbridge-acp-bridge listen [--bind 127.0.0.1:0] -- goose acp
landbridge-acp-bridge connect ws://127.0.0.1:PORT/acp
```

`listen` prints one `listening <url>` line on stdout, then only stderr. Each
WebSocket at `/acp` spawns a fresh agent and pumps one JSON-RPC object per
line ↔ one text frame. A second connection while the first is live is `409`.

`connect` is the profile `spawn`:

```json
"spawn": ["landbridge-acp-bridge", "connect", "ws://127.0.0.1:PORT/acp"]
```

Same machine, the session `cwd` landbridged sends is a real path. The far-side
agent must be able to reach `PublicMcpUrl`. This is not a new machine type
and it does not kill remote children if the listen process is gone — cancel
and exit are the backstop.
