# `docketd` runner config — ACP-only

`docketd` contains no harness knowledge; everything specific is data (spec §10).
A worker is an **ACP agent**. `docketd` is the ACP client. `spawn` is the agent
argv; stdin/stdout are JSON-RPC (NDJSON). Supporting a new harness is a config
file, never a code change.

The plane channel is still MCP (`get_task`, `report_result`, …). The client
injects it on `session/new` as an HTTP MCP server named `docket` with a Bearer
token. Profiles do not write `mcp.json` or declare `events.mapping`.

## Schema

| Section | Field | Notes |
|---|---|---|
| `machine` | `work_root` | Per-task scratch dirs; each task runs in `{work_root}/{task_id}` (§10). |
| `machine` | `heartbeat_seconds` | Machine-liveness cadence; default `15`. |
| `machine` | `back_pressure` | `max_cpu_load` / `max_memory_load` / `max_disk_usage` in [0,1]; defaults `0.90` / `0.90` / `0.95`. CPU is not yet observed cross-platform, so `max_cpu_load` is inert. |
| `profiles[]` | `name` | Identifier. Exactly one entry MUST be named `default`. |
| `profiles[]` | `spawn` | argv of the ACP agent — **never a shell**. Substitutions below. |
| `profiles[]` | `stop` | `wind_down_seconds` (default `30`). A stop is `session/cancel`, then a tree-kill at `min(ttl, wind_down)`. `ttl=0` kills immediately. `mode` / `message` are ignored if present (unknown keys parse). |
| `profiles[]` | `env` | String map stamped on every spawn. Same `{…}` substitutions as `spawn`. Reserved `DOCKET_*` names are refused at load. |
| `profiles[]` | `files` | Files written into `{work_dir}` before spawn. Paths jailed to the work dir. Prefer this for harness-local config that is **not** the plane MCP server. |
| `profiles[]` | `hooks` | Argv hooks, never a shell. `before_spawn` is fail-closed (10s); `after_exit` is best-effort. |
| `profiles[]` | `telemetry` | `otel` (default false), `endpoint`, `env`. Visibility only — see [docs/TELEMETRY.md](../../../docs/TELEMETRY.md). |
| `profiles[]` | `logs` | `capture` (default false), `max_bytes`, `prune_after_days`. Capture tees **stderr** only; the ACP session is the transcript. |
| `profiles[]` | `max_concurrent` | Optional hard cap unrelated to load. |
| `profiles[]` | `processes` | `agent_initiated` (default false) and `max` (default 8) for `start_process`. |
| `services[]` | — | Operator-declared long-lived processes. Same shape as before. |

Removed as load-bearing: `stdin`, `events`, `resume.args`. A config still carrying them loads (unknown / unused keys) and they do nothing. Resume is `session/load` of the stamped session id. stdin is always the ACP pipe — EOF on `docketd` death is the dead-man.

## Spawn substitutions

| Token / env | Value |
|---|---|
| `{task_id}` / `DOCKET_TASK_ID` | Dispatched task id. |
| `{machine_id}` / `DOCKET_MACHINE_ID` | This machine. |
| `{work_dir}` | `{work_root}/{task_id}`. |
| `{mcp_url}` / `DOCKET_MCP_URL` | Plane public MCP URL. Also injected on `session/new`. |
| `{worker_token}` / `DOCKET_WORKER_TOKEN` | Instance token. Also the `session/new` Bearer. |
| `{session_id}` | Parked session id, when redispatching. Used by `session/load`, not argv. |
| `DOCKET_TRACEPARENT` | W3C trace id for this dispatch. |

`{mcp_config}` still substitutes if a leftover argv names it, but a new profile should not.

## What `docketd` does on dispatch

1. `execve` `spawn` with stdin/stdout as pipes.
2. ACP `initialize`.
3. `session/new` (or `session/load` when the dispatch carries a session ref), with `mcpServers: [{ type: "http", name: "docket", url, headers: [{ Authorization: Bearer <token> }] }]`.
4. `session/prompt` with a short worker brief: call `get_task`, do the work, `report_result`.
5. `session/update` `tool_call` events refresh the progress clock.
6. `session/request_permission` is answered by `docketd` (routine `allow-once` today; ask-kinds will complete via the plane). There is no `--always-approve` / `bypassPermissions` / `--permission-prompt-tool`.
7. Stop / park: `session/cancel`, then the wind-down kill.

One ACP process per dispatch in this cut. The protocol allows one host to multiplex sessions; that is not implemented yet.

## Permission

Do **not** put bypass / always-approve / yolo flags in `spawn`. The agent asks; the client answers. A profile that still passes those flags is asking the harness to skip a dialog Docket is now the one answering.

## Park vs wait

Wait on a live session (permission, a still-open prompt) stays live. The wait TTL is **off by default** (`Timeout.InfiniteTimeSpan`). Parking is a deliberate Lead/human command (`park_task`): `session/cancel`, instance revoked, later redispatch is `session/load`. Answering a still-live wait is `answer_input_request` / `answer_permission_request`, not park.

## Worked examples

### Grok Build (`grok agent stdio`)

```jsonc
{
  "machine": { "work_root": "/var/lib/docketd/work" },
  "profiles": [
    {
      "name": "default",
      "spawn": ["grok", "agent", "stdio"],
      "env": { "GROK_FOLDER_TRUST": "0" },
      "logs": { "capture": true },
      "processes": { "agent_initiated": true }
    }
  ]
}
```

Auth is `XAI_API_KEY`. Pin a model with the harness's own flag if you need one; Docket does not.

### OpenCode (`opencode acp`)

```jsonc
"spawn": ["opencode", "acp"]
```

Auth is the provider key in the environment (`ANTHROPIC_API_KEY`, …).

### Claude Code (Zed ACP adapter)

```jsonc
"spawn": ["npx", "-y", "@zed-industries/claude-agent-acp"]
```

Or a pinned local binary. Do not use `claude -p`. Do not set `--permission-mode bypassPermissions`.

### Codex (Zed ACP adapter)

```jsonc
"spawn": ["npx", "-y", "@zed-industries/codex-acp"]
```

Auth is `CODEX_API_KEY`. Do not use `codex exec`. Do not set `--dangerously-bypass-approvals-and-sandbox`.

## Operator-declared services

Unchanged. A `services[]` entry is a process `docketd` supervises as its own child, outside any task tree. `start_process` is the agent-started path. See the previous edition of this file's services section, or spec §10: names and ports unique, readiness is a real TCP check, `enabled: false` is how you stop one.

## Validating

`POST /dashboard/conformance` still mints dummy tasks at `default`. A pass means a worker called `report_result` after an ACP handshake. Then cancel or `park_task` a live task and confirm the process is gone.

`docketd` reads the file once, at start. Restart after every edit.
