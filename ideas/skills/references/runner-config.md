# `docketd` runner config — schema and a worked Claude Code example

`docketd` contains no harness knowledge; everything specific is data (spec §10).
This is the reference the enroll skill (`docket-enroll`) and spec §10 point at:
the config schema plus a working Claude Code profile, including the exact spawn
argv a worker is launched with.

## Schema

| Section | Field | Notes |
|---|---|---|
| `machine` | `work_root` | Per-task scratch dirs; `docketd` spawns each task in `{work_root}/{task_id}` (§10). Not the workspace. |
| `machine` | `heartbeat_interval` | Machine-liveness cadence (§10). |
| `machine` | `backpressure` | `max_cpu_load` / `max_memory_load` / `max_disk_usage` in [0,1]; sensible defaults, tune per box (§10). |
| `profiles.<name>` | `spawn` | argv passed to `execve` — **never a shell** (§10). Substitutions below. One profile MUST be `default`. |
| `profiles.<name>` | `stop` | `mode` (`message` \| `signal`), `signal`, `message_template`, `wind_down`. Message delivery lets the agent honour the disposition (§10, §11). |
| `profiles.<name>` | `resume` | argv to resume a parked task's transcript, directory-scoped (§11). |
| `profiles.<name>` | `events` | `source` (`hooks` \| `otel` \| `terminal` \| `none`) + name `mapping` → `started`/`tool-call`/`subagent-spawned`/`exited`. `none` is honest (§10). |
| `profiles.<name>` | `telemetry` | `otel` bool + `endpoint` for budget attribution (§10). |
| `profiles.<name>` | `logs` | transcript `path` + `format` for tail-and-stream (§10). |
| `profiles.<name>` | `max_concurrent` | Optional hard cap for a licence/rate/posture reason, unrelated to load (§10). |

## Spawn substitutions

`docketd` substitutes these `{...}` tokens in each `spawn` arg (and injects the
first three as environment on every spawn, not configurably — §10):

| Token / env | Value |
|---|---|
| `{task_id}` / `DOCKET_TASK_ID` | The dispatched task id. |
| `{machine_id}` / `DOCKET_MACHINE_ID` | This machine's id. |
| `{work_dir}` | `{work_root}/{task_id}`, the spawn cwd. |
| `{budget}` | The task's harness-local hard cap in USD, if any (§9 check 9). |
| `{mcp_config}` | Path to the generated MCP config `docketd` writes to `{work_dir}/mcp.json` (mode 0600). |
| `DOCKET_WORKER_TOKEN` | The minted worker-instance token (also embedded in `{mcp_config}`). |

## The generated MCP config (`{mcp_config}`)

At dispatch `docketd` writes the worker's MCP client config — the control plane
mints the worker token and builds it (`DispatchService`), the runner only writes
it 0600 and passes the path. It is Claude Code's `--mcp-config` HTTP shape:

```json
{
  "mcpServers": {
    "docket": {
      "type": "http",
      "url": "https://plane.example/mcp",
      "headers": { "Authorization": "Bearer dkt_w_<worker-instance-token>" }
    }
  }
}
```

The `url` is the plane's public MCP endpoint (`Docket:PublicMcpUrl` /
`DOCKET_PUBLIC_MCP_URL` on the control plane; default `http://127.0.0.1:5000`).
This is a worker's **only** channel to Docket (§5): it authenticates as the
dispatched instance, and its token dies with the instance (§9 check 14).

## Worked example — Claude Code, default profile

```jsonc
{
  "machine": {
    "work_root": "/var/lib/docketd/work",
    "heartbeat_interval": "00:00:15",
    "backpressure": { "max_cpu_load": 0.90, "max_memory_load": 0.90, "max_disk_usage": 0.95 }
  },
  "profiles": {
    "default": {
      // argv only — no shell. {mcp_config} is the injected mcp.json path.
      "spawn": [
        "claude",
        "-p",
        "You are a Docket worker running headless under docketd. You have been dispatched exactly one task. First call the docket MCP tool get_task to read your assignment (namespace, description, completion_criteria, workspace, attempt). Read the docket-worker skill. Do the work inside the assigned workspace. When done, call report_result with a reference to where the work lives (a branch/commit/URL) — not the work itself. If you are blocked or a decision is above your scope, call request_input instead of guessing. You do not verify or complete the task yourself.",
        "--mcp-config", "{mcp_config}",
        "--output-format", "stream-json",
        "--input-format", "stream-json",
        "--permission-mode", "bypassPermissions",
        "--allowedTools", "Bash,Edit,Write,Read,Glob,Grep,mcp__docket__get_task,mcp__docket__report_result,mcp__docket__request_input,mcp__docket__register_service"
      ],
      "stop": {
        // Injected turn so the agent reads the disposition and winds down (§10/§11).
        "mode": "message",
        "message_template": "{\"type\":\"stop\",\"disposition\":\"{disposition}\",\"ttl_seconds\":{ttl_seconds},\"reason\":\"{reason}\"}",
        "wind_down": "00:00:30"
      },
      "resume": { "args": ["claude", "-p", "--resume", "--mcp-config", "{mcp_config}"] },
      "events": {
        "source": "hooks",
        "mapping": { "PostToolUse": "tool-call", "SessionStart": "started", "SessionEnd": "exited", "SubagentStart": "subagent-spawned" }
      },
      "telemetry": { "otel": true, "endpoint": "http://127.0.0.1:4318" },
      "logs": { "path": "{work_dir}/transcript.jsonl", "format": "stream-json" },
      "max_concurrent": null
    }
  }
}
```

### Notes on the argv

- **`--permission-mode bypassPermissions`** is the headless prerequisite (§10):
  a worker must run to completion without prompting. Managed settings on a
  corporate machine can forbid bypass outright — confirm it is permitted before
  writing it, and prefer a permission-prompt tool (approvals become
  `request_input` escalations) where the posture allows one. This is the single
  most important line in `spawn`.
- **`--input-format stream-json`** is what lets a graceful `stop` reach the agent
  as an injected turn rather than a signal (§10) — required for the `message`
  stop mode above.
- **`{mcp_config}`** is the injected path; the worker reads the plane URL and its
  bearer token from that file. Nothing else carries the token to the harness.

## Validating for real — operator step, not an automated test

The automated walking-skeleton test
(`Docket.Mcp.Tests/WalkingSkeletonEndToEndTests`) proves the dispatch → spawn →
authenticate → `get_task` → `report_result` loop with a **scripted** MCP worker
(`Docket.WorkerHarness`), no LLM. Running the argv above against **real**
`claude -p` — confirming the bootstrap prompt, permission posture, hooks, and
stop delivery actually behave — is the operator-run validation and belongs to
the §17.0 feasibility spikes and the §11 conformance run, deliberately out of
scope for CI.
