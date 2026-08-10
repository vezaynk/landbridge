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
| `profiles[]` | `stop` | `mode` (`message` \| `signal`), `message`, `wind_down_seconds` (default `30`). **`mode: message` is a declaration about your harness** — that a running session reads turns off stdin — and docketd takes it at its word: it writes the turn, then waits `min(ttl, wind_down_seconds)` for a voluntary exit before a hard tree-kill backstops it. It cannot check the claim, so declaring it for a harness that does not read stdin buys nothing and makes `preserve` a promise the machine will break. **`claude -p` is such a harness — use `signal` there** ([Stopping a `claude -p` worker](#stopping-a-claude--p-worker-10-11)). A `signal` profile writes nothing, and the worker gets the full `ttl` the plane granted to exit on its own before the kill (`wind_down_seconds` does not apply). Only `ttl=0` is killed immediately (§10, §11). The `signal` **key** parses but is not acted on: the deadline's kill is always the tree-kill. |
| `profiles[]` | `stdin` | `deadman` (default) \| `closed` — what the worker's stdin is. `deadman` holds the pipe's write end open for the worker's whole life; that pipe **is** the §10 dead-man's switch. `closed` sends EOF right after the spawn, for a harness that blocks reading piped stdin — **`codex exec` requires it**, and gives up nothing, because it never reaches the read that would observe EOF-as-death. Two things behave unlike the other enums here: a **typo is refused** rather than defaulted (defaulting would silently restore the pipe a profile was written to escape), and `closed` is **refused together with `stop.mode: message`**, whose wind-down turn would have nowhere to land. See [Closing the worker's stdin](#closing-the-workers-stdin-10). |
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
        // stream-json OUT only. Do not add `--input-format stream-json` here: with a
        // prompt in argv it makes claude ignore the prompt and block on stdin forever
        // (see Stopping a claude -p worker below). `--verbose` is what makes claude
        // actually emit the stream under -p, and the terminal event source needs it.
        "--output-format", "stream-json",
        "--verbose",
        "--permission-mode", "bypassPermissions",
        "--allowedTools", "Bash,Edit,Write,Read,Glob,Grep,mcp__docket__get_task,mcp__docket__report_result,mcp__docket__request_input,mcp__docket__register_service"
      ],
      "stop": {
        // `signal`, not `message`, and this is not a limitation of Docket: a `claude -p`
        // worker cannot be handed a mid-task turn at all (below). So a stop here is the
        // TTL the Lead granted, then a tree-kill — no wind-down turn, no final
        // report_result. `preserve` still holds, via the plane's session ref rather than
        // the agent's cooperation. Declaring `message` would only make docketd write a
        // line nobody reads while reporting that it had.
        "mode": "signal"
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
  writing it. The alternative when the posture will not allow bypass is
  [the permission bridge](#permission-bridge--approvals-through-docket-instead-of-bypass-11),
  which replaces this flag with `--permission-prompt-tool` and routes approvals
  through Docket instead of skipping them. One or the other: this is the single
  most important line in `spawn`.
- **Do not add `--input-format stream-json`.** With a prompt in argv, claude ignores
  the prompt and blocks on stdin instead — the worker never runs a turn and the task
  dies of the liveness window. It is the one flag here that turns a working profile
  into a machine that accepts dispatches and does nothing with them. See below.
- **`--output-format stream-json` needs `--verbose`** to actually produce the stream
  under `-p`. Without it the `events.source: terminal` profile above reads nothing,
  which costs you the session ref (§11 resume) and per-task liveness (§10) — the
  same forever-requeue failure as declaring a non-`terminal` source.
- **`{mcp_config}`** is the injected path; the worker reads the plane URL and its
  bearer token from that file. It is not the only carrier — the same token is stamped on
  every spawn's environment as `DOCKET_WORKER_TOKEN` ([Spawn
  substitutions](#spawn-substitutions)), and which of the two matters is the harness's
  business, not `docketd`'s. Claude Code takes the file; a harness with no
  `--mcp-config` equivalent has to read the environment variable instead, which for
  `codex exec` is the only route that works at all — see the Codex example below, where
  the generated file is written and then ignored.
- **Nothing here caps spend, and the local cost bounds are not symmetric across
  harnesses.** `claude -p` is where one is even available: `{budget}` exists to fill
  Claude Code's `--max-budget-usd` (§9 check 9), and `--max-turns` bounds a runaway by
  turn count instead (what the real-harness E2E tier pins its own workers with).
  `codex exec` has neither, so on a Codex profile cost control is the pinned model, the
  Team budget (§9), and the §10 no-progress ceiling — plan for that before opening a
  profile up, because it is the difference between a bounded runaway and an unbounded
  one.

### Stopping a `claude -p` worker (§10, §11)

**A `claude -p` worker cannot be handed a mid-task turn, so its `stop` is a TTL'd
kill.** Two CLI facts, each established by an isolation spike against the real
binary (#103):

1. `claude -p "<prompt>" --input-format stream-json` never runs a turn at all. The
   argv prompt is ignored and the process waits on stdin indefinitely.
2. Without that flag, claude reads stdin **once** at startup — a ~3s window, after
   which it logs `no stdin data received in 3s, proceeding without it` — and never
   looks again. A turn written mid-task has nowhere to land.

There is therefore no configuration of the current CLI in `-p` mode where an injected
stop turn is consumed. And the prompt has to be argv: stdin is docketd's dead-man's
switch, held open for the whole task so a runner death is visible to the worker (§10).

So set **`mode: signal`** and read a stop as: the worker gets the full `ttl` the Lead
granted to reach its own exit, then the process tree is killed. There is no final
`report_result` — a stopped worker's progress is whatever it had already reported.
What survives is the **transcript**, via the session ref the plane recorded from the
worker's own stream: `preserve` and `preserve_and_park` mean the task can be resumed
from that ref later (§11), which is delivered by the plane's record, not by the
agent's cooperation. `Docket.MultiMachine.Tests`'
`A_stop_reaches_a_real_claude_worker_as_a_kill_deadline_not_as_a_turn_it_reads`
asserts exactly this against the real CLI, including the non-zero (killed) exit.

**What docketd reports.** On `mode: message` it acks that the turn was *written*, never
that it was read — it cannot observe consumption without harness-specific knowledge
§10 keeps out of its code. The line on this machine's stdout says so in as many words
(`written, not confirmed read`), and the `mode` you declare here is the only claim in
the system that the harness consumes stdin turns. Declare it only for a harness you
have watched honour one; the kill-path check below is how you watch.

**The mechanism is not dead.** `mode: message` remains correct for any harness that
reads turns off a held-open stdin — an interactive-mode or SDK-hosted session, or a
custom harness — and is the delivery §10 prefers, because a signal cannot carry a
disposition. It is `claude -p` specifically that has no seam for it.

## Closing the worker's stdin (§10)

`docketd` redirects **every** worker's stdin to a pipe. What a profile chooses is whether
`docketd` keeps *holding the write end*:

- **`deadman` (the default)** — held open for the worker's whole life. EOF therefore means
  `docketd` is gone (crashed, or `SIGKILL`ed), and a well-behaved harness kills its own
  process tree when it sees one. This is the cooperative, immediate half of §10's kill
  guarantee; the stray reaper's restart-time sweep is the non-cooperative backstop.
- **`closed`** — the write end is closed immediately after the spawn, so the worker's first
  read is a deterministic EOF. The same thing an agent-started process gets from
  `open_stdin: false`, for the same reason.

**When to close: a harness that blocks reading piped stdin, and nothing else.** `codex exec`
is the known case and it is not a matter of taste. Its prompt resolution
(`codex-rs/exec/src/lib.rs:1961`) calls `read_prompt_from_stdin(OptionalAppend)` *even when the
prompt arrived as argv*, and that function short-circuits only when stdin is a **terminal** — a
pipe is not — so it reaches `read_to_end` (`lib.rs:1909`) and waits for an EOF that a `deadman`
profile never sends. The worker sits there having never contacted a model; the give-away in a
transcript is a lone `Reading additional input from stdin...` on stderr and then silence. No
flag escapes it. `claude -p` survives the same pipe only because it abandons the read after
about three seconds, so **a claude profile should stay `deadman`** — it can honour the switch,
and closing stdin would only throw that away.

**What you give up: the dead-man kill, and only that.** `docketd`'s own death stops taking such
a worker down with it. Everything else is untouched — the restart-time stray sweep still
collects it (keyed on `DOCKET_MACHINE_ID`), Windows Job Objects still contain the whole tree,
Linux `PDEATHSIG` still fires if the harness arms it, and per-task exit cleanup and `stop` work
exactly as before. The window that opens is *"`docketd` is dead and has not restarted yet"*, and
what happens in it is a worker spending tokens on a task the plane has already requeued. Note
what that costs for Codex specifically: **nothing it ever had**, since it never reaches the read
that would observe the EOF. That asymmetry is why this is a per-profile declaration rather than
a machine-wide switch — one box can run a claude profile that keeps the switch and a codex
profile that cannot use it.

`docketd` says so on startup, once per declaring profile, alongside the `max_cpu_load is inert`
and event-relay notices:

```
docketd: profile 'codex': stdin is 'closed', so the worker sees EOF at once and this profile
has NO dead-man's switch — if docketd dies, its workers keep running on already-requeued tasks
until the next docketd start sweeps them. That is the declared trade for a harness that blocks
reading a held-open stdin (§10).
```

**Two things the config refuses rather than tolerates.**

1. **An unrecognized value.** `"stdin": "close"` is an error, not a fall back to `deadman`.
   Every other enum here degrades to a documented default on a typo; this one cannot, because
   the default *is* the pipe the profile was written to escape, and the symptom is a worker that
   hangs before its first turn with nothing logged anywhere.
2. **`stop.mode: message` together with `stdin: closed`.** A message-mode stop delivers its
   wind-down turn by writing to the worker's stdin
   ([Stopping a `claude -p` worker](#stopping-a-claude--p-worker-10-11)), and a closed stdin
   gives that write nowhere to land — so the declaration would promise a graceful wind-down the
   machine can never deliver. Declare `signal`, which is the honest choice for such a harness
   anyway: one that does not read stdin cannot honour a turn.

## Worked example — Codex CLI (`codex exec`), and what it costs

> ⚠️ **A Codex profile MUST declare `stdin: closed`** (see the section above). Without it
> `codex exec` blocks forever on the dead-man pipe and never takes a turn — the profile below
> declares it, and this is the one line that makes the rest of the example work at all.
>
> **Status of everything else here: verified by reading the Codex CLI source** at tag
> `rust-v0.147.0` (what `npm install -g @openai/codex` currently resolves to), with file:line
> citations — **not** by running it. No `codex` binary was available. Claims about `docketd`'s
> own behaviour are verified by tests: `Docket.Runner.Tests/CodexStreamMappingTests` pins how
> Codex's event stream maps onto §10, and
> `Docket.MultiMachine.Tests/RealCodexCollaborationTests` holds the opt-in end-to-end tier —
> three token-spending facts, live as of #110, plus one that keeps characterizing the hang
> under an explicit `stdin: deadman` for $0.

Codex is the second harness anyone reaches for, and it is a genuine test of §10's claim that
`docketd` holds no harness knowledge. The verdict is worth stating plainly: the config-only
promise holds for authentication, tool naming, the resume ref, and — since #110 — for stdin
too, but only because a knob was added to `docketd` for it. Read that as the promise bending
rather than breaking: what it took was one profile field, not harness knowledge in the daemon.

```jsonc
{
  "profiles": [
    {
      "name": "default",
      "spawn": [
        "codex", "exec",
        "You are a Docket worker running headless under docketd. You have been dispatched exactly one task. First call the docket MCP tool get_task to read your assignment. Do the work inside the assigned workspace. When done, call report_result with a reference to where the work lives (a branch/commit/URL) — not the work itself. If you are blocked or a decision is above your scope, call request_input instead of guessing.",
        // NOTE: no MCP flag. Codex has no `--mcp-config`; see below.
        "--json",                                        // the NDJSON stream `terminal` reads
        "--skip-git-repo-check",                         // work_dir is scratch, not a repo
        "--dangerously-bypass-approvals-and-sandbox",    // the bypassPermissions equivalent
        // Pin the model. There is no --max-turns for `codex exec`, so the model choice is one
        // of the few cost levers you have; gpt-5.1-codex-mini is the cheapest codex-family
        // slug the CLI knows ("Cheaper, faster, but less capable" —
        // codex-rs/tui/src/model_migration.rs:520). The catalog is server-side, so a slug can
        // be retired: confirm against `codex --help`/your account rather than trusting this.
        "--model", "gpt-5.1-codex-mini"
      ],
      // NOT OPTIONAL for this harness. Without it codex exec blocks reading the dead-man
      // pipe and never takes a turn; see "Closing the worker's stdin" above for the trade.
      "stdin": "closed",
      // Forced by `stdin: closed` (a message-mode turn would have nowhere to land) and the
      // honest declaration for this harness regardless — see seam 2 below.
      "stop": { "mode": "signal" },
      "resume": { "args": ["codex", "exec", "resume", "{session_id}", "Your task has resumed. Call get_task for the answer you were waiting for, then continue.", "--json", "--skip-git-repo-check", "--dangerously-bypass-approvals-and-sandbox"] },
      // REQUIRED for Codex — without this mapping the profile reads nothing at all.
      "events": {
        "source": "terminal",
        "mapping": {
          "system_type": "thread.started",
          "subtype_key": "type",
          "init_subtype": "thread.started",
          "session_id_key": "thread_id"
        }
      },
      "logs": { "capture": true }
    }
  ]
}
```

### Why `stdin: closed` is mandatory here — the source trace

This is the detail behind the one non-obvious line in the profile above. Under
`stdin: deadman` (the default), `docketd` holds the write end of the worker's stdin pipe for
the task's whole life, so a runner death is visible to the worker as EOF.

Codex's cold-start path cannot tolerate it. `resolve_root_prompt`
(`codex-rs/exec/src/lib.rs:1961`) handles the case where a prompt *was* given as argv, and it
still calls `read_prompt_from_stdin(OptionalAppend)` to see whether piped bytes should be
appended as extra context. That function (`lib.rs:1888`) short-circuits **only** when
`std::io::stdin().is_terminal()`:

```rust
StdinPromptBehavior::OptionalAppend if stdin_is_terminal => return None,
StdinPromptBehavior::OptionalAppend => {
    eprintln!("Reading additional input from stdin...");
}
```

A pipe is not a terminal, so it falls through to `std::io::stdin().read_to_end(&mut bytes)`
(`lib.rs:1909`) and blocks until EOF. A `deadman` profile never sends one, so the worker sits
there having never begun. The give-away in a transcript is a lone stderr line
`Reading additional input from stdin...` and then silence.

**No amount of Codex-side configuration avoids it.** No flag suppresses the append-read, and
`codex exec -` forces stdin *as* the prompt, which blocks identically. `claude -p` survives the
same pipe only because it gives up after about three seconds; Codex waits forever. So the fix
had to be on `docketd`'s side, and it is `stdin: closed`: the write end is closed right after
the spawn, that `read_to_end` returns immediately, and the turn begins.

Two notes on what that did and did not cost. **The resume path was never affected**:
`codex exec resume <id> "<prompt>"` resolves through `resolve_prompt` (`lib.rs:1944`), whose
first arm returns a non-`-` argv prompt immediately without touching stdin — so `resume.args`
worked even before #110. And the dead-man switch this profile gives up is one Codex could never
have used: it does not reach the read that would observe the EOF, so what is actually traded
away is `docketd`'s ability to end a worker by dying, in exchange for the worker being able to
start at all. `Docket.MultiMachine.Tests/RealCodexCollaborationTests` keeps a `stdin: deadman`
fact pinned against the real binary so that reasoning stays checkable rather than remembered.

### The three seams that do not fit, and what to do about each

**1. `{mcp_config}` is unusable — wire the MCP server through `CODEX_HOME` instead.**
Codex has no `--mcp-config <file>` flag; its only MCP client surface is a `[mcp_servers.<name>]`
table in `config.toml` under `CODEX_HOME` (default `~/.codex`). So the JSON config `docketd`
generates per dispatch is written, and then ignored. What replaces it is one **static** file —
and it can be static, because Codex resolves the bearer from an environment variable and
`docketd` already injects a fresh per-instance token as `DOCKET_WORKER_TOKEN` on every spawn:

```toml
# ~/.codex/config.toml — written once by the operator, correct for every dispatch
[mcp_servers.docket]
url = "https://plane.example/mcp"          # Docket:PublicMcpUrl / DOCKET_PUBLIC_MCP_URL
bearer_token_env_var = "DOCKET_WORKER_TOKEN"   # → Authorization: Bearer <that spawn's token>
enabled_tools = ["get_task", "report_result", "request_input", "register_service"]
required = true                            # fail the run loudly, not as a toolless agent
startup_timeout_sec = 30.0
tool_timeout_sec = 120.0
```

Every key above is a field on `RawMcpServerConfig` (`codex-rs/config/src/mcp_types.rs:272`),
which carries `#[schemars(deny_unknown_fields)]` — so a misspelled key is rejected rather than
silently ignored, unlike `events.mapping` below. A `command` and a `url` are mutually exclusive
transports and the HTTP-only keys are refused on a stdio server (`mcp_types.rs:381-416`).

Four things worth knowing. **The per-instance token really does work this way**:
`resolve_bearer_token` (`codex-rs/codex-mcp/src/rmcp_client.rs:822`) calls `env::var` on the
named variable *at connect time*, against the live process environment — which is where
`docketd` put the fresh token — and errors loudly if it is unset or empty. So one static file is
correct for every dispatch and the token never lands on disk, strictly better than the 0600 JSON
file the claude path needs. Note `bearer_token` (a literal token in the file) exists as a field
but is deliberately rejected: *"uses unsupported `bearer_token`; set `bearer_token_env_var`"*
(`config/src/mcp_edit.rs:43`).

**`enabled_tools` takes bare tool names**, not the `mcp__docket__*` spelling — but the names the
*model* sees are `mcp__docket__<tool>`, built as `mcp__{namespace}__{name}`
(`codex-rs/core/src/tools/handlers/mcp.rs:87-97`), which is identical to Claude Code's, so
worker prompts, skills, and allow-list vocabulary need no rewording. **`required = true`** turns
a broken wiring into an error instead of an agent that runs happily with no docket tools and
reports nothing. And **the server name must match `^[a-zA-Z0-9_-]+$`**
(`codex-mcp/src/rmcp_client.rs:849`) — `docket` is fine; anything with a dot or slash is refused.

The cost is that this server is now declared for **every** `codex` invocation on the machine,
including the operator's own interactive ones, where `DOCKET_WORKER_TOKEN` is unset and the
server will simply fail to authenticate. If that matters, the alternative is a per-spawn
`CODEX_HOME` — which `docketd` **cannot currently express**: profiles have no environment seam
(`telemetry.env` is gated on `telemetry.otel` plus a resolved endpoint, and its values are not
placeholder-substituted), and there is no `{codex_home}` token. That is a real gap, not a
matter of taste.

**2. `stop.mode` must be `signal`, for the same reason it must be for `claude -p`** — and the
source makes this sharper than a docs reading could. `codex exec` reads stdin exactly once, at
prompt-resolution time, before the turn starts (`exec/src/lib.rs:1888`); there is no reader
afterwards, so a turn written mid-task has nowhere to land. Its only signal handler is
`tokio::signal::ctrl_c()` (`exec/src/lib.rs:852`) — SIGINT; there is no SIGTERM handler, so
`docketd`'s tree-kill arrives unhandled with no flush.

Per the honesty rule above, declaring `message` would only make `docketd` write a line nobody
reads while reporting that it had. So a stop is the TTL the Lead granted, then a tree-kill — no
final `report_result`. `preserve` still holds via the plane's record rather than the agent's
cooperation: the `thread_id` `docketd` captured is exactly what `codex exec resume <SESSION_ID>`
takes, and it survives the kill.

**3. `events.mapping` is mandatory, and it still cannot give you `tool-call`.**
The built-in defaults describe claude's `stream-json`; against a Codex stream they match
nothing, so a Codex profile that omits `mapping` silently loses its session ref (§11 resume
becomes a permanent cold start). The four keys above fix that: Codex emits
`{"type":"thread.started","thread_id":"…"}` with **no** `subtype` property, so the
sub-discriminator is pointed back at `type` and matched against the same value — both checks
then read the one property Codex does emit.

What no mapping can recover is `tool-call`. The reader wants `message` → `content` to be an
**array** of blocks and reads the tool name off a block; Codex puts exactly one tool call in
`item`, an object — `ThreadItem { id, #[serde(flatten)] details }` where the payload for a tool
call is `McpToolCallItem { server, tool, arguments, result, error, status }`
(`codex-rs/exec/src/exec_events.rs:98`, `:286`). So the tool name is at `item.tool` and the
server at `item.server`, both one level down and never in a list. `mapping` renames properties —
it cannot change nesting or arity. (Source also settles something the docs left open: the event
enum includes `item.updated` alongside `item.started`/`item.completed`, `exec_events.rs:29`.) The
consequence is bounded but real: the short aliveness clock is fine (the periodic `alive` is not
gated on the events source, and every well-formed Codex line also bumps local activity), so
tasks are not requeued for silence. What you lose is the **progress** clock — the §10
no-progress ceiling (30 minutes) becomes the only thing governing a Codex worker, a wedged one
cannot be told from a busy one before it fires, and the dashboard's per-task tool-call feed is
empty.

### Two more differences worth budgeting for

**No turn cap, and `CODEX_API_KEY` is the auth variable.** The claude recipe bounds a runaway
with `--max-turns`; `codex exec` has no equivalent, so `{budget}` has nothing to bind to either.
Cost control is the pinned model, the Team budget (§9) and the no-progress ceiling, not a
harness-local cap — plan accordingly on an open profile. For auth in an unattended profile, note
that **`OPENAI_API_KEY` is not read by `codex exec`**: the exec path enables `CODEX_API_KEY`
specifically (`exec/src/lib.rs:541` sets `enable_codex_api_key_env: true`;
`login/src/auth/manager.rs:841` defines the variable), while `OPENAI_API_KEY` is consulted only
by the TUI onboarding prefill and the realtime-conversation path. Otherwise `codex exec` reuses
the cached login under `CODEX_HOME`.

**The sandbox is a network decision, not just a filesystem one.** `codex exec` defaults to a
read-only sandbox, and per the docs "By default, the agent runs with network access turned
off" — which a worker that must reach forwarded services cannot live with.
`--dangerously-bypass-approvals-and-sandbox` removes both boundaries; the narrower alternative
is `--sandbox workspace-write` plus `-c sandbox_workspace_write.network_access=true`. Note this
governs commands the **model** runs; Codex's own MCP client connection to the plane is made by
the `codex` process itself and is not subject to it.

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
  "--output-format", "stream-json", "--verbose",
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
  "--output-format", "stream-json", "--verbose",
  "--permission-mode", "bypassPermissions",
  "--allowedTools", "Read,Glob,Grep,mcp__docket__get_task,mcp__docket__report_result,mcp__docket__request_input"
]
```

Both archetypes take `stop.mode: signal` for the same reason the default profile does —
they are both `claude -p`, and neither can be handed a wind-down turn.

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

## Permission bridge — approvals through Docket instead of bypass (§11)

**Opt-in.** `bypassPermissions` stays the right answer for a machine you fully
trust, and it is the default in the worked example above. This section is the
other option: instead of skipping the approval dialog, route it to Docket, where
the Lead answers routine requests and hands the rest to a human.

Swap one flag. `--permission-prompt-tool` **replaces**
`--permission-mode bypassPermissions` — they are alternatives, not a pair:

```jsonc
"spawn": [
  "claude", "-p", "<worker prompt>",
  "--mcp-config", "{mcp_config}", "--strict-mcp-config",
  "--output-format", "stream-json", "--input-format", "stream-json",
  // Instead of --permission-mode bypassPermissions: Claude Code asks Docket
  // whenever a tool call is not covered by --allowedTools below.
  "--permission-prompt-tool", "mcp__docket__request_permission",
  // Still the routine baseline. THIS is the volume control: everything listed
  // here runs without asking anyone, so the bridge only fires for exceptions.
  "--allowedTools", "Bash,Edit,Write,Read,Glob,Grep,mcp__docket__get_task,mcp__docket__report_result,mcp__docket__request_input"
]
```

Keep `--allowedTools` carrying the routine baseline. Per-call human approval as a
*default* is unusably slow — a worker doing an hour of ordinary edits would
generate hundreds of prompts and finish nothing — so the allowlist is what makes
this posture workable at all: it decides what is routine, and the bridge only
sees what falls outside it. A profile with no `--allowedTools` and a prompt tool
asks about everything, which is a way to stall a fleet rather than to secure one.

Do **not** put `mcp__docket__request_permission` in `--allowedTools`. Claude Code
calls it itself when a dialog would have opened; it is not a tool for the agent,
and the agent calling it does nothing useful.

What the operator should know about the resulting behaviour:

- **The worker stays alive while it waits.** Every other `blocked_on_input` kind
  ends the worker's turn and parks (§11); a permission request cannot, because the
  harness has nowhere to deliver an answer to a process that exited. So the process
  sits inside the tool call, holding its workspace and its registered services,
  until someone answers. Per-task liveness is suspended while it waits, so the
  no-progress clock does not kill it for being blocked on a person.
- **An unanswered request still parks.** The wait TTL (`Docket:WaitTtl`, 30 min by
  default) applies unchanged. When it expires the task parks and the worker is told
  to stop — Claude Code never times out a permission prompt on its own, so without
  this a forgotten request would hold a process open indefinitely.
- **A denial is delivered as guidance.** The answerer's message reaches the agent
  verbatim as the refusal reason, which is why the plane requires one on a deny.
- **One at a time per task.** A task already blocked on a permission request
  refuses a second one, which serializes concurrent prompts rather than letting the
  second overwrite the first.
- **`--setting-sources` matters more than it looks.** The spawned worker otherwise
  inherits the machine account's own `settings.json` allow rules, so a tool you
  expected the bridge to catch may already be approved locally. Pass
  `--setting-sources ""` if you want the profile's `--allowedTools` to be the whole
  story.

Every decision lands on the §12 event log with its verdict and whether a Lead or a
person made it, and pending requests appear in the §12 inbox with the tool name and
proposed arguments. The Lead's triage rubric — what to approve and what to escalate
— is in the Lead skill, not here: this file is only the wiring.

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
`claude -p` — confirming the bootstrap prompt, permission posture, and hooks
actually behave on *your* machine — is the operator-run validation and belongs to
the §17.0 feasibility spikes and the §11 conformance run, deliberately out of
scope for CI.

Stop delivery is the exception: it is no longer yours to characterize, because
`Docket.MultiMachine.Tests/RealClaudeCollaborationTests` now runs the real binary in
an opt-in tier and pins what a stop does to a `claude -p` worker — a kill at the
deadline, with the transcript preserved via the session ref. What is still worth
checking on your own machine is the *kill* (that the tree is really gone) and, if you
declared `mode: message` for a non-`claude -p` harness, that a written turn is actually
honoured there. Nothing but a real run can tell you the latter, which is why docketd
does not claim it.
