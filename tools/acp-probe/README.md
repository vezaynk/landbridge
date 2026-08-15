# ACP capability probe

Drives the Agent Client Protocol `initialize` handshake against a real agent binary and
prints what it declares. One question it answers is worth the whole tool: **`loadSession`**,
which decides whether §11 resume (`preserve`, `preserve_and_park`) survives on that harness
or degrades to a permanent cold start. The spec defaults it to `false`, and nothing in a
profile can compensate for an agent that lacks it.

Run it before writing a `protocol: acp` profile for a harness this repo has not measured,
and again when you bump an adapter — an adapter is a second upstream between `docketd` and
the model, and capabilities are exactly the kind of thing that moves in a minor release.

```sh
node tools/acp-probe/probe.mjs
```

It probes the four entry points named in
[`runner-config.md`](../../ideas/skills/references/runner-config.md); edit the `targets`
array to point at whatever you have installed. No credentials are needed — `initialize` is a
capability handshake and happens before any provider is contacted, which is precisely why
this is cheap enough to run on every machine during enroll.

## What to look at

- **`loadSession`** — false means every redispatch of a parked task is a cold start.
- **`mcpCapabilities.http`** — false means the plane's MCP server cannot be handed over on
  `session/new`, so the worker has no docket tools and can neither read its task nor report
  a result.
- **`protocolVersion`** — every agent measured on 2026-08-15 answered `1`. `AcpClient`
  speaks 1 and 2 and warns only outside that range.
- **`authMethods`** — how that agent expects the machine to be authenticated. An
  unauthenticated agent still completes `initialize` happily and fails later at
  `session/prompt`, so a clean probe is not evidence that a dispatch will work.

## Measured 2026-08-15

| Agent | Entry point | ver | `loadSession` | `mcp.http` |
|---|---|:--:|:--:|:--:|
| Claude Agent 0.68.0 | `claude-agent-acp` | 1 | ✅ | ✅ |
| Claude Code 0.16.2 (deprecated) | `claude-code-acp` | 1 | ✅ | ✅ |
| Codex 1.3.0 | `codex-acp` | 1 | ✅ | ✅ |
| OpenCode 1.18.18 | `opencode acp` | 1 | ✅ | ✅ |

Grok Build (`grok agent stdio`) is unmeasured: its installer resolves releases through the
GitHub API, which the environment this was written in blocks.
