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
| `profiles[]` | `events` | `source` (`hooks` \| `otel` \| `terminal` \| `none`) + `mapping`, which overrides the **stdout stream's property names** — not harness event names. **Only `terminal` is implemented**; `hooks` and `otel` parse but are wired to nothing, so all three non-`terminal` values behave as `none`. See [Event relay](#event-relay-10) below before choosing — a non-`terminal` profile requeues any task that runs longer than about a minute. |
| `profiles[]` | `telemetry` | `otel` bool (opt-in, default **false**), `endpoint` (OTLP destination; falls back to the one docketd inherited), and `env` (a string map of harness-specific variables, applied verbatim). When on, docketd sets the vendor-neutral `OTEL_*` exporter variables and appends `docket.task_id`/`docket.machine_id` to `OTEL_RESOURCE_ATTRIBUTES`, so the harness's own token/cost telemetry is attributable per task (§10). `otel: true` with **no endpoint configured and none inherited sets nothing at all** and warns once — telemetry is never enabled without a destination. Claude Code additionally needs `"env": { "CLAUDE_CODE_ENABLE_TELEMETRY": "1" }` (its own flag is data, since docketd holds no harness knowledge). **Visibility only**: Docket ingests none of it and enforces no ceiling — see [docs/TELEMETRY.md](../../../docs/TELEMETRY.md). |
| `profiles[]` | `logs` | §12 machine-local transcript capture: `capture` (bool, default **false**), `max_bytes` (per-stream cap, default 50 MiB), `prune_after_days` (local hygiene, default 7, `0` disables). Legacy `format`/`path` are advisory/reserved — see [Transcript capture](#transcript-capture-12) below. |
| `profiles[]` | `max_concurrent` | Optional hard cap for a licence/rate/posture reason, unrelated to load (§10). |
| `profiles[]` | `processes` | §10 agent-started **processes**: `agent_initiated` (bool, default **false**) and `max` (default 8). Named `processes`, not `services` — they are different things (§10). Whether a task on this profile may call `start_process`, and how many the machine may hold. |
| `services[]` | — | Optional: long-lived processes `docketd` supervises as its own children. See [Operator-declared services](#operator-declared-services-10) below. |

## Operator-declared services (§10)

A worker that starts `npm run dev` loses it the moment its task ends — the service is
inside the task's process tree, which is tree-killed, and it carries `DOCKET_*`, which
the stray reaper matches. For a service that must outlive the task using it, declare it
here and `docketd` supervises it as **its own child**, outside every task's tree:

```jsonc
"services": [
  {
    "name": "web-dev",                    // [a-zA-Z0-9_-]{1,64} — becomes a directory name
    "spawn": ["/abs/node/bin/npm", "run", "dev"],   // argv, never a shell
    "working_directory": "/abs/path/to/checkout",
    "env": { "PORT": "5173" },            // explicit: nothing is inherited implicitly
    "port": 5173,                         // the loopback port this service owns
    "readiness": { "tcp_port": 5173, "timeout_seconds": 60 },
    "restart": { "max_backoff_seconds": 60 },
    "logs": { "capture": true },          // → <state>/services/web-dev/NNNN.ndjson
    "backend": "direct",                  // the only supported value
    "enabled": true                       // false = declared but deliberately not started
  }
]
```

**Why this is not an escape hatch.** The process is not a descendant of any harness, so
the task tree-kill does not reach it, and it is tagged with `DOCKET_MACHINE_ID` but
deliberately **not** `DOCKET_TASK_ID` — so the restart sweep (keyed on machine id) reaps
the previous generation when `docketd` restarts, while per-task exit cleanup (which
requires a matching task id) steps over it. It escapes the task's lifetime while staying
inside Docket's kill guarantee, on every OS, with no `setsid` and no environment
scrubbing. The worker skill forbids the other route to the same effect for exactly that
reason.

**Names and ports must both be unique on a machine.** Names because they are identifiers
(and directory names); ports because a forward dial is resolved to a service *by port*, so a
shared port would make that lookup answer for whichever service came first, and the resulting
refusal would make no sense from the consumer's side. `docketd` rejects either at config load
and names both offenders — it prints the problem and exits non-zero before connecting, so this
is caught at start rather than at the first dial.

**`readiness` is a real check.** The port must accept a connection before the service is
reported `running`. That is what a holder task waits for before calling
`register_service` (§8.2), and what lets `docketd` refuse a forward dial for a service
that is down instead of connecting to whatever else may hold the port.

**Restart, not re-adopt.** On `docketd` restart every service is killed and started
again from config; there is no PID registry and no attempt to inherit survivors. Absolute
paths in `spawn` and an explicit `env` matter for the same reason they do under a system
service manager: the service gets `docketd`'s environment, not your shell's.

**`backend`** is `direct` today and a config naming anything else is refused rather than
quietly supervised the other way. Delegation to `systemd-run`/`pm2`/`docker` is a later
option, and it costs the property refuse-at-dial relies on: `docketd` would no longer own
the process, so "is my service up" becomes a query rather than a fact.

**To stop a service, set `enabled: false`** — not a dashboard button, and deliberately so.
A service's desired state lives in this file, so a command that stopped it would leave the
config and reality disagreeing until the next restart silently undid it; keeping the switch
here means the declaration is always the truth. A disabled service is still declared: it
reports as `disabled` (distinct from `stopped`, so you can tell "I turned this off" from
"this died and nobody meant it"), and a forward dial for its port is still refused rather
than connecting to whatever else has taken it.

**Agents can start their own background processes**, and on most machines that is the primary
path — the `services[]` block above is for operator-owned fixtures. The two are different
things and §10 defines both: a **service** is operator-declared and restart-supervised (a
daemon); a **process** is agent-started via `start_process`, **never restarted**, and lives
until something stops it or this `docketd` restarts (a job). Same supervision, same machine
tagging, same stray-sweep bound.

Gate processes per profile with `processes.agent_initiated`; cap them with `processes.max`. Names
are unique across processes *and* services on a machine, checked at admission among live entries —
an exited process releases its name. **Ports are not part of a process at all**, and that is the
one place the two diverge sharply: a service declares a port and gets refuse-at-dial protection, a
process declares nothing and is invisible to it. If an agent's process listens on something that
is the agent's business, and reachability is a separate `register_service` call. Processes also
carry a start-time stdin choice (`open_stdin`, **default off**): without a pipe there is no
`write_process` and no graceful stop, which suits the fire-and-forget majority.

Worth knowing as the operator: **nothing reclaims a process when its task ends.** Cleanup is
the Lead's job via a continuation task, and the Machine Group view is where you see what a
machine is still holding.

**Status, not logs, on the dashboard.** Each service's state, port, uptime, restart count
and last exit code ride the machine heartbeat to the §12 Machine Group view. The log
*contents* stay on the machine — serving them would be live tailing, which §16 open
question 8 defers. Read them on the box, under the state dir.

Services are not tasks: they never count toward `max_concurrent`, and the load they
consume is already visible to back-pressure. And they need a profile permissive enough to
be useful alongside — see the archetypes below.

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
      // `terminal` reads this worker's stdout stream — the only implemented event
      // source, and the only one that yields tool-call events and the per-task
      // liveness they carry. No `mapping` is needed: the built-in defaults already
      // describe claude's stream-json shape. See Event relay below.
      "events": { "source": "terminal" },
      // Harness telemetry to YOUR collector, attributed per task (§10). `otel` is the
      // opt-in; `endpoint` may be omitted when docketd already has one in its own
      // environment. `env` carries what this harness needs: Claude Code exports
      // nothing without its own flag, and the default 60s export interval can outlive
      // a short task. docketd adds docket.task_id/docket.machine_id to
      // OTEL_RESOURCE_ATTRIBUTES so a collector can bucket token/cost per task.
      // Visibility only — Docket ingests none of it (docs/TELEMETRY.md).
      "telemetry": {
        "otel": true,
        "endpoint": "http://127.0.0.1:4318",
        "env": {
          "CLAUDE_CODE_ENABLE_TELEMETRY": "1",
          "OTEL_METRIC_EXPORT_INTERVAL": "10000"
        }
      },
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

## Profile archetypes — open vs. strict

Two flags decide how much of the machine a worker can use, and the choice is
made **per profile, by the machine's operator** — never by the Lead. A Lead
targets a profile *name*; what that name can do (which MCP servers, which
commands) is the machine's declaration, invisible to the plane (§1's
infrastructure/work split, §10's "everything specific is data").

**Open** — the worker uses the machine like its owner would, including starting its own
background processes (§10 `start_process`). Omit
`--allowedTools` entirely (with `--permission-mode bypassPermissions` every
tool is available) and omit `--strict-mcp-config`: the injected `{mcp_config}`
then **merges** with the machine's own user- and project-scope MCP servers, so
the worker sees `docket` *plus* every locally installed MCP — GitHub, database,
browser servers — using credentials that live on that machine and never touch
Docket:

```jsonc
"spawn": [
  "claude", "-p", "<worker prompt>",
  "--mcp-config", "{mcp_config}",
  "--output-format", "stream-json", "--input-format", "stream-json",
  "--permission-mode", "bypassPermissions"
],
// §10: let a worker start its own background processes. On an open profile this grants
// nothing it could not already do with a shell — and it means the agent uses the supported
// route instead of discovering `setsid`/env-scrubbing, which would defeat the kill guarantee
// for everything else on the machine.
"processes": { "agent_initiated": true }
```

On a **strict** profile leave `processes.agent_initiated` off (the default): a worker with no
shell cannot start a background process by hand, and `start_process` refuses honestly rather
than granting a capability the rest of the profile withholds.

**Strict** — the worker gets an enumerated toolbox and nothing else. Keep
`--allowedTools` narrow and add `--strict-mcp-config`, which makes the injected
config the *only* MCP config loaded — local servers are excluded:

```jsonc
"spawn": [
  "claude", "-p", "<worker prompt>",
  "--mcp-config", "{mcp_config}", "--strict-mcp-config",
  "--output-format", "stream-json", "--input-format", "stream-json",
  "--permission-mode", "bypassPermissions",
  "--allowedTools", "Read,Glob,Grep,mcp__docket__get_task,mcp__docket__report_result,mcp__docket__request_input"
]
```

The trade is blast radius: on an open profile, a prompt-injected worker can do
anything the machine account can, including using every local MCP server's
credentials. Docket's containment still holds at its own boundaries — the
worker's plane token stays task-scoped (§5), spend is bounded by the harness
caps and the Team budget (§9) — but the machine-local exposure is the
operator's chosen risk. The two archetypes compose: one machine can declare a
locked-down `ci-runner` profile and an open `dev-box` profile side by side, and
the Lead picks per task by profile name. Hard limits are per-instance and a
Lead's answer cannot loosen them mid-flight — granting a capability means
editing the profile and dispatching a fresh task (§10); `request_input` is for
judgment questions, not tool grants.

**A profile that must host long-lived services needs to be on the open side of
this choice.** The supported way for a worker to run a service that outlives its
own task is to hand it to the machine's service manager (`systemd-run --user`, or
a unit the operator declares) — see the worker skill's holder-task section. That
takes shell access, so a strict profile with a narrow `--allowedTools` and no
`Bash` cannot do it, and a worker on such a profile is expected to report that
rather than work around it. If this machine is meant to host dev servers or
databases for a Team, declare a profile that permits it and let the Lead route
service work there by name.

Worth knowing as the operator: a service started that way is **deliberately
outside** `docketd`'s supervision — the service manager forks it, so it is
neither a descendant of the harness (the tree-kill misses it) nor a carrier of
`DOCKET_*` (the stray reaper's environment scan misses it). That is by
construction, and it means stopping such a service is the service manager's job,
not `docketd`'s: a `docketd` restart or a task kill will not take it down. The
worker skill forbids the other way of achieving the same escape — scrubbing
`DOCKET_*` off a spawned process — precisely because that one defeats the kill
guarantee for everything else too.

## Event relay (§10)

> ⚠️ **Only `events.source: terminal` is implemented. A profile that declares
> anything else will have every task longer than about a minute requeued, forever.**
>
> Per-task liveness on the control plane is refreshed *only* by an inbound event —
> `started`, `session-started`, `alive`, `tool-call`, or `subagent-spawned`. Of those,
> a non-`terminal` profile emits exactly one: `started`, once, at spawn. Nothing
> refreshes it again. The plane requeues any `working` task whose last activity is
> older than the per-task liveness window (**60s, hardcoded**), so the task is
> declared lost at the first check after the minute mark, requeued, redispatched,
> and lost again. Nothing caps that loop.

`source` selects how `docketd` observes a worker's progress. The four values it
accepts are not four implementations:

| `source` | Status | What you get |
|---|---|---|
| `terminal` | **Implemented** — the only consumer is the stdout drain | `started`, `session-started` (the resume ref), `tool-call` per tool use, `exited`. Liveness refreshes on every well-formed line. |
| `hooks` | Parses, wired to nothing | `started` + `exited` only — identical to `none` |
| `otel` | Parses, wired to nothing | `started` + `exited` only — identical to `none` |
| `none` | Honest declaration of no stream | `started` + `exited` only |

So the choice is really binary: either the harness streams structured output on
stdout that `docketd` can read, or this profile has no progress signal and no
usable per-task liveness. `docketd` prints a warning at startup for every profile
that declares a non-`terminal` source, naming the requeue consequence — if you see
it, the profile is not merely degraded, it is unusable for work that takes longer
than the liveness window.

**Two spec promises that are not yet kept**, so do not plan around them: §10 says
liveness for a source-less profile "degrades to process-alive," and that per-task
liveness includes "process-alive for that PID." `docketd` *has* that check
(`ProcessSupervisor.IsTaskLive`), but nothing calls it and the plane never learns
process state — the machine heartbeat carries a running-task list but does not
refresh per-task activity, and a worker's own MCP calls do not either. Until a
process-alive or periodic `alive` signal is wired, `none` is honest about what it
reports and misleading about what it costs.

**`mapping` overrides stream *property names*, not harness event names.** It does
not map `PostToolUse` → `tool-call`; there is no hook-name seam. The recognized
keys, with the built-in defaults in parentheses, are `type_key` (`type`),
`system_type` (`system`), `assistant_type` (`assistant`), `subtype_key`
(`subtype`), `init_subtype` (`init`), `session_id_key` (`session_id`),
`message_key` (`message`), `content_key` (`content`), `block_type_key` (`type`),
`tool_use_block_type` (`tool_use`), and `tool_name_key` (`name`).

Those defaults already describe `claude -p --output-format stream-json`, which is
why the worked example above declares `"events": { "source": "terminal" }` with no
`mapping` at all. Supply keys only for a harness whose stream uses different names
— that is a config change, not a code change, which is the point of the seam.

**Unrecognized keys are silently ignored.** Each key falls back to its default
independently, with no error at load, so a typo or a leftover hook-name mapping
produces a profile that parses cleanly and reports nothing. If you write a
`mapping`, verify against a real run rather than against the config.

**`subagent-spawned` has no producer.** It is in the wire vocabulary and the
plane persists and renders it, but `docketd` never emits one, so no `mapping`
value will produce it. The dashboard's per-task subagent line always reads "no
subagents reported."

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

**What "verbatim" means — the line boundary.** Capture is **line-oriented**: `docketd`
reads a line, then writes that line plus a single `\n`. So "verbatim" is exact with
respect to the **captured file** — the serving path returns the file's bytes unchanged,
which is what spec §13's "served exactly as captured" states — but the capture step
itself normalizes line endings, and three consequences follow:

- **CRLF becomes LF.** A harness on Windows emitting `\r\n` lands as `\n`.
- **A lone `\r` is a line break.** Progress bars and spinners that redraw by returning
  the carriage — common on **stderr** — are read as separate lines and written as
  separate `\n`-terminated ones, so the file holds each redraw as its own line instead of
  one rewritten line.
- **A final unterminated line gains a `\n`.** Bytes are also decoded as text and
  re-encoded UTF-8, so output that was not valid UTF-8 is normalized (`U+FFFD`).

This is deliberate, not an oversight. The artifact that matters — a
`--output-format stream-json` stdout transcript — **is** one JSON object per line, so
line orientation costs it nothing, and the size cap, the truncation marker, and event
mapping are all line-oriented too. The only thing reshaped is stderr progress
*rendering*, which is noise rather than evidence. If a future harness needs stderr
byte-for-byte (raw ANSI, embedded binary), that is a byte-oriented capture mode, not a
tweak to this one.

**Size cap.** `max_bytes` (default 50 MiB) bounds each stream (stdout, stderr) per
instance. On reaching it, `docketd` writes one truncation marker line
(`{"docket":"transcript_truncated","limit_bytes":N}`) and stops writing that stream.
It keeps draining the pipe (so the worker never blocks) and never kills the worker —
logging is not allowed to affect the task.

**Local pruning.** `prune_after_days` (default 7; `0` disables) is machine-local disk
hygiene: on each capturing spawn, `docketd` removes any task's transcript dir whose
newest file is older than the window. When profiles disagree, the most generous wins
(any `0` keeps everything; otherwise the longest window). This window **is** the
retention story: the control plane stores no transcript bytes and has no retention tier
of its own (§12), so once the sweep removes a dir the transcript is gone everywhere.

> ⚠️ **Turning capture on means raw agent output becomes readable from the dashboard.**
> A transcript is served **verbatim** — Docket does **not** redact it (spec §13, open
> question 8) — so it may contain credentials the agent echoed, customer data, internal
> hostnames, or anything else it read or printed. What limits exposure is scope, not
> filtering: an operator reads it only through a **human** dashboard session (a Lead
> token is refused), and only for a task in a **terminal** state, whose worker
> credential is already revoked. Treat a downloaded transcript as sensitive: do not
> paste it into a ticket, a chat, or another agent.

**Serving (§12).** With capture on, a human operator can read a terminal task's
transcript from the dashboard: the control plane asks this machine for one byte range at
a time over the runner channel (`read-transcript`), and `docketd` replies with the file's
bytes. Nothing is cached or stored plane-side, one range is in flight at a time (so a
large transcript cannot crowd out heartbeats or a `kill`), and a machine that is offline
simply has no readable transcript until it reconnects.

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
