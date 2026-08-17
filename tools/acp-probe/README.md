# ACP capability probe

Drives the Agent Client Protocol `initialize` handshake against a real agent binary and
prints what it declares. One question it answers is worth the whole tool: **`loadSession`**,
which decides whether §11 resume (`preserve`, `preserve_and_park`) survives on that harness
or degrades to a permanent cold start. The spec defaults it to `false`, and nothing in a
profile can compensate for an agent that lacks it.

Run it before writing a profile for a harness this repo has not measured,
and again when you bump an adapter — an adapter is a second upstream between `landbridged` and
the model, and capabilities are exactly the kind of thing that moves in a minor release.

```sh
node tools/acp-probe/probe.mjs
```

It probes the entry points named in
[`runner-config.md`](../../ideas/skills/references/runner-config.md); edit the `targets`
array to point at whatever you have installed. No credentials are needed — `initialize` is a
capability handshake and happens before any provider is contacted, which is precisely why
this is cheap enough to run on every machine during enroll.

## What to look at

- **`loadSession`** — false means every redispatch of a parked task is a cold start.
- **`mcpCapabilities.http`** — false means the plane's MCP server cannot be handed over on
  `session/new`, so the worker has no landbridge tools and can neither read its task nor report
  a result.
- **`protocolVersion`** — every agent measured on 2026-08-15 answered `1`. `AcpClient`
  speaks 1 and warns outside that.
- **`authMethods`** — which ids the agent will accept at `authenticate`, in its own order.
  A non-empty list means authentication is *available*; whether it is *required* only shows
  up when `session/new` answers `-32000`. `AcpClient` runs the step on that refusal and
  requires the profile's `auth_method` — it does not guess the first id.

## Measured 2026-08-15 / 2026-08-16

| Agent | Entry point | ver | `loadSession` | `mcp.http` | `authMethods` |
|---|---|:--:|:--:|:--:|---|
| Claude Agent 0.68.0 | `claude-agent-acp` | 1 | ✅ | ✅ | `[]` |
| Claude Code 0.16.2 (deprecated) | `claude-code-acp` | 1 | ✅ | ✅ | `[]` |
| Codex 1.3.0 | `codex-acp` | 1 | ✅ | ✅ | `api-key`, `chat-gpt` — **required** |
| OpenCode 1.18.18 | `opencode acp` | 1 | ✅ | ✅ | `[]` |
| Goose 1.37.0 | `goose acp` | 1 | ✅ | ✅ | `goose-provider` — available; `session/new` succeeded without it |

Goose's row is from a captured 1.37.0 handshake, not a run of this probe in this
repo. `session/new` succeeded without `authenticate`. Do not put `goose-provider`
on a profile — that method is interactive `goose configure`.

**Codex is the one that needs the step, and a clean probe does not show it.** `initialize`
succeeds, and then `session/new` answers `-32000 "Authentication required"` — the whole
codex e2e tier produced two transcript lines and failed on exactly this. Reproduce without
spending anything, since none of these calls reach a model:

```
echo '{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"protocolVersion":1,"clientCapabilities":{"fs":{"readTextFile":false,"writeTextFile":false},"terminal":false},"clientInfo":{"name":"p","version":"0"}}}
{"jsonrpc":"2.0","id":1,"method":"authenticate","params":{"methodId":"api-key"}}
{"jsonrpc":"2.0","id":2,"method":"session/new","params":{"cwd":"/tmp","mcpServers":[]}}' | codex-acp
```

With `authenticate` in the middle the session opens; without it, id 2 is refused. A dummy
key is enough to see the difference, and codex-acp reads either `CODEX_API_KEY` or
`OPENAI_API_KEY`.

Grok Build (`grok agent stdio`) is **still unmeasured** — its installer resolves releases
through the GitHub API, which the environment this was written in blocks — and it is the
one harness misbehaving in CI: every turn returns `stopReason: "cancelled"` after ~11.3k
tokens without calling a single landbridge tool, on a cancel the plane never sent. Measuring it
is the first move, not another paid run.
