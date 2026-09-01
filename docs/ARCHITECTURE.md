# Landbridge architecture

This is a map of how the pieces fit and why. It summarizes; the authoritative
design is [`ideas/spec.md`](../ideas/spec.md), cited by section (§) throughout.
Where the implementation on this branch differs from the spec's aspiration, the
difference is called out — the spec is where Landbridge is going, the code is what
runs today.

## The shape

One control plane per Instance, many machines, an optional relay, and a
dashboard. Everything an agent touches goes through the control plane's MCP
surface; everything a machine does goes through `landbridged`. `landbridged` only ever
dials *out*, so it works behind NAT with no inbound firewall rule. A worker mails
`report_result`; the Lead closes the session with `stop_session` when they are done
with that worker (§7, §9 check 4) — there is no verifier process.

```
   human (browser / harness)
        │  OAuth 2.1 / operator passphrase
        ▼
  ┌───────────────────────── Landbridge.Mcp (the host) ─────────────────────────┐
  │  MCP tools  ·  /runner WS  ·  OAuth AS  ·  /enroll                       │
  │  /relay/validate  ·  /dashboard         (spec §5, §10, §12)              │
  │                                                                          │
  │  Landbridge.ControlPlane:  SessionStore ─► Landbridge.Core (pure state machine)     │
  │  DispatchService · WaitTtlSweeper · TokenService · DashboardQueries      │
  └───────────────┬──────────────────────────────────────────┬─────────────┘
                  │  Postgres (per Instance)                   │ runner channel
                  ▼  SKIP LOCKED dispatch · LISTEN/NOTIFY       │ (WebSocket, frozen §10 vocab)
          ┌───────────────┐                            ┌───────┴───────────────┐
          │  tasks, events│                            │  landbridged (Machine A)  │
          │  credentials  │                            │  ProcessSupervisor    │
          │  relay_grants │                            │  spawns worker harness│
          └───────────────┘                            └───────┬───────────────┘
                                                               │ spawns
                                        LANDBRIDGE_SESSION_ID, mcp.json (0600) ▼
                                                        ┌──────────────────────┐
                                                        │  worker harness       │
                                                        │  (claude -p, MCP      │
                                                        │  client → the host)   │
                                                        └──────────────────────┘

  landbridge-relay (separate module):  Machine A's landbridged ⇄ relay ⇄ Machine B's landbridged
```

- **`Landbridge.Mcp`** is the single ASP.NET process (`landbridge` + `landbridge-mcp` in spec
  terms). It hosts the MCP tool endpoint, the `/runner` WebSocket, the OAuth
  authorization-server endpoints, `/enroll` + `/machine/refresh`,
  `/relay/validate`, and the web dashboard. It owns one Postgres
  database and is the *only* path to the state machine — there is no
  client-direct table access (spec §3, §15).
- **`Landbridge.ControlPlane`** is the library behind that host: the store, auth,
  dispatch loop, TTL sweeper, and dashboard read models.
- **`Landbridge.Core`** is the pure engine underneath the store.
- **`landbridged`** (`Landbridge.Runner`) runs on each machine. It is transport: it
  spawns and supervises harness processes, heartbeats, relays events, holds the
  machine credential, and opens relay tunnels. It never touches the workspace or
  interprets task content (spec §2 principle 6, §10).
- **`landbridge-relay`** is a standalone module that dials the plane; it is not part
  of the host process. (There is no verifier module — the Lead closes the session.)

## The engine is pure; opaque metadata rides the row

The load-bearing design rule (spec §2): **the control plane interprets nothing.**
It validates that fields exist, checks identities, counts, and enforces
transitions — it never parses a task description or evaluates completion
criteria.

`Landbridge.Core` embodies this literally. `SessionStateMachine` is a pure function:
`Apply(SessionRecord, SessionCommand)` returns a `TransitionResult` that is either
`Transitioned(newRecord, effects)` or `Rejected(rule, reason)`. It has no clock,
no I/O, and no dependencies — its `.csproj` references nothing (verify: no
`PackageReference`, no `ProjectReference`). Callers supply facts as command
fields; side effects come *back* as data (`Effect` records like
`MintWorkerInstanceToken`, `RevokeWorkerInstanceToken`, `ClearServicesAndForwards`,
`WriteParkRecord`), which the store then applies. Each rejection names exactly
one rule from `enum Rule` — the §9 checks (1–14) plus the §6 structural
invariants (≥100).

Because the engine never interprets content, the opaque fields sit on the task
record as plain columns the plane stores and returns but never dereferences:

| Field | Carries | Interpreted by |
|---|---|---|
| `Description` | the whole brief — what to do and how it will be judged | the worker, then the Lead reading reports (§7, §9 check 4) |
| `Workspace` | optional context (repo, package, base ref), not isolation | the worker's skill (§7) |
| `ResultReference` | where the work lives (a commit/URL) | the Lead reading it via `get_lead_inbox(sessionId)`, and a human on the §12 dashboard (§8.1, §7) |
| `CompletionProvenance` | who closed the session (`lead-session` \| `human`) | the §12 dashboard (§9 check 4) |
| `ParkRecord{Machine, Directory, HarnessSessionRef, Attempt}` | resume affinity | `landbridged` on redispatch (§11) |
| `TraceContext` | W3C `traceparent` for cross-process tracing | OpenTelemetry, not the domain |
| `Profile` | required runner-profile routing key | exact-match at dispatch, never parsed |

These are stored as **text columns, not a serialized blob** — the "opaque blob"
is a discipline about *not reading* them, not a storage format. The dispatch hot
path (task state) is kept separate from the event firehose (spec §3).

## Credential classes, and how a worker's identity is minted

Every credential descends from a human (spec §2 principle 5, §5). There are four
classes, held as **opaque tokens** (not JWTs) so revocation takes effect
instantly — `TokenService` stores only a SHA-256 hash and validates by lookup:

| Identity | Token prefix | Obtained | Authorizes |
|---|---|---|---|
| Human | `lbr_h_` | OAuth code flow / operator passphrase | create Teams, confirm verdicts, dashboard |
| Lead | `lbr_l_` | claimed as a factory under a human session | `create_team`; create tasks, answer, close sessions (`stop_session`) on a `teamId` that factory owns |
| Machine (`landbridged`) | `lbr_m_` / `lbr_r_` | enrollment token → client credentials | runner channel |
| Worker | `lbr_w_` | **minted at dispatch** | MCP worker tools, scoped to `{team, task, worker, instance}` |

The asymmetry is deliberate. A Lead's authority comes down from a human directly.
A worker's is minted by the control plane from the Lead's dispatch decision — **a
worker never authenticates.** This makes the §6 authority table *structural*
rather than checked: a worker cannot create a task because its token carries no
lead claim, not because an `if` rejected it.

```
  human session ──claim Lead factory──► Lead token (owns Teams via lead_teams)
                                     │ create_team / create_session(teamId)
                                     ▼
                              submitted task
                                     │ control-plane dispatch (SKIP LOCKED)
                                     ▼
        TokenService.MintWorkerTokenAsync ──► Worker token {team,task,worker,instance}
                                     │  injected into mcp.json, delivered down the runner channel
                                     ▼
                              worker harness authenticates as exactly that instance
```

Each dispatch mints a **distinct worker instance**. Requeue or redispatch revokes
the predecessor instance's token first (the `RevokeWorkerInstanceToken` effect),
and worker-triggered transitions are accepted only from the incumbent instance
(§9 check 14). An orphaned harness — a SIGKILLed daemon, a healed partition —
holds a token that is already dead. Token exchange is strictly narrowing: the
only exchange in the system is enrollment → machine credentials; there is no path
from a worker credential to a lead claim or a human session (§5, §9 check 13).

The host authenticates callers with a single `LandbridgeAuthenticationHandler` that
maps a bearer token to a typed `Principal`; the MCP tools then narrow (Lead tools
require a lead principal, worker tools a worker principal), and `/runner` requires
a machine principal.

**Un-trusting a machine is `MachineRevocationService`, not `TokenService` alone.**
Two of the three things a revoke has to reach are not credential rows. `/runner`
authenticates once at the upgrade and never again, so revoking the machine's tokens
leaves an open, dispatchable command channel; and a worker token carries no machine
id — only `{team, task, instance}` — so a credential sweep keyed on machine reaches
none of the workers on the box. So the service composes all three: revoke the
credentials, drop and close the connection (requeueing what it held, exactly as a
dead socket does), then revoke the worker instances recorded as having run there.
`TokenService.RevokeMachineCredentialsAsync` is deliberately named as the half it is.
The surface is one human-only action on the §12 Machine Group view — a machine
belongs to no Team, so there is no Lead twin.

**A copied `credentials.json` is a working machine identity on any host.** The
refresh token's machine binding is server-side only (which machine the row mints
for), and nothing collects or checks a host fingerprint, so the copy is not
defeated by it — revocation is the whole answer today. Binding refresh and connect
to a host-sealed secret (TPM, keychain) is a tracked follow-up, not built.

## Occupancy and the message machine (§6)

A session is a durable row. Occupancy is `desired`/`observed` (`none | on_disk | running`).
The outstanding envelope is `idle | awaiting_lead | awaiting_permission | awaiting_report | awaiting_pull`.
`report_result` is mail (`awaiting_report`); occupancy stays running. `stop_session` closes the row
(hide + `desired=on_disk`). Derived `SessionState` is a compatibility view, not the source of truth.

Two counters, not one (`SessionRecord.InfrastructureRequeues` vs
`VerificationFailures`): a machine rebooting three times must not exhaust the
retries a task has for failing its criteria. **Only the verification counter drives
`rejected`** (default limit 3). Both are capped, but they end differently: the
infrastructure cap (`InfrastructureRequeueLimit`, default 5, configurable via
`Landbridge:InfrastructureRequeueLimit`, non-positive for uncapped) abandons the task as
`canceled` — the plane giving up on placing the work, not a verdict on it — and every
requeue records `LivenessLossReason` on the task row and its event row so the trail
says which signal fired. Terminal states — `completed`, `rejected`,
`canceled` — are final and never resumed. Leaving `working` clears the task's
registered services and releases its relay forwards
(`ClearServicesAndForwards`).

No transition requires reading a task description or interpreting its criteria —
every guard is a field check, an identity check, a count, or a state check.

## The frozen runner wire vocabulary (§10)

The control plane can be fixed by redeploying. **`landbridged` cannot** — it runs on
customer machines and may go a year untouched. So the runner contract is the one
frozen interface in the system, and `Landbridge.Contracts` enforces that in the type
system: the message hierarchy has `private protected` constructors, so no message
type can be added from outside the assembly. A runner rejects anything outside
the vocabulary (`RunnerWire.Decode*` returns null on an unknown `type`).

| Direction | Messages |
|---|---|
| **Outbound** (plane → runner) | `dispatch` · `stop(ttl, disposition)` · `kill` · `open-forward` · `close-forward` · `read-transcript` · `start-process` · `stop-process` · `write-process` |
| **Inbound** (runner → plane) | `started` · `session-started` · `alive` · `tool-call` · `subagent-spawned` · `exited` · `auth-failed` · `forward-opened` · `forward-closed` · `rebooted` · `transcript-chunk` · `process-started` · `process-stopped` · `process-written` |

**Every message carries a task id** — it is the required first field of every
record. The one exception is `rebooted`, which is machine-scoped precisely because
it is emitted when the runner holds no task to reference. A machine runs many
agents concurrently, so there is no implied "current task" in either direction;
this is the change most expensive to retrofit, so it is structural.

Messages are serialized by `RunnerWire` as a snake_case JSON object with a `type`
discriminator alongside the payload fields (plus an optional `traceparent`
envelope key for tracing and a `heartbeat` message that carries a machine's
declared profiles and load). Nothing in the vocabulary is domain-specific.

> **Implemented subset.** The vocabulary is frozen and complete in the types, and
> `landbridged` on this branch emits all of it except two: `auth-failed` and
> `subagent-spawned` are defined but not yet produced (their dashboard rows render as
> "not reported" rather than fabricated — spec §10, §12). Everything else has a
> producer, including `alive` (landbridged's own per-task process-alive assertion, §10),
> `session-started`, `transcript-chunk`, and the three §10 process replies
> (`process-started`, `process-stopped`, `process-written`) — each produced by the
> handler for its command, and each handled on the plane by the process-control relay's
> request/reply rendezvous.
>
> The three outbound `*-process` commands are the agent-facing half of §10's background
> processes: a worker starts a long-running child of `landbridged`, writes to its stdin, and
> stops it. They are the largest single addition the frozen vocabulary has taken, and
> they are distinct records rather than one polymorphic command for the same reason
> `stop` and `kill` are distinct — the closed hierarchy is the boundary, and the three
> replies carry genuinely different payloads (port + log path, exit code, bytes accepted).

## `stop` is a message, not a signal

A signal cannot carry a disposition. Claude Code's SIGTERM behavior is
abort-and-exit, which would silently turn `preserve` into `kill`. So where the
harness reads turns off a held-open stdin, `landbridged` delivers `stop` as an
*injected turn* — the agent reads the disposition, winds down, persists, and exits
— and reserves signals for TTL expiry and `kill`. A profile's `stop.mode`
(`message` | `signal`) declares which delivery it supports. `ttl == 0` means kill
immediately.

The message path needs a pipe to arrive on, so it is coupled to the profile's `stdin`
policy: `stop.mode: message` together with `stdin: closed` is refused at config load
rather than accepted as a promise the machine cannot keep.

**`claude -p` is not one of those harnesses, as built.** It never reads stdin after
startup, and the flag that looks like it would enable this (`--input-format
stream-json`) makes it ignore its argv prompt and hang instead. So the reference
profiles declare `signal`, and a stop there is the granted TTL then a tree-kill.
`preserve` still holds — the recorded session ref outlives the kill, so the
transcript is resumable — but by the plane's record rather than the agent's
cooperation. Correspondingly, `landbridged`'s ack reports only what it did (a turn was
*written*; a deadline was *armed*): whether a harness consumed a written line is
not observable without harness-specific knowledge, which the runner does not carry.
Spec §10's as-built note has the two CLI facts.

## Restart equals reboot

`landbridged` keeps no persistent local state and does no process re-adoption (spec
§10, §15). A restart *is* a machine reboot:

- On clean shutdown it kills every harness it started.
- **On start, before accepting dispatch, it kills any stray harness processes** —
  because a SIGKILLed daemon kills nothing on the way down, and orphaned
  harnesses keep burning tokens against tasks the plane has already requeued.
  Putting the guarantee on *start* is what makes it survive a hard crash.
- On reconnect it emits `rebooted`, and the affected tasks requeue against the
  infrastructure counter.

Stray cleanup keys off two environment variables `landbridged` stamps on every child
it spawns, non-configurably: `LANDBRIDGE_MACHINE_ID` (the start-of-day sweep) and
`LANDBRIDGE_SESSION_ID` (the per-task-exit sweep, which catches a dev server that
`setsid`'d out of the task's process group and would otherwise hold a port a
later consumer's forward could reach). Discovery is per-OS: `/proc/<pid>/environ`
on Linux, `KERN_PROCARGS2` on macOS, and on Windows a kill-on-close **Job Object**
per worker (so the kernel does the cleanup and there is nothing to discover). The
primary dead-man's switch is simpler still: `landbridged` holds each worker's stdin
pipe open, and if `landbridged` dies the OS closes the write end, so a well-behaved
harness sees EOF and tears down its own tree.

That switch is a per-profile declaration (`stdin`), not a universal property, because
it is not universally survivable. `deadman` is the default and holds the pipe; `closed`
gives the worker a deterministic EOF right after spawn, for a harness that would
otherwise block on the pipe forever instead of taking its first turn — `codex exec`
reads stdin during prompt resolution and does exactly that. A `closed` worker gives up
the switch: it no longer dies with `landbridged`, and the next start's stray sweep is the
only thing that collects it. `landbridged` states that cost on its startup line rather than
leaving it implicit.

## The relay (§8.3)

`landbridge-relay` gives authenticated cross-machine service access with no network
prerequisite. A producer task registers `{name, port}` after binding; a consumer
calls `open_forward(name)`; the control plane checks Team membership,
registration, and that the owning task is `working`, then issues a grant bound to
`{consumer, service, expiry}`. The relay itself moves opaque bytes and holds no
`landbridged` channel of its own — so in this deployment the **control plane** relays
`open-forward` to *both* ends over the runner channel, and each end dials the
relay independently.

```
 consumer's client
   → 127.0.0.1:8391            landbridged binds loopback ONLY, on demand
     → authenticated tunnel     HTTP upgrade, forward-scoped grant
       → landbridge-relay           ForwardRegistry pairs the two ends by forward id, splices
         → producer's landbridged
           → 127.0.0.1:5432     the registered service
```

Both ends authenticate to the relay independently; neither authenticates to the
other, so there is no peer key exchange and nothing to distribute. The grant is a
**connection-establishment** credential, checked once when the tunnel opens (the
relay calls the plane's `POST /relay/validate`); an established splice is never
severed mid-flight by grant expiry — it persists until the owning task leaves
`working`, and that bound is enforced rather than hoped for: the same
`ClearServicesAndForwards` effect that revokes the task's grants sends `close-forward`
to both machines, because revoking a grant only stops the *next* open and a splice
already running has nothing else that can end it. Only registered services are
forwardable — that requirement is the one
thing between the relay and a fleet-wide port scanner (§13). The relay validator
is **fail-closed**: an unreachable or unconfigured plane refuses every tunnel.

> **Implemented subset.** The generic-TCP splice, `ForwardRegistry` rendezvous,
> fail-closed control-plane grant validation, the per-Team **byte counter** and the
> **forward rate limit** (§9.10 — counted in the splice loop and reported to the plane;
> the limit is enforced at grant mint), and the **HTTP layer** (§8.4's
> subdomain-per-service preview) all work today. Still deferred: generalizing beyond
> **one tunnel per forward id**, and any *enforcement* on the byte counter — §8.3
> forbids severing an established splice *by policy*, so a reached ceiling has no defined
> action. (That prohibition is about a ceiling cutting work in progress; the owning task
> leaving `working` is the splice's own end, and `close-forward` is what makes it happen.)

## Observability (§12)

The dashboard renders three read models from `DashboardQueries` (pure reads,
separate from the write-path store): the **Machine Group view** (machines,
readiness/back-pressure, heartbeat age, running tasks with owning Team), the
**Team view** (tasks by state, registered services, open input requests, parks per
task, whether a Lead is attached — doubles as the §4 reattachment surface), and
the **human inbox** (everything waiting on a person: open questions, tasks
awaiting review, parked tasks, and the auth failures still blocking a live task).
Lead takeovers, machine reboots, and evictions land in the event log. The dashboard's
writes are few, and all but the preview mint are human-only: answering a permission
request, and **Revoke machine** — the §5/§13 un-trust action, which is the only surface
the operation has (see above). Both, plus login/logout, are same-origin only
(`OriginGuard`); the preview mint is deliberately exempt as the one POST an agent
legitimately drives.

Views render as a plain server-rendered web dashboard (spec §12: "a plain web
dashboard first"); MCP Apps are not built. Most of §12's data points now have a
source: a Team's measured relay byte burn is surfaced (§9.10), and the
derived-telemetry events — auth failures, subagent spawns, and
the typed input-request kind — have a **plane-side path end to end**: each is persisted
as a task event row and renders structured in the event log, with the auth failures on
live tasks also joined into the inbox (the log is history; the inbox is what needs a
person). Two of those three are waiting on a producer rather than on a read model:
`landbridged` emits neither `auth-failed` nor `subagent-spawned` (above), so in practice no
such row arrives today — the plane can record one, and nothing sends it. **Permission requests** now have
a source too — §11's permission bridge records them on the task row, and the inbox
section renders them with an allow/deny form. What genuinely has no source is the
**subagent tree nested under a machine**, which renders as an honest empty state rather
than fabricated numbers. Cross-process tracing is real: the host exports
OpenTelemetry (traces/metrics/logs via `Landbridge.ServiceDefaults`), and a stored
`traceparent` lets one trace span `create_session → dispatch → runner → worker`.

## Where to go next

- **[docs/RUNNING.md](RUNNING.md)** — running the dev loop, authenticating,
  enrolling a real machine, pointing a profile at a real `claude -p`, and the full
  config-key reference.
- **[`ideas/spec.md`](../ideas/spec.md)** — the authoritative design and rationale.
