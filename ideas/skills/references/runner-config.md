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
| `profiles[]` | `protocol` | `stream` (default) \| `acp` — **how `docketd` talks to the worker**, which is a bigger choice than it looks: it decides who speaks first, where the prompt lives, what stdin is for, and how a stop is delivered. `stream` spawns the harness with its prompt in the argv and reads whatever NDJSON it prints; `acp` drives it over the [Agent Client Protocol](https://agentclientprotocol.com). Everything written before this key existed is a `stream` profile and stays one. A typo is **refused**, not defaulted, for the same reason `stdin`'s is: the default is the other mode, and an ACP agent left in stream mode is never prompted, prints nothing `docketd` reads, and dies of the liveness window. See [Protocol: `acp`](#protocol-acp-10) below. |
| `profiles[]` | `follow_up` | The turn sent to **wake a live ACP session** when there is new input on the assignment — an answered question today, a Lead's message later (`ideas/sessions.md`). **Configuration, never content**: the input itself stays on the assignment and is pulled by the worker's own authenticated `get_task`, which is what makes that read a *receipt*. Pushing the text here instead would reduce delivery to "queued", put answer content on a second path out of the MCP channel, and mix per-message content into profile config. Name the docket tools the way *this* harness spells them; the default names none. Only meaningful for `protocol: acp`. |
| `profiles[]` | `prompt` | The worker's opening turn. **Required for `protocol: acp` and meaningless without it** — an ACP agent takes no prompt on argv, so the text travels as `session/prompt` instead. Same `{...}` substitutions as the spawn argv. |
| `profiles[]` | `stop` | `mode` (`message` \| `signal`), `message`, `wind_down_seconds` (default `30`). **`mode: message` is a declaration about your harness** — that a running session reads turns off stdin — and docketd takes it at its word: it writes the turn, then waits `min(ttl, wind_down_seconds)` for a voluntary exit before a hard tree-kill backstops it. It cannot check the claim, so declaring it for a harness that does not read stdin buys nothing and makes `preserve` a promise the machine will break. **`claude -p` is such a harness — use `signal` there** ([Stopping a `claude -p` worker](#stopping-a-claude--p-worker-10-11)). A `signal` profile writes nothing, and the worker gets the full `ttl` the plane granted to exit on its own before the kill (`wind_down_seconds` does not apply). Only `ttl=0` is killed immediately (§10, §11). There is no `signal` **key**: the deadline's kill is always the portable tree-kill, so a signal name had nothing to select. A config still declaring one is accepted unchanged — unknown keys are ignored — and means exactly what it always meant, which is nothing. |
| `profiles[]` | `stdin` | `deadman` (default) \| `closed` — what the worker's stdin is. `deadman` holds the pipe's write end open for the worker's whole life; that pipe **is** the §10 dead-man's switch. `closed` sends EOF right after the spawn, for a harness that blocks reading piped stdin — **`codex exec` requires it**, and gives up nothing, because it never reaches the read that would observe EOF-as-death. Two things behave unlike the other enums here: a **typo is refused** rather than defaulted (defaulting would silently restore the pipe a profile was written to escape), and `closed` is **refused together with `stop.mode: message`**, whose wind-down turn would have nowhere to land. See [Closing the worker's stdin](#closing-the-workers-stdin-10). |
| `profiles[]` | `env` | String map stamped on every spawn (and resume) of this profile. Values take the same `{task_id}` / `{machine_id}` / `{work_dir}` / `{mcp_config}` / `{session_id}` / `{mcp_url}` substitutions `spawn` does. Applied after the reserved `DOCKET_*` stamps and before `telemetry.env`. The four names docketd owns — `DOCKET_MACHINE_ID`, `DOCKET_TASK_ID`, `DOCKET_WORKER_TOKEN`, `DOCKET_TRACEPARENT` — are **refused at load**, not silently dropped. Use this to isolate a home (`GROK_HOME` / `CODEX_HOME`) only when the operator asked for a sealed box. Prefer `files[]` for additive project-local MCP. |
| `profiles[]` | `files` | Files written into `{work_dir}` **before** the harness starts (#112 G2). Each entry is `path` + `contents` (both substituted) and optional `mode` (octal, default `0600`). A relative path is resolved against the work dir (so `.grok/config.toml` and `{work_dir}/.grok/config.toml` land in the same place). After substitution the path must stay under the work dir — `..` that escapes fails the spawn. Parent directories are created. This is how a Grok profile drops `{work_dir}/.grok/config.toml` so Grok **merges** docket with `~/.grok` instead of replacing it. |
| `profiles[]` | `hooks` | Argv hooks, **never a shell** (§10). `before_spawn` runs after `files[]` and before `Process.Start`; non-zero or timeout (10s) is fail-closed (`spawn_failed`). `after_exit` is best-effort after the worker's `exited` and stray reap, skipped for superseded instances. Hook processes get `DOCKET_MACHINE_ID`, `DOCKET_HOOK`, and the same `profiles[].env` map the worker does (minus reserved `DOCKET_*`), not `DOCKET_TASK_ID` / `DOCKET_WORKER_TOKEN`. Use only when the harness will not read a project-local file (Codex / `CODEX_HOME`). |
| `profiles[]` | `resume` | `args`: argv to resume a parked task's transcript, directory-scoped (§11). |
| `profiles[]` | `events` | `source` (`hooks` \| `otel` \| `terminal` \| `none`) + `mapping`, which overrides the **stdout stream's property names** — not harness event names — and, via `tool_event_type` + `tool_name_path`, describes a harness that emits one flat event object per tool call. **Only `terminal` is implemented**; `hooks` and `otel` parse but are wired to nothing, so all three non-`terminal` values behave as `none`. See [Event relay](#event-relay-10) below before choosing — a non-`terminal` profile has no progress signal, so the no-progress ceiling is the only clock governing its tasks (the periodic `alive` keeps the short aliveness clock satisfied either way). |
| `profiles[]` | `telemetry` | `otel` bool (opt-in, default **false**), `endpoint` (OTLP destination; falls back to the one docketd inherited), and `env` (a string map of harness-specific variables, applied verbatim). When on, docketd sets the vendor-neutral `OTEL_*` exporter variables and appends `docket.task_id`/`docket.machine_id` to `OTEL_RESOURCE_ATTRIBUTES`, so the harness's own token/cost telemetry is attributable per task (§10). `otel: true` with **no endpoint configured and none inherited sets nothing at all** and warns once — telemetry is never enabled without a destination. Claude Code additionally needs `"env": { "CLAUDE_CODE_ENABLE_TELEMETRY": "1" }` (its own flag is data, since docketd holds no harness knowledge). **Visibility only**: Docket ingests none of it and enforces no ceiling — see [docs/TELEMETRY.md](../../../docs/TELEMETRY.md). |
| `profiles[]` | `logs` | §12 machine-local transcript capture: `capture` (bool, default **false**), `max_bytes` (per-stream cap, default 50 MiB), `prune_after_days` (local hygiene, default 7, `0` disables). There is no `format` or `path`: both were read by nothing and have been removed, and a config still carrying either is accepted unchanged — see [Transcript capture](#transcript-capture-12) below. |
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

`docketd` substitutes these `{...}` tokens in each `spawn` arg and each
`profiles[].env` value (and injects the first three as environment on every
spawn, not configurably — §10):

| Token / env | Value |
|---|---|
| `{task_id}` / `DOCKET_TASK_ID` | The dispatched task id. |
| `{machine_id}` / `DOCKET_MACHINE_ID` | This machine's id. |
| `{work_dir}` | `{work_root}/{task_id}`, the spawn cwd. |
| `{mcp_config}` | Path to the generated MCP config `docketd` writes to `{work_dir}/mcp.json` (mode 0600) — **only when this token appears in spawn or resume argv**. Prefer `files[]` + `{worker_token}` / `{mcp_url}` (below) for a new profile; this token is the Claude convenience that remains so existing argv keeps working. |
| `{mcp_url}` | The plane's public MCP URL (`Docket:PublicMcpUrl`). Filled by the plane on every dispatch so a `files[]` body can name the URL without parsing `mcp.json`. Also stamped on the worker as `DOCKET_MCP_URL`. |
| `{worker_token}` | The minted worker-instance token (`dkt_w_` + 64 hex). For a `files[]` body that must embed the bearer (Claude's `--mcp-config` does not expand `${DOCKET_WORKER_TOKEN}`). Same secret as `DOCKET_WORKER_TOKEN` on the spawn env. |
| `{session_id}` | The opaque harness session ref to resume. Substituted in `resume.args` only, never `spawn` (§11). |
| `DOCKET_WORKER_TOKEN` | The minted worker-instance token (also `{worker_token}`). |

## The generated MCP config (`{mcp_config}`)

At dispatch the plane still *sends* the worker's MCP client config (it cannot see
the profile). `docketd` writes it 0600 **only if** spawn or resume argv contains
`{mcp_config}` (#112 G11). It is Claude Code's `--mcp-config` HTTP shape:

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
        "You are a Docket worker running headless under docketd. You have been dispatched exactly one task. First call the mcp__docket__get_task MCP tool to read your assignment (namespace, description, completion_criteria, workspace, attempt). Read the docket-worker skill. Do the work inside the assigned workspace. When done, call report_result with a reference to where the work lives (a branch/commit/URL) — not the work itself. If you are blocked or a decision is above your scope, call request_input instead of guessing. Every docket tool is an MCP tool named mcp__docket__<name>; there is no `docket` command line, so never run one in a shell and never curl the MCP server. You do not verify or complete the task yourself.",
        "--mcp-config", "{work_dir}/mcp-{task_id}.json",
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
      // Claude's --mcp-config does not expand ${DOCKET_WORKER_TOKEN}. files[]
      // writes the same JSON {mcp_config} used to, with {worker_token} and
      // {mcp_url}. mcp-{task_id}.json so a continuation does not overwrite
      // its predecessor's bearer in a borrowed work dir.
      "files": [{
        "path": "{work_dir}/mcp-{task_id}.json",
        "contents": "{\"mcpServers\":{\"docket\":{\"type\":\"http\",\"url\":\"{mcp_url}\",\"headers\":{\"Authorization\":\"Bearer {worker_token}\"}}}}"
      }],
      "resume": { "args": ["claude", "-p", "Resume your task.", "--resume", "{session_id}", "--mcp-config", "{work_dir}/mcp-{task_id}.json"] },
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
      "logs": { "capture": true, "max_bytes": 52428800, "prune_after_days": 7 },
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
- **`--mcp-config {work_dir}/mcp-{task_id}.json`** is the file `files[]` writes
  (above). `{mcp_config}` still works as a shorthand that has docketd write
  Claude's JSON itself, but a new profile should use `files[]` + `{worker_token}`
  / `{mcp_url}` so the plane's Claude-shaped builder is not load-bearing.
  A harness with no `--mcp-config` equivalent reads `DOCKET_WORKER_TOKEN` from
  the environment instead — see Codex below.
- **Nothing caps spend — not here and not in the plane.** The Team dollar ceiling was
  removed 2026-08-12 (spec §9's note), and the `{budget}` substitution that fed Claude
  Code's `--max-budget-usd` went with it, since its value came from that ceiling. What
  bounds a runaway on a profile is what you write into the argv yourself — `claude -p`
  has `--max-budget-usd` and `--max-turns`, and the real-harness E2E tier pins its own
  workers with the latter; `codex exec` has neither — plus the pinned model and the §10
  no-progress ceiling. Decide that before opening a profile up: it is the difference
  between a bounded runaway and an unbounded one.

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
        "You are a Docket worker running headless under docketd. You have been dispatched exactly one task. First call the mcp__docket__get_task MCP tool to read your assignment. Do the work inside the assigned workspace. When done, call report_result with a reference to where the work lives (a branch/commit/URL) — not the work itself. If you are blocked or a decision is above your scope, call request_input instead of guessing. Every docket tool is an MCP tool named mcp__docket__<name>; there is no `docket` command line, so never run one in a shell and never curl the MCP server.",
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
      // Codex has no project-local MCP file. Either leave the static ~/.codex
      // config.toml in place (enroll writes it once) or run an idempotent
      // hooks.before_spawn argv that ensures the docket table exists. Do not
      // remove it in after_exit — that races a sibling worker.
      // Forced by `stdin: closed` (a message-mode turn would have nowhere to land) and the
      // honest declaration for this harness regardless — see seam 2 below.
      "stop": { "mode": "signal" },
      "resume": { "args": ["codex", "exec", "resume", "{session_id}", "Your task has resumed. Call get_task for the answer you were waiting for, then continue.", "--json", "--skip-git-repo-check", "--dangerously-bypass-approvals-and-sandbox"] },
      // REQUIRED for Codex — without this mapping the profile reads nothing at all.
      "events": {
        "source": "terminal",
        "mapping": {
          // The §11 resume ref. Codex has no sub-discriminator, so the sub-check is
          // pointed back at `type` and matched against the same value the outer check
          // already matched; the id key does the real work.
          "system_type": "thread.started",
          "subtype_key": "type",
          "init_subtype": "thread.started",
          "session_id_key": "thread_id",
          // The progress clock, via flat mode: one Codex event object IS one tool call.
          // `item.started` rather than `item.completed` so a long command reports at
          // minute zero. Two name paths because Codex names a shell call in `command`
          // and an MCP call in `tool`, both under this same event type.
          "tool_event_type": "item.started",
          "tool_name_path": "item.command, item.tool"
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
table in `config.toml` under `CODEX_HOME` (default `~/.codex`). The JSON the plane still
sends is not written, because this profile never names `{mcp_config}`. What replaces it is one **static** file —
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
worker prompts, skills, and allow-list vocabulary carry over **between these two harnesses**.
Do not read that as a general rule: it is a coincidence of two vendors picking the same
convention, not a standard. OpenCode is the counterexample and it is the third harness anyone
tries — it names the same tool `docket_get_task`, as `<server>_<tool>`
(`packages/opencode/src/mcp/catalog.ts:119` at `v1.18.17`), so a prompt or allow-list that
spells `mcp__docket__get_task` sends that agent hunting a tool it does not have. **Naming docket
tools bare in a worker prompt is the phrasing that ports to all three**; the qualified spelling
is per-harness data. **`required = true`** turns
a broken wiring into an error instead of an agent that runs happily with no docket tools and
reports nothing. And **the server name must match `^[a-zA-Z0-9_-]+$`**
(`codex-mcp/src/rmcp_client.rs:849`) — `docket` is fine; anything with a dot or slash is refused.

The cost is that this server is now declared for **every** `codex` invocation on the machine,
including the operator's own interactive ones, where `DOCKET_WORKER_TOKEN` is unset and the
server will simply fail to authenticate. Isolate it with `profiles[].env`:

```jsonc
"env": { "CODEX_HOME": "/var/lib/docketd/codex" }   // a directory the operator prepared
// or, per task: "CODEX_HOME": "{work_dir}/.codex"  // only works if that dir already has config.toml
```

`env` alone does not create the directory or the file. Pair it with `files[]`
(or a prepared home). A working isolated-home composition:

```jsonc
"env": { "CODEX_HOME": "{work_dir}/.codex" },
"files": [{
  "path": "{work_dir}/.codex/config.toml",
  "contents": "[mcp_servers.docket]\nurl = \"{mcp_url}\"\nbearer_token_env_var = \"DOCKET_WORKER_TOKEN\"\nenabled_tools = [\"get_task\", \"report_result\", \"request_input\", \"register_service\"]\nrequired = true\n"
}]
```

Pointing `CODEX_HOME` at an empty `{work_dir}/.codex` with no `files[]` is still
a silent toolless agent.

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

**3. `events.mapping` is mandatory — and with flat mode it now reaches `tool-call` too.**
The built-in defaults describe claude's `stream-json`; against a Codex stream they match
nothing, so a Codex profile that omits `mapping` loses its session ref (§11 resume becomes a
permanent cold start) and every tool call with it. The first four keys above fix the ref: Codex
emits `{"type":"thread.started","thread_id":"…"}` with **no** `subtype` property, so the
sub-discriminator is pointed back at `type` and matched against the same value — both checks
then read the one property Codex does emit.

`tool-call` used to be unreachable at any price, and the reason is worth keeping: the reader
wanted `message` → `content` to be an **array** of blocks and read the tool name off a block,
while Codex puts exactly one tool call in `item`, an object — `ThreadItem { id,
#[serde(flatten)] details }` where the payload for an MCP call is `McpToolCallItem { server,
tool, arguments, result, error, status }` (`codex-rs/exec/src/exec_events.rs:98`, `:286`). So the
tool name is at `item.tool` (a shell call names it `command` instead) and the server at
`item.server`, both one level down and never in a list — and renaming properties cannot change
nesting or arity.

What changed (issue #111) is that the seam now also carries a *shape*: `tool_event_type` names
the `type` value that **is** a tool call and `tool_name_path` the dotted path to its name, so the
last two keys above give a Codex worker its progress clock back with no code in `docketd` and no
Codex knowledge in it either. Two details the source settles: the event enum includes
`item.updated` alongside `item.started`/`item.completed` (`exec_events.rs:29`) — map only
`item.started`, or one call reports two or three times — and non-tool items (`agent_message`,
`reasoning`) share that same `item.started` type, so they are filtered by carrying neither
`command` nor `tool`. `docketd`'s side of this is pinned by
`Docket.Runner.Tests/CodexStreamMappingTests`; against the real binary it is still unrun, like
everything else in this section.

Omit the mapping and the cost is bounded but real: the short aliveness clock is fine (the
periodic `alive` is not gated on the events source, and every well-formed Codex line also bumps
local activity), so tasks are not requeued for silence. What you lose is the **progress** clock —
the §10 no-progress ceiling (30 minutes) becomes the only thing governing a Codex worker, and a
wedged one cannot be told from a busy one before it fires. That is no longer a silent loss:
`docketd` writes one line per task when a `terminal` profile reads event lines and extracts
nothing from them (see [Event relay](#event-relay-10)).

### Usage reporting: the mapping keys, and the one with teeth

`events.mapping` also describes where the harness states what it **consumed** (§10 measured
view). The built-in defaults describe claude's `stream-json` `result` line, so a claude
profile needs none of these:

| key | default | what it is |
|---|---|---|
| `usage_type` | `result` | the `type` value of a usage-bearing line (Codex: `turn.completed`; OpenCode: `step_finish`). A **value**, not a path |
| `usage_key` | `usage` | path to the object holding the aggregate counters (OpenCode: `part.tokens`) |
| `usage_input_key` | `input_tokens` | |
| `usage_output_key` | `output_tokens` | |
| `usage_cache_read_key` | `cache_read_input_tokens` | Codex: `cached_input_tokens`; OpenCode: `cache.read` |
| `usage_cache_write_key` | `cache_creation_input_tokens` | Codex: `cache_write_input_tokens`; OpenCode: `cache.write` |
| `usage_reasoning_key` | unset | a reasoning portion **of** output, where a harness breaks one out (Codex: `reasoning_output_tokens`). Not a portion for every harness — see `usage_reasoning_is_subset` |
| `usage_cost_key` | `total_cost_usd` | a cost the **harness** computed. Set empty for a harness that computes none — nothing derives one from tokens |
| `usage_models_key` | `modelUsage` | path to an object keyed **by model name**, each value holding that model's own counters. Set empty for a harness that reports no model |
| `model_input_key` … `model_cost_key` | `inputTokens`, `outputTokens`, `cacheReadInputTokens`, `cacheCreationInputTokens`, `costUSD` | field names *inside* each per-model entry |
| `usage_cached_is_subset` | `false` | **read this one twice — see below** |
| `usage_reasoning_is_subset` | `true` | the other one that changes numbers — see below |
| `usage_is_cumulative` | `true` | `false` for a harness reporting per-turn deltas (Codex, OpenCode); docketd then accumulates |

Note the two casings in one claude payload — snake_case in the aggregate `usage` object,
camelCase inside `modelUsage`. That is why every key is separately overridable rather than
one naming convention applied twice.

**Every key above except `usage_type` is a dotted path, not a bare property name.** Claude and
Codex both put their counters in one object a single level below the line root with the buckets
flat inside it, so a bare name reached everything and the distinction never came up. OpenCode
broke all three assumptions at once — counters at `part.tokens`, cache buckets one deeper at
`tokens.cache.read`, and cost *beside* the counters at `part.cost` rather than at the line root —
and no rename reaches any of them. **This cost no new key:** a path with no dots is a
one-segment path, so every default above and every mapping written before this behaves exactly
as it did. What each path is rooted at did not change either: `usage_key`, `usage_models_key` and
`usage_cost_key` walk from the **line root**; the four buckets and `usage_reasoning_key` walk
from the `usage_key` object; the `model_*` keys walk from each per-model entry. Same segment
rules as `tool_name_path` — property names only, no wildcards or indexes — and a malformed path
fails the config load rather than reading nothing quietly. There are no comma-separated
alternatives here, because a counter has one home.

⚠️ **`usage_type` must not equal your effective `system_type`.** docketd stops at a
session-init line, so usage riding that same stream type is never read — and the symptom is a
profile that resumes perfectly and reports zero spend forever. The config load refuses the
collision. It is an easy one to hit on a harness with few stream types to choose from: OpenCode
has six, which is why its profile puts the session ref on `step_start` and usage on
`step_finish`.

**`usage_cached_is_subset` is the one that changes numbers.** The two harnesses do not mean
the same thing by "input":

- **claude** counts uncached prompt tokens in `input_tokens` and its cache hits separately.
  Its buckets are already disjoint. Leave this `false`.
- **`codex exec`** counts the **whole prompt** in `input_tokens`, with `cached_input_tokens`
  a *subset* of it — Codex's own `non_cached_input()` subtracts one from the other for its
  own display. Set this `true` and docketd subtracts, so the four buckets stay disjoint.

Get it wrong on a Codex profile and every cached token is counted twice the moment the
buckets are summed — on a cache-heavy worker that roughly doubles the reported total. It is
declared per profile as data because it is a fact about the harness, and docketd holds no
harness knowledge in code (§10).

**`usage_reasoning_is_subset` is the same problem one bucket over, and it fails the other way.**
`ReasoningOutputTokens` on the wire is *defined* as a portion of the output count, which is why
the §12 total is `input + output + cache_read + cache_write` and excludes it. Two conventions
exist in the wild:

- **claude and `codex exec`** report reasoning *inside* their output total. Leave this `true`
  (the default) and it rides along as the informational portion it is.
- **OpenCode** has already subtracted it: it publishes `output = output − reasoning` and
  `reasoning` separately. Set this `false` and docketd folds it back in before emitting.

Where `usage_cached_is_subset` fails by double-counting, this one fails by *losing* tokens: leave
it `true` on an OpenCode profile and every reasoning token vanishes from the task's total, which
on a thinking model is most of the spend. Note the two defaults deliberately disagree — `false`
for cache, `true` for reasoning — because that is what claude actually does: its cache counters
are disjoint from input while its reasoning is inside output. The defaults describe a harness,
not a symmetry.

A worked Codex `events.mapping` for usage:

```jsonc
"usage_type": "turn.completed",
"usage_cache_read_key": "cached_input_tokens",
"usage_cache_write_key": "cache_write_input_tokens",
"usage_reasoning_key": "reasoning_output_tokens",
"usage_cost_key": "",             // Codex reports no cost, anywhere
"usage_models_key": "",           // and names no model on its stream
"usage_cached_is_subset": "true", // cached_input_tokens ⊂ input_tokens
"usage_is_cumulative": "false"    // per-turn, so docketd accumulates
```

A Codex row's model therefore reads **"not reported"** in the §12 view, with real token counts
beside it. That is deliberate and there is no profile key to change it: a model the *plane*
declared, shown in a section labelled "reported by the harness", would misattribute the claim
(§2 principle 2). Where a harness does name its models — claude does, per model — those names
are carried exactly as reported.

### Two more differences worth budgeting for

**No turn cap, and `CODEX_API_KEY` is the auth variable.** The claude recipe bounds a runaway
with `--max-turns`; `codex exec` has no equivalent. Cost control is then the pinned model and the
no-progress ceiling alone — plan accordingly on an open profile. For auth in an unattended profile, note
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

## Worked example — OpenCode (`opencode run`), the third harness

> **Status: verified by reading OpenCode's source** at tag `v1.18.17` (npm `opencode-ai@1.18.17`,
> which the README's own badge and install line point at) — **not** by running it. No `opencode`
> binary was available. Docket's own side is pinned by tests:
> `Docket.Runner.Tests/OpenCodeStreamMappingTests` covers the mapping and usage for $0, and
> `Docket.MultiMachine.Tests/RealOpenCodeCollaborationTests` holds the opt-in end-to-end tier.
> Note the upstream repo moved: `sst/opencode` now redirects to `anomalyco/opencode`.

**OpenCode was the cheapest of the three harnesses to support, and that is the interesting
result.** It needed no seam Codex had not already forced into existence — `stdin: closed` and the
flat tool-call mode are exactly the two knobs it wants. What it did force was one generalization
of the usage keys (bare names became dotted paths, **no new key**) and one new semantic boolean
(`usage_reasoning_is_subset`). Read that as the §10 config-only promise holding: a third vendor's
CLI cost one crank turn on an existing seam rather than harness knowledge in the daemon.

```jsonc
{
  "profiles": [
    {
      "name": "default",
      "spawn": [
        "opencode", "run",
        // Name docket tools the way OpenCode spells them: `docket_get_task`, NOT the
        // `mcp__docket__get_task` the claude and Codex profiles use — see the naming note in the
        // Codex section above. This prompt used to name them bare (`get_task`) because the bare
        // form is the one spelling that ports to all three harnesses; it no longer does, because
        // bare is exactly what a real worker misread as a shell command and tried to run as
        // `docket get_task`. Each harness naming its own real tool is unambiguous; a shared
        // spelling cannot be.
        "You are a Docket worker running headless under docketd. You have been dispatched exactly one task. First call the docket_get_task MCP tool to read your assignment (namespace, description, completion_criteria, workspace, attempt). Do the work inside the assigned workspace. When done, call docket_report_result with a reference to where the work lives (a branch/commit/URL) — not the work itself. If you are blocked or a decision is above your scope, call docket_request_input instead of guessing. Every docket tool is an MCP tool named docket_<name>; there is no `docket` command line, so never run one in a shell and never curl the MCP server.",
        // NOTE: no MCP flag. OpenCode has no `--mcp-config`; see below.
        "--format", "json",   // the NDJSON stream `terminal` reads. Needs no companion flag,
                              // unlike claude's --output-format stream-json + --verbose pair.
        // The bypassPermissions equivalent. NOT OPTIONAL, and its failure mode is quiet:
        // without it a headless worker AUTO-REJECTS every permission ask and fails the task
        // rather than hanging (`run.ts:806-815`).
        "--auto",
        // Pin the model. `opencode run` has no --max-turns and no budget flag, so this plus the
        // no-progress ceiling is the whole cost bound — and since the stream carries no model
        // field, this argv is also the only record of what produced the tokens.
        "--model", "anthropic/claude-haiku-4-5-20251001"
      ],
      // NOT OPTIONAL for this harness, and the failure is SILENT — see below.
      "stdin": "closed",
      // Isolate OpenCode from the operator's ~/.config/opencode. env alone does
      // not create the file — pair with files[] (or a prepared path).
      "env": { "OPENCODE_CONFIG": "{work_dir}/opencode.json" },
      "files": [{
        "path": "{work_dir}/opencode.json",
        "contents": "{\"$schema\":\"https://opencode.ai/config.json\",\"mcp\":{\"docket\":{\"type\":\"remote\",\"url\":\"{mcp_url}\",\"enabled\":true,\"headers\":{\"Authorization\":\"Bearer {env:DOCKET_WORKER_TOKEN}\"},\"oauth\":false}}}"
      }],
      // Forced by `stdin: closed`, and honest regardless: stdin is read once before the loop
      // starts and never again, and the CLI installs no SIGTERM handler at all.
      "stop": { "mode": "signal" },
      // `--session`, never `--continue` — see the project-scoping trap below.
      "resume": {
        "args": ["opencode", "run", "Your task has resumed. Call get_task for the answer you were waiting for, then continue.",
                 "--session", "{session_id}", "--format", "json", "--auto",
                 "--model", "anthropic/claude-haiku-4-5-20251001"]
      },
      // REQUIRED — the built-in claude defaults match nothing in this stream.
      "events": {
        "source": "terminal",
        "mapping": {
          // §11 resume ref. OpenCode emits no init line, but `sessionID` is top-level on EVERY
          // line, so the Codex trick applies: point the sub-discriminator back at `type` and
          // match the same value. `step_start` because it is the earliest line emitted.
          "system_type": "step_start",
          "subtype_key": "type",
          "init_subtype": "step_start",
          "session_id_key": "sessionID",
          // Progress clock. One `tool_use` line IS one tool call, and every tool kind names
          // itself in the same property — so unlike Codex this needs no alternatives.
          "tool_event_type": "tool_use",
          "tool_name_path": "part.tool",
          // Usage. Every one of these is a dotted path, which is what this harness forced.
          // usage_type MUST differ from system_type (docketd returns early on an init line).
          "usage_type": "step_finish",
          "usage_key": "part.tokens",
          "usage_input_key": "input",
          "usage_output_key": "output",
          "usage_cache_read_key": "cache.read",     // one level deeper than its siblings
          "usage_cache_write_key": "cache.write",
          "usage_reasoning_key": "reasoning",
          "usage_cost_key": "part.cost",            // BESIDE the counters, not at the root
          "usage_models_key": "",                   // OpenCode names no model anywhere
          "usage_cached_is_subset": "false",        // it already subtracts cache out of input
          "usage_reasoning_is_subset": "false",     // ...and reasoning out of output. Fold back.
          "usage_is_cumulative": "false"            // step_finish is one step's own figures
        }
      },
      // No harness-specific opt-in needed: opencode reads the vendor-neutral OTEL_* variables
      // directly, where claude needs CLAUDE_CODE_ENABLE_TELEMETRY. Exports logs and traces
      // only — no metrics, so no token/cost here. Those ride stdout (see docs/TELEMETRY.md).
      "telemetry": { "otel": true, "endpoint": "http://127.0.0.1:4318" },
      "logs": { "capture": true }
    }
  ]
}
```

### The MCP wiring: a static file, and the best bearer story of the three

OpenCode has no `--mcp-config`, so this profile never names `{mcp_config}` and
`mcp.json` is not written. What replaces it is one **static** operator-written file, and it can be static because
OpenCode applies `{env:VAR}` substitution to the config **text** before parsing it, reading
`process.env` at load time (`packages/opencode/src/config/variable.ts:33-38`, called from
`config/config.ts:220`) — and `docketd` already stamps a fresh per-instance token on every spawn
as `DOCKET_WORKER_TOKEN`:

```jsonc
// ~/.config/opencode/opencode.json — written once, correct for every dispatch
{
  "$schema": "https://opencode.ai/config.json",
  "mcp": {
    "docket": {
      "type": "remote",
      "url": "https://plane.example/mcp",     // Docket:PublicMcpUrl / DOCKET_PUBLIC_MCP_URL
      "enabled": true,
      "headers": { "Authorization": "Bearer {env:DOCKET_WORKER_TOKEN}" },
      "oauth": false,                          // REQUIRED — see below
      "timeout": 120000
    }
  }
}
```

Three things worth knowing.

**`"oauth": false` is load-bearing, not defensive.** Without it OAuth auto-detection can take
over a server meant to authenticate by bearer header; the documented recipe pairs the two exactly
this way (`opencode.ai/docs/mcp-servers`, "API key authentication").

**A missing token fails silently, and there is no `required` to catch it.** An unset variable
substitutes to the **empty string** rather than erroring (`variable.ts:37`), so the wire carries
`Authorization: Bearer ` and the plane answers 401 — leaving an agent that runs happily with no
docket tools and reports nothing. Codex's `bearer_token_env_var` errors loudly and its
`required = true` fails the run; OpenCode's remote schema has neither. Nothing config-side
prevents it, so this is one to catch in the conformance smoke test.

**Tool allow-listing is a config map with wildcards, not a flag.** `"tools": { "docket_*": true }`
and the per-agent form `"agent": { "<name>": { "tools": { … } } }` (`opencode.ai/docs/mcp-servers`).
Patterns use the `<server>_<tool>` naming, so a `mcp__docket__*` pattern matches nothing.

**Per-dispatch config is now expressible via `profiles[].env`.** `OPENCODE_CONFIG` (a file
path), `OPENCODE_CONFIG_CONTENT` (a whole config inline) and `OPENCODE_CONFIG_DIR` all exist
(`packages/core/src/flag/flag.ts:21-22, 63-65`). Any of them can go in `env` now (#112 G3):

```jsonc
"env": { "OPENCODE_CONFIG": "/var/lib/docketd/opencode.json" }
```

`env` alone does not write the file — pair `OPENCODE_CONFIG` with a `files[]` entry,
or use a prepared path, or put the whole config in `OPENCODE_CONFIG_CONTENT` (the one
of the three that needs no file on disk).

### Why `stdin: closed` is mandatory here — and why the failure is worse than Codex's

`opencode run` resolves its prompt through `packages/opencode/src/cli/cmd/run.ts:416`:

```ts
const piped = process.stdin.isTTY ? undefined : await Bun.stdin.text()
```

`Bun.stdin.text()` reads to EOF, a pipe is not a TTY, and a `deadman` profile never sends one —
so the worker blocks here, *before* the empty-prompt check at `:420` and before any session
exists. Two ways this is nastier than the Codex equivalent:

- **The argv prompt does not save you.** Codex at least has an argv arm; OpenCode reads stdin
  whenever it is not a TTY and then *concatenates* the two (`run.ts:40-50`).
- **It is completely silent.** Codex prints `Reading additional input from stdin...` on stderr —
  the give-away this file teaches operators to look for. OpenCode prints nothing and leaves an
  empty transcript. A worker that starts, emits zero bytes, and dies of the liveness window is
  all you get.

`stdin: closed` costs this harness nothing it ever had, for the same reason it costs Codex
nothing: it never reaches a read that would observe EOF-as-death. `RealOpenCodeCollaborationTests`
keeps the hang characterized against the real binary for $0.

### Three more differences worth budgeting for

**No tool-call *start* event, so the progress clock lags.** OpenCode emits a `tool_use` line only
when a call reaches `completed` or `error`; the `running` branch is explicitly excluded from JSON
mode (`run.ts:719`, `:729-738`). There is nothing to point `tool_event_type` at that fires
earlier — `step_start` marks LLM round-trips, not tool starts. So a 25-minute build reports at
minute 25, which is most of the no-progress ceiling spent looking wedged, and it is the one place
OpenCode is strictly worse than Codex (which has `item.started`). Nothing in config fixes it; the
honest fix is upstream.

**Every non-git work dir is the same "project".** `packages/core/src/project.ts:111-112`: with no
repository discovered, the project id is the global one and its directory is the filesystem root.
`docketd`'s `{work_root}/{task_id}` scratch dirs are not repos, so **all** OpenCode workers on a
machine share one project and one session store. Two consequences pointing opposite ways: it is
why `--session {session_id}` resumes fine from a fresh scratch dir, and it is why `--continue`
must never appear in a profile — it would find another concurrent task's session. A per-task
`OPENCODE_CONFIG_DIR` is now expressible (`"env": { "OPENCODE_CONFIG_DIR": "{work_dir}/.opencode" }`)
and `files[]` can create that directory (and write a config into it) if you want a
per-task isolated project.

**Auth is the simplest of the three.** OpenCode resolves a provider key by mapping its catalog's
per-provider `env` list over the process environment
(`packages/opencode/src/provider/provider.ts:1527-1531`), so `ANTHROPIC_API_KEY` (or the relevant
provider's variable) in `docketd`'s environment is enough — no harness-specific spelling like
Codex's `CODEX_API_KEY`, and nothing refuses an API key in favour of a first-party login. Stored
credentials otherwise live in `auth.json` under the global data dir (`src/auth/index.ts:10`), and
an undocumented `OPENCODE_AUTH_CONTENT` accepts them inline (`auth/index.ts:59`).

## Worked example — Grok Build (`grok -p`), the fourth harness

> **Status: verified against `grok` 1.0.3** on 2026-08-14 (live `-p` runs, not
> source reading). Parser half is pinned for $0 by
> `Docket.Runner.Tests/GrokStreamMappingTests` against a captured
> `streaming-messages-json` stream.
> `Docket.MultiMachine.Tests/RealGrokCollaborationTests` is the opt-in token-spending
> tier. Auth is `XAI_API_KEY` (not `XAI_KEY`) or `grok login`.

**Grok is the cheapest fourth harness, and the interesting result is why.** Pick
`--output-format streaming-messages-json` and the stream *is* Claude's Messages NDJSON
(`system`/`init` + `assistant`/`tool_use` + `result`/`usage`). No `events.mapping`.
What it did force is `stdin: closed`, for a reason Codex and OpenCode do not share.

```jsonc
{
  "profiles": [
    {
      "name": "default",
      "spawn": [
        "grok", "-p",
        // Name docket tools the way Grok spells them: `docket__get_task`, NOT
        // `mcp__docket__get_task` (claude/Codex) and NOT `docket_get_task` (OpenCode).
        "You are a Docket worker running headless under docketd. You have been dispatched exactly one task. First call the docket__get_task MCP tool to read your assignment (namespace, description, completion_criteria, workspace, attempt). Do the work inside the assigned workspace. When done, call docket__report_result with a reference to where the work lives (a branch/commit/URL) — not the work itself. If you are blocked or a decision is above your scope, call docket__request_input instead of guessing. Every docket tool is an MCP tool named docket__<name>; there is no `docket` command line, so never run one in a shell and never curl the MCP server.",
        // NOTE: no --mcp-config. Grok has none; see the static file below.
        "--output-format", "streaming-messages-json",
        // Always-approve. --yolo and --permission-mode bypassPermissions are the same mode.
        // There is no --permission-prompt-tool, so Docket's permission bridge does not plug in.
        "--always-approve",
        "--no-auto-update",
        // Unlike Codex/OpenCode, grok -p has --max-turns.
        "--max-turns", "30",
        "-m", "grok-4.6"
      ],
      // NOT OPTIONAL. grok -p starts immediately with a held-open pipe, then never
      // exits until stdin EOF. deadman leaks the process after report_result.
      "stdin": "closed",
      // Additive MCP: Grok merges {cwd}/.grok/config.toml with ~/.grok. Do NOT set
      // GROK_HOME — that replaces the operator's auth, skills, and MCP servers.
      // 1.0.4+ gates project-local MCP behind folder trust; a docketd work dir
      // is a throwaway folder, so disable the gate (or launch with --trust).
      "env": { "GROK_FOLDER_TRUST": "0" },
      "files": [{
        "path": "{work_dir}/.grok/config.toml",
        "contents": "[mcp_servers.docket]\nurl = \"{mcp_url}\"\nenabled = true\nheaders = { \"Authorization\" = \"Bearer {worker_token}\" }\n",
        "mode": "600"
      }],
      "stop": { "mode": "signal" },
      "resume": {
        "args": [
          "grok", "-p",
          "Your task has resumed. Call docket__get_task for the answer you were waiting for, then continue.",
          "--resume", "{session_id}",
          "--output-format", "streaming-messages-json",
          "--always-approve",
          "--no-auto-update",
          "-m", "grok-4.6"
        ]
      },
      // No mapping. The built-in claude defaults match this stream.
      "events": { "source": "terminal" },
      "logs": { "capture": true }
    }
  ]
}
```

### Why `stdin: closed` is mandatory — and why it is not Codex's hang

Measured against 1.0.3: first stdout at ~1.3s with the dead-man pipe still held, process
still alive until the pipe closed (20s hold → 20s wall clock). `</dev/null` finished in
~5s. So Grok **does take its turn** under deadman — then it waits for EOF before
exiting. A Docket worker would call `report_result`, the task would go `verifying`, and
the process would sit on the pipe until the next `docketd` restart.

The user-guide line "Headless mode does not read piped stdin into the prompt" is true
and incomplete. Codex/OpenCode block *before* the first turn; Grok blocks *after* it.

`stdin: closed` costs the dead-man switch. Grok never needed it to start; it needed it
to stop. Isolation (`sleep | grok`) waits for EOF before exiting; under `docketd` a
worker has been observed to exit after the turn anyway. The profile still closes stdin
so we do not depend on that, and so `stop.mode: signal` stays legal.
`RealGrokCollaborationTests` keeps both halves: a deadman fact that asserts the session
ref still arrives (Grok is not Codex-shaped), and the closed-stdin facts that complete.

### The MCP wiring: a project file, `{worker_token}`

Grok has no `--mcp-config`. It **merges** MCP servers from `{cwd}/.grok/config.toml`
and `~/.grok/config.toml` (cwd wins on a name clash). Write only the docket
block into the work dir via `files[]` (the profile above). Auth, skills, plugins,
memory, and the operator's other MCP servers stay in `~/.grok`.

Do **not** set `GROK_HOME` unless the operator asked for a sealed home. That
replaces the whole directory, not just MCP.

Both `{mcp_url}` and `{worker_token}` are docketd `files[]` substitutions (§13),
written **verbatim** into the file. Grok does **not** expand `${DOCKET_WORKER_TOKEN}`
(or any `${ENV}`) in `config.toml` — an earlier `${…}` bearer produced an empty
Bearer and a plane 401, the same silent toolless-agent failure as OpenCode. So the
bearer must be the concrete `{worker_token}`, exactly as Claude's `mcp.json` embeds
it. Because the file then carries a live token, write it `"mode": "600"` (owner-only),
matching Claude's `mcp.json`. If a future grok gains a bearer-from-env field (as Codex
has `bearer_token_env_var`), prefer that — it keeps the token off disk.

Tool names are `server__tool` → **`docket__get_task`**. A prompt that says
`mcp__docket__get_task` or `docket_get_task` sends the agent hunting a tool it does
not have.

### Do not pick `streaming-json`

That is the ACP shape (`tool_call` / `end`) — and as of `protocol: acp` below, that is
no longer a dead end so much as the wrong half of a choice: read Grok's ACP output by
speaking ACP to it, not by pointing a stream profile's mapping at it. The Claude
defaults read nothing from that shape — no session ref, no progress clock — and
`GrokStreamMappingTests` pins the miss. A **stream** profile uses
`streaming-messages-json`, as the one above does.

`--resume {session_id}` works from a different cwd (confirmed 2026-08-14). Never
`-c/--continue`: that is "latest session in this directory."

## Protocol: `acp` (§10)

> **Status: capabilities measured against real agent binaries on 2026-08-15; sessions not
> run.** The `initialize` handshake below was driven against each agent for real and the
> capability table is what they answered. What was *not* done here is an authenticated
> session — no provider credentials were available — so `session/prompt`, tool-call
> reporting and `session/load` are still spec-and-test claims rather than observed ones.
> `Docket.Runner.Tests/AcpClientTests` pins docketd's whole half of the conversation against
> a scripted peer. The `RealOpenCode`/`RealCodex`/`RealClaude` opt-in tiers are where the
> rest gets confirmed.

### The one thing worth measuring first, measured

`loadSession` decides whether §11 resume survives the migration, and it defaults to
**false** in the spec — so the honest expectation was that some harnesses would lose
`preserve`/`preserve_and_park`. They do not. Every agent answers `initialize` like this:

| Agent | Entry point | ver | `loadSession` | `mcp.http` | Auth |
|---|---|:--:|:--:|:--:|---|
| Claude Agent 0.68.0 | `claude-agent-acp` (adapter) | 1 | ✅ | ✅ | ambient (`authMethods: []`) |
| Claude Code 0.16.2 | `claude-code-acp` (**deprecated**) | 1 | ✅ | ✅ | `claude /login` |
| Codex 1.3.0 | `codex-acp` (adapter) | 1 | ✅ | ✅ | API key or ChatGPT |
| OpenCode 1.18.18 | `opencode acp` (native) | 1 | ✅ | ✅ | `opencode auth login` |
| Grok Build | `grok agent stdio` (native) | ? | ? | ? | `XAI_API_KEY` / `grok login` |

All four measured agents also declare `sessionCapabilities` well beyond the base spec —
`resume`, `fork`, `list`, `close`, and on the two newest `delete` and
`additionalDirectories`. Nothing here uses them yet; a §11 fork/chain is the obvious future
customer.

Two things to take from the table. **Every agent negotiates protocol version 1**, not 2 —
so 1 is what this client actually speaks, and negotiating it is deliberately not warned
about (a warning that fires on every task is one an operator learns to skip). And
`@zed-industries/claude-code-acp` is **deprecated**, renamed to
`@agentclientprotocol/claude-agent-acp`; use the new one, which is also the only agent that
needed no interactive login.

Grok's row is unmeasured: its installer resolves releases through the GitHub API, which this
environment blocks. `grok agent stdio` is documented as its ACP entry point, so the profile
below is written from that and marked accordingly.

### Installing the adapters

Two of the five entry points are adapters and have to be on every machine that runs the
profile, alongside the harness itself:

```bash
npm install -g @agentclientprotocol/claude-agent-acp   # NOT @zed-industries/claude-code-acp
npm install -g @agentclientprotocol/codex-acp
```

OpenCode and Grok need nothing extra — their ACP server is a subcommand of the CLI you
already installed. Pin the versions the way you pin the harnesses: an adapter is a second
upstream between `docketd` and the model, and it moves on its own schedule.

The four worked examples above are all the same exercise: read a vendor's NDJSON, guess
which key holds the session id, discover the hard way that a counter is nested one level
deeper than the last vendor put it. `protocol: acp` is the alternative — the agent speaks a
standard, so the shapes are in a spec instead of in your `events.mapping`.

**What changes, key for key:**

| Stream mode | ACP mode |
|---|---|
| prompt in the `spawn` argv | `prompt`, sent as `session/prompt` |
| `events.source` + `events.mapping` (up to 13 keys for OpenCode) | nothing — tool calls arrive as `session/update` notifications with fixed field names |
| session ref scraped from a log line | `session/new` returns `sessionId` as a JSON-RPC result |
| `resume.args`, a whole second spawn | `session/load` on the connection already open |
| `{mcp_config}` file, or a `files[]`-written `config.toml`/`opencode.json` carrying a live token | `mcpServers` handed over on `session/new`, bearer header and all — no file, no token on disk |
| `stop.mode: signal`, i.e. a tree-kill | `session/cancel`, which the agent is specified to honour |
| `stdin: closed` for harnesses that block on the pipe | not applicable — stdin is the request channel |

Six keys are **refused** on an ACP profile rather than ignored: `events.source` (other than
`none`), `events.mapping`, `resume.args`, `stop.mode: message`, `stdin: closed`, and a
missing `prompt`. Every one of them describes something ACP replaces, and an operator
porting a stream profile will carry them over by hand — a key that looks like a knob and
moves nothing is the failure mode this file keeps returning to.

**The dead-man's switch survives, and for once the spec agrees with us.** ACP's stdio
transport defines shutdown as *"the client terminates the subprocess after closing stdin"* —
which is exactly what `stdin: deadman` already means. The held write end says `docketd` is
alive; its EOF says `docketd` is gone. So an ACP profile keeps the cooperative kill for
free, and the whole `stdin: closed` trade-off that Codex and OpenCode force in stream mode
simply does not arise: an ACP agent reads stdin as a protocol, not as a prompt, so the
blocking read that caused it never happens.

### The prompt is the only harness-specific text left

One thing ACP does **not** standardize: what the agent calls docket's MCP tools. ACP is the
client↔agent channel; tool naming belongs to the agent↔MCP one, so each harness keeps its
own spelling and each `prompt` below differs only in that.

| Harness | Docket tool spelling |
|---|---|
| Claude, Codex | `mcp__docket__get_task` |
| OpenCode | `docket_get_task` |
| Grok | `docket__get_task` |

Everything else in these four profiles is the same profile.

### Worked example — OpenCode over ACP

Native (`opencode acp`), so no adapter. This is the reference ACP profile; the three that
follow differ only in the spawn argv and the tool spelling.

```jsonc
{
  "machine": { "work_root": "/var/lib/docketd/work" },
  "profiles": [
    {
      "name": "default",
      "protocol": "acp",
      // `opencode acp` starts OpenCode as an ACP agent over stdio. NOT `opencode run` —
      // that is the stream-mode command, and pointing an acp profile at it produces a
      // worker that never answers `initialize`. docketd reports exactly that, per task.
      "spawn": ["opencode", "acp", "--model", "anthropic/claude-haiku-4-5-20251001"],
      // The opening turn, on the wire instead of in the argv. Note the tool names are
      // still harness-specific: ACP standardizes the CLIENT-agent channel, not the
      // agent-MCP one, so OpenCode still spells docket's tools `docket_<name>`.
      "prompt": "You are a Docket worker running headless under docketd. You have been dispatched exactly one task. First call the docket_get_task MCP tool to read your assignment (namespace, description, completion_criteria, workspace, attempt). Do the work inside the assigned workspace. When done, call docket_report_result with a reference to where the work lives (a branch/commit/URL) — not the work itself. If you are blocked or a decision is above your scope, call docket_request_input instead of guessing.",
      // The wake-up turn, sent when there is new input on the assignment (an answered
      // question). Configuration, never content: the answer is pulled by the worker over
      // MCP, and that pull is the read receipt (§11).
      "follow_up": "There is new input on your assignment. Call docket_get_task to read it, then continue.",
      // No events block: ACP is the event source. No resume block: resume is session/load.
      // No stdin key: deadman is correct and `closed` is refused.
      "stop": { "mode": "signal", "wind_down_seconds": 30 },
      "logs": { "capture": true }
    }
  ]
}
```

Note what is **not** here: no `--format json`, no `--auto`, no thirteen-key `events.mapping`,
and no `--session` resume argv. Compare against the `opencode run` profile above — that is
the same harness, doing the same work.

**And no MCP file of any kind**, which is worth separating from the rest. `profiles[].env`
and `files[]` (#112 G2/G3) already solved the per-dispatch config problem for stream
profiles — a Codex, OpenCode or Grok profile can now write its own `config.toml` into the
work dir with a real `{worker_token}` in it, and the Grok example above does exactly that.
So the ACP win here is no longer "this is the only way to wire MCP per dispatch"; it is
narrower and still real: the server is a **session parameter**, so there is no file to
write, no mode to get right, and **no live bearer token sitting on disk** for the length of
the task. `files[]` closed the capability gap; ACP removes the artifact.

### Worked example — Claude Code over ACP

Needs the adapter, and specifically the **renamed** one: `@zed-industries/claude-code-acp`
still works but is deprecated. Claude Agent 0.68.0 is also the only agent measured that
declared no auth methods at all — it uses whatever credentials the machine already has, so
there is no interactive login step in the enroll path.

```jsonc
{
  "profiles": [
    {
      "name": "default",
      "protocol": "acp",
      // The adapter, not `claude`. It spawns claude itself.
      "spawn": ["claude-agent-acp"],
      "prompt": "You are a Docket worker running headless under docketd. You have been dispatched exactly one task. First call the mcp__docket__get_task MCP tool to read your assignment (namespace, description, completion_criteria, workspace, attempt). Read the docket-worker skill. Do the work inside the assigned workspace. When done, call mcp__docket__report_result with a reference to where the work lives (a branch/commit/URL) — not the work itself. If you are blocked or a decision is above your scope, call mcp__docket__request_input instead of guessing. You do not verify or complete the task yourself.",
      "follow_up": "There is new input on your assignment. Call mcp__docket__get_task to read it, then continue.",
      // Model and turn caps are the adapter's business, not a docketd key — it reads the
      // same environment claude does. `--max-turns` has no ACP equivalent, so on this
      // profile the bound is the model plus the §10 no-progress ceiling. See the cost note.
      "env": { "ANTHROPIC_MODEL": "claude-haiku-4-5-20251001" },
      "stop": { "mode": "signal", "wind_down_seconds": 30 },
      "telemetry": { "otel": true, "env": { "CLAUDE_CODE_ENABLE_TELEMETRY": "1" } },
      "logs": { "capture": true }
    }
  ]
}
```

Note what this profile does *not* need, against the `claude -p` one at the top of this file:
no `--permission-mode bypassPermissions` (permissions arrive as
`session/request_permission`), no `--output-format stream-json --verbose` pair, no
`--mcp-config`, no `--allowedTools` list, and no `resume.args`. The single most important
line in the stream profile — the bypass flag — has no counterpart here because the decision
moved onto the protocol.

### Worked example — Codex over ACP

Needs `@agentclientprotocol/codex-acp`. This is the profile that changes most, because
almost everything hard about the stream-mode Codex profile was a workaround for something
ACP simply has.

```jsonc
{
  "profiles": [
    {
      "name": "default",
      "protocol": "acp",
      "spawn": ["codex-acp"],
      "prompt": "You are a Docket worker running headless under docketd. You have been dispatched exactly one task. First call the mcp__docket__get_task MCP tool to read your assignment. Do the work inside the assigned workspace. When done, call mcp__docket__report_result with a reference to where the work lives (a branch/commit/URL) — not the work itself. If you are blocked or a decision is above your scope, call mcp__docket__request_input instead of guessing.",
      "follow_up": "There is new input on your assignment. Call mcp__docket__get_task to read it, then continue.",
      // codex-acp authenticates from the environment (API Key) or a cached ChatGPT login,
      // both of which it declares as authMethods at initialize.
      "env": { "CODEX_API_KEY": "{env:CODEX_API_KEY}" },
      "stop": { "mode": "signal", "wind_down_seconds": 30 },
      "logs": { "capture": true }
    }
  ]
}
```

**Four stream-mode problems that stop existing.** `stdin: closed` — gone, and this is the
sharpest reversal in the file: `codex exec` blocks forever on a held-open stdin pipe, which
is why its stream profile *must* close stdin and give up the dead-man's switch, whereas
`codex-acp` reads stdin as a protocol and keeps the switch. The mandatory six-key
`events.mapping` — gone. The static `~/.codex/config.toml` with `bearer_token_env_var` —
gone, along with its side effect of declaring a docket MCP server for every interactive
`codex` on the machine. And `resume.args` — gone; `codex-acp` declares `loadSession: true`.

### Worked example — Grok Build over ACP

> **Unmeasured.** Every other row in the capability table was driven for real; Grok's
> installer resolves releases through the GitHub API, which this environment blocks, so
> `grok agent stdio` here is from xAI's documentation rather than from a handshake. Run the
> probe against it before trusting this profile — in particular, confirm `loadSession`, and
> confirm the agent does not want a client-side `terminal/*` (Grok's ACP integration is
> documented as implementing client-side `terminal/`, `fs/` and `request_permission`, which
> is a strong hint that it may **expect** its client to provide them — see the caveat below).

```jsonc
{
  "profiles": [
    {
      "name": "default",
      "protocol": "acp",
      // `grok agent stdio`, NOT `grok -p --output-format streaming-json`. The latter is an
      // output shape that merely resembles ACP; the former is the protocol.
      "spawn": ["grok", "agent", "stdio"],
      "prompt": "You are a Docket worker running headless under docketd. You have been dispatched exactly one task. First call the docket__get_task MCP tool to read your assignment (namespace, description, completion_criteria, workspace, attempt). Do the work inside the assigned workspace. When done, call docket__report_result with a reference to where the work lives (a branch/commit/URL) — not the work itself. If you are blocked or a decision is above your scope, call docket__request_input instead of guessing.",
      "follow_up": "There is new input on your assignment. Call docket__get_task to read it, then continue.",
      // 1.0.4+ gates project-local config behind folder trust and a work dir is a
      // throwaway folder. Carried over from the stream profile; re-confirm under ACP.
      "env": { "GROK_FOLDER_TRUST": "0" },
      "stop": { "mode": "signal", "wind_down_seconds": 30 },
      "logs": { "capture": true }
    }
  ]
}
```

If Grok turns out to require a client-side terminal, it cannot run under this client today
and stays on its `streaming-messages-json` stream profile — which is a perfectly good
profile and the only one of the four whose stream mapping needed no `events.mapping` at all.

### The caveat that could make an ACP worker useless

This client declares the ACP `fs` and `terminal` capabilities **UNSUPPORTED**. Those exist
so an editor can hand an agent its unsaved buffers and its terminal panel; a Docket worker
has its own work dir and its own shell, so an agent doing that I/O itself is the
arrangement, not a degradation. All three measured agents carry their own tools.

But an agent that routes *all* its shell and file access through the client would, under
that declaration, be unable to do anything — and the symptom is a task that starts, calls
no tools, and reports nothing, which reads exactly like a lazy model. So a refused request
is reported, once per method per task:

```
docketd: task <id>: the agent asked docketd to perform 'terminal/create' and was refused —
this client declares the ACP fs and terminal capabilities UNSUPPORTED […] check whether this
harness needs a client-side terminal (§10).
```

If you see that line, the harness needs a client-side terminal and this profile will not
work until docketd grows one. It is the first thing to look for when an ACP worker does
nothing.

### What `wind_down_seconds` means here

More than it did. A stream-mode stop writes a turn nobody reads (or, honestly, declares
`signal` and skips the pretence), so the wind-down was only ever the gap before the kill.
An ACP stop sends `session/cancel` first, the agent is specified to stop its model requests
and tool calls and end the turn with a `cancelled` stop reason, and *then* the deadline
kills if it did not. The ack still says only that the cancel was **sent** — cancel is a
notification with no reply, so nothing on this side can honestly claim it was honoured —
but for the first time the thing being reported is a mechanism the agent is obliged to
implement.

### Resume, and the one capability to check

§11 resume is `session/load` on the live connection: no respawn, no argv, and no replay
cost paid twice. It is gated on the agent's `loadSession` capability, which **defaults to
false** in the spec. An agent that does not declare it cold-starts, and `docketd` says so
per task rather than letting a resume quietly become one:

```
docketd: task <id>: the plane handed back a resume ref but this agent does not declare the
ACP 'loadSession' capability, so the transcript cannot be reloaded and this dispatch is a
COLD START. Every redispatch of this task will be one (§11).
```

This is the single most important thing to verify against a real agent before moving a
production profile to ACP — `preserve` and `preserve_and_park` are worth much less without
it.

### Cost bounds get weaker, and this one is not a footnote

Stream profiles bound a runaway with harness flags on the argv: `claude -p` has
`--max-turns` and `--max-budget-usd`, `grok -p` has `--max-turns`. **ACP has no equivalent
for either**, and the adapters expose no flag surface at all — they are stdio servers, not
CLIs (`--help` on `claude-agent-acp` and `codex-acp` just waits on stdin). What survives is
the pinned model, via the harness's own environment variable, and the §10 no-progress
ceiling.

For `codex exec` and `opencode run` that is no change — neither ever had a turn cap. For
`claude -p` and `grok -p` it is a real loss: those two profiles go from a bounded runaway
to an unbounded one. Weigh that per profile before migrating, and note it compounds with
the usage gap below — you lose the cap and the meter in the same move.

### Two things this increment does not do yet

**Permissions are auto-allowed, not bridged.** ACP delivers a permission request
structured, with the agent's own options attached, which is a far better fit for §11's
permission bridge than the per-harness prompt-tool it was built on. This increment answers
with the agent's own always-allow option — a like-for-like port of what
`bypassPermissions`/`--auto` buy a stream profile, chosen so the protocol change is not
also a silent policy change. Routing it into the plane is the next increment.

**Token accounting is not carried over.** ACP's accepted usage surface (`usage_update`) is
context-window utilization plus an optional cumulative cost, not the four disjoint token
buckets `UsageReportedEvent` carries today, and per-turn token accounting is still a
separate RFD. So an ACP profile currently reports **no usage at all** — it does not report
zeros, and it does not fabricate buckets from a context gauge. Reshaping the measured view
around `used`/`size`/`cost` is tracked separately; until then, a profile whose spend you
need to see should stay on `stream`.

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
worker's plane token stays task-scoped (§5), and time is bounded by the
no-progress ceiling (§10) — but the machine-local exposure is the
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
> anything else has no progress signal, so the no-progress ceiling (30 minutes by
> default) is the only clock governing its tasks.**
>
> Per-task liveness on the control plane is refreshed *only* by an inbound event —
> `started`, `session-started`, `alive`, `tool-call`, or `subagent-spawned`. A
> non-`terminal` profile emits three of those: `started` at spawn, `exited` at the
> end, and the periodic `alive` that `docketd` sends for every live process on its
> own heartbeat timer. **`alive` is not gated on `events.source`**, so the short
> aliveness window (**60s by default** — `Docket:PerTaskLivenessWindow` on the control
> plane, not anything a runner config declares) stays satisfied and such a task is *not*
> requeued merely for being quiet.
>
> What it loses is `tool-call`, and with it the progress clock. Work that
> legitimately runs longer than the no-progress ceiling without a tool call is
> requeued, and until then a wedged agent cannot be told from a busy one. Such
> requeues are capped (§9 check 7 — 5 by default): at the cap the task is abandoned
> as `canceled` with the reason that reclaimed it on the record and its workspace
> preserved, rather than being redispatched forever.

`source` selects how `docketd` observes a worker's progress. The four values it
accepts are not four implementations:

| `source` | Status | What you get |
|---|---|---|
| `terminal` | **Implemented** — the only consumer is the stdout drain | `started`, `session-started` (the resume ref), `tool-call` per tool use, `exited`. Liveness refreshes on every well-formed line. |
| `hooks` | Parses, wired to nothing | `started` + `alive` + `exited` — identical to `none` |
| `otel` | Parses, wired to nothing | `started` + `alive` + `exited` — identical to `none` |
| `none` | Honest declaration of no stream | `started` + `alive` + `exited` |

So the choice is really binary: either the harness streams structured output on
stdout that `docketd` can read, or this profile has no progress signal at all.
`docketd` prints a warning at startup for every profile that declares a
non-`terminal` source, naming exactly that cost — such a profile is degraded, not
unusable: its tasks survive on the `alive` signal, but nothing distinguishes a hung
agent from a busy one, and work quiet for longer than the no-progress ceiling is
reclaimed.

**The §10 process-alive promise is kept.** §10 says liveness for a source-less
profile "degrades to process-alive," and that is now wired: `docketd` emits the
frozen-wire `alive` event for each supervised live process on its heartbeat timer,
which is the one channel by which a fact only the runner can observe reaches the
plane. The machine heartbeat is machine-scoped and refreshes no task's clock, and a
worker's own MCP calls refresh nothing either — so `alive` is what makes `none` an
honest declaration about its cost as well as its coverage.

**`mapping` overrides stream *property names*, and — in flat mode — one event
type.** It does not map `PostToolUse` → `tool-call`; there is no hook-name seam. The
eleven property-name keys, with the built-in defaults in parentheses, are `type_key`
(`type`), `system_type` (`system`), `assistant_type` (`assistant`), `subtype_key`
(`subtype`), `init_subtype` (`init`), `session_id_key` (`session_id`),
`message_key` (`message`), `content_key` (`content`), `block_type_key` (`type`),
`tool_use_block_type` (`tool_use`), and `tool_name_key` (`name`). Two further keys —
`tool_event_type` and `tool_name_path` — describe a harness that emits one flat event
object per tool call, which renaming alone cannot express; they have their own
subsection below.

Those defaults already describe `claude -p --output-format stream-json`, which is
why the worked example above declares `"events": { "source": "terminal" }` with no
`mapping` at all. Supply keys only for a harness whose stream uses different names
— that is a config change, not a code change, which is the point of the seam.

**Flat tool-call mode — for a harness that emits one event object per tool call.** The eleven
keys above all rename *within* claude's nesting: an assistant turn wrapping an **array** of
content blocks, one of which is the tool call. A harness that emits one object per tool call
instead — Codex's `{"type":"item.started","item":{…}}` — cannot be reached by renaming, because
a rename cannot change nesting or arity. Two more keys describe that shape directly:

| key | default | meaning |
|---|---|---|
| `tool_event_type` | unset | the `type_key` value of a line that **is** one tool call |
| `tool_name_path` | unset | dotted path from the line root to the string naming the tool (`item.tool`) |

Declare **both** or neither — either alone is inert, and `docketd` fails the config load rather
than accept the half. The emitted event is the same `tool-call` the block-array mode produces;
nothing about the wire contract changes. Rules worth knowing before you write a path:

- **Segments are object property names, split on `.`, and the walk must end on a JSON string.**
  There are no wildcards, no array indexes, no filters, and no escape for a property name that
  itself contains a dot. `items.0.tool` reads a property literally named `0`, finds none, and
  emits nothing. An empty or space-padded segment (`item..tool`, `item . tool`) is a config-load
  error, not a silent miss.
- **Comma-separated alternatives are tried in declaration order; the first that resolves to a
  non-empty string wins.** One harness commonly spells the name differently per tool kind —
  Codex uses `item.command` for a shell call and `item.tool` for an MCP call — and a single path
  would silently drop the other half.
- **At most one `tool-call` per line.** That is what "flat" means; a line where no path resolves
  emits nothing.
- **That absence is the item-kind filter.** Codex's non-tool items (`agent_message`,
  `reasoning`) arrive under the same `item.started` type and carry neither `command` nor `tool`,
  so they are excluded by not matching. There is deliberately no extra "which item kind is a
  tool" key.
- **`tool_event_type` must differ from the effective `system_type` and `assistant_type`.** One
  line cannot be both the session-init line and a tool call; the collision is a load error.
- **The two modes coexist.** They key off different `type` values, so a profile may declare both.

Pick the event that fires when a tool call *starts*, not when it completes, if the harness
offers both. `tool-call` drives the progress clock, so `item.started` reports a 25-minute build
at minute zero where `item.completed` reports it 25 minutes late — which is most of the
no-progress ceiling spent looking wedged.

**Unrecognized keys are silently ignored.** Each key falls back to its default
independently, with no error at load, so a typo or a leftover hook-name mapping
produces a profile that parses cleanly and reports nothing. If you write a
`mapping`, verify against a real run rather than against the config.

**A `terminal` profile that reads its stream to no effect now says so.** If a worker's stdout
parsed as one or more JSON event lines but the mapping extracted neither a session ref nor a
tool-call over the whole task, `docketd` writes one line per task to its log at that worker's
exit, naming the task, how many lines it read, whether a `mapping` was declared at all, and what
the silence cost (no ref to resume from, no progress signal). It is the only signal that
distinguishes "this harness needs a `mapping`" from "this harness is quiet" — a stream that
carried no parseable event line at all is *not* reported, since that is a harness not streaming
JSON or a worker that died early, which its exit code and transcript already show. A profile
with `source: none` never reads a stream and is never warned about one; it gets the separate
startup warning above instead.

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

**`format` and `path` are gone.** Both were documented for a never-built
"tail-and-stream": `format` was an advisory label for the stdout stream's shape and was
never acted on, and `path` could not influence anything once capture settled on the fixed
state-dir layout above. Neither was ever read, so both have been removed rather than kept
as knobs that move nothing. A config still declaring either is accepted unchanged —
unknown keys are ignored — and behaves exactly as it already did.

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
