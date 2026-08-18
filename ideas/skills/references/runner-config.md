# `landbridged` runner config — schema and worked profiles

`landbridged` contains no harness knowledge; everything specific is data (spec §10). This is the
reference the enroll skill (`landbridge-enroll`) and spec §10 point at: the config schema plus a
worked profile for each supported harness.

**Every worker is driven over the [Agent Client Protocol](https://agentclientprotocol.com).**
`landbridged` spawns the agent, then speaks JSON-RPC to it over stdin/stdout: `initialize`, a
session, and the profile's `prompt` as the opening turn. There is no second mode. The
protocol that preceded it — spawn the CLI with its prompt in the argv, read whatever NDJSON
it prints, and describe that vendor's shape in an `events.mapping` of up to thirteen keys —
is gone, along with the four per-harness recipes that documented it. What replaced them is
one shape and an entry point per harness.

## Schema

| Section | Field | Notes |
|---|---|---|
| `machine` | `work_root` | Per-task scratch dirs; `landbridged` spawns each task in `{work_root}/{session_id}` (§10). Not the workspace. |
| `machine` | `heartbeat_seconds` | Machine-liveness cadence, in seconds (§10); default `15`. |
| `machine` | `back_pressure` | `max_cpu_load` / `max_memory_load` / `max_disk_usage` in [0,1]; defaults `0.90` / `0.90` / `0.95`, tune per box (§10). CPU is not yet observed cross-platform, so `max_cpu_load` is currently inert — memory and disk carry the signal (§10). |
| `profiles[]` | `name` | Profile identifier. `profiles` is a JSON **array**; exactly one entry MUST be named `default` (§10). |
| `profiles[]` | `spawn` | argv passed to `execve` — **never a shell** (§10). Substitutions below. |
| `profiles[]` | `follow_up` | The turn sent to **wake a live session** when there is new input on the assignment — an answered question today, a Lead's message later (`ideas/sessions.md`). **Configuration, never content**: the input itself stays on the assignment and is pulled by the worker's own authenticated `get_session`, which is what makes that read a *receipt*. Pushing the text here instead would reduce delivery to "queued", put answer content on a second path out of the MCP channel, and mix per-message content into profile config. Name the landbridge tools the way *this* harness spells them; the default names none. |
| `profiles[]` | `prompt` | The worker's opening turn, sent as `session/prompt`. **Required**: an ACP agent takes no prompt on argv, so a profile without one spawns an agent that completes the handshake, waits, and does nothing. No default is possible — the text has to name the landbridge tools the way *this* harness spells them. Same `{...}` substitutions as the spawn argv. |
| `profiles[]` | `auth_method` | Which of the agent's declared ACP `authMethods` to use. Consulted **only** when the agent refuses `session/new` with `-32000 "authentication required"` — a declared method means authentication is *available*, not required, so an agent already holding a credential is never authenticated. **Required on that refusal.** Unset is a fail, not a guess at the first declared method (that is often a browser login). `codex-acp` needs `"auth_method": "api-key"`. The request carries a method id and nothing else — the credential is the agent's own business, read from its environment (see `env`), so this key never holds a secret. `claude-agent-acp` declares no methods and needs none. |
| `profiles[]` | `config_options` | String map sent as ACP `session/set_config_option` after `session/new` (or `session/load`). Each key is a `configId` the agent advertised on that session; the value must be one of that option's listed values. An unadvertised key, or a value the agent did not list, is skipped — not an error. OpenCode ACP defaults to `opencode/big-pickle` and ignores `opencode.json`, so this is how you pin `"model": "anthropic/claude-haiku-4-5-20251001"`. Leave it unset on an agent that advertises nothing (`claude-agent-acp`, and so far `codex-acp`). Strings only: boolean ACP options require a client capability this client does not declare. |
| `profiles[]` | `session_mode` | ACP `session/set_mode` after `session/new` (or `session/load`). Sent only when that session advertised the `modeId`. Goose 1.46 defaults to `auto` (auto-approve); pin `"approve"` so permissions stay on `session/request_permission`. Unadvertised is skipped, not an error. |
| `profiles[]` | `stop` | `wind_down_seconds` (default `30`) — the window an agent gets to end its turn after `session/cancel` before the portable tree-kill backstops it. No `mode` and no `message`: a stop is a cancel the agent is *specified* to honour, so there is nothing left for a mode to select. `ttl=0` kills immediately (§9 check 12). |
| `profiles[]` | `env` | String map stamped on every spawn (and resume) of this profile. Values take the same `{session_id}` / `{machine_id}` / `{work_dir}` / `{harness_session_ref}` / `{mcp_url}` / `{worker_token}` substitutions `spawn` does. Applied after the reserved `LANDBRIDGE_*` stamps and before `telemetry.env`. The four names landbridged owns — `LANDBRIDGE_MACHINE_ID`, `LANDBRIDGE_SESSION_ID`, `LANDBRIDGE_WORKER_TOKEN`, `LANDBRIDGE_TRACEPARENT` — are **refused at load**, not silently dropped. Use this to isolate a home (`GROK_HOME` / `CODEX_HOME`) only when the operator asked for a sealed box. Prefer `files[]` for additive project-local MCP. |
| `profiles[]` | `files` | Files written into `{work_dir}` **before** the harness starts (#112 G2). Each entry is `path` + `contents` (both substituted) and optional `mode` (octal, default `0600`). A relative path is resolved against the work dir (so `.grok/config.toml` and `{work_dir}/.grok/config.toml` land in the same place). After substitution the path must stay under the work dir — `..` that escapes fails the spawn. Parent directories are created. This is how a Grok profile drops `{work_dir}/.grok/config.toml` so Grok **merges** landbridge with `~/.grok` instead of replacing it. |
| `profiles[]` | `hooks` | Argv hooks, **never a shell** (§10). `before_spawn` runs after `files[]` and before `Process.Start`; non-zero or timeout (10s) is fail-closed (`spawn_failed`). `after_exit` is best-effort after the worker's `exited` and stray reap, skipped for superseded instances. Hook processes get `LANDBRIDGE_MACHINE_ID`, `LANDBRIDGE_HOOK`, and the same `profiles[].env` map the worker does (minus reserved `LANDBRIDGE_*`), not `LANDBRIDGE_SESSION_ID` / `LANDBRIDGE_WORKER_TOKEN`. Use only when the harness will not read a project-local file (Codex / `CODEX_HOME`). |
| `profiles[]` | `telemetry` | `otel` bool (opt-in, default **false**), `endpoint` (OTLP destination; falls back to the one landbridged inherited), and `env` (a string map of harness-specific variables, applied verbatim). When on, landbridged sets the vendor-neutral `OTEL_*` exporter variables and appends `landbridge.session_id`/`landbridge.machine_id` to `OTEL_RESOURCE_ATTRIBUTES`, so the harness's own token/cost telemetry is attributable per task (§10). `otel: true` with **no endpoint configured and none inherited sets nothing at all** and warns once — telemetry is never enabled without a destination. Claude Code additionally needs `"env": { "CLAUDE_CODE_ENABLE_TELEMETRY": "1" }` (its own flag is data, since landbridged holds no harness knowledge). **Visibility only**: Landbridge ingests none of it and enforces no ceiling — see [docs/TELEMETRY.md](../../../docs/TELEMETRY.md). |
| `profiles[]` | `logs` | §12 machine-local transcript capture: `capture` (bool, default **false**), `max_bytes` (per-stream cap, default 50 MiB), `prune_after_days` (local hygiene, default 7, `0` disables). There is no `format` or `path`: both were read by nothing and have been removed, and a config still carrying either is accepted unchanged — see [Transcript capture](#transcript-capture-12) below. |
| `profiles[]` | `max_concurrent` | Optional hard cap for a licence/rate/posture reason, unrelated to load (§10). |
| `profiles[]` | `processes` | §10 agent-started **processes**: `agent_initiated` (bool, default **false**) and `max` (default 8). Named `processes`, not `services` — they are different things (§10). Whether a task on this profile may call `start_process`, and how many the machine may hold. |
| `services[]` | — | Optional: long-lived processes `landbridged` supervises as its own children. See [Operator-declared services](#operator-declared-services-10) below. |

## Operator-declared services (§10)

A worker that starts `npm run dev` from its own shell loses it the moment its session ends
— the service is inside the session's process tree, which is tree-killed, and it carries
`LANDBRIDGE_*`, which the stray reaper matches. For a service that must outlive the session
using it, declare it here and `landbridged` supervises it as **its own child**, outside
every session's tree:

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
the session tree-kill does not reach it, and it is tagged with `LANDBRIDGE_MACHINE_ID` but
deliberately **not** `LANDBRIDGE_SESSION_ID` — so the restart sweep (keyed on machine id) reaps
the previous generation when `landbridged` restarts, while per-session exit cleanup (which
requires a matching session id) steps over it. It escapes the session's lifetime while
staying inside Landbridge's kill guarantee, on every OS, with no `setsid` and no environment
scrubbing. The worker skill forbids the other route to the same effect for exactly that
reason.

**Names and ports must both be unique on a machine.** Names because they are identifiers
(and directory names); ports because a forward dial is resolved to a service *by port*, so a
shared port would make that lookup answer for whichever service came first, and the resulting
refusal would make no sense from the consumer's side. `landbridged` rejects either at config load
and names both offenders — it prints the problem and exits non-zero before connecting, so this
is caught at start rather than at the first dial.

**`readiness` is a real check.** The port must accept a connection before the service is
reported `running`. That is what a holder task waits for before calling
`register_service` (§8.2), and what lets `landbridged` refuse a forward dial for a service
that is down instead of connecting to whatever else may hold the port.

**Restart, not re-adopt.** On `landbridged` restart every service is killed and started
again from config; there is no PID registry and no attempt to inherit survivors. Absolute
paths in `spawn` and an explicit `env` matter for the same reason they do under a system
service manager: the service gets `landbridged`'s environment, not your shell's.

**`backend`** is `direct` today and a config naming anything else is refused rather than
quietly supervised the other way. Delegation to `systemd-run`/`pm2`/`docker` is a later
option, and it costs the property refuse-at-dial relies on: `landbridged` would no longer own
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
until something stops it or this `landbridged` restarts (a job). Same supervision, same machine
tagging, same stray-sweep bound.

Gate processes per profile with `processes.agent_initiated`; cap them with `processes.max`. Names
are unique across processes *and* services on a machine, checked at admission among live entries —
an exited process releases its name. **Ports are not part of a process at all**, and that is the
one place the two diverge sharply: a service declares a port and gets refuse-at-dial protection, a
process declares nothing and is invisible to it. If an agent's process listens on something that
is the agent's business, and reachability is a separate `register_service` call. Processes also
carry a start-time stdin choice (`open_stdin`, **default off**): without a pipe there is no
`write_process` and no graceful stop, which suits the fire-and-forget majority.

Worth knowing as the operator: **nothing reclaims a process when its session ends.** Cleanup
is the Lead's job — a message on the session that started it, or a later cleanup session —
and the Machine Group view is where you see what a machine is still holding. Expect more of
them than the "keep the dev server up" case suggests: the worker skill tells agents to run
*all* long work this way, because `start_process` is the only non-blocking route that works
on every harness, so ordinary builds and test runs land here too.

**Status, not logs, on the dashboard.** Each service's state, port, uptime, restart count
and last exit code ride the machine heartbeat to the §12 Machine Group view. The log
*contents* stay on the machine — serving them would be live tailing, which §16 open
question 8 defers. Read them on the box, under the state dir.

Services are not tasks: they never count toward `max_concurrent`, and the load they
consume is already visible to back-pressure. And they need a profile permissive enough to
be useful alongside — see the archetypes below.

## Spawn substitutions

`landbridged` substitutes these `{...}` tokens in each `spawn` arg and each
`profiles[].env` value (and injects the first three as environment on every
spawn, not configurably — §10):

| Token / env | Value |
|---|---|
| `{session_id}` / `LANDBRIDGE_SESSION_ID` | The dispatched Landbridge session id. |
| `{machine_id}` / `LANDBRIDGE_MACHINE_ID` | This machine's id. |
| `{work_dir}` | `{work_root}/{session_id}`, the spawn cwd. |
| `{mcp_config}` | Path to the generated MCP config `landbridged` writes to `{work_dir}/mcp.json` (mode 0600) — **only when this token appears in spawn or resume argv**. Prefer `files[]` + `{worker_token}` / `{mcp_url}` (below) for a new profile; this token is the Claude convenience that remains so existing argv keeps working. |
| `{mcp_url}` | The plane's public MCP URL (`Landbridge:PublicMcpUrl`). Filled by the plane on every dispatch so a `files[]` body can name the URL without parsing `mcp.json`. Also stamped on the worker as `LANDBRIDGE_MCP_URL`. |
| `{worker_token}` | The minted worker-instance token (`lbr_w_` + 64 hex). For a `files[]` body that must embed the bearer (Claude's `--mcp-config` does not expand `${LANDBRIDGE_WORKER_TOKEN}`). Same secret as `LANDBRIDGE_WORKER_TOKEN` on the spawn env. |
| `{harness_session_ref}` | The ACP harness resume token, when the plane has one. Resume itself is `session/load` on the connection, not an argv token. |
| `LANDBRIDGE_WORKER_TOKEN` | The minted worker-instance token (also `{worker_token}`). |

## The plane's MCP server, and how a worker gets it

At dispatch the control plane mints the worker's token and builds its MCP client config
(`DispatchService`). `landbridged` hands it to the agent as a **`session/new` parameter** —
ACP's `mcpServers`, one entry, HTTP transport, bearer header:

```json
{ "type": "http", "name": "landbridge",
  "url": "https://plane.example/mcp",
  "headers": [{ "name": "Authorization", "value": "Bearer lbr_w_<worker-instance-token>" }] }
```

The `url` is the plane's public MCP endpoint (`Landbridge:PublicMcpUrl` / `LANDBRIDGE_PUBLIC_MCP_URL`;
default `http://127.0.0.1:5000`). This is a worker's **only** channel to Landbridge (§5): it
authenticates as the dispatched instance, and its token dies with the instance (§9 check 14).

**Nothing is written to disk.** The stream protocol wrote this config into the work dir 0600
and substituted its path into the argv, because a CLI could only be handed a file. A session
parameter needs no file, no mode to get right, and leaves no live bearer token sitting in the
work dir for the length of the task.

`{mcp_config}` still exists as a substitution — a profile that writes its own config through
`files[]` or points a harness at one through `env` can reference it, and the file is written
only when something actually does (#112 G11). No profile in this document needs it.


## The protocol (§10)

> **Status: capabilities measured against real agent binaries on 2026-08-15; sessions not
> run.** The `initialize` handshake below was driven against each agent for real and the
> capability table is what they answered. What was *not* done here is an authenticated
> session — no provider credentials were available — so `session/prompt`, tool-call
> reporting and `session/load` are still spec-and-test claims rather than observed ones.
> `Landbridge.Runner.Tests/AcpClientTests` pins landbridged's whole half of the conversation against
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
| Goose 1.37.0 | `goose acp` (native) | 1 | ✅ | ✅ | `goose-provider` (available; `session/new` succeeded without it) |

The measured agents also declare `sessionCapabilities` well beyond the base spec —
`resume`, `fork`, `list`, `close`, and on the two newest `delete` and
`additionalDirectories`. Nothing here uses them yet; a §11 fork/chain is the obvious future
customer.

Two things to take from the table. **Every agent negotiates protocol version 1**, and
that is what this client offers — not 2, which would claim v2 shapes we do not
speak. And
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

OpenCode, Grok, and Goose need nothing extra — their ACP server is a subcommand of the CLI you
already installed. Pin the versions the way you pin the harnesses: an adapter is a second
upstream between `landbridged` and the model, and it moves on its own schedule.

The protocol this replaced was the same exercise four times over: read a vendor's NDJSON,
guess which key holds the session id, discover the hard way that a counter is nested one
level deeper than the last vendor put it. The agent speaks a standard now, so the shapes are
in a spec instead of in your config.

**What that removed, key for key** — none of the left column exists any more:

| Was | Is |
|---|---|
| prompt in the `spawn` argv | `prompt`, sent as `session/prompt` |
| `events.source` + `events.mapping` (up to 13 keys for OpenCode) | nothing — tool calls arrive as `session/update` notifications with fixed field names |
| session ref scraped from a log line | `session/new` returns `sessionId` as a JSON-RPC result |
| `resume.args`, a whole second spawn | `session/load` on the connection already open |
| `{mcp_config}` file, or a `files[]`-written `config.toml`/`opencode.json` carrying a live token | `mcpServers` handed over on `session/new`, bearer header and all — no file, no token on disk |
| `stop.mode: signal`, i.e. a tree-kill | `session/cancel`, which the agent is specified to honour |
| `stdin: closed` for harnesses that block on the pipe | not applicable — stdin is the request channel |

A config still declaring any of the left column **loads unchanged** — unknown keys are
skipped, so a machine never refuses to start on a file that worked yesterday — and every one
of them now means nothing. Delete them at leisure, not under an outage. The one key that is
*enforced* is `prompt`: without it a worker connects, waits, and does nothing, so its absence
fails the load.

**The dead-man's switch survives, and the spec agrees with it.** ACP's stdio
transport defines shutdown as *"the client terminates the subprocess after closing stdin"* —
which is exactly what `stdin: deadman` already means. The held write end says `landbridged` is
alive; its EOF says `landbridged` is gone. So an ACP profile keeps the cooperative kill for
free, and the `stdin: closed` trade-off that Codex, OpenCode and Grok each forced in their
own way simply does not arise: an ACP agent reads stdin as a protocol, not as a prompt, so
the blocking read that caused it never happens.

### The prompt is the only harness-specific text left

One thing ACP does **not** standardize: what the agent calls landbridge's MCP tools. ACP is the
client↔agent channel; tool naming belongs to the agent↔MCP one, so each harness keeps its
own spelling and each `prompt` below differs only in that.

| Harness | Landbridge tool spelling |
|---|---|
| Claude, Codex | `mcp__landbridge__get_session` |
| OpenCode | `landbridge_get_session` |
| Grok, Goose | `landbridge__get_session` |

Everything else in these profiles is the same profile. Goose's spelling is from its
extension naming (`{server}__{tool}` on the `landbridge` MCP server handed over at
`session/new`), not a live Landbridge turn — confirm on the first one.

### Worked example — OpenCode over ACP

Native (`opencode acp`), so no adapter. This is the reference ACP profile; the three that
follow differ only in the spawn argv and the tool spelling.

```jsonc
{
  "machine": { "work_root": "/var/lib/landbridged/work" },
  "profiles": [
    {
      "name": "default",
      // `opencode acp` starts OpenCode as an ACP agent over stdio. NOT `opencode run` —
      // that is the stream-mode command, and pointing an acp profile at it produces a
      // worker that never answers `initialize`. landbridged reports exactly that, per task.
      "spawn": ["opencode", "acp"],
      // `opencode acp` does not take `--model`. It defaults every session to
      // opencode/big-pickle and ignores opencode.json; the pin is ACP
      // session/set_config_option, which landbridged sends for each pair here that
      // the agent advertised.
      "config_options": { "model": "anthropic/claude-haiku-4-5-20251001" },
      // The opening turn, on the wire instead of in the argv. Note the tool names are
      // still harness-specific: ACP standardizes the CLIENT-agent channel, not the
      // agent-MCP one, so OpenCode still spells landbridge's tools `landbridge_<name>`.
      "prompt": "You are a Landbridge worker on a live session. First call the landbridge_get_session MCP tool to read your assignment (namespace, description, completion_criteria, workspace, attempt). Do the work inside the assigned workspace. When you think you are done, call landbridge_report_result with a reference to where the work lives (a branch/commit/URL) — not the work itself — and stay up; the Lead may reply. If you are blocked or a decision is above your scope, call landbridge_request_input instead of guessing. You do not complete the session yourself.",
      // The wake-up turn, sent when there is new input on the assignment (an answered
      // question). Configuration, never content: the answer is pulled by the worker over
      // MCP, and that pull is the read receipt (§11).
      "follow_up": "There is new input on your assignment. Call landbridge_get_session to read it, then continue.",
      // No events block: ACP is the event source. No resume block: resume is session/load.
      // No stdin key: deadman is correct and `closed` is refused.
      "stop": { "wind_down_seconds": 30 },
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
      // The adapter, not `claude`. It spawns claude itself.
      "spawn": ["claude-agent-acp"],
      "prompt": "You are a Landbridge worker on a live session. First call the mcp__landbridge__get_session MCP tool to read your assignment (namespace, description, completion_criteria, workspace, attempt). Read the landbridge-worker skill. Do the work inside the assigned workspace. When you think you are done, call mcp__landbridge__report_result with a reference to where the work lives (a branch/commit/URL) — not the work itself — and stay up; the Lead may reply. If you are blocked or a decision is above your scope, call mcp__landbridge__request_input instead of guessing. You do not complete the session yourself.",
      "follow_up": "There is new input on your assignment. Call mcp__landbridge__get_session to read it, then continue.",
      // Model and turn caps are the adapter's business, not a landbridged key — it reads the
      // same environment claude does. `--max-turns` has no ACP equivalent, so on this
      // profile the bound is the model plus the §10 no-progress ceiling. See the cost note.
      "env": { "ANTHROPIC_MODEL": "claude-haiku-4-5-20251001" },
      "stop": { "wind_down_seconds": 30 },
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
      "spawn": ["codex-acp"],
      "prompt": "You are a Landbridge worker on a live session. First call the mcp__landbridge__get_session MCP tool to read your assignment. Do the work inside the assigned workspace. When you think you are done, call mcp__landbridge__report_result with a reference to where the work lives (a branch/commit/URL) — not the work itself — and stay up; the Lead may reply. If you are blocked or a decision is above your scope, call mcp__landbridge__request_input instead of guessing. You do not complete the session yourself.",
      "follow_up": "There is new input on your assignment. Call mcp__landbridge__get_session to read it, then continue.",
      // codex-acp refuses session/new until ACP `authenticate` has run, and declares two
      // methods: `api-key` (from the environment) and `chat-gpt` (a cached login). The
      // method is required on the profile — guessing the first declared one is how a
      // headless worker ends up in a browser. Measured 2026-08-16: codex-acp 1.3.0
      // accepts the key from CODEX_API_KEY or OPENAI_API_KEY.
      "auth_method": "api-key",
      "env": { "CODEX_API_KEY": "{env:CODEX_API_KEY}" },
      "stop": { "wind_down_seconds": 30 },
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
gone, along with its side effect of declaring a landbridge MCP server for every interactive
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
      // `grok agent stdio`, NOT `grok -p --output-format streaming-json`. The latter is an
      // output shape that merely resembles ACP; the former is the protocol.
      "spawn": ["grok", "agent", "stdio"],
      "prompt": "You are a Landbridge worker on a live session. First call the landbridge__get_session MCP tool to read your assignment (namespace, description, completion_criteria, workspace, attempt). Do the work inside the assigned workspace. When you think you are done, call landbridge__report_result with a reference to where the work lives (a branch/commit/URL) — not the work itself — and stay up; the Lead may reply. If you are blocked or a decision is above your scope, call landbridge__request_input instead of guessing. You do not complete the session yourself.",
      "follow_up": "There is new input on your assignment. Call landbridge__get_session to read it, then continue.",
      // 1.0.4+ gates project-local config behind folder trust and a work dir is a
      // throwaway folder. Carried over from the stream profile; re-confirm under ACP.
      "env": { "GROK_FOLDER_TRUST": "0" },
      "stop": { "wind_down_seconds": 30 },
      "logs": { "capture": true }
    }
  ]
}
```

If Grok turns out to require a client-side terminal, it cannot run under this client today.

### Worked example — Goose over ACP

Native (`goose acp`), no adapter. Handshake captured against Goose 1.37.0: protocol 1,
`loadSession: true`, `mcpCapabilities.http: true`. `authMethods` lists `goose-provider`
("Run `goose configure`"), and `session/new` still succeeded without `authenticate` —
same rule as every other agent: a declared method is *available*, not required. Do
**not** put `"auth_method": "goose-provider"` on the profile; that method is an
interactive configure, not a headless key. The operator must already have run
`goose configure` (or set `GOOSE_PROVIDER` / `GOOSE_MODEL` plus that provider's key).

`goose serve` is the remote HTTP/WebSocket server. It is **not** this profile. Spawn
is `goose acp`, stdio, one process per task. To put Goose behind a WebSocket instead,
wrap it with [`landbridge-acp-bridge`](../../../tools/Landbridge.AcpBridge/README.md):
`listen -- goose acp` on the far side, `connect <ws-url>` on `spawn`.

```jsonc
{
  "profiles": [
    {
      "name": "default",
      // `goose acp`, NOT `goose serve` and NOT `goose run`. serve is a long-lived
      // remote transport; run is not the protocol.
      "spawn": ["goose", "acp"],
      "prompt": "You are a Landbridge worker on a live session. First call the landbridge__get_session MCP tool to read your assignment (namespace, description, completion_criteria, workspace, attempt). Do the work inside the assigned workspace. When you think you are done, call landbridge__report_result with a reference to where the work lives (a branch/commit/URL) — not the work itself — and stay up; the Lead may reply. If you are blocked or a decision is above your scope, call landbridge__request_input instead of guessing. You do not complete the session yourself.",
      "follow_up": "There is new input on your assignment. Call landbridge__get_session to read it, then continue.",
      // Goose 1.46's session/new starts on `auto` (auto-approve). Pin approve so
      // tool calls go through session/request_permission. Skipped if this
      // session did not advertise the mode.
      "session_mode": "approve",
      // Optional override of whatever `goose configure` stored. Leave unset to use
      // the machine's existing provider.
      // "env": { "GOOSE_PROVIDER": "openai", "GOOSE_MODEL": "gpt-4o-mini" },
      "stop": { "wind_down_seconds": 30 },
      "logs": { "capture": true }
    }
  ]
}
```

**Default session mode is `auto`.** Goose 1.46's `session/new` result advertises
modes `auto` / `approve` / `smart_approve` / `chat` and starts on `auto`
("Automatically approve tool calls"). Pin `"session_mode": "approve"` so those
calls go through `session/request_permission`. Unadvertised is skipped. Do not
also put a bypass flag on `spawn`.

Goose as an *editor* agent often expects the client to provide `fs` and `terminal`.
This client declares both UNSUPPORTED. A Goose that does its own I/O (its Developer
extension) can still work; one that asks the client for `terminal/create` cannot.
Watch for the refusal line below on the first turn.

### The caveat that could make an ACP worker useless

This client declares the ACP `fs` and `terminal` capabilities **UNSUPPORTED**. Those exist
so an editor can hand an agent its unsaved buffers and its terminal panel; a Landbridge worker
has its own work dir and its own shell, so an agent doing that I/O itself is the
arrangement, not a degradation. All three measured agents carry their own tools.

But an agent that routes *all* its shell and file access through the client would, under
that declaration, be unable to do anything — and the symptom is a task that starts, calls
no tools, and reports nothing, which reads exactly like a lazy model. So a refused request
is reported, once per method per task:

```
landbridged: task <id>: the agent asked landbridged to perform 'terminal/create' and was refused —
this client declares the ACP fs and terminal capabilities UNSUPPORTED […] check whether this
harness needs a client-side terminal (§10).
```

If you see that line, the harness needs a client-side terminal and this profile will not
work until landbridged grows one. It is the first thing to look for when an ACP worker does
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
false** in the spec. An agent that does not declare it cold-starts, and `landbridged` says so
per task rather than letting a resume quietly become one:

```
landbridged: task <id>: the plane handed back a resume ref but this agent does not declare the
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
to an unbounded one. Weigh that per profile before migrating. You keep the meter (below) —
what you lose is the cap.

### Token accounting: better than the docs suggested

This section used to say usage was not carried over, on the reading that ACP's only usage
surface is `usage_update` — context-window utilization plus an optional cost, and not the
four disjoint buckets `UsageReportedEvent` carries. **Real transcripts say otherwise**, and
the correction is worth stating plainly because a real decision was taken on the old claim.

Measured 2026-08-16 against live workers, `PromptResponse` carries per-turn buckets:

| Agent | `inputTokens` | `outputTokens` | `cachedReadTokens` | `cachedWriteTokens` | `totalTokens` | cost |
|---|--:|--:|--:|--:|--:|--:|
| `claude-agent-acp` 0.68.0 | 6 | 866 | 61,019 | 6,701 | 68,592 | $0.09490875 |
| `opencode acp` 1.18.18 | 99 | 14 | 14,208 | — | 14,321 | `{"amount":0}` |

Both reconcile exactly against `totalTokens`, so the buckets are **disjoint** — they map
onto §12's four columns with no subset correction, which is the per-harness
`usage_cached_is_subset` knob the stream mapping needed and ACP does not. `totalTokens` is
derivable and deliberately not stored.

Three things the mapping does deliberately:

- **Reports cumulative totals, not per-turn ones.** `SessionStore.RecordUsageAsync` keeps a
  high-water mark per bucket, so per-turn reports would leave the row holding the largest
  single turn rather than the dispatch's spend.
- **Treats an explicit zero cost as no cost.** OpenCode priced a 14,321-token turn at
  `{"amount":0}`; recording $0.00 would assert the dispatch was free. Same rule as Codex,
  same §2 principle 2.
- **Records no model.** Nothing in ACP attributes usage to a model, and the profile's argv
  names a CLI rather than whatever it routed to.

What is still open: `usage_update`'s `used`/`size` context gauge has nowhere honest to go in
a cumulative column, and reshaping the §12 measured view to hold a gauge is tracked
separately.

## Profile archetypes — open vs. strict

Two flags decide how much of the machine a worker can use, and the choice is
made **per profile, by the machine's operator** — never by the Lead. A Lead
targets a profile *name*; what that name can do (which MCP servers, which
commands) is the machine's declaration, invisible to the plane (§1's
infrastructure/work split, §10's "everything specific is data").

**Open** — the worker uses the machine like its owner would, including starting its own
background processes (§10 `start_process`). Permissions go through
`session/request_permission` to the plane; do not put bypass flags on `spawn`.

```jsonc
"spawn": ["claude-agent-acp"],
"prompt": "<opening turn naming mcp__landbridge__get_session / report_result / request_input>",
"follow_up": "There is new input on your assignment. Call mcp__landbridge__get_session to read it, then continue.",
"processes": { "agent_initiated": true }
```

On a **strict** profile leave `processes.agent_initiated` off (the default): a worker
that cannot start a background process by hand is refused honestly by `start_process`
rather than granted a capability the rest of the profile withholds.

**Strict** is a prompt and a permission policy, not an argv allow-list. The plane
answers `session/request_permission`; deny what should not run. There is no
`--allowedTools` key on an ACP profile.

The trade is blast radius: on an open profile, a prompt-injected worker can do
anything the machine account can, including using every local MCP server's
credentials. Landbridge's containment still holds at its own boundaries — the
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
outside** `landbridged`'s supervision — the service manager forks it, so it is
neither a descendant of the harness (the tree-kill misses it) nor a carrier of
`LANDBRIDGE_*` (the stray reaper's environment scan misses it). That is by
construction, and it means stopping such a service is the service manager's job,
not `landbridged`'s: a `landbridged` restart or a task kill will not take it down. The
worker skill forbids the other way of achieving the same escape — scrubbing
`LANDBRIDGE_*` off a spawned process — precisely because that one defeats the kill
guarantee for everything else too.

## Permissions (§11)

A permission request is part of the protocol: an agent that wants to use a tool its policy
does not cover sends `session/request_permission`, with its own options attached, and waits.

**landbridged routes it through the plane.** The runner posts the worker bearer at
`POST /worker/permission`, which is the same `PermissionRelay` as the MCP
`request_permission` tool. A Lead or human answers with allow or deny. Allow maps
to the agent's `allow_once` option, never `allow_always`. Deny maps to a reject
option, or `cancelled` if the agent offered none.

Do not put bypass / `--always-approve` / `--auto` on `spawn`. That skips a dialog
Landbridge is now the one answering.

## Park vs wait

A live ACP session is held **indefinitely**. `Landbridge:WaitTtl` is off by default (infinite).
The sweeper still requeues a task whose machine died while waiting.

**`park_session` is the release.** A Lead or human who wants the machine back sends it; the
runner `session/cancel`s, the instance token is revoked, and a later wake is `session/load`.
Answering a still-live wait is `answer_input_request`, which delivers the profile's
`follow_up` turn so the worker pulls `get_session`. Do not put bypass / `--always-approve` /
`--auto` in `spawn` — permissions are protocol, not argv.


## Transcript capture (§12)

When a profile sets `logs.capture: true`, `landbridged` records that worker's transcript
locally. A `claude -p --output-format stream-json` worker's stdout **is** the full
transcript of its work — the single most valuable artifact when a task goes wrong —
so `landbridged` **tees** it: the same stdout read that maps events (`events.source:
terminal`) also writes each line verbatim to a file, and stderr is captured alongside.
Capture never disturbs event mapping or the stdin dead-man/stop path — it is a tee,
not a divert — and it works for any `events.source` (with `none`, stdout is drained
solely to capture it).

**Where.** Under the **state dir** (the `credentials.json` dir; `--state-dir`,
`LANDBRIDGE_STATE_DIR`, `$XDG_STATE_HOME/landbridge`, or `~/.landbridge`), **not** the per-task
`work_root` scratch — the transcript must outlive a task teardown and a `landbridged`
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

**What "verbatim" means — the line boundary.** Capture is **line-oriented**: `landbridged`
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
instance. On reaching it, `landbridged` writes one truncation marker line
(`{"landbridge":"transcript_truncated","limit_bytes":N}`) and stops writing that stream.
It keeps draining the pipe (so the worker never blocks) and never kills the worker —
logging is not allowed to affect the task.

**Local pruning.** `prune_after_days` (default 7; `0` disables) is machine-local disk
hygiene: on each capturing spawn, `landbridged` removes any task's transcript dir whose
newest file is older than the window. When profiles disagree, the most generous wins
(any `0` keeps everything; otherwise the longest window). This window **is** the
retention story: the control plane stores no transcript bytes and has no retention tier
of its own (§12), so once the sweep removes a dir the transcript is gone everywhere.

> ⚠️ **Turning capture on means raw agent output becomes readable from the dashboard.**
> A transcript is served **verbatim** — Landbridge does **not** redact it (spec §13, open
> question 8) — so it may contain credentials the agent echoed, customer data, internal
> hostnames, or anything else it read or printed. What limits exposure is scope, not
> filtering: an operator reads it only through a **human** dashboard session (a Lead
> token is refused), and only for a task in a **terminal** state, whose worker
> credential is already revoked. Treat a downloaded transcript as sensitive: do not
> paste it into a ticket, a chat, or another agent.

**Serving (§12).** With capture on, a human operator can read a terminal task's
transcript from the dashboard: the control plane asks this machine for one byte range at
a time over the runner channel (`read-transcript`), and `landbridged` replies with the file's
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
(`Landbridge.Mcp.Tests/WalkingSkeletonEndToEndTests`) proves the dispatch → spawn →
authenticate → `get_session` → `report_result` loop with a **scripted** MCP worker
(`Landbridge.WorkerHarness`), no LLM. Running the argv above against **real**
`claude -p` — confirming the bootstrap prompt, permission posture, and hooks
actually behave on *your* machine — is the operator-run validation and belongs to
the §17.0 feasibility spikes and the §11 conformance run, deliberately out of
scope for CI.

Stop delivery is the exception: it is no longer yours to characterize, because
`Landbridge.MultiMachine.Tests/RealClaudeCollaborationTests` now runs the real binary in
an opt-in tier and pins what a stop does to a `claude -p` worker — a kill at the
deadline, with the transcript preserved via the session ref. What is still worth
checking on your own machine is the *kill* (that the tree is really gone) and, if you
declared `mode: message` for a non-`claude -p` harness, that a written turn is actually
honoured there. Nothing but a real run can tell you the latter, which is why landbridged
does not claim it.
