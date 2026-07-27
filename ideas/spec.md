# Docket — Specification

**Status:** Draft. Nothing here has been run.

Docket coordinates AI agents across multiple machines. A human drives a *Lead* agent, which decomposes work into tasks; *worker* agents on other machines claim and execute them. The control plane keeps the record and enforces procedure. It never reads the work.

Docket is the communication, runner, and relay layer. It does not supply models, resell inference, or hold model provider credentials — customers bring their own keys, which live on their own machines and never touch Docket infrastructure.

**Coding is the primary use case, not a built-in assumption.** The schema is domain-neutral: Docket knows a task has a completion mode and a workspace, and nothing about what either contains. The shipped skill bundle is code-oriented because that is where most of the demand is, but repositories, branches, and test suites appear only in guidance — never in the data model.

Docket ships first as a hosted product. Self-hosting comes later.

---

## 1. Terminology

| Term | Meaning |
|---|---|
| **Account** | Billing entity. Owns one or more Instances. |
| **Control Plane Instance** | One deployed control plane. The isolation boundary — its own database, signing key, endpoint, skill bundle. |
| **Machine Group** | Every machine running `docketd` within an Instance. A resource pool, not an owner. |
| **Team** | A group of agents working across the Machine Group under a single Lead. Many Teams per Instance. |
| **Lead** | A harness client driven by a human. Creates tasks, answers questions, holds the plan. |
| **Worker** | An agent executing one task. May spawn local subagents. |
| **Machine** | One enrolled host running `docketd` and one or more harnesses. |

Infrastructure and work are deliberately separate nouns. Machines belong to the Machine Group; Teams draw on it. **A machine may run many agents concurrently, including several from the same Team.**

---

## 2. Design principles

These are load-bearing. Most decisions below follow from them, and a change here invalidates a lot downstream.

**1. The control plane interprets nothing.**
It validates that fields exist, checks identities, counts, and enforces state transitions. It never parses a task description, never evaluates completion criteria, never decides what work means. Thin means *doesn't interpret* — not *has no rules*.

**2. Derive telemetry; don't ask for it.**
An agent in trouble is the one that won't report. Liveness comes from the runner's own heartbeat, progress from tool-call events already crossing the server, lineage from OTel span attributes. Agent-authored reports are annotation, never load-bearing for "is this stuck."

**3. Enforcement cannot live where a model can reason past it.**
Anything you want guaranteed goes in the schema or the identity check. Skills and prompts are advisory and can be bypassed, including by text an agent was fed.

**4. Skill for judgment, schema for existence.**
The skill teaches agents to write good completion criteria. The schema guarantees criteria exist. Use guidance only where the failure mode is bounded and recoverable.

**5. Every credential descends from a human.**
A Lead's authority is a human's session. A worker's authority is minted at dispatch from that Lead's decision. A machine's authority comes from a human-issued enrollment token.

**6. The runner is transport.**
It moves bytes and manages processes. It knows nothing about version control, toolchains, or task semantics.

**7. Domain knowledge lives in skills, never in the schema.**
If a field would only make sense for software work, it belongs in an opaque blob the skill defines.

> **Note on scope.** Docket is thin in *logic* and not thin in *role*. `docket-relay` carries service traffic between customer machines (§8.3). Principle 1 still holds — it moves bytes without interpreting them — but the product is a data path, not only a coordinator.

---

## 3. Architecture

### What Docket ships

| Component | Role |
|---|---|
| `docket` | Control plane: state machine, event log, enforcement. One process, one Postgres, one Instance. |
| `docket-mcp` | MCP server surface agents connect to. Same process as the control plane. |
| `docket-relay` | Authenticated byte relay between machines (§8.3). Separate module, separately deployable. |
| `docketd` | Per-machine runner daemon. Config-driven. |
| `docket-meta` | Provisioning service. Creates, suspends, and destroys Instances; owns Accounts and image rollout. |

`docket-relay` is a distinct module rather than a control plane feature specifically so that who operates it stays a deployment decision.

### External dependencies

| Dependency | Relationship |
|---|---|
| **Harness** | Claude Code or any MCP-capable agent CLI. `docketd` starts and stops processes it did not build, described entirely by config. |
| **Model provider** | Customer's keys, customer's machines, customer's bill. Docket never sees them. |
| **Workspace substrate** | Wherever work products live — a version control host, a document store, a filesystem. Docket stores an opaque reference and never dereferences it. |
| **Verifier** | Supplies the completion verdict. May be automated or a person. Docket requires only that it is not an agent. |
| **Object storage / file transport** | Optional. Artifacts are URLs (§8.1). |

Every one of these is a place where a less disciplined design would grow an integration, a credential store, and an opinion.

### Deployment

Hosted. One Instance per customer deployment, as a container behind public TLS. Instances are completely independent: separate database, signing key, endpoint, skill bundle.

Because the control plane is operated rather than distributed, it can be fixed by deploying. **`docketd` cannot** — it runs on customer machines and may go a year without being touched. The runner contract in §10 is therefore the only frozen interface in the system.

### `docket-meta`

A service, with a single operator today and self-serve signup later. It owns Account records and billing, Instance lifecycle (create, suspend, destroy), Instance provisioning (container, database, signing key, endpoint, DNS), and image rollout including canarying.

It does **not** route work between Instances, aggregate a cross-Instance view, or hold shared agent identity.

**No agent access, ever.** It is not an MCP server. Separate network, separate credential class, human-only.

### Datastore

Postgres per Instance. Not a backend platform — client-direct table access would bypass the state machine, which is the one thing the architecture cannot tolerate.

- `SELECT … FOR UPDATE SKIP LOCKED` for task claims — the one concurrency-critical path.
- `LISTEN` / `NOTIFY` for dispatch push. Requires a session-mode or direct connection; transaction-mode poolers break it.
- `JSONB` for event payloads and opaque task blobs.

Split hot from cold in the same database. Never join a task-state read against the event firehose.

---

## 4. Teams and Leads

A Team is a unit of human-authorized work. It owns a budget, a scope declaration, and a set of tasks. It terminates.

**The Lead is a harness client the human drives.** Not a dispatched agent, not a daemon. Someone opens their harness, attaches to a Team, and works. The human is the event loop — which is why the Lead needs no lease, no wake conditions, and no runner.

- A Lead's machine does not need `docketd`. Enrollment and attachment are independent choices.
- Lead authority is the human's session scoped to `{team, lead}`.
- Multiple Teams run in parallel within an Instance. Leads have no channel to each other; the human is the channel.
- There are no sub-Teams.

### Claiming, releasing, and takeover

One Lead per Team, enforced as a conditional claim — the second claimant is refused, same shape as `claim_task`.

A Team can be left leadless by explicit release or by the human's session ending. A leadless Team is claimable and is a **visible state**, not an invisible one: it explains why nothing is progressing.

**Takeover is permitted and evicts the incumbent.** Claiming an actively-held Team shows who holds it and their last activity, and requires confirmation. Claiming a leadless Team is silent.

Two requirements on eviction:

- **The evicted session must learn why.** Its next privileged call fails with an explicit reason — evicted by whom, when — not a bare authorization error. This is the only place in the design where one human's action is invisible to the person it affects, and a generic 403 produces an agent inventing explanations for a permission denial.
- **Every takeover is a logged event.** Two people contending over a Team should be legible afterward.

### Attachment and reattachment

**The session is ephemeral; the Team is durable.** A human closes their laptop, workers keep running, questions accumulate.

Attaching gives a fresh Lead an empty context window. It must read its way back from control plane state: task states, open questions, registered services, recent results, budget burn. Takeover is not a handoff — the incumbent's context is not recoverable — so the reattachment path serves both cases and the Team view must be rich enough to reconstruct from.

---

## 5. Authentication and authorization

The control plane is both the OAuth 2.1 authorization server and the resource server for its Instance.

### Credential classes

| Identity | Obtained | Lifetime | Authorizes |
|---|---|---|---|
| **Human** | auth code / device flow | session | create Teams, approve permissions, supply review verdicts, dashboard |
| **Lead** | human session, claimed against a Team | session or until evicted | create tasks, answer questions, supply review verdicts, read Team state |
| **Machine** (`docketd`) | enrollment token → client credentials | long, refreshed | runner channel, log stream, relay tunnels |
| **Worker** | minted at dispatch | task lifetime | MCP tools, scoped to `{team, task, worker}` |
| **Verifier** | client credentials, human-provisioned | long | the `completed` transition, nothing else |

Two derivation paths, deliberately asymmetric: a Lead's authority comes down from a human directly; a worker's is minted by the control plane from the Lead's dispatch decision.

### Worker identity derives from dispatch

A worker never authenticates. The control plane dispatches task X to machine M, mints a token carrying exactly those claims, and `docketd` injects it into the harness's MCP config.

This makes the authority table in §6 structural rather than checked. A worker cannot create a task because its token carries no lead claim — not because an `if` statement rejected it.

### Bootstrap

A human-issued enrollment token, single-use and short-lived, exchanged by `docketd` for machine credentials during `/docket-enroll`.

### The invariant

**No path from a worker credential to a verifier credential, or to a lead claim.** Token exchange must be strictly narrowing.

### Token format

Opaque, not JWT. Revocation is the priority — un-trusting a machine must take seconds.

### MCP alignment

Targets the 2026-07-28 spec: Protected Resource Metadata (RFC 9728), Resource Indicators (RFC 8707) for audience-bound tokens, Client ID Metadata Documents rather than Dynamic Client Registration, and `application_type` declaration (SEP-837) since harnesses are CLI clients.

MCP hardens *authentication*. Every authorization decision in §6 is Docket's own.

---

## 6. Task state machine

```
submitted ──► working ──► verifying ──► completed
    ▲            │             │
    │            │             └──► rejected
    └────────────┘
      liveness lost               (retries exhausted)
```

Additional states: `blocked_on_input`, `parked`, `canceled`.

| Transition | Triggered by | Control plane checks |
|---|---|---|
| → `submitted` | Lead | session carries lead claim for this Team; completion criteria non-empty; namespace assigned; Team budget remains |
| `submitted` → `working` | claiming worker | single claimant (`SKIP LOCKED`); machine not under back-pressure and declares a profile matching the task's `profile`, if set |
| `working` → `submitted` | control plane | ack timeout, per-task liveness loss, or machine reboot; increments the relevant counter |
| `working` → `verifying` | working agent | result reference present |
| `verifying` → `completed` | verifier | **caller identity is not an agent** |
| `verifying` → `submitted` | verifier | verification retries remain |
| `verifying` → `rejected` | verifier | verification retries exhausted |
| `working` → `blocked_on_input` | working agent | typed request kind present |
| `blocked_on_input` → `working` | Lead or human | — |
| `blocked_on_input` → `parked` | control plane | wait TTL expired; lease released |
| any → `canceled` | Lead or human | disposition enum present |

No row requires reading a task description or interpreting its criteria.

**Two counters, not one.** Verification failures and infrastructure requeues are different things. A machine rebooting three times should not exhaust the budget a task has for failing its criteria. Only the verification counter drives `rejected`.

Terminal states — `completed`, `rejected`, `canceled` — are final and never resumed.

Leaving `working` clears the task's registered services and releases its relay forwards.

---

## 7. Task schema

**Typed**

| Field | Notes |
|---|---|
| `completion.mode` | `automated` or `review`. Determines which verifier identity is expected, nothing else. |
| `completion.criteria` | Opaque, non-empty string. In `automated` mode the verifier interprets it; in `review` mode a person reads it. The control plane never parses it. |
| `namespace` | Server-assigned `team-{id}/task-{id}`. Guaranteed unique. What an agent maps it onto is convention. |
| `workspace` | Opaque blob assigned by the Lead. Where the work happens, how it is isolated, which ports it may use. Shape defined by the skill. |
| `team_id`, `parent_task` | Lineage. |
| `budget` | Token ceiling, charged against the Team. |
| `expected_duration` | Lead's guess. Distinguishes stuck-short from long-running. |
| `profile` | Optional runner profile name. Exact-match routing; the control plane never interprets it. |
| `author_identity` + `is_human` | **Provenance.** Lets the receiving side treat human- and agent-authored instructions differently. |

**Prose:** `description`, `result_summary`, `blocker_note`.

### Completion modes

Not all work has a mechanical check. What generalizes is not the *check* but the *authority*: the worker does not decide it is done.

| Mode | Verdict from | Typical use |
|---|---|---|
| `automated` | verifier credential | test suite, linter, schema validation, any pipeline |
| `review` | Lead or human session | written deliverables, research, design, judgment calls |

Both land in `verifying` and take the same transitions. Tasks awaiting review appear in the human inbox (§12), which is what stops `review` from being a black hole.

### Workspace and isolation

**Isolation is assigned by the Lead at decomposition time, never chosen by the worker.** Workers who each pick their own isolation collide, because they have no channel to coordinate. Since `namespace` is server-assigned and unique, isolation derives from it and collision is structurally impossible.

The general rule: **each concurrent task gets its own mutable copy; anything shared is read-only.** Separate worktrees, directories, containers, or schemas depending on substrate.

`workspace` also carries any port assignments, for the same reason — two agents on one machine binding the same port is the Lead's problem to avoid, not the worker's to discover.

---

## 8. Artifacts, endpoints, and connectivity

### 8.1 Artifacts — a URL, and nothing else

The control plane stores a string and has no opinion about the scheme. It does not move bytes, broker grants, or register IDs. Maps to A2A's `url` part.

**No durability guarantee.** A URL to a laptop is live only while that laptop is up. Anything downstream depends on belongs in the workspace substrate.

Unreachable URLs surface as `blocked_on_input`, never a mystery timeout.

### 8.2 Live endpoints — advertised while working

A task registers `{name, port}` on its record while `working`. Visible to other tasks **in the same Team only**. Cleared when the task leaves `working`.

**Register after a successful bind, never before.** An agent that registers and then fails to bind leaves an entry pointing at whatever process actually owns that port — and a consumer forwards into the wrong stack and gets plausible wrong answers instead of an error. Bind collisions are otherwise loud (`EADDRINUSE`) and safe to leave to guidance.

A consumer finding nothing goes to `blocked_on_input` and is woken when the endpoint appears.

### 8.3 `docket-relay`

Reverse-tunnel relay giving authenticated cross-machine service access with no network prerequisite.

```
consumer's client
  → 127.0.0.1:8391          (docketd binds on demand)
    → authenticated tunnel   (HTTP upgrade, forward-scoped grant)
      → docket-relay         (splice by forward id)
        → producer's docketd
          → 127.0.0.1:5432
```

1. Producer's agent registers `{name, port}` after binding.
2. Consumer calls `open_forward(name)`. Control plane checks Team membership, registration, and that the owning task is `working`. Issues a grant bound to `{consumer, service, expiry}`.
3. Consumer's `docketd` binds `127.0.0.1:8391` — **loopback only, never `0.0.0.0`** — and returns the port.
4. Each accepted local connection opens a fresh upgraded connection to the relay presenting the grant. One listener, N tunnels.
5. Relay sends `open-forward{id}` to the producer's `docketd`, which dials its local port and opens its own outbound tunnel.
6. Relay splices by forward id.

**Both ends authenticate independently. Neither authenticates to the other.** No peer key exchange, no service credentials, nothing to distribute.

**Only registered services are forwardable.** Otherwise it is a fleet-wide port scanner, and local-trust services like Postgres with `trust` in `pg_hba.conf` become reachable from any agent in the Team.

**Generic TCP is the primitive.** An HTTP layer sits on top: subdomain per service, never path prefix, wildcard cert, websocket upgrade from day one. Its justification is human-to-service access. Build TCP first.

Per-Team byte counters and rate limits alongside the token budget.

---

## 9. Enforcement checks

1. `completion.criteria` is non-empty at task creation.
2. `namespace` is server-assigned; collision is structurally impossible.
3. Only a lead claim may create tasks.
4. Only a non-agent identity may transition to `completed`.
5. Single claimant per task; claiming machine is accepting work and declares a matching profile name.
6. One Lead per Team; takeover is explicit and logged.
7. Ack timeout and per-task liveness timeout → requeue.
8. Verification retries exhausted → `rejected`.
9. Team token budget ceiling.
10. Team byte allowance and forward rate limit.
11. Forwards resolve only to registered services in the same Team owned by a `working` task.
12. Cancellation carries a disposition enum; `TTL=0` means immediate kill.
13. Token exchange is strictly narrowing.

Nothing else. Any addition that requires knowing what a task is *about* should be rejected outright.

**Subagent depth is the harness's problem.** Fan-out cost is contained by check 9 instead — the budget ceiling bounds spend regardless of tree shape. Enforcement is refusing new dispatch plus `kill`, since Docket does not hold the model keys and cannot stop an in-flight call.

---

## 10. Interfaces

### Agent → control plane (MCP)

**Lead:** `claim_lead` · `release_lead` · `create_task` · `answer_input_request` · `submit_review` · `cancel_task` · `get_team_state` · `list_teams` · `get_machine_group_status`
**Worker:** `claim_task` · `report_result` · `report_blocker` · `request_input` · `register_service` · `open_forward`

Status tools return counts and states — **never prose**. Free text is fetched deliberately, one item at a time, delimited as untrusted. Responses are scoped by credential: a Lead gets full Team state, a worker gets its own task plus registered services and whether a Lead is attached.

**Slash commands are a convenience layer, not the API.** `/docket-teams`, `/docket-machines`, `/docket-lead`, `/docket-status`, `/docket-enroll` ship as MCP prompts. Surfacing prompts as slash commands is client behaviour and not universal, so every command must map onto independently-callable tools. Nothing may be reachable only through a prompt.

Skills ship as MCP resources (`skill://`, SEP-2640), reaching every agent on connect rather than being relayed by the Lead.

### Control plane ↔ runner (closed enum)

**The only frozen interface in the system.** A runner rejects anything outside the vocabulary.

**Outbound:** `dispatch` · `stop(ttl, disposition)` · `kill` · `open-forward`
**Inbound:** `started` · `alive` · `tool-call` · `subagent-spawned` · `exited` · `auth-failed` · `forward-closed` · `rebooted`

**Every message carries a task id.** A machine runs many agents concurrently; there is no implied current task in either direction. This is the change most expensive to retrofit — get it right before anyone enrolls.

`started` (harness up) is distinct from dispatch ack (runner received). Cold start takes time, and their requeue semantics differ: a failed ack means nothing happened, so requeue is free; a death after `started` means side effects may exist.

Nothing in this vocabulary is domain-specific.

### Runner capabilities

`docketd` does five things. The invariant is not that it does little, but that **none of it requires domain knowledge**.

| Capability | Generic | From config |
|---|---|---|
| Process supervision | spawn, signal, kill, liveness per PID | invocation command, stop signal, exit semantics |
| Event relay | heartbeat on own timer, forward events upstream | hook wiring, OTel endpoint, event name mapping |
| Log streaming | tail and stream, drop under pressure | log path, format |
| Relay endpoint | bind loopback, open outbound tunnels, dial local ports | — |
| Credential holding | machine token refresh, inject worker tokens | where the harness reads MCP config |

What it cannot do: touch the workspace, read or interpret task content, decide anything, initiate anything not triggered by a command or its own timer, or reach arbitrary paths or ports.

**`docketd` never listens on a network interface.** Its only listener is loopback, for forwards and optionally for harness event callbacks. Every other connection is outbound — which is why it works behind NAT with no configuration and adds no attack surface to its host network.

**Each task spawns into its own process group, and `kill` targets the group.** A harness spawns children — subagents, tool invocations, dev servers — and killing only the parent orphans them, which is precisely the leak stray-process cleanup exists to catch. Group kill takes the whole task down and touches nothing else, because siblings are in different groups. A harness that cannot be spawned into its own group cannot be killed cleanly on a shared machine.

### Runner config

`docketd` contains no harness knowledge; everything specific is data. The config is therefore the contribution surface — supporting a new harness should be a config file plus a passing conformance run, never a change to `docketd`.

Full schema and a worked Claude Code example: `skills/docket-enroll/references/runner-config.md`.

| Section | Covers |
|---|---|
| `machine` | `work_root` for per-task scratch directories; back-pressure thresholds |
| `profiles` | Named configurations, one required to be `default`. Each carries `spawn`, `stop`, `resume`, `events`, `telemetry`, `logs`, and an optional `max_concurrent` cap. |

**A daemon can drive several harnesses or postures.** Profiles exist for genuinely different setups on one machine: Claude Code alongside Codex, a restricted permission posture for sensitive work, a pinned version being canaried during an upgrade.

A task may carry an optional `profile` string, matched **by exact string equality** at claim time. The control plane never learns what a profile name means — only whether a machine declares one. Absent a request, `default`. Requested-but-absent, the task sits visibly unclaimable. This is deliberately not a capability manifest, which §15 still excludes: profiles are identifiers a human chose, not descriptions Docket reasons over.

**Profiles describe how to run an agent, never what kind of work it does.** `profiles: {frontend, backend}` is task routing disguised as machine config, and it puts the control plane back in the business of meaning.

Three constraints are load-bearing rather than cosmetic:

**`docketd` never invokes a shell.** `command` is argv passed to `execve`, which is what makes it safe to deliver an agent-authored prompt as an argument — and most harnesses require that. There is no shell to inject into. If a harness genuinely needs shell interpretation, it gets wrapped in a script; a shell is never added to `docketd`.

**Two hard prerequisites, neither of which degrades gracefully.** A harness must be an MCP client, since that is a worker's only channel to Docket. And it must run to completion without prompting for approval — a headless agent waiting for a click nobody will make surfaces as a liveness timeout rather than an error, which is the most expensive way to find a misconfiguration.

**`docketd` sets `DOCKET_MACHINE_ID` and `DOCKET_TASK_ID` on everything it spawns**, not configurably. Stray-process cleanup on start scans for its own machine id, which is what makes the restart-equals-reboot guarantee survive a `SIGKILL`ed daemon.

**`events.source: none` is a supported, honest answer.** Liveness degrades to process-alive and progress renders as "not reported." A fabricated event mapping produces a machine that looks healthy and is not, which is worse than a machine that admits what it cannot see.

`work_root` deserves a note: `docketd` spawns each task in `{work_root}/{task_id}`. This is *not* the task's workspace — the runner never interprets the opaque `workspace` blob. It is a unique machine-local scratch directory to start in; the agent constructs its real workspace from what the Lead assigned.

### Concurrency and back-pressure

Machines do not declare a concurrency limit. A declared number is a guess that is wrong in both directions, and agents vary too much in weight for it to mean anything.

Instead `docketd` observes its own load, memory, and disk, and **stops claiming when it is under pressure** — resuming when it clears. Derived, not asked for, consistent with principle 2. A saturated machine keeps running what it holds and appears as `saturated` in the Machine Group view.

This exists to break a feedback loop rather than to ration capacity. Without it, a thrashing machine misses heartbeats on every task it holds at once, all of them requeue, and nothing prevents the same machine from immediately re-claiming them. Back-pressure makes overload self-correcting instead of self-reinforcing.

A profile may declare `max_concurrent` for reasons unrelated to load — a licence limit, a rate-limited provider, a restricted posture kept to one at a time.

Liveness splits accordingly:

- **Machine heartbeat** — `docketd` on its own timer. Loss means every task on that machine is suspect.
- **Per-task liveness** — derived from `started` / `tool-call` / `exited` scoped to a task id, plus process-alive for that PID.

Requeue keys off per-task liveness, so one hung agent is requeued while its siblings keep working.

Token attribution must carry a task id, or budget enforcement cannot tell which Team's ceiling a shared machine's spend counts against.

### Runner restart

**A `docketd` restart is equivalent to a machine restart.** No re-adoption, no local state, no persistence.

- On clean shutdown, `docketd` kills every harness it started.
- **On start, `docketd` kills any stray harness processes before accepting dispatch.** Clean shutdown is not guaranteed — a SIGKILLed daemon kills nothing, and orphaned harnesses keep burning tokens against tasks the control plane has already requeued. Putting the guarantee on start is what makes it survive a hard crash.
- On reconnect, it emits `rebooted` and the affected tasks requeue against the infrastructure counter.

Leads are informed of reboots. A Lead may direct a worker to resume, which means recovering from the harness's own session transcript with a note that the previous run stopped abruptly.

**This makes the session log recovery substrate, not only forensics.** The local transcript survives a reboot and is the primary source; the streamed copy matters when the machine is gone entirely. Transcript retention is therefore load-bearing, not a debugging convenience.

### Buffering

**Runner → control plane:** a bounded in-memory ring. Drop oldest, record a gap marker. Not disk — that reintroduces the state the restart model just eliminated. Depth only needs to cover the liveness timeout window; past that the control plane has already requeued the task and the buffered events describe work nobody is waiting on.

**Control plane → runner: nothing.** Commands are best-effort against a live connection and are never queued. `dispatch` never targets an unreachable machine because it is not `ready`. `stop` and `kill` are moot during a partition — the runner halts its agents on lease-renewal failure, and everything dies on restart anyway. There is no delivery guarantee to reason about and no stale command arriving twenty minutes late.

**Log channel:** bounded, droppable, gap marker.

### Channel separation

| Channel | Priority | Loss behaviour |
|---|---|---|
| Control | Highest | Never dropped within buffer depth |
| Relay forwards | Normal | Per-forward failure |
| Session log | Lowest | Droppable, gap marker recorded |

A `kill` queued behind a dev server's asset traffic or a transcript backlog is a broken escalation path at exactly the wrong moment.

### Telemetry ingest

OTel from harnesses. For Claude Code, `agent_id` / `parent_agent_id` span attributes give the subagent tree and per-subagent token attribution. Harnesses without equivalent signals render as "not reported" — degraded telemetry is normal, not broken.

This is the source of truth for budget enforcement, since Docket does not sit between the harness and the model provider.

### Verifier webhook

An automated verifier posts a verdict against a task in `verifying`, authenticated as a non-agent identity. Docket does not invoke the verifier, poll it, or know what it ran. `review`-mode verdicts arrive through `submit_review` instead — same transition, different door.

### A2A (external boundary only)

Docket does **not** speak A2A internally. It is exposed at the outer boundary for inbound delegation from foreign orchestrators, outbound delegation to agents Docket doesn't own, and federation between Instances. The A2A data model is adopted internally regardless.

---

## 11. Lifecycles

### Machine enrollment

1. SSH to the box.
2. Start the harness.
3. Connect to `docket-mcp`. Present a human-issued enrollment token; exchange for machine credentials. Declare purpose, OS, specs, permission level.
4. Run `/docket-enroll`, which reads the enrollment skill from the server and writes the `docketd` config.
5. Agent guides the human through registering `docketd` as a service. Registration needs sudo, so the human executes it.
6. **Conformance run.** The control plane dispatches trivial tasks and judges the results itself:
   - `started` and tool-call events arrive, correctly attributed by task id
   - heartbeat cadence matches config
   - two concurrent tasks are independently trackable
   - `stop` with short TTL is acked
   - `TTL=0` kills one PID and leaves the sibling running
   - a relay forward round-trips and the listener closes on release
   - a task that would normally prompt for approval completes without hanging
   - every declared profile passes the above independently, plus one cross-profile concurrency case
7. Pass → machine joins the Machine Group as `ready`. Fail → registered but unclaimable, with the failing step named.

The wizard *displays* results; the control plane *determines* them.

Configs are stamped with the generating skill version. `/docket-enroll` is idempotent and re-runnable.

### Cancellation

`stop` carries a TTL and a disposition enum (`preserve` / `discard` / `preserve_and_park`), plus optional free-text reason. Default is `preserve`.

TTL is set by the Lead per situation. `TTL=0` means kill immediately without waiting for ack.

Preservation is the agent's job — persist work in progress to the workspace substrate however that domain does it. The runner does not touch the workspace, so **the kill path is lossy by construction.**

`discard` means removing this task's workspace instance, which is only safe *because* isolation is task-scoped. Under a shared checkout it would destroy a sibling's work.

### Blocked on input

| Kind | Answered by | Resolution |
|---|---|---|
| `question` | Lead or human | answer text |
| `spawn_request` | Lead | new task created, id returned |
| `auth_help` | human | credential provisioned |
| `endpoint_wait` | control plane | woken when service registers |
| `unreachable` | human | artifact or forward could not be reached |

Threaded on the originating task, provenance-tagged. A wait TTL prevents indefinite lease holding: on expiry the agent parks and is redispatched when the answer lands. This is what lets a Team survive its Lead's session ending.

Auth failures report **structured facts** — operation, target, error code, missing scope. The control plane renders the remediation menu from a fixed set it owns.

### Partition

On lease-renewal failure the runner halts its agents. Prefer a stall over two machines doing the same expensive work with divergent results.

---

## 12. Observability

**Machine Group view** — machines, slots used and free, heartbeat age, tasks currently running with owning Team, subagent tree expandable beneath each task. Subagents are children in a tree, not peers: no lease, die with their parent, columns are duration and token spend.

**Team view** — tasks by state, budget and byte burn, registered services, open input requests, last activity, whether a Lead is attached and who. Doubles as the reattachment surface (§4), so it must be consumable as structured data by a Lead. Sorted so idle Teams drift to the bottom.

**Human inbox** — everything waiting on a person across all Teams: permission requests, auth failures, questions, and **tasks awaiting review**. Without it, `review` mode becomes a place work goes to die.

Lead takeovers, machine reboots, and eviction events all appear in the event log.

Views render as MCP Apps (SEP-1865) plus a plain web dashboard.

### Retention

Contractual, not an ops preference.

| Tier | Retention |
|---|---|
| Full transcripts | Hours — and load-bearing, since they are the resume-after-reboot substrate |
| Structured events | Weeks |
| Task records | Life of the Account |

Support access to transcripts is a data access problem. Design redaction in rather than retrofitting it.

Relay traffic is never persisted. Connection metadata is an event; payload is spliced and forgotten.

---

## 13. Security model

**Connected implies trusted, within an Instance.** A machine in the Machine Group is the customer's machine and their blast radius. Enrollment stays install-and-connect.

**But trusting a machine is not trusting its content.** The realistic attack is an agent on a legitimate machine that read a poisoned dependency file, an outside contributor's issue, or a fetched page, then acted with fleet privileges. Inter-agent messages arrive marked as data, never as instruction.

**Cheap eviction, not expensive admission.** Short-lived tokens with refresh held by `docketd`.

**The Instance boundary separates paying strangers, not colleagues.** Container-and-database-per-Instance. Keep it even when someone points out it is less efficient than a tenant column.

**Docket is a designed-in intermediary for customer service traffic.** `docket-relay` carries whatever runs on customer machines between them — the strongest argument for it staying a separate module.

**Docket sends `kill` to processes on customer machines.** Compromise of an Instance is not a data breach; it is remote execution across that customer's Machine Group.

**Forwards are the widest hole in the agent-facing model.** The registration requirement is the only thing between that and lateral movement.

### Credential storage

| Credential | Lives | Protection |
|---|---|---|
| Machine token + refresh | `/var/lib/docket/credentials`, 0600, dedicated service user | Filesystem perms; `systemd` `LoadCredential` where available |
| Worker token | Generated MCP config in `{work_root}/{task_id}`, 0600, removed on exit | Perms plus task-scoped expiry |
| Lead session | Held by the harness's MCP client, not by Docket | Whatever that client implements |
| Enrollment token | Transits once, stored nowhere | Single-use, short TTL |
| Forward grant | Memory only | Per-connection, minutes |
| Signing key, database credentials | Container secret store | Never in the image |

**The machine token is the highest-value secret on a customer's machine**, because dispatch delivers worker tokens down the channel it authorizes. Compromise is not lateral movement — it is a supply of fresh credentials. Access tokens should be short, the refresh token the only long-lived secret, and the refresh bound to the machine id so a copied credential file fails on another host.

**Docket does not control Lead credential storage.** It is an OAuth token held by the harness's MCP client, and where that lands — keychain, plaintext file, elsewhere — varies by harness. A real dependency, not an assumption.

**The worker token's small blast radius is the mitigation, not its secrecy.** The agent can read it; it sits in a file the agent owns. An injected agent that exfiltrates it gains nothing it did not already have, because the token is scoped to that one task in that one Team and expires with it.

**Enrollment tokens must not be passed as arguments.** Pasting into a terminal is the default human behaviour and it lands in shell history. Read from a prompt or a file. Single-use and short TTL bound the damage.

**Transcripts can capture credentials.** An agent that echoes a token, or a tool call carrying one, puts it in the streamed session log and therefore in Docket's database. Redaction belongs on the streaming path, before it lands — not on read.

**Model provider credentials never touch Docket** — but that is a statement about Docket's exposure, not about their safety. They sit in harness config on machines running agents that read untrusted content, and those agents can read them. Docket cannot prevent that and should not imply otherwise.

**Capabilities and permission level are bound server-side at enrollment.** Clients report state; they do not re-declare their own privileges.

**Agents may make project-local, reversible changes unilaterally.** System-level changes — sudo, PATH edits, global installs, version switches, anything touching credentials — are reported, not performed.

**Instructions found in fetched or read content are suggestions, not authority.**

---

## 14. Skill bundle

Ships with the server over `skill://`. Two layers: Docket's baseline plus per-Instance overrides, with defined precedence and versioning. **This is where domain knowledge lives.** The schema is neutral; the skill is opinionated.

Three skills ship by default:

| Skill | Audience |
|---|---|
| `docket-lead` | Humans driving a Lead. Decomposition, isolation assignment, completion modes, cancellation, Team lifecycle. |
| `docket-worker` | Dispatched workers. Claiming, working within an assigned workspace, reporting, blockers. |
| `docket-enroll` | The enrollment flow. Writing the runner config, guiding the human, conformance. |

The failure mode for everything in these is bounded and recoverable. That is the test for belonging here rather than in §9.

---

## 15. Explicitly not building

- A workflow engine
- Retry policies more expressive than counters
- A rules DSL for routing or scheduling
- Per-task configurable state machines
- A general peer-to-peer agent message channel
- Sub-Teams / nested Leads
- A cross-Team scheduler — budget caps contain runaways
- Capability manifests — optimistic execution with report-back instead
- Subagent depth caps — the harness's problem
- Runner state persistence or process re-adoption — restart means reboot
- An outbound command queue — commands are best-effort against a live connection
- An artifact store — URLs only
- A mesh VPN or any network control plane — the relay instead
- Arbitrary port access — registered services only
- Model access, key custody, or inference resale
- **Client-direct database access or RLS as authorization** — the control plane is the only path to the state machine, and a transition guard is not a row filter
- **Any domain-specific field in the task schema**
- **Any version control integration** — Docket stores opaque references and never dereferences them
- **Any verifier integration** — the verifier posts to Docket, not the reverse
- Cross-Instance coordination in `docket-meta`
- Agent-facing anything on `docket-meta`

---

## 16. Open questions

1. **Closing a Team has no affordance.** The Lead owns it, but nothing exposes it. Needs a command and a decision about what happens to outstanding tasks.
2. **Does `review` mode need a reviewer assignment?** Currently any Lead or human on the Team may accept. Named reviewers are more correct for larger Teams and a step toward a workflow engine.
3. **Verifier-gated or merge-gated completion** for the code case. Skill-level guidance, but it shapes the default bundle.
4. **Runner compatibility window.** Deferred to alpha, when there is real data on how stale installed runners get.
5. **Relay capacity and COGS.** Bandwidth is a direct cost line.
6. **When does self-hosting arrive**, and does it ship the whole Instance or only `docket-relay`?

---

## 17. Build order

1. Control plane: state machine, Postgres schema, auth, the thirteen checks.
2. `docketd` against Claude Code: dispatch, stop, kill, heartbeat, tool-call events, concurrent slots.
3. MCP server: Lead and worker tool sets, slash command prompts, skill serving.
4. `/docket-enroll` plus the conformance run.
5. Machine Group view, Team view, human inbox.
6. `docket-relay`: TCP primitive first, HTTP layer after.
7. `docket-meta`: Account records, Instance provisioning, image rollout.
8. **Chaos test.** Kill a runner mid-task with siblings running. SIGKILL `docketd` and restart it. Partition a machine. Cancel with each disposition. Fail verification three times. Sever a forward mid-transfer. Evict a Lead mid-decomposition. Close a laptop and reattach.
9. Second runner config against a different harness.
10. A non-code skill bundle — the forcing function separating coordination assumptions from software assumptions.
