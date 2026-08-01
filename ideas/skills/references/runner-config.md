# `docketd` runner config — schema and a worked Claude Code example

`docketd` contains no harness knowledge; everything specific is data (spec §10).
This is the reference the enroll skill (`docket-enroll`) and spec §10 point at:
the config schema plus a working Claude Code profile, including the exact spawn
argv a worker is launched with.

## Schema

| Section | Field | Notes |
|---|---|---|
| `machine` | `work_root` | Per-task scratch dirs; `docketd` spawns each task in `{work_root}/{task_id}` (§10). Not the workspace. |
| `machine` | `heartbeat_seconds` | Machine-liveness cadence, in seconds (§10); default `15`. |
| `machine` | `back_pressure` | `max_cpu_load` / `max_memory_load` / `max_disk_usage` in [0,1]; defaults `0.90` / `0.90` / `0.95`, tune per box (§10). CPU is not yet observed cross-platform, so `max_cpu_load` is currently inert — memory and disk carry the signal (§10). |
| `profiles[]` | `name` | Profile identifier. `profiles` is a JSON **array**; exactly one entry MUST be named `default` (§10). |
| `profiles[]` | `spawn` | argv passed to `execve` — **never a shell** (§10). Substitutions below. |
| `profiles[]` | `stop` | `mode` (`message` \| `signal`), `signal`, `message`, `wind_down_seconds` (default `30`). Message delivery lets the agent honour the disposition; docketd injects the turn, then waits `min(ttl, wind_down_seconds)` for a voluntary exit before a hard tree-kill backstops it. A `signal` profile injects nothing, but the worker still gets the full `ttl` the plane granted to exit on its own before the kill (`wind_down_seconds` does not apply). Only `ttl=0` is killed immediately (§10, §11). |
| `profiles[]` | `resume` | `args`: argv to resume a parked task's transcript, directory-scoped (§11). |
| `profiles[]` | `events` | `source` (`hooks` \| `otel` \| `terminal` \| `none`) + name `mapping` → `started`/`tool-call`/`subagent-spawned`/`exited`. `none` is honest (§10). |
| `profiles[]` | `telemetry` | `otel` bool + `endpoint` for budget attribution (§10). |
| `profiles[]` | `logs` | §12 machine-local transcript capture: `capture` (bool, default **false**), `max_bytes` (per-stream cap, default 50 MiB), `prune_after_days` (local hygiene, default 7, `0` disables). Legacy `format`/`path` are advisory/reserved — see [Transcript capture](#transcript-capture-12) below. |
| `profiles[]` | `max_concurrent` | Optional hard cap for a licence/rate/posture reason, unrelated to load (§10). |

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
| `{session_id}` | The opaque harness session ref to resume. Substituted in `resume.args` only, never `spawn` (§11). |
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
    "heartbeat_seconds": 15,
    "back_pressure": { "max_cpu_load": 0.90, "max_memory_load": 0.90, "max_disk_usage": 0.95 }
  },
  "profiles": [
    {
      "name": "default",
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
        // Injected as a claude stream-json user turn so the agent reads the
        // disposition and winds down (§10/§11): it reports current progress via
        // report_result, then stops. docketd substitutes {disposition}/{ttl_seconds}/
        // {reason} and writes it as one line to the harness's held-open stdin, then
        // waits min(ttl, wind_down_seconds) for the agent to exit before hard-killing.
        "mode": "message",
        "message": "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"Docket is winding this task down (disposition={disposition}, ~{ttl_seconds}s left; reason: {reason}). Immediately call report_result with a reference to your current progress, then stop — do not begin new work.\"}}",
        "wind_down_seconds": 30
      },
      "resume": { "args": ["claude", "-p", "Resume your task.", "--resume", "{session_id}", "--mcp-config", "{mcp_config}"] },
      "events": {
        "source": "hooks",
        "mapping": { "PostToolUse": "tool-call", "SessionStart": "started", "SessionEnd": "exited", "SubagentStart": "subagent-spawned" }
      },
      "telemetry": { "otel": true, "endpoint": "http://127.0.0.1:4318" },
      // §12 capture: tee this worker's stdout transcript + stderr to the state dir.
      "logs": { "capture": true, "format": "stream-json", "max_bytes": 52428800, "prune_after_days": 7 },
      "max_concurrent": null
    }
  ]
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
  stop mode above. docketd writes one turn to stdin and waits
  `min(ttl, wind_down_seconds)` for the agent to persist and exit; if it does not,
  the process tree is hard-killed at that deadline. A `signal`-mode profile injects
  no turn, but the worker still gets the full `ttl` the plane granted to exit on its
  own before the kill (`wind_down_seconds` does not apply — it is the message-path
  budget); only `ttl=0` skips the wait and is killed outright. The `message`
  template may reference `{disposition}`, `{ttl_seconds}`, and `{reason}`, which
  docketd substitutes per stop.
- **`{mcp_config}`** is the injected path; the worker reads the plane URL and its
  bearer token from that file. Nothing else carries the token to the harness.

## Transcript capture (§12)

When a profile sets `logs.capture: true`, `docketd` records that worker's transcript
locally. A `claude -p --output-format stream-json` worker's stdout **is** the full
transcript of its work — the single most valuable artifact when a task goes wrong —
so `docketd` **tees** it: the same stdout read that maps events (`events.source:
terminal`) also writes each line verbatim to a file, and stderr is captured alongside.
Capture never disturbs event mapping or the stdin dead-man/stop path — it is a tee,
not a divert — and it works for any `events.source` (with `none`, stdout is drained
solely to capture it).

**Where.** Under the **state dir** (the `credentials.json` dir; `--state-dir`,
`DOCKET_STATE_DIR`, `$XDG_STATE_HOME/docket`, or `~/.docket`), **not** the per-task
`work_root` scratch — the transcript must outlive a task teardown and a `docketd`
restart, because per §11 the local transcript is the resume-after-reboot substrate.

```
<state>/transcripts/<task-id>/0001.ndjson   # stdout (stream-json, one object per line)
<state>/transcripts/<task-id>/0001.stderr   # stderr (plain lines)
```

**Per instance.** Each dispatch — a first spawn, a requeue, a §11 resume — is a
distinct worker instance and gets the next ordinal (`0001`, `0002`, …), derived by
scanning the dir, so a redispatch never clobbers its predecessor's transcript and the
ordinal is stable across a restart. Files open lazily on the first line (a silent
stream leaves no file). The root, task dirs, and files are owner-only (0700/0600) — a
transcript can capture credentials an agent echoed (§13).

**Size cap.** `max_bytes` (default 50 MiB) bounds each stream (stdout, stderr) per
instance. On reaching it, `docketd` writes one truncation marker line
(`{"docket":"transcript_truncated","limit_bytes":N}`) and stops writing that stream.
It keeps draining the pipe (so the worker never blocks) and never kills the worker —
logging is not allowed to affect the task.

**Local pruning.** `prune_after_days` (default 7; `0` disables) is machine-local disk
hygiene: on each capturing spawn, `docketd` removes any task's transcript dir whose
newest file is older than the window. When profiles disagree, the most generous wins
(any `0` keeps everything; otherwise the longest window). This is **not** the §12
retention tiers — those, plus redaction on the streaming path and serving to the
plane, are a later plane-side increment. This increment is capture only; nothing
leaves the machine, and the transcript is written **verbatim** (redaction is applied
plane-side, before anything lands off-box).

**`format` / `path`.** `format` is an advisory label for the stdout stream's shape
(e.g. `stream-json`); it is not acted on. `path` was documented for a never-built
"tail-and-stream" and is now ignored — capture writes to the fixed state-dir layout
above, and how a transcript is exposed is the plane increment's decision. Both keys
still parse so existing configs are accepted unchanged.

## Validating for real — operator step, not an automated test

The automated walking-skeleton test
(`Docket.Mcp.Tests/WalkingSkeletonEndToEndTests`) proves the dispatch → spawn →
authenticate → `get_task` → `report_result` loop with a **scripted** MCP worker
(`Docket.WorkerHarness`), no LLM. Running the argv above against **real**
`claude -p` — confirming the bootstrap prompt, permission posture, hooks, and
stop delivery actually behave — is the operator-run validation and belongs to
the §17.0 feasibility spikes and the §11 conformance run, deliberately out of
scope for CI.
