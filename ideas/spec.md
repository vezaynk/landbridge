# Landbridge — Specification

**Status:** Draft. Nothing here has been run.

Landbridge coordinates AI agents across multiple machines. A human drives a *Lead* agent, which decomposes work into tasks; *worker* agents on other machines claim and execute them. The control plane keeps the record and enforces procedure. It never reads the work.

Landbridge is the communication, runner, and relay layer. It does not supply models, resell inference, or hold model provider credentials — customers bring their own keys, which live on their own machines and never touch Landbridge infrastructure.

**Coding is the primary use case, not a built-in assumption.** The schema is domain-neutral: Landbridge knows a session has a description and an optional workspace, and nothing about what either contains. The shipped skill bundle is code-oriented because that is where most of the demand is, but repositories, branches, and test suites appear only in guidance — never in the data model.

Landbridge ships first as a hosted product. Self-hosting comes later.

---

## 1. Terminology

| Term | Meaning |
|---|---|
| **Account** | Billing entity. Owns one or more Instances. |
| **Control Plane Instance** | One deployed control plane. The isolation boundary — its own database, signing key, endpoint, skill bundle. |
| **Machine Group** | Every machine running `landbridged` within an Instance. A resource pool, not an owner. |
| **Team** | A group of agents working across the Machine Group under a single Lead. Many Teams per Instance. |
| **Lead** | A harness client driven by a human. Creates tasks, answers questions, holds the plan. |
| **Worker** | An agent executing one task. May spawn local subagents. |
| **Machine** | One enrolled host running `landbridged` and one or more harnesses. |

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

> **Note on scope.** Landbridge is thin in *logic* and not thin in *role*. `landbridge-relay` carries service traffic between customer machines (§8.3). Principle 1 still holds — it moves bytes without interpreting them — but the product is a data path, not only a coordinator.

---

## 3. Architecture

### What Landbridge ships

| Component | Role |
|---|---|
| `landbridge` | Control plane: state machine, event log, enforcement. One process, one Postgres, one Instance. |
| `landbridge-mcp` | MCP server surface agents connect to. Same process as the control plane. |
| `landbridge-relay` | Authenticated byte relay between machines (§8.3). Separate module, separately deployable. |
| `landbridged` | Per-machine runner daemon. Config-driven. |
| `landbridge-meta` | Provisioning service. Creates, suspends, and destroys Instances; owns Accounts and image rollout. |

`landbridge-relay` is a distinct module rather than a control plane feature specifically so that who operates it stays a deployment decision.

### External dependencies

| Dependency | Relationship |
|---|---|
| **Harness** | Claude Code or any MCP-capable agent CLI. `landbridged` starts and stops processes it did not build, described entirely by config. |
| **Model provider** | Customer's keys, customer's machines, customer's bill. Landbridge never sees them. |
| **Workspace substrate** | Wherever work products live — a version control host, a document store, a filesystem. Landbridge stores an opaque reference and never dereferences it. |
| **Adjudication** | Not a module. Completion is a Lead-session or human verdict, never the task's own worker (§7, §9 check 4) — CI and tests are evidence a Lead gathers itself, not a verdict-issuing actor. External automated completion stays possible by holding a Lead-class credential (e.g. a CI webhook), but Landbridge requires no dedicated verifier role. |
| **Object storage / file transport** | Optional. Artifacts are URLs (§8.1). |

Every one of these is a place where a less disciplined design would grow an integration, a credential store, and an opinion.

### Deployment

Hosted. One Instance per customer deployment, as a container behind public TLS. Instances are completely independent: separate database, signing key, endpoint, skill bundle.

Because the control plane is operated rather than distributed, it can be fixed by deploying. **`landbridged` cannot** — it runs on customer machines and may go a year without being touched. The runner contract in §10 is therefore the only frozen interface in the system.

### `landbridge-meta`

The human-only provisioning control panel: a server-rendered web panel (no MCP SDK reference, so it is structurally not an MCP server) behind an operator passphrase — hash-configured, fail-closed when unset, mirroring the dashboard's operator door. A single operator today; self-serve signup later. It owns Account labels (a name on an Instance; no billing yet), Instance lifecycle (create, suspend, resume, destroy), Instance provisioning, and image rollout.

It runs over a **pool of Docker hosts** behind an `ISubstrate` seam: the local unix socket (the pool-of-one), or a remote Docker Engine API over mutual TLS (the host record carries the client cert + CA). Placement picks a host at create — the operator chooses, defaulting to the least-loaded.

An **Instance is a per-host recipe**: a dedicated Docker network, a Postgres container on a named volume, a `landbridge-mcp` container, and a `landbridge-relay` container, co-located and independent. Lifecycle is a **resumable saga** — `provisioning → ready → suspended → destroyed`, plus `failed(step)` — where every step is idempotent (Docker objects are adopted by name/label) and checkpointed, so a meta restart mid-provision resumes from the first incomplete step or an operator retries a stall. Destroyed ≠ suspended: destroy removes containers, network, **and volume** (with a typed-name confirm) and tombstones the record; suspend stops the containers and drops the edge routes but keeps the volume, config, and secrets for resume.

**Every transition has a state, and the row is the lock** (as-built, 2026-08-14). Alongside the five above the Instance also reads `suspending`, `resuming`, `upgrading`, or `destroying` while that transition runs, and each transition starts by *claiming* the row — a conditional state change that only succeeds from the states it is legal from. That is what makes the saga's promise hold for the transitions that are not a first provision. Resume and upgrade **are** the saga: each records what it wants (an upgrade writes the new tag), marks the checkpoints its intent falsified, and runs the same ordered steps, so both are checkpointed and resumable, and an upgrade leaves Postgres and its volume alone because those checkpoints still stand. Written instead as their own container sequences, an upgrade removed both plane containers up front and recorded nothing in flight — so the ordinary failure of the thing it waits for, the plane not answering in time, left the row persisted `ready` with no relay container and only a log warning, and a destroy arriving mid-upgrade walked straight in. Reconciliation is therefore also **periodic rather than startup-only**: any Instance left mid-transition with nothing driving it is re-entered — the three heading for `ready` by running the saga, `suspending`/`destroying` by finishing in their own direction, never converged upward against the operator's intent — and a `failed` one is retried on a backoff, so a tag that will never pull is not re-pulled every sweep.

Meta generates each Instance's secrets **before any side effect** and injects them into the containers: the operator-passphrase **hash** (the plaintext is shown once at create and never retained), the private Postgres connection string, the public MCP URL, and a relay bearer shared by the plane and relay. It sets `Landbridge:MigrateOnStartup` so a fresh Instance self-migrates and upgrades re-migrate, and it **never** sets the dev-only gates (dev-seed, insecure client metadata). It retains the secrets it must re-inject on resume/upgrade (DB password, relay bearer); only the passphrase is shown-once-and-discarded.

An edge **Caddy**, driven live over its admin API (pure HTTP, routes keyed by a stable `@id`), publishes **two routes per Instance** — the MCP endpoint at `<name>.landbridge.<domain>` and the relay endpoint at `relay-<name>.landbridge.<domain>`, since `landbridged` dials the relay directly (§8.3). Wildcard DNS and Caddy's own TLS automation are operator-side prerequisites. A down edge never blocks suspend or destroy — route removal is best-effort and reconciled later.

Image rollout pins a tag per Instance; upgrade recreates the mcp + relay containers on the new tag with Postgres and its volume untouched (migrations run on plane startup). No canarying in v1. Meta keeps its **own Postgres**, separate from every Instance's database.

It does **not** route work between Instances, aggregate a cross-Instance view, or hold shared agent identity. **Deferred:** billing, canarying, self-serve signup, and per-Instance preview/SNI routing.

**No agent access, ever.** It is not an MCP server. Separate network, separate credential class, human-only.

### Datastore

Postgres per Instance. Not a backend platform — client-direct table access would bypass the state machine, which is the one thing the architecture cannot tolerate.

- `SELECT … FOR UPDATE SKIP LOCKED` for the dispatch transaction — the one concurrency-critical path.
- `LISTEN` / `NOTIFY` for dispatch push. Requires a session-mode or direct connection; transaction-mode poolers break it.
- `JSONB` for event payloads and opaque task blobs.

Split hot from cold in the same database. Never join a task-state read against the event firehose.

---

## 4. Teams and Leads

A Team is a unit of human-authorized work. It owns a scope declaration and a set of tasks. It terminates. (It owned a dollar budget too, until that was removed — §9's note.)

**The Lead is a harness client the human drives.** Not a dispatched agent, not a daemon. Someone opens their harness, attaches to a Team, and works. The human is the event loop — which is why the Lead needs no lease, no wake conditions, and no runner.

- A Lead's machine does not need `landbridged`. Enrollment and attachment are independent choices — the one thing it buys is the §8.3 human path: only a Lead that has *bound* an enrolled machine can open a forward onto it for its human. Leading a Team never requires it.
- Lead authority is the human's session scoped to `{team, lead}`.
- Multiple Teams run in parallel within an Instance. Leads have no channel to each other; the human is the channel.
- There are no sub-Teams.

**A Lead may act unattended, including adjudicating completion (§7, §9 check 4), without a human in the loop for each verdict.** This is not a new authority: the human remains the root, and delegation happens once, at the lead-claim, where the human's session authority descends to the Lead (§5 credential descent). What a Lead may never do is complete a task its own worker produced — the doer/judge split holds structurally regardless of who is watching. Judgment calls a human must own are what `review` mode is for (§7).

### Claiming, releasing, and takeover

One Lead per Team, enforced as a conditional claim — the second claimant is refused.

A Team can be left leadless by explicit release or by the human's session ending. A leadless Team is claimable and is a **visible state**, not an invisible one: it explains why nothing is progressing.

**Takeover is permitted and evicts the incumbent.** Claiming an actively-held Team shows who holds it and their last activity, and requires confirmation. Claiming a leadless Team is silent.

Two requirements on eviction:

- **The evicted session must learn why.** Its next privileged call fails with an explicit reason — evicted by whom, when — not a bare authorization error. This is the only place in the design where one human's action is invisible to the person it affects, and a generic 403 produces an agent inventing explanations for a permission denial.
- **Every takeover is a logged event.** Two people contending over a Team should be legible afterward.

### Attachment and reattachment

**The session is ephemeral; the Team is durable.** A human closes their laptop, workers keep running, questions accumulate.

Attaching gives a fresh Lead an empty context window. It must read its way back from control plane state: task states, open questions, registered services, recent results, what the work has cost so far (§12). Takeover is not a handoff — the incumbent's context is not recoverable — so the reattachment path serves both cases and the Team view must be rich enough to reconstruct from.

---

## 5. Authentication and authorization

The control plane is both the OAuth 2.1 authorization server and the resource server for its Instance.

### Credential classes

| Identity | Obtained | Lifetime | Authorizes |
|---|---|---|---|
| **Human** | auth code / device flow | session | create Teams, approve permissions, adjudicate completion, dashboard |
| **Lead** | human session, claimed against a Team | session or until evicted | create tasks, answer questions, **adjudicate completion** (§7), read Team state, bind its human's machine and open forwards onto it (§8.3) |
| **Machine** (`landbridged`) | enrollment token → client credentials | long, refreshed | runner channel, log stream, relay tunnels |
| **Worker** | minted at dispatch | task lifetime | MCP tools, scoped to `{team, task, worker, instance}` |

There is no verifier credential class. Completion is a Lead-session or human verdict, never the task's own worker (§7, §9 check 4); an external automated gate, if wanted, holds a Lead-class credential rather than a role of its own.

Two derivation paths, deliberately asymmetric: a Lead's authority comes down from a human directly; a worker's is minted by the control plane from the Lead's dispatch decision.

### Worker identity derives from dispatch

A worker never authenticates. The control plane dispatches task X to machine M, mints a token carrying exactly those claims, and `landbridged` injects it into the harness's MCP config.

This makes the authority table in §6 structural rather than checked. A worker cannot create a task because its token carries no lead claim — not because an `if` statement rejected it.

**Each dispatch mints a distinct worker instance.** The token carries `{team, task, worker, instance}`; requeue or redispatch revokes the predecessor instance's token, and worker-triggered transitions are accepted only from the incumbent instance. An orphaned harness — SIGKILLed daemon, healed partition — holds a token that is already dead: it cannot flip the task's state under the new worker, and it cannot interleave into a resumed transcript (§11).

### Bootstrap

A human-issued enrollment token, single-use and short-lived, exchanged by `landbridged` for machine credentials during `/landbridge-enroll`.

`landbridged --enroll --control-url <plane>` performs the exchange at `POST /enroll`, reading the enrollment token from `--enroll-token-file` or stdin — never argv (§13). The short-lived access token is re-minted at `POST /machine/refresh` (proactively at ~50% of its lifetime and reactively on a 401 reconnect), and credentials persist 0600 under the state dir (`--state-dir`, else `$XDG_STATE_HOME/landbridge`, else `~/.landbridge`).

### The invariant

**No path from a worker credential to a lead claim or a human session.** Token exchange must be strictly narrowing.

### Token format

Opaque, not JWT. Revocation is the priority — un-trusting a machine must take seconds, and requeue must kill a worker-instance token just as fast.

**Un-trusting a machine is three things, not one** (as-built, 2026-08-14). Revoking its credential rows is only the part the credential store can express: the `/runner` socket authenticates once at the upgrade and is never re-checked, so it stays open and dispatchable afterwards, and a worker token is scoped to `{team, task, instance}` with no machine id on it, so a by-machine credential sweep reaches none of the workers on the box. So the revoke also drops and closes the machine's connection — requeueing what it held, exactly as a dead socket does — and revokes the worker instances recorded as having run there. All three, or the machine is not un-trusted. The surface is one human-only action on the §12 Machine Group view.

### MCP alignment

Targets the released MCP spec (2025-11-25 as of this draft), with the 2026-07-28 release candidate tracked rather than assumed — the RC removes protocol sessions, server-initiated requests, and SSE resumability, and deprecates DCR in favour of Client ID Metadata Documents, so `landbridge-mcp` ships dual-version and every dependency below names its fallback.

Adopted where already real: Protected Resource Metadata (RFC 9728), Resource Indicators (RFC 8707) for audience-bound tokens, Client ID Metadata Documents (supported by Claude Code today), and `application_type` declaration (SEP-837) since harnesses are CLI clients. Speculative dependencies are progressive enhancement, never load-bearing: `skill://` (SEP-2640, open draft) falls back to plain MCP resources the dispatch prompt directs the worker to read; MCP Apps (SEP-1865, draft extension) sit behind the plain web dashboard, which is §12's primary surface.

MCP hardens *authentication*. Every authorization decision in §6 is Landbridge's own.

---

## 6. Session occupancy and the message machine

A session is a durable object. It has no terminal state. Two facts live on the same row:

1. **Occupancy.** Desired and observed share one vocabulary: `none | on_disk | running`. Mismatch *is* the in-flight window (`desired=running` / `observed=none` is a claim waiting for `started`). `health` is mechanical only (`ok | failed`). `hidden` is a list filter, not a phase.
2. **The message exchange.** At most one outstanding envelope per session: `idle | awaiting_lead | awaiting_permission | awaiting_report | awaiting_pull`. Busy is derived from the envelope.

| Occupancy / health | Meaning |
|---|---|
| `desired=running`, `observed=none` or `on_disk`, instance null | Claimable (dispatch SKIP LOCKED). Profile-matching machine, not under back-pressure. |
| `desired=running`, instance set, `observed≠running` | Claimed, spawn in flight. Ack timeout and the aliveness clock cover it. |
| `observed=running` | Process up. Seat occupied. |
| `desired=on_disk` | Lead (or wait TTL, when configured) released the seat. `park_session`. Later wake is `session/load`. |
| `health=failed` | Mechanical loss (ack timeout, liveness, no-progress, process exit, reboot, turn ended with no report). Token revoked, workspace kept. The plane does **not** requeue. Retry is the Lead: `answer_input_request` on the **same id** (`session/new`). |

| Message | Meaning |
|---|---|
| `idle` | No outstanding envelope. Worker may be cranking (`running`/`running`). |
| `awaiting_lead` | Prose question, spawn request, auth help. Process may have ended the turn. |
| `awaiting_permission` | Live ACP wait inside `session/request_permission`. Occupancy stays `running`. `park_session` is refused. Process exit here is a failure. |
| `awaiting_report` | Worker called `report_result`. Process stays. Lead accepts, discards, replies, or parks. |
| `awaiting_pull` | Lead spoke; worker has not pulled `get_session`. |

`hidden=true` is accept, discard, or cancel on a healthy row. Same-id wake of a hidden healthy row is refused. New work is `create_session(continues:)` against a healthy transcript. `hidden` plus `health=failed` still allows same-id retry.

A **permission** wait is live: the ACP session stays up inside `session/request_permission`. A prose question may end the turn while the process stays idle for a follow-up `session/prompt`. Wait TTL is off by default; `park_session` is how a Lead frees the machine.

Dispatch claims on occupancy, not on a session phase. The claim transaction is SKIP LOCKED: target machine `ready`, not under back-pressure, declaring a matching `profile`. Workers do not claim; they are dispatched (§5). After the claim: instance and token minted, then best-effort spawn. Failed send is `health=failed` (`AckTimeout`).

Leaving `observed=running` because of deactivate, accept, discard, cancel, or mechanical fail clears registered services and relay forwards. A report does not.

No row requires reading a task description or interpreting its criteria.

**Two counters, not one.** Verification failures and infrastructure losses are different things. A machine rebooting three times is not a verdict on the work. Infrastructure losses increment `InfrastructureRequeues` and record **why** (`LivenessLossReason`). The plane does not auto-requeue and does not cancel on the cap; `health=failed` waits for the Lead. The cap is per-task, fixed at creation from control-plane config, and is observability. Non-positive means uncapped as a counter.

Accept and discard are Lead or human verdicts (**never** the session's own worker — doer/judge). Provenance (`lead-session` \| `human`) is recorded. Discard hides; it is not a retry loop. More work on the same worker is `answer_input_request`.

**Two targeting classes.** *Profile targeting* (the default) routes a claimable session to any `ready` machine declaring its `profile`. *Continuation targeting* (`create_session(continues: <session-id>)`) resumes a prior session's harness conversation: the new session is seeded at creation — `continues_session_id` (lineage), the machine that last held/ran it (live lease, preferred machine, or the most recent `worker_instances` row), that session's `harness_session_ref`, and an `on_machine_gone` policy — as affinity, so its *first* dispatch prefers that machine and hands the runner the session ref to resume the transcript (§11), under a **new session id and a freshly minted worker token** (§5). Continuation is **same-Team only**. `profile` is required; a name the preferred machine does not declare is a creation-time error. Continuation from `health=failed` is refused (retry is the same id). If the preferred machine is gone at dispatch, `on_machine_gone` decides: `degrade` (default) cold-starts on any profile-matching machine and records that conversational memory was lost, or `pin` waits for that machine to return. Forking is legal — several continuations of one session each resume the same transcript, and each stamps its own new session ref after its first turn.

---

## 7. Session schema

**Typed**

| Field | Notes |
|---|---|
| `completion.provenance` | Set on completion: `lead-session` or `human` (§9 check 4). Null until completed. |
| `namespace` | Server-assigned `team-{id}/session-{id}`. Guaranteed unique. What an agent maps it onto is convention. |
| `workspace` | Optional opaque context the Lead may pass (repo, package, base ref). Shape defined by the skill. Not isolation — the worker stays in `{work_root}/{session_id}`, uses a worktree, and binds a random port. |
| `team_id`, `parent_task` | Lineage. |
| `expected_duration` | Lead's guess. Distinguishes stuck-short from long-running. |
| `profile` | Required runner profile name. Exact-match routing; the control plane never interprets it. There is no reserved `default`. |
| `attempt` | Server-maintained counter, incremented on every requeue and redispatch. Visible to the worker, so a dispatched agent knows it may be inheriting a dirty workspace and should inspect before trusting or overwriting (§11). |
| `author_identity` + `is_human` | **Provenance.** Lets the receiving side treat human- and agent-authored instructions differently. |

**Prose:** `description` (the whole brief — what to do and how it will be judged; there is no separate completion-criteria field), `result_summary`, `blocker_note`.

### Who completes

The worker does not decide it is done — and a session's own worker can never complete it (§9 check 4, the doer/judge split, the same shape as a subagent that never accepts its own work). The plane trusts the Lead. A human session can also complete. Completion records its provenance (`lead-session` \| `human`). There is no completion mode and no human-confirmation gate: if a judgment is a person's to own, the Lead escalates rather than the plane refusing.

This is the Claude Code shape: the Lead decomposed the work and holds the plan, so it is the right judge of whether a session met its bar — but it judges *another* worker's output, never its own, and it judges against evidence it gathers itself. Landbridge hands it no verdict: CI and tests are that evidence, not a verdict-issuing actor, and the deterministic-verifier role is deliberately not something Landbridge runs (§15). Reject cheaply, accept carefully.

### Workspace and isolation

**The worker isolates itself.** Several sessions share a machine, including several from the same Team. `landbridged` starts each worker in `{work_root}/{session_id}`. The worker writes only there, uses a git worktree for repo work, and binds a random loopback port. The Lead does not assign ports or working directories. `workspace` on the session, if present, is context — which repo, which package — not a lock and not a path the worker is entitled to mutate.

The general rule: **each concurrent session's mutable state lives under its session directory; anything shared is read-only.**

**One deliberate exception: a continuation inherits its predecessor's working directory** (§11), whether or not it resumes that agent's transcript. That is what a continuation is *for* — carrying on the same work, in the same session directory — so a continuation inherits the directory too; it still needs the worktree and artifacts the predecessor left. (Transcript resume additionally requires it, since a harness session is resumable only from the directory that created it, but does not define it.) So two session ids can share one directory, by the Lead's own choice in asking for a continuation. Nothing else about them is shared: each keeps its own identity, credential, liveness, and transcript.

---

## 8. Artifacts, endpoints, and connectivity

### 8.1 Artifacts — a URL, and nothing else

The control plane stores a string and has no opinion about the scheme. It does not move bytes, broker grants, or register IDs. Maps to A2A's `url` part.

**No durability guarantee.** A URL to a laptop is live only while that laptop is up. Anything downstream depends on belongs in the workspace substrate.

Unreachable URLs surface as `blocked_on_input`, never a mystery timeout.

### 8.2 Live endpoints — advertised while working

A task registers `{name, port}` on its record while `working`. Visible to other tasks **in the same Team only**. Cleared when the task leaves `working`.

**A name is the Team-scoped address, so one live registration holds it.** Every resolver — `open_forward`, an §8.4 preview, the §8.3 human path — is handed a name and a Team and nothing else, so two holders make "which port does this reach" a raffle. Re-registering a name a task already holds *corrects* that entry (a service restarted on a new port); another task claiming a live name is *refused*, so the second worker picks another name instead of silently redirecting the first one's consumers. A finished task's name is free again, since registrations do not outlive `working`. Uniqueness is a database constraint, not just a check, so two concurrent registrations cannot both win.

**A preview resolves by `(task, name)`, not by name.** §8.4 mints a label against one task's service and records which; resolving the label by name alone would let it outlive its subject and reach whatever answers to that name later — a URL reaching a service its holder never exposed.

**Register after a successful bind, never before.** An agent that registers and then fails to bind leaves an entry pointing at whatever process actually owns that port — and a consumer forwards into the wrong stack and gets plausible wrong answers instead of an error. Bind collisions are otherwise loud (`EADDRINUSE`) and safe to leave to guidance.

**A registration may advertise a process the registering task does not own.** With §10 operator-declared services, the process is supervised by `landbridged` independently of any task, so its lifetime is no longer bounded by the holder task's — the registration and the process can now disagree in a way this section originally could not produce. The failure that matters is not the refused connection; it is the **successful** one, dialing a port whose service has died and something else has taken.

So **the machine that owns the process verifies it at dial time.** When a forward's dial target is a port belonging to a declared service that is not currently running, `landbridged` refuses the dial instead of connecting, and the refusal reaches the consumer as a reason ("the service backing this registration is not running") rather than a bare connection error. A port `landbridged` declares nothing about is dialed as before — it may be a worker-started listener, and refusing those would break registrations that are entirely correct. This closes a hazard that predates services: until something knew which listener was *intended*, nothing could tell a live service from an impostor on the same port.

A consumer finding nothing goes to `blocked_on_input` and is woken when the endpoint appears — the park path, like every other wait (§11).

### 8.3 `landbridge-relay`

Reverse-tunnel relay giving authenticated cross-machine service access with no network prerequisite.

```
consumer's client
  → 127.0.0.1:8391          (landbridged binds on demand)
    → authenticated tunnel   (HTTP upgrade, forward-scoped grant)
      → landbridge-relay         (splice by forward id)
        → producer's landbridged
          → 127.0.0.1:5432
```

1. Producer's agent registers `{name, port}` after binding.
2. Consumer calls `open_forward(name)`. Control plane checks Team membership, registration, and that the owning task is `working`. Issues a grant bound to `{consumer, service, expiry}`.
3. Consumer's `landbridged` binds `127.0.0.1:8391` — **loopback only, never `0.0.0.0`** — and returns the port.
4. Each accepted local connection opens a fresh upgraded connection to the relay presenting the grant. One listener, N tunnels.
5. Relay sends `open-forward{id}` to the producer's `landbridged`, which dials its local port and opens its own outbound tunnel.
6. Relay splices by forward id.

**Implementation note (this deployment).** The relay holds no `landbridged` channel of its own, so the **control plane** — which does — relays `open-forward` to *both* ends over the runner channel: the producer end (step 5) and the consumer end (which carries the bind instruction of step 3). Both ends receive the same single-use-per-role grant and dial the relay themselves. The consumer's bound loopback port returns to the control plane as a `forward-opened` event, and `open_forward` hands the worker a ready `{host, port}` — the grant and relay URL stay inside `landbridged` and never reach the agent. v1 is one tunnel per forward id (the grant is single-use per role and the relay pairs exactly two ends), so the consumer listener accepts exactly one connection; the step-4 "one listener, N tunnels" generalization is deferred.

**Both ends authenticate independently. Neither authenticates to the other.** No peer key exchange, no service credentials, nothing to distribute.

**Only registered services are forwardable.** Otherwise it is a fleet-wide port scanner, and local-trust services like Postgres with `trust` in `pg_hba.conf` become reachable from any agent in the Team.

**Generic TCP is the primitive.** An HTTP layer sits on top: subdomain per service, never path prefix, wildcard cert, websocket upgrade from day one. Its justification is human-to-service access. Build TCP first; the HTTP layer is specified in §8.4.

**A human is a consumer too — the non-HTTP human path.** §8.4's preview covers a human reaching an *HTTP* service with no `landbridged` on their side. It cannot cover `psql` against a worker's Postgres, and that is the case a person most often needs. The consumer end of a forward was never really a task: it is a **machine**, and a worker's is merely derived from its dispatch. So a **Lead** may be the consumer, with its human's own machine as that end — the same single grant presented once per role, the same two `open-forward` commands, the same splice.

Two gaps close for that to work:

- **A Lead binds a machine, explicitly.** A Lead is a harness client a human drives (§4) and its machine need not run `landbridged` at all, so nothing is derivable — the human *says* which enrolled machine is theirs, and can revoke it. The binding keys on the **human**, not the Lead credential or the Team: the box on a person's desk outlives their session and spans the Teams they lead, and a takeover therefore does **not** inherit the evicted human's machine — the new Lead has its own binding or none. One live binding per human and per machine (moving desks is unbind-then-bind; two people never both claim one box, which would land one person's forward on the other's machine). Visible in `get_team_state` and on the §12 Machine Group view, since a bound machine is a Lead-facing forward target.
- **The grant binds no consumer task.** Exactly like an §8.4 preview mint: the consumer is not a worker instance, so the grant records only the producer and the Team. Check 11 is unchanged and Team-scoped — a Lead reaches nothing a worker in its Team could not already reach, and another Team's service reads as not-registered.

**The human's forward is one connection, briefly available.** v1 is one tunnel per forward id (§8.3 above), so the returned address carries exactly one connection — one `psql` session, not a pool — and the consumer listener closes if nobody connects within the bound accept window. A Lead therefore opens it *when its human is ready to connect* and opens another for another session. Once spliced, the ordinary rule holds: the connection survives grant expiry and lives until the owning task leaves `working`.

**A grant is a connection-establishment credential.** It is checked when a tunnel opens; an established splice persists until the owning task leaves `working`. A database session or websocket is never severed mid-flight by grant expiry, and no renewal path needs to exist.

**"Until the owning task leaves `working`" is a bound something has to enforce, and revoking the grant is not it** — a grant gates only the *next* open, so a splice already running has no handle in it. That is what `close-forward{task, forward_id}` is for: the same effect that clears the task's registered services and revokes its grants sends it to both ends' machines, and each cancels that forward — ending an established splice through the ordinary teardown, and closing a consumer listener that never accepted. Without it the bound held only by accident, where the plane happened to kill the worker and the producer's sockets died with it; on `report_result` or `cancel_session`, where nothing is killed, the tunnel simply outlived the task that authorized it. This is the one addition to §10's frozen outbound vocabulary that the section needed, and it is skew-safe in the usual way: a `landbridged` that predates it rejects the envelope and behaves exactly as it does today.

Per-Team accounting (§9 check 10): the forward rate limit is enforced at grant mint, and the relay counts the bytes it splices and reports them to the plane over the plane-facing HTTP contract. The counting gates nothing — an established splice is never severed, so there is no byte allowance to breach; §9's as-built note records why.

### 8.4 HTTP preview layer

The HTTP layer of §8.3 (subdomain per service, wildcard cert, websocket upgrade), specified now that the TCP primitive is built. Its sole justification is **human-to-service access** — a shareable preview URL for a registered service, needing no `landbridged` install on the human's side. It supplements, and does not replace, the `landbridged`-loopback path of §8.3, which remains the mechanism for non-HTTP services (e.g. Postgres) and machine-to-machine forwards.

**Topology — a separate module on top of the tunnel.** The preview frontend is its own separately-deployable module (§3), not a change to the pure byte-splicer of §8.3. It is a TLS frontend on a wildcard origin (`*.preview.<domain>`; domain and cert are its config — a provided PEM to start, ACME DNS-01 later) that terminates HTTPS and routes strictly by **Host header** — subdomain label, never path prefix, so cookies and absolute paths in the served app are never rewritten. The frontend *is* the consumer end (there is no consumer `landbridged`): for each browser connection it dials the relay's tunnel as the **consumer** for a freshly-minted forward id and reverse-proxies the request — including a websocket upgrade — while the producer's `landbridged` dials the matching **producer** end; the relay splices them by forward id exactly as §8.3, unchanged. The grant and splice rules of §8.3 hold (grant checked on connect; a live splice survives until the owning task leaves `working`).

**Subdomain labels are opaque and unguessable.** A label is a random token, never `service-task-team`; structure is never encoded in the hostname. The label is the lookup key into a preview mapping `{team, task, service, expiry, auth-policy}`.

**One forward id per browser connection — the splicer is unchanged.** A browser opens many parallel connections; rather than generalizing §8.3's one-connection-per-forward-id splice, the preview mints a **fresh forward id per browser connection**, so the pure relay pairs each exactly as it already does. N browser connections are N forward ids orchestrated by the control plane, and the producer `landbridged` **dials on demand** — a fresh producer tunnel per connection — instead of the single pre-dial of the machine-to-machine path. (Per-connection orchestration cost is acceptable for preview traffic; a pooled generalization can come later.)

**Auth is per-mint, gated by default.** A mint may explicitly opt into a **public capability URL** — the unguessable label alone admits the request — which then carries a short mandatory TTL. Public is the exception and always time-boxed; gated is the default, and requires a §12 operator session.

The preview lives on `*.preview.<domain>`, a different origin from the dashboard, so the operator's `landbridge_session` cookie is **never** widened to reach it — because a browser's request bytes are spliced verbatim into team workload code, an operator session on the preview origin would be exfiltratable. Gated auth is therefore a **redirect + per-label cookie** handoff (an oauth2-proxy shape): a gated request with no preview session is 302'd to the dashboard origin (`/dashboard/preview-auth?label=…&return=…`), where the existing host-scoped `landbridge_session` confirms the operator may reach the label's Team and the control plane mints a **one-time, short-lived code** (in memory — no schema); the browser carries the code back to the preview origin, the frontend exchanges it at the plane for a per-label preview session and sets that as its own short-TTL `HttpOnly` cookie on the preview origin. Subsequent connects present that cookie, which the plane validates alongside the grant. A `Bearer` is the tooling path — a Lead validates directly and receives a plain 401 rather than a redirect. And on **every** spliced request the frontend strips the `Authorization` header and the `landbridge_session`/`landbridge_preview` cookies from the head before forwarding — the one deliberate exception to never-rewrite, so auth material never reaches the previewed service.

**Minted two ways (§10, §12).** The owning task's worker mints via `open_preview(name)`, receiving the URL to hand back in a report; a human mints from the §12 dashboard's registered-service list. Both produce the same mapping; the public/gated choice is set at mint. Only a registered service owned by a `working` task in the caller's Team is previewable (check 11).

Preview traffic rides the same per-Team accounting as any other forward (§9 check 10): each browser connection mints a grant, so preview load counts against the forward rate limit, and its bytes are counted and reported like any other splice.

---

## 9. Enforcement checks

1. `description` is non-empty at session creation. `profile` is required (check 15): an exact name from `list_profiles`.
2. `namespace` is server-assigned; collision is structurally impossible.
3. Only a lead claim may create tasks.
4. Completion comes from a Lead or human credential, **never the task's own worker** (doer/judge split); verdict provenance (`lead-session` | `human`) is recorded on the completion event.
5. Single dispatch per task; the dispatched machine is accepting work and declares a matching profile name.
6. One Lead per Team; takeover is explicit and logged.
7. Ack timeout and per-task liveness timeout → requeue, **capped per task**: the requeue that reaches the cap abandons the task as `canceled` instead (§6), never `rejected`. Every requeue records which signal fired — undelivered dispatch, aliveness loss, no progress, process exit, machine reboot — on the task and on its event row.
8. Verification retries exhausted → `rejected`.
9. *(Removed 2026-08-12 — the dollar budget ceiling. The number is vacant rather than reused; see the note below.)*
10. Forward rate limit per Team per window, enforced at grant mint — the enforcing half. Per-Team relay bytes are **measured and reported, but nothing is enforced on them**: there is no byte *allowance*, because §8.3 forbids severing an established splice (see the as-built note below).
11. Forwards and previews (§8.4) resolve only to registered services in the same Team owned by a `working` task.
12. Cancellation carries a disposition enum; `TTL=0` means immediate kill.
13. Token exchange is strictly narrowing.
14. Worker-triggered transitions are accepted only from the incumbent worker instance; requeue and redispatch revoke the predecessor's token first.
15. `profile` is required at session creation — an exact name, no reserved `default`.

Nothing else. Any addition that requires knowing what a task is *about* should be rejected outright.

**Check 7's cap: 5 by default, and a recorded reason on every requeue.** The default is five because a task should survive an ordinary bad patch — one flaky dispatch, a reboot, a redeploy — and should not survive being unplaceable: five attempts at a 30-minute no-progress ceiling bounds a wedged task to hours rather than letting it spend indefinitely. The cap counts requeues from `blocked_on_input` too, or a task that only ever wedges while waiting would escape it. **Recording the reason is half the fix and the more load-bearing half:** the reason was previously computed at requeue time and discarded, so N requeues left N identical marks in the record and the §12 event log, and the one thing an operator needed to know — whether this is a wedged agent (no progress), a silent daemon (aliveness lost), a crashing harness (process exited), or a rebooting machine — was recoverable only from plane logs. It now lands on the task row (the live reason, for `get_session_report` / `get_team_state`) and on each requeue's own event row (the history, for the §12 log), the same row-vs-event-log split the typed input-request kind uses. A `canceled` task whose infrastructure count reached its cap is therefore distinguishable from one a person called off, which is what makes the terminal state honest rather than mysterious.

**Check 9 is removed (2026-08-12), for now.** The dollar budget subsystem is gone: the per-Team ceiling, the per-dispatch cap it handed each harness, the committed-authorization total, the containment sweep, the creation gate, and the human set-limits form. What replaces it is not a different enforcement mechanism but a different kind of number — **measured** telemetry the harness itself reports, surfaced on the §12 dashboard and enforcing nothing. Landbridge now bounds a runaway by *time* (§10's no-progress ceiling), by *attempts* (check 7's cap), and by an operator who can see what work costs.

Two decisions ride along, both narrowings worth naming: `dispatch` no longer carries `budget_usd` and the runner no longer offers a `{budget}` spawn substitution (their only value source was the deleted per-Team cap, so a harness-side dollar cap would need new global config invented for it); and `cancel`'s `budget` disposition is gone with the control plane's only authority to cancel, which it never exercised.

**The removed design, retained for revival.** It was *containment, not metering*, and every part of it followed from one fact: Landbridge does not sit between a harness and its model provider, so measured spend was not available to enforce on. What was knowable was what Landbridge **authorized**. So:

- **A ceiling on committed authorization.** One `team_budgets` row per Team: a lifetime `ceiling_usd`, a `per_task_usd` hard cap handed to each dispatch's harness, and a monotonic `committed_usd`. Enforcing on a reservation rather than a measurement cannot be defeated by a telemetry signal that never arrives, which is exactly how a metered ceiling fails.
- **Charged per dispatch, not per task**, inside the dispatch transaction. Each attempt is a fresh process that can burn the whole cap, so a requeued task committed twice — conservative and true, and it made requeue amplification visibly expensive.
- **Committed only rises**, with no release path and no need for one. "Unspent" is the one quantity that could not be known, and releasing would have turned a lifetime ceiling into a concurrency limiter — a $100 Team running ten thousand sequential $10 tasks, which §4 rules out. A task cancelled before it ever dispatched never charged the ceiling, because the charge happened at dispatch.
- **Unconfigured meant unbounded**, never zero. A ceiling with no per-dispatch cap admitted work, committed nothing, and could never be reached; the dashboard said so rather than implying enforcement.
- **Two halves made it containment rather than an admission check**: reaching the ceiling refused new dispatch *and* `stop(preserve)`-ed the Team's working tasks. `stop` rather than cancel, deliberately, so a human raising the ceiling could resume the work.
- **A human set it, from the §12 dashboard only.** No MCP tool existed and the dashboard's write refused a Lead session even for its own Team — the one place that surface departed from "a Lead may act within its Team" — because a budget is the control that bounds the Lead itself, and a Lead able to raise it is enforcement living exactly where a model can reason past it (§2 principle 3). A Lead could *read* its ceiling through `get_team_state`, so a refused `create_session` was legible rather than mysterious.

Reviving it means re-adding that row (the drop migration's `Down` restores the shape), the commit-at-dispatch call in the store, and the two halves of enforcement. What it cannot restore is the amounts, which only a human knows. **Whether the ceiling should return at all is the open question**: measured telemetry can now feed a metered ceiling, which the note above argues is the weaker design — so a revival should decide between them rather than assume the old one.

**Subagent depth is the harness's problem, and nothing here bounds fan-out cost.** Check 9 used to be the answer — a subagent tree runs inside its parent dispatch, so a per-dispatch cap bounded it whatever its shape — and with check 9 gone there is no cost bound at all, only the no-progress ceiling (§10) bounding *time* per attempt and the check 7 cap bounding attempts. This is a real gap, stated rather than papered over: measured telemetry (§12) makes fan-out spend **visible**, which is what the operator now has instead. Landbridge does not hold the model keys and cannot stop an in-flight call regardless.

**Check 10 ships as a forward rate limit only.** A grant is the one thing no forward can happen without, so the limit is enforced per Team per rolling window at grant mint in the control plane — the cheapest and strongest point: it needs no cooperation from a relay that may be unreachable, and it sits upstream of the thing it bounds, so no peer can outrun it. It counts *authorizations* (grants minted) rather than tunnels that opened, since what a peer did with a grant is not knowable — the same choice the removed check 9 made, and for the same reason. The default is deliberately generous — an §8.4 preview mints a fresh grant per browser connection, so a page load is legitimately several — because this bounds a runaway loop rather than rationing normal use.

**Bytes are measured; no byte allowance is enforced.** The relay counts every byte it splices, at the one choke point in its pump, and reports per-forward totals to the control plane over the plane-facing HTTP contract that already carries grant validation (`/relay/usage`, same shared bearer) — deliberately **not** the frozen runner wire of §10, which the relay does not speak. The plane attributes those bytes to Teams through the forward ids it minted, so the relay never learns whose traffic it is moving; it still moves opaque bytes and interprets nothing, it only says how many.

Two properties keep that number honest, and both are why it is a containment signal rather than an invoice:
- **Best-effort.** Reporting is periodic plus once on close, so the figure trails live traffic by up to one interval and a relay that dies loses its unsent tail. The Team's view therefore carries the timestamp of the last report, and an unmeasured Team is distinguishable from one measured at zero — an absence of measurement is not a measurement of no traffic.
- **Counted, never gated.** Nothing in the splice pump can refuse a frame. There is no byte ceiling, because §8.3 forbids severing an established splice mid-flight — a database session or websocket is never cut by policy once spliced — so what a reached byte ceiling should actually *do* remains unresolved, and guessing at the pump would break that promise. Bytes bound nothing today; the *forward rate limit* is what bounds a Team's relay use.

Bytes kept their own table rather than sitting beside the removed check 9 ceiling, and the reason outlived the ceiling: a measured number next to an authorized one invites confusion about which is which, and a Team should not acquire a spend record merely because bytes flowed.

---

## 10. Interfaces

### Agent → control plane (MCP)

**Lead:** `create_session` · `answer_input_request` · `submit_review` · `cancel_session` · `park_session` (deliberate release of a live ACP session) · `get_team_state` · `get_session_report` · `get_session_question` · `list_profiles` (the routing read: which profiles exist and where they can run, §7) · `bind_machine` · `unbind_machine` · `open_lead_forward` (§8.3 human path)
**Worker:** `get_session` · `report_result` · `request_input` · `start_process` / `stop_process` / `write_process` (§10) · `register_service` · `open_forward` · `open_preview` (§8.4)

There is no `claim_task`. Workers are dispatched, never claimants (§5, §6) — the first thing a worker does with its minted token is work, and its calls identify it.

**As-built reconciliation (2026-07-30).** The tool surface implemented deliberately differs from an earlier draft of this list, and this list now reflects what shipped:
- **No `claim_lead` / `release_lead` tools.** A Lead is not an agent that claims a tool — Lead identity *is* the credential: a human authenticates (§5 OAuth), claims the Team through the lead-claim flow, and holds a Lead session token. There is nothing for an MCP agent turn to call; claiming/releasing is the credential lifecycle, not a task action.
- **No `list_teams` / `get_machine_group_status` tools.** The cross-Team and machine-group views are a *human* surface, served by the §12 web dashboard (with a structured-data twin for a reattaching Lead) — not agent MCP tools. An agent sees only its own scope via `get_team_state`.
- **`list_profiles` added (Lead), and it is the one refinement to the rule above.** Machine **enumeration** stays a non-goal for agents; **profile→machine routing does not**, and the two were being conflated. A Lead is the thing that chooses a task's `profile`, routing is exact-match (§7), and the only surface that listed the declared profile names was the human Machine Group view — so a Lead had no way to learn which names exist and was told to ask its operator for the string. In practice that means it guesses, and a guessed name produces a task no machine can ever claim, sitting in `submitted` forever with **nothing anywhere reporting why**. The unclaimable task was the design's own doing, so the capability is restored in the shape an agent should have had: the routing projection and nothing else — the declared profile names, the machines offering each, and whether each of those machines can accept work right now. It carries no tasks, no owning Teams, no services, no processes and no takeover history; the Machine Group view stays human-only (§12), and there is deliberately **no Lead dashboard route** for it — a Lead is an agent, so MCP is its surface. It reads the live connection registry, i.e. the same `MachineSnapshot` a dispatch pass hands the engine, so a Lead is shown what routing would actually match rather than a second view free to disagree with it. This also makes the *profile* half of a machine's enrollment declarations readable, having been write-only until now. Machine ids were already in a Lead's world (`bind_machine` takes one, `get_team_state` returns its bound one), so what this widens is the profile names and per-machine liveness. It is the one Lead read that is not Team-scoped, and correctly so: a declared profile is operator config belonging to a machine, not content belonging to a Team, so there is no Team to scope it to.
- **`report_blocker` folded into `request_input`.** Blocking is a single typed request (§6/§11), so the one `request_input` tool carries the kind; there is no separate blocker tool — and the kind is not the whole request: see the question/answer bullet below.
- **`get_session` added (worker).** A dispatched worker's opening move is to read its own assignment; this is that read, scoped to `{team, task, worker, instance}`.
- **`start_process` / `stop_process` / `write_process` added (worker, §10).** A worker starts a background process that outlives its own turn, stops one by name, and writes to one's stdin. They exist because an agent that needs this will otherwise discover the bad version (`setsid`, or scrubbing `LANDBRIDGE_*` to escape the reaper), which silently defeats the kill guarantee for everything else on the machine — so the sanctioned path has to be at least as capable as the unsanctioned one. Deliberately **separate from `register_service`**: starting is a machine act, registering is a Team-visibility act, and fusing them would force every private helper to be advertised and would break "register only after the port answers." `write_process` is a **pipe, not a TTY** — see §10.
- **`open_preview` added (worker, §8.4).** The reversed decision to build the HTTP preview layer adds a worker tool that mints a shareable preview URL for a service the task has registered; the human-facing mint is the §12 dashboard. Scoped like `open_forward` (worker owns the service); the public-vs-gated auth choice is set at mint.
- **`report_result` gains an optional in-band `report` (worker), read back by `get_session_report` (Lead).** The worker's summary of what it did and verified, evidence pointers, and proposals (e.g. "task X should run on profile Y") rides UP through the plane exactly as the Lead's `description` rides DOWN — opaque content the plane stores verbatim and never parses (§2 principle 1). It is the symmetric half of the brief: the plane already carries Lead→worker prose in-band, so a worker→Lead summary is the same shape in the other direction. The result *reference* stays the load-bearing artifact pointer; the report is **annotation, not authority** — a Lead reads it as untrusted agent claims (§13) and verifies before accepting (§7, §9 check 4). It is size-capped (16 KB); over-cap is refused so real detail goes to the workspace behind the reference, not the plane. Being prose, it never rides the bulk status read: `get_team_state` carries only a `has_report` flag per task, and the Lead pulls each report deliberately, one task at a time, with `get_session_report` (delimited as untrusted, §13). It surfaces to a successor worker on `get_session` (that task's own report to its next worker — not a fan-in surface) and to a human on the §12 dashboard. There is deliberately **no `await`/long-poll tool**: a Lead polls `get_team_state` on its own pacing, and a worker's dispatch *proposal* rides `request_input` (blocking) or this report (non-blocking) — Landbridge has no worker-creates-tasks machinery.
- **`request_input` gains a bounded `question` (worker) and `answer_input_request` a bounded `answer` (Lead), read back by `get_session_question` (Lead).** The typed `kind` routes a request; it does not say what is being asked, and the answer transition carried no words at all — so the §11 park/resume machinery could unblock a task without ever telling it anything, and the worker's only recourse was to guess or ask again. Both fields are opaque content the plane stores verbatim and never parses (§2 principle 1), the same shape as `description` going down and `report` coming up, and both are capped at the report's 16 KB with the same refuse-don't-truncate discipline (§11). Being prose, neither rides the bulk status read: `get_team_state` carries the typed `input_kind` (structure — it is the triage fact, `auth_help` needs a human where a `question` can be the Lead's) plus a `has_question` flag, and the Lead pulls the text per task with `get_session_question`, delimited as untrusted (§13) and returning any answer already given so a reattached or new Lead does not answer twice. **The answer reaches the resumed worker on `get_session`, never through argv** (§11, §13). The §12 dashboard renders both verbatim on the Team view and the inbox — the human surface is where a person answers, so it is the one place the prose must be legible.
- **`bind_machine` / `unbind_machine` / `open_lead_forward` added (Lead, §8.3 human path).** The human path to a non-HTTP service. `open_lead_forward` is deliberately its own tool rather than making `open_forward` lead-callable: tool names are one flat namespace on this server, and the two differ in what "your machine" means (a task's dispatch machine vs. a human's bound machine) and in what a caller must be told (one connection, briefly available). Forking one tool on the caller's credential class would also make it the only tool that inspects *which* class called and changes meaning — authority stays structural either way (each tool refuses the other's credential at the door), so the split costs nothing and keeps both descriptions honest. `bind_machine` is an *action* by the human's session, which is why it is a tool rather than a §12 dashboard view; the dashboard shows the resulting binding.

This keeps §5's rule intact — authority is structural, from the credential, not from which tools exist — and moves human-facing reads to the human-facing surface (§12).

Status tools return counts and states — **never prose**. Free text is fetched deliberately, one item at a time, delimited as untrusted — including the worker's report and the question it is blocked on, which `get_team_state` flags (`has_report`, `has_question`, plus the typed `input_kind`) but never carries, leaving `get_session_report` and `get_session_question` to pull each per task (§13). Responses are scoped by credential: a Lead gets full Team state, a worker gets its own task plus registered services and whether a Lead is attached.

**Slash commands are a convenience layer, not the API.** `/landbridge-teams`, `/landbridge-machines`, `/landbridge-lead`, `/landbridge-status`, `/landbridge-enroll` ship as MCP prompts. Surfacing prompts as slash commands is client behaviour and not universal, so every command must map onto an independently-reachable surface — nothing may be reachable *only* through a prompt. Per the as-built reconciliation above, that surface is a tool for agent actions (`/landbridge-status` → `get_team_state`), the §12 dashboard and its structured-data twin for the human cross-Team/machine views (`/landbridge-teams`, `/landbridge-machines`), the credential/lead-claim flow for `/landbridge-lead`, and the enrollment flow for `/landbridge-enroll`.

Skills ship as MCP resources, reaching every agent on connect where the client supports auto-discovery (`skill://`, SEP-2640, is draft — see §5); the guaranteed path is the dispatch prompt directing the worker to read the skill resource before starting.

### Control plane ↔ runner (closed enum)

**The only frozen interface in the system.** A runner rejects anything outside the vocabulary.

**Outbound:** `dispatch` · `stop(ttl, disposition)` · `kill` · `open-forward` · `close-forward` · `read-transcript`
**Inbound:** `started` · `session-started` · `alive` · `tool-call` · `subagent-spawned` · `exited` · `auth-failed` · `forward-opened` · `forward-closed` · `rebooted` · `transcript-chunk`

`read-transcript`/`transcript-chunk` are a strict request/reply pair correlated by an opaque request id — the only place in the vocabulary where the control plane pulls and the runner answers, rather than the runner pushing. It exists so transcript bulk is flow-controlled by the reader: one chunk in flight, the next requested only once the reader has taken the last, and replies bypass the runner's bounded event ring entirely (a dropped chunk is a corrupted read, and a transcript backlog must never evict a liveness event or delay a `kill`). The runner sends the file's bytes and interprets nothing (§13). §12 has the read path.

**Every message carries a session id.** A machine runs many agents concurrently; there is no implied current session in either direction. This is the change most expensive to retrofit — get it right before anyone enrolls.

**`dispatch` names a task, never a path.** Two of its fields are about continuity. One is the opaque harness session ref to resume, present only when there is a transcript to continue. The other is the task whose **working directory** the harness runs in, present on every dispatch of a continuation — a continuation runs where its predecessor worked whether or not it resumes that transcript (§7, §11), so this is not conditional on the first. It is a *task id* rather than a directory on purpose: `work_root` is machine-local runner config, and mapping a task to a directory under it is the runner's job, so the plane never learns or dictates a machine's filesystem layout. That is also why the park record's directory field never acquired a producer — the plane had nothing true to put in it. Both fields are absent on an ordinary task, including every park-resume of one, where the runner's own directory is already the right one.

`started` (harness up) is distinct from dispatch ack (runner received). Cold start takes time, and their requeue semantics differ: a failed ack means nothing happened, so requeue is free; a death after `started` means side effects may exist.

Nothing in this vocabulary is domain-specific.

**`stop` is a message to the agent, not a signal, wherever the harness allows it.** A signal cannot carry a disposition: Claude Code's documented SIGTERM behaviour is abort-the-turn and exit, which silently turns `preserve` into `kill` for the flagship harness. Where the harness supports stdin message injection, `landbridged` delivers `stop` as an injected turn — the agent reads the disposition, winds down, persists, and exits — and signals are reserved for TTL expiry and `kill`. The runner config's `stop` section declares which delivery the profile supports; the frozen vocabulary names the command, never the transport.

**As built, the flagship harness does not allow it.** The `wherever` clause above is load-bearing, and for `claude -p` it excludes: two facts about the current CLI, each established by an isolation spike against the real binary, leave no configuration in which a mid-task injected turn is consumed. (a) `claude -p "<argv prompt>" --input-format stream-json` never runs a turn at all — the argv prompt is ignored and the process blocks on stdin indefinitely, so the flag this spec once named as the enabling one produces a worker that hangs on dispatch. (b) Without that flag, claude reads stdin **once** at startup (a ~3s window, then `proceeding without it`) and never looks again. The prompt must be argv regardless, because stdin is `landbridged`'s dead-man's switch and is held open unread for the life of the task. So a `claude -p` worker's `stop` is, as built, **the granted TTL and then a tree-kill**: no wind-down turn, no final `report_result`. `preserve` and `preserve_and_park` still mean what they say, but they are delivered by the plane's record — the harness session ref survives the kill, so the transcript stays resumable (§11) — rather than by the agent's cooperation.

Two consequences worth stating so they cannot be re-lost. First, **`landbridged` reports the write, not the reading.** Consumption of a line on a harness's stdin is not observable without harness-specific knowledge, which §10 keeps out of the runner's code; the ack therefore names the action taken (a turn was written; a deadline was armed) and the profile's `stop.mode` is the only claim in the system that a harness consumes stdin turns — the machine operator's claim, checkable only by running a stop and watching. An ack that asserted delivery-to-the-agent was a false positive for every `claude -p` worker, which is how this went unnoticed. Second, **the design principle stands unchanged.** Message delivery remains the right mechanism, and remains built and exercised, for harnesses that support it — an interactive-mode or SDK-hosted session, or a custom harness reading turns off a held-open stdin. What changed is that the reference profiles no longer declare it for a harness that cannot honour it.

### Runner capabilities

`landbridged` does five things. The invariant is not that it does little, but that **none of it requires domain knowledge**.

| Capability | Generic | From config |
|---|---|---|
| Process supervision | spawn, signal, kill, liveness per PID | invocation command, stop signal, exit semantics |
| Event relay | heartbeat on own timer, forward events upstream | hook wiring, OTel endpoint, event name mapping |
| Log streaming | tail and stream, drop under pressure | log path, format |
| Relay endpoint | bind loopback, open outbound tunnels, dial local ports | — |
| Credential holding | machine token refresh, inject worker tokens | where the harness reads MCP config |

What it cannot do: touch the workspace, read or interpret task content, decide anything, initiate anything not triggered by a command or its own timer, or reach arbitrary paths or ports.

**`landbridged` never listens on a network interface.** Its only listener is loopback, for forwards and optionally for harness event callbacks. Every other connection is outbound — which is why it works behind NAT with no configuration and adds no attack surface to its host network.

**Each task spawns into its own process group, and `kill` targets the group.** A harness spawns children — subagents, tool invocations, dev servers — and killing only the parent orphans them, which is precisely the leak stray-process cleanup exists to catch. Group kill takes the whole task down and touches nothing else, because siblings are in different groups. A harness that cannot be spawned into its own group cannot be killed cleanly on a shared machine.

**The dead-man's switch is a per-profile declaration, not a universal law.** `landbridged` redirects every worker's stdin; what a profile chooses is whether `landbridged` keeps *holding the write end*. Held open — `stdin: deadman`, the default — that pipe is a death notification: EOF means `landbridged` is gone, crashed or `SIGKILL`ed, and a well-behaved harness tears down its own tree rather than keep burning tokens against a task the control plane has already requeued. That is the cooperative and immediate half of the kill guarantee, where the start-of-day stray sweep is non-cooperative and only as timely as the next restart. But the switch is not universally survivable, and treating it as if it were excludes harnesses for no gain: one that blocks reading piped stdin never reaches its first turn under it, and `codex exec` is such a harness by construction — its prompt resolution reads stdin to EOF even when the prompt arrived as argv, with no flag to opt out. A profile may therefore declare `stdin: closed` and take the EOF at spawn instead. **What that gives up is stated rather than hidden:** `landbridged`'s own death no longer ends the worker, and the restart sweep becomes the only thing that will; `landbridged` says so on startup for every profile declaring it. For a harness that could never observe the EOF in the first place this costs nothing it had — which is why the choice belongs to the profile, the one place that knows which harness it spawns, and not to the machine. `stop.mode: message` is incompatible with it and refused at config load: a wind-down turn is a write to the worker's stdin, and a closed stdin gives that write nowhere to land.

### Runner config

`landbridged` contains no harness knowledge; everything specific is data. The config is therefore the contribution surface — supporting a new harness should be a config file plus a passing conformance run, never a change to `landbridged`.

Full schema and a worked Claude Code example: `skills/landbridge-enroll/references/runner-config.md`.

| Section | Covers |
|---|---|
| `machine` | `work_root` for per-task scratch directories; back-pressure thresholds |
| `profiles` | Named configurations; at least one required, none reserved as `default`. Enroll convention: `<harness>-<hostname>-<os>`, plus optional group names like `any-linux`. Each carries `spawn`, `prompt`, `follow_up`, `env`, `files`, `hooks`, `stop`, `telemetry`, `logs`, and an optional `max_concurrent` cap. |
| `profiles[].env` | Per-spawn environment map. Values take the same `{…}` substitutions `spawn` does. Applied after the reserved `LANDBRIDGE_*` stamps and before `telemetry.env`. The four names landbridged owns (`LANDBRIDGE_MACHINE_ID`, `LANDBRIDGE_SESSION_ID`, `LANDBRIDGE_WORKER_TOKEN`, `LANDBRIDGE_TRACEPARENT`) are refused at load. |
| `profiles[].files` | Files written under `{work_dir}` before spawn. Paths are jailed to the work dir after substitution. Prefer this for additive project-local MCP (Grok merges `{cwd}/.grok/config.toml` with `~/.grok`). |
| `profiles[].hooks` | Argv hooks, never a shell. `before_spawn` is fail-closed; `after_exit` is best-effort. For a harness whose only MCP surface is a user-global file (Codex). |
| `profiles[].telemetry` | Per-profile opt-in (off by default) that points a harness's own OTel export at the operator's collector: `otel`, `endpoint`, and `env` for the harness's own enable flag. `landbridged` sets only vendor-neutral `OTEL_*` — a harness's telemetry variables are data like everything else. Never enabled without a destination. |
| `services` | Operator-declared long-lived processes `landbridged` supervises: `name`, `spawn` argv, `working_directory`, `env`, `port`, `readiness`, `restart`, `logs`, `backend`, and `enabled`. Optional; absent on most machines. |

### Operator-declared services

**A service is `landbridged`'s own child, not any task's descendant.** A service a worker starts dies with the task that started it — it is inside the task's process tree, which is tree-killed, and it carries `LANDBRIDGE_*`, which the stray reaper matches. Both are right for a build step and wrong for "keep the dev server up". Handing the process to the machine's own service manager solves it where one exists, but macOS has no clean transient equivalent, a container has no init, and Windows has nothing user-level — so the only answer that is the same on every machine is for `landbridged` to own the process. That places it outside every task's tree **by construction**, with no `setsid` and no environment scrubbing, and keeps the kill guarantee inside Landbridge. The worker skill forbids the alternative route to the same escape — stripping `LANDBRIDGE_*` off a spawned process — precisely because that one silently defeats the guarantee for everything else on the machine.

**Restart equals reboot here too.** A service is tagged with `LANDBRIDGE_MACHINE_ID` and deliberately **not** `LANDBRIDGE_SESSION_ID`. Both halves are load-bearing: the restart sweep is keyed on machine id, so a restarting `landbridged` reaps the previous generation's services before starting them again — a `SIGKILL`ed daemon cannot leave a port-holding orphan for its successor to collide with — while per-task exit cleanup requires a matching task id, so an ordinary task ending steps over them. No PID registry and no re-adoption: services are restartable, so restarting them is cheaper and more predictable than reasoning about which survivors are still healthy.

**Services are not tasks.** They have no per-task liveness clocks, they do not count toward a profile's `max_concurrent` (that gates task admission), and the load they consume is already observed directly by back-pressure. Their status rides the machine heartbeat (§12); the control plane stores what a machine reports and interprets none of it.

**Declared ports must be unique on a machine, and names are identifiers.** A forward dial is resolved to a service *by port*, so two services claiming one port would make that lookup answer for whichever was found first — and a dial refused on that basis is unexplainable from outside. Both are rejected at config load, naming both offenders. (The port that must be unique is the one a forward could dial: `port` when declared, else the readiness port. A readiness-only port nothing dials is not part of the rule.)

`readiness` is a real check, not a delay: the declared loopback port must accept a connection before the service counts as running. That is what lets a holder task register only once the port answers (§8.2), and what lets `landbridged` answer "is the intended service actually up" when it refuses a dial.

`backend` is `direct` — `landbridged` supervises the process itself — and a config naming anything else is refused rather than silently supervised the other way. Delegating to a system service manager (`systemd-run`, `pm2`, `docker`) is a later option, and it gives up the property refuse-at-dial depends on: with a delegated backend, "is my service up" becomes a query rather than a fact `landbridged` owns.

**`enabled: false` is the supported way to stop a service**, and it is a config edit rather than a command precisely so declared state stays the single source of truth — see §12 for why there is no dashboard stop. A disabled service is still *declared*, so it is reported (as `disabled`, distinct from `stopped`) and a forward dial for its port is still refused rather than landing on whatever else took it.

### Processes and services are different things

Both are long-lived children of `landbridged`, spawned the same way, tagged the same way, and bound by the same stray sweep. They differ in three respects, and the distinction does real work:

| | **service** | **process** |
|---|---|---|
| Declared by | the operator, in the runner config | an agent, over the wire (`start_process`) |
| On exit | restarted with backoff | **never restarted**; the exit code is recorded |
| Ended by | `enabled: false`, or the daemon restarting | `stop_process`, its own exit, or the daemon restarting |

A service is a **daemon**; a process is a **job**. Neither is a Landbridge **task** — a task is a unit of delegated work with a state machine, and the collision in casual language ("long-running task") is why the naming here is deliberate.

### Agent-started processes

**A process is machine-scoped, not task-scoped.** It survives the worker exiting, the task blocking on input, the task completing, and the task being cancelled. The declaring task id travels with it as *provenance* — useful on the dashboard — and confers no ownership: any worker dispatched to that machine may stop any process on it.

That is not a loosening; it is what the purpose requires. The point of the feature is that a `claude` process can end without losing the long work it started, so a lifetime keyed to the worker's turn would defeat it. And keying it to the *task* would fail the cleanup case: the agent that tidies up is a **continuation** — a new task id, resumed with the memory of what it started — so a task-scoped stop would be unusable by the very worker sent to do the tidying.

**Cleanup is orchestration, not enforcement.** A Lead sends a continuation task; the resumed agent stops its processes and tidies its workspace. The consequence is stated plainly rather than hidden: **a Lead that never sends that continuation leaks the process until the machine's `landbridged` restarts.** Three things make that acceptable — the leak is bounded by the machine generation, it is visible on the §12 Machine Group view, and the alternative (the plane reclaiming a process whose task is gone) would require exactly the desired-state persistence that restart-equals-reboot exists to avoid.

**No restart, and that is the point.** A crash is information. Recording the exit code and time and letting the agent decide is more useful than a backoff ladder that hides the failure — and a job that should be retried is a decision for whoever reads the code, not a policy for the daemon.

**What bounds it is the profile, not an allowlist of commands.** `processes.agent_initiated` is per-profile and off by default; enabling it is the machine owner's decision, the same shape as the open/strict archetypes. An allowlist would be the wrong control: a worker on an open profile can already start a background process by hand, so restricting the sanctioned tool below its existing capability would only push agents back to the escape the worker skill forbids — and it would put domain knowledge in `landbridged`. A strict profile with no shell cannot start one either way and refuses honestly. `processes.max` is a separate, resource bound: the gate answers *may this task start processes*, the cap answers *how many*.

**Names are machine-scoped and unique across processes and services** — one namespace, so a clash is always reported rather than silently resolved, and the refusal says which kind holds the name. Uniqueness is among *live* entries: an exited process releases its name, so a retry is not blocked by its own corpse. The admission check and the insert are indivisible, or two concurrent calls could both see a name free and both take it. The **name arrives off the wire** here rather than from a file an operator wrote, which is the case the name validator exists for: it becomes a directory name, and the closed path construction depends on it being unable to steer one.

**Ports are out of scope for a process entirely.** This is a process manager: it keeps a process alive and reports how it ended. Reachability is a different noun with its own surface — §8.2 `register_service` — and keeping them apart means there is no overlap to reconcile and no half-registered state. A process declares no port, so it is `running` once its OS process is up (a crash on start is caught by the ordinary exit path, and a "still alive after N seconds" timer would be a heuristic dressed as a check), it is invisible to refuse-at-dial by construction, and a port clash between two processes is the agent's problem — consistent with there being no restarts. If a process happens to listen on something, that is the agent's business, exactly as if it had started the server from a shell.

**Stdin is a choice at start time, and the default is closed.** Most background work is fire-and-forget — a build, a dev server, a watcher — and gets the simplest spawn with nothing held open; a program that reads stdin sees EOF immediately rather than blocking on input nobody will send. Closing is done by redirecting stdin and closing it at once, which is the portable option: .NET has no cross-platform null-device handle, and leaving stdin un-redirected would hand the child whatever the daemon inherited, which is not a defined thing to give it.

`open_stdin: true` is the opt-in for a process you intend to talk to. **So `write_process` is opt-in by construction**, and so is the graceful half of `stop_process`: without a pipe there is no EOF lever, and stopping is the bounded wait and then a tree kill. Both costs are reported alongside the process, because a cleanup agent has to know which kind of stop it is about to perform. **A process started without stdin can only be stopped hard.**

Either way it is **not** a terminal. A program that behaves differently because `isatty` is false behaves the same however this is set, since a real TTY needs a pty. Opening stdin buys a writable pipe, not interactivity — it fixes *blocking*, never *detection*.

**`write_process` is a pipe, not a TTY**, and the difference is documented where an agent will read it. It refuses outright against a process started without a pipe — which, since that is the default, is the refusal an agent is most likely to meet. The cause says the pipe was never opened rather than that the name is wrong, so the caller learns to restart the process with `open_stdin` instead of hunting for a typo. Programs that test whether stdin is a terminal behave differently on a pipe: no interactive prompts, block buffering instead of line buffering, and some refuse outright; a password prompt reading `/dev/tty` never sees these bytes, and a curses or full-screen program will not work at all. Success means the pipe accepted the bytes, never that the program understood them — the reply, if any, appears in the captured log, so the interaction idiom is *write, read the log, decide*. Payloads are capped at 16 KB per write, the same in-band prose discipline as elsewhere, UTF-8 only, newline-appended by default because line-oriented is the overwhelming case. There is deliberately no `signal_process`: signals do not port to Windows, stdin writes do.

**Restart-equals-reboot survives.** A process has no config to re-read, so a naive design would force `landbridged` to persist desired state — the invariant it refuses a dashboard stop button to protect. It does not need to: the restart sweep kills the previous generation, nothing revives them, and a resumed agent starts what it still wants.

Config-declared services remain, for operator-owned fixtures.


**Eventually, services should probably be machine-advertised rather than task-registered.** A config-declared service is a machine fact, so the structurally clean shape is to advertise it on the heartbeat and resolve forwards against that — no registration to go stale, because a dead service simply stops being advertised. That is not built, and the blocker is not effort: §8.2 registrations are Team-scoped for isolation, and a machine-declared service has no Team, so "who may forward to it" is an open §13 question. Until it is answered, a service reaches consumers through a holder task's Team-scoped registration, which gets that scoping for free.

**A daemon can drive several harnesses or postures.** Profiles exist for genuinely different setups on one machine: Claude Code alongside Codex, a restricted permission posture for sensitive work, a pinned version being canaried during an upgrade.

A session **must** carry a `profile` string, matched **by exact string equality** at dispatch. The control plane never learns what a profile name means — only whether a machine declares one. There is no reserved `default` and no omit-fallback. A name no machine declares sits visibly undispatchable. This is deliberately not a capability manifest, which §15 still excludes: profiles are identifiers a human chose, not descriptions Landbridge reasons over. Enroll names a box-specific profile `<harness>-<hostname>-<os>` and may also declare a group name such as `any-linux`.

**Profiles describe how to run an agent, never what kind of work it does.** `profiles: {frontend, backend}` is task routing disguised as machine config, and it puts the control plane back in the business of meaning.

Three constraints are load-bearing rather than cosmetic:

**`landbridged` never invokes a shell.** `spawn` is argv passed to `execve`, which is what makes it safe to deliver an agent-authored prompt as an argument — and most harnesses require that. There is no shell to inject into. If a harness genuinely needs shell interpretation, it gets wrapped in a script; a shell is never added to `landbridged`.

**Two hard prerequisites, neither of which degrades gracefully.** A harness must be an MCP client, since that is a worker's only channel to Landbridge. And it must run to completion without prompting for approval — a headless agent waiting for a click nobody will make surfaces as a liveness timeout rather than an error, which is the most expensive way to find a misconfiguration. Headless posture is a named prerequisite with sharp edges the enroll skill enumerates: managed settings on corporate machines can forbid permission bypass outright; "don't ask" modes silently *deny* tools that require user interaction rather than prompting; and a permission-prompt tool is the middle path that turns approvals into `request_input` escalations instead of hangs — built as the §11 `permission` flavor, and the one place a worker's wait is live rather than parked.

**`landbridged` sets `LANDBRIDGE_MACHINE_ID` and `LANDBRIDGE_SESSION_ID` on everything it spawns**, not configurably. Stray-process cleanup on start scans for its own machine id, which is what makes the restart-equals-reboot guarantee survive a `SIGKILL`ed daemon. The same scan runs per task at exit, keyed by `LANDBRIDGE_SESSION_ID`: a dev server that `setsid`s out of the task's process group survives group kill and keeps the task's assigned port — worse than a leak, since a later consumer's forward reaches a plausibly-alive stale service. Task-exit cleanup catches it while the port assignment is still known.

**`events.source: none` is a supported, honest answer.** Liveness degrades to process-alive and progress renders as "not reported." A fabricated event mapping produces a machine that looks healthy and is not, which is worse than a machine that admits what it cannot see.

**As-built reconciliation (2026-08-03).** `terminal` is the only *stream* source implemented: `hooks` and `otel` parse and are consumed nowhere, so for progress signals they behave exactly as `none`, and `landbridged` warns at startup for each profile declaring one. What makes that survivable rather than fatal is the process-alive half of this section, which **is** now wired: `landbridged` emits the frozen-wire `alive` event for each supervised live process on its heartbeat timer. That is the only channel by which a fact only the runner can observe reaches the plane — the machine heartbeat is machine-scoped and refreshes no task's clock, and a worker's own MCP calls refresh nothing either. Without it a non-`terminal` profile refreshed per-task liveness exactly once, at `started`, and every task outliving the window was requeued forever, uncapped.

Two smaller as-built notes on the same seam: `subagent-spawned` is in the wire vocabulary but has no producer, and `events.mapping` maps the stdout stream's *property names* (`type_key`, `tool_use_block_type`, …) rather than mapping harness event names onto that vocabulary — with unrecognized rename keys falling back silently, so a fabricated rename does not fail at load.

**As-built amendment (2026-08-11).** `mapping` also describes one alternative *shape*, not only renamed properties: `tool_event_type` (the `type` value that itself is a tool call) plus `tool_name_path` (a dotted path to the string naming it, comma-separated alternatives tried in order) let a harness that emits one flat event object per tool call — `codex exec`'s `{"type":"item.started","item":{…}}` — produce `tool-call` at all. Before it, the rename-only keys could not reach that shape, and such a worker ran with the no-progress ceiling as its only governor. The frozen vocabulary is untouched: the emitted event is still `tool-call`. Unlike the rename keys, this pair *is* validated at load — a half-declared pair, an unwalkable path, or a `tool_event_type` colliding with the effective `system_type`/`assistant_type` is rejected rather than accepted and left inert. The resolver stays deliberately small (property names split on `.`, must land on a JSON string; no wildcards, indexes, or filters), so a stream that hides its tool calls anywhere else is a code change, not a config one.

`work_root` deserves a note: `landbridged` spawns each task in `{work_root}/{session_id}`. This is *not* the task's workspace — the runner never interprets the opaque `workspace` blob. It is a unique machine-local scratch directory to start in; the agent constructs its real workspace from what the Lead assigned.

### Concurrency and back-pressure

Machines do not declare a concurrency limit. A declared number is a guess that is wrong in both directions, and agents vary too much in weight for it to mean anything.

Instead `landbridged` observes its own load, memory, and disk, and **stops accepting dispatch when it is under pressure** — resuming when it clears. Derived, not asked for, consistent with principle 2. A saturated machine keeps running what it holds and appears as `back-pressure` in the Machine Group view. (As-built, 2026-08-03: the view renders `ready` / `not ready` / `back-pressure` from two heartbeat booleans; there is no `saturated` label and no machine state enum.)

This exists to break a feedback loop rather than to ration capacity. Without it, a thrashing machine misses heartbeats on every task it holds at once, all of them requeue, and nothing prevents the same machine from immediately being redispatched them. Back-pressure makes overload self-correcting instead of self-reinforcing.

A profile may declare `max_concurrent` for reasons unrelated to load — a licence limit, a rate-limited provider, a restricted posture kept to one at a time.

Liveness splits accordingly:

- **Machine heartbeat** — `landbridged` on its own timer. Loss means every task on that machine is suspect.
- **Per-task liveness** — **two clocks**, both scoped to a task id and both configurable. *Aliveness* moves on any inbound signal for the task, including the periodic `alive` that carries process-alive from the runner; losing it (default 60s) means the process died without an `exited` or the daemon is wedged, and requeues fast. *Progress* moves only on `started` / `session-started` / `tool-call` / `subagent-spawned`; losing it (default 30 min) means the process is alive but the agent is stuck. Both are suspended while a task is `blocked_on_input` or `parked` (§11).

  They are separate because one number cannot express both: if process-alive refreshed the only clock, a wedged agent would never be requeued, and if it refreshed nothing, every task quiet for a minute would be. The progress ceiling has to be generous — a single long tool call legitimately emits nothing for minutes, and requeueing slow-but-healthy work just makes it slower.

  **A task that has registered a service (§8.2) is exempt from the progress ceiling**, never from aliveness. Registration is the worker declaring that something it started is meant to stay reachable, so idling while others use it is the job rather than a hang — and it is the right signal because the worker earns it by a deliberate, observable act at the moment it becomes true, instead of the Lead predicting it at `create_session` (principle 2: derive, do not ask).

Requeue keys off per-task liveness, so one hung agent is requeued while its siblings keep working.

Token attribution must carry a task id, or a shared machine's spend cannot be told apart per task — and the §12 measured view, which is what reports it, would be attributing to nothing.

### Runner restart

**A `landbridged` restart is equivalent to a machine restart.** No re-adoption, no local state, no persistence.

- On clean shutdown, `landbridged` kills every harness it started.
- **On start, `landbridged` kills any stray harness processes before accepting dispatch.** Clean shutdown is not guaranteed — a SIGKILLed daemon kills nothing, and orphaned harnesses keep burning tokens against tasks the control plane has already requeued. Putting the guarantee on start is what makes it survive a hard crash.
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

OTel from harnesses, plus tool-call hooks where the harness has them — Claude Code's hooks can POST JSON to a loopback HTTP handler, a cleaner per-task event source for `landbridged` than log scraping, and hook processes inherit `LANDBRIDGE_SESSION_ID` for attribution. Per-subagent lineage (`agent_id` / `parent_agent_id`) currently arrives only on beta trace telemetry — treat the subagent tree as progressive enhancement, not a given. Harnesses without equivalent signals render as "not reported" — degraded telemetry is normal, not broken.

**Attribution is wired; ingestion is not.** A profile's `telemetry` block turns the harness's own exporter on and appends `landbridge.session_id` (plus `landbridge.machine_id`) to `OTEL_RESOURCE_ATTRIBUTES`, so every metric and event the harness emits reaches the *operator's* collector already bucketed per task — the id §10 requires, carried the only way available to a supervisor that never sees a token. Landbridge itself receives none of it: there is no OTLP receiver, no token or cost field in the schema, and joining spend to a Team still means joining on task id against the plane's records (`dispatch` carries no Team id). Operator guide: `docs/TELEMETRY.md`.

Best-effort by construction, since Landbridge does not sit between the harness and the model provider and the machine is the customer's. What lands is therefore **the harness's claim about its own spend**, not the plane's derivation of it (§2 principle 2) — and it is what an operator has instead of the removed dollar ceiling (§9's note), which is a visibility control rather than an enforcement one. Nothing is enforced on it, and that is not a limitation to be fixed later: a number a harness self-reports can be turned off or reported wrong, which is exactly why enforcing on it was rejected when the ceiling existed.

**As built (2026-08-12): `landbridged` parses usage off the harness's own stdout and forwards it on this wire.** A new optional inbound event, `usage-reported`, carries four disjoint token buckets (input, output, cache-read, cache-write), a model where one is known, and a cost where the harness computed one. Additive like every field added here since: a `landbridged` that predates it never sends one and the §12 view shows the honest empty state. Per-instance attribution is free — the event rides the connection the dispatch went out on — and the transport monopoly is preserved, which the alternative would not have been.

**The four buckets are disjoint only because `landbridged` normalizes, and that was not a detail.** The two harnesses disagree on what "input" means: Claude counts uncached prompt tokens in `input_tokens` and its cache hits separately, while Codex counts the whole prompt in `input_tokens` with `cached_input_tokens` as a *subset* of it (its own `non_cached_input()` subtracts one from the other). Adding the four as reported would therefore double-count every cached token on a Codex worker, which on a cache-heavy one is most of the figure. A profile declares the subset relationship (`usage_cached_is_subset`) and `landbridged` subtracts — adopting each harness's own semantics as config data rather than inferring from a harness name, exactly as the tool-call mapping keys do.

**The OTLP-receiver alternative was considered and rejected on evidence, not preference.** It would have delivered strictly more — Claude's self-computed cost metric plus tool accept/reject decisions, lines-of-code, commit and PR counts, active time, and per-subagent lineage in its trace beta — at the price of an ingestion surface on the plane and harness traffic outside this wire. It was rejected because it does not work on the fleet Landbridge actually runs on:
- **Codex ignores `OTEL_EXPORTER_OTLP_ENDPOINT` entirely.** Its exporter comes from its own `config.toml` `[otel]` block (`core/src/otel_init.rs` reads `config.otel.*`); the only `OTEL_*` variables it honours are the OTLP crate's *timeout* constants. `landbridged`'s whole telemetry mechanism is environment variables on the spawn, so it cannot point Codex anywhere — that would need a per-machine config edit outside the plane's control.
- **Managed settings can strip a developer-set endpoint.** Claude Code gives MDM-deployed `OTEL_EXPORTER_OTLP_*` values highest precedence and drops conflicting developer ones, so on a corporate machine a plane-hosted receiver sits empty while the wiring looks correct — and a managed `OTEL_RESOURCE_ATTRIBUTES` puts the `landbridge.session_id` attribution the whole scheme depends on at risk.
Both findings are recorded here rather than in a commit message because a future revisit should start from them instead of rediscovering them.

**What is deliberately NOT surfaced, and what each would take.** Everything below is a "could, don't yet" — the scoping rule is that a field is implemented when both harnesses populate it and noted when only one does:
- **Cost for a tokens-only harness.** Claude reports `total_cost_usd` and a per-model `costUSD`, so its dollars are the harness's own claim and are shown. Codex reports no cost anywhere, on its stream or in its metrics. Deriving one would need a rate per (model, bucket) — the four are priced differently, which is why they are stored separately — plus operator-owned rate config and an effective date, since re-deriving an old task at today's rate would rewrite history. `ModelPricing` is the stub that boundary lives at; it returns nothing, so a Codex cost renders "not reported" rather than `$0.00`.
- **A model for a harness that names none.** Claude's `modelUsage` attributes tokens to the model that actually ran, so a dispatch whose subagents used a cheaper model reports both. Codex names no model on any stream event, so a Codex row is **unattributed** and the view says "not reported" against real token counts. Filling it would mean the plane asserting a model — from a profile declaration or the spawn argv — inside a surface labelled "reported by the harness", which is exactly the confusion §2 principle 2 and this section's visual separation exist to prevent. The honest empty state is the answer here, not a placeholder: it is the price of the by-model dimension being *measured* wherever it appears.
- **Codex's `reasoning_output_tokens`** is carried but is a portion *of* output, never an addition to it. Claude's stream exposes no equivalent.
- **The OTel-only set** — tool accept/reject counts, lines-of-code, commits, pull requests, active time, and subagent lineage — needs the receiver path above.
- **Operationally:** a release-build Codex sends its metrics to OpenAI (`ab.chatgpt.com`) by default, because its built-in exporter is Statsig unless its `config.toml` says otherwise. Nothing Landbridge sets changes that; an operator who does not want it must configure Codex.

**Never widen the log opt-ins.** Claude Code redacts prompts, assistant responses and tool details by default, and `OTEL_LOG_USER_PROMPTS` / `OTEL_LOG_ASSISTANT_RESPONSES` / `OTEL_LOG_TOOL_DETAILS` / `OTEL_LOG_TOOL_CONTENT` / `OTEL_LOG_RAW_API_BODIES` turn that off. A worker's prompt **is** the task description, so a profile must never set them: it would put task content into a metrics pipeline that §13 never scoped for it.

### Completion is adjudicated, not webhooked

There is no verifier webhook. A task in `verifying` is completed by the Lead (or a human) through `submit_review` over MCP — the Lead reads the result reference and the completion criteria it wrote, gathers its own evidence (a test run, a CI check), and rules (§7, §9 check 4). A verdict against an already-terminal task is refused with the state machine's "gone" rejection, never a silent success. External automated completion, where wanted, is a client holding a Lead-class credential (e.g. a CI webhook posting `submit_review`), not a role Landbridge runs.

### A2A (external boundary only)

Landbridge does **not** speak A2A internally. It is exposed at the outer boundary for inbound delegation from foreign orchestrators, outbound delegation to agents Landbridge doesn't own, and federation between Instances. The A2A data model is adopted internally regardless.

---

## 11. Lifecycles

### Machine enrollment

1. SSH to the box.
2. Start the harness.
3. Connect to `landbridge-mcp`. Present a human-issued enrollment token; exchange for machine credentials. Declare name, purpose, OS, permission level. (As-built, 2026-08-03: **specs are not collected** — the enrollment record is `name` / `purpose` / `os` / `permission_level` only, and CPU/memory exist solely as live load on the heartbeat, never as declared capacity. The same stale "specs" claim is mirrored in a code comment on `MachineRow`. `permission_level` is persisted but not yet consulted by dispatch, forwarding, or any tool decision.)
4. Run `/landbridge-enroll`, which reads the enrollment skill from the server and writes the `landbridged` config.
5. Agent guides the human through registering `landbridged` as a service. Registration needs sudo, so the human executes it.
6. **Conformance run.** The control plane dispatches trivial tasks and judges the results itself:
   - `started` and tool-call events arrive, correctly attributed by task id
   - heartbeat cadence matches config
   - two concurrent tasks are independently trackable
   - `stop` with short TTL is acked, and a message-delivered `stop` demonstrably reaches the agent as a turn
   - `TTL=0` kills one PID and leaves the sibling running
   - a relay forward round-trips and the listener closes on release
   - a task that would normally prompt for approval completes without hanging
   - a parked task resumes on this machine from its recorded directory with context intact
   - every declared profile passes the above independently, plus one cross-profile concurrency case
7. Pass → machine joins the Machine Group as `ready`. Fail → registered but unclaimable, with the failing step named.

**As-built reconciliation (2026-08-03).** Steps 4–7 do not exist. There is no `/landbridge-enroll` prompt (no MCP prompt is registered at all) and no conformance run: nothing in the plane dispatches trivial work to a new machine or judges it. Nor are `ready` and `unclaimable` machine *states* — there is no machine state enum; readiness is two per-heartbeat booleans, and an enrolled-but-disconnected machine is absent from the Machine Group view rather than shown as unclaimable. Today enrollment yields credentials, the operator authors the runner config with the `landbridge-enroll` skill, and the operator-only `POST /dashboard/conformance` + `GET /dashboard/conformance/{runId}` mint dummy sessions aimed at a required named profile and report their states. That is a dispatch check, not the plane judging results — `verifying` means the worker called `report_result`. The config-stamping below is likewise unbuilt.

The wizard *displays* results; the control plane *determines* them.

Configs are stamped with the generating skill version. `/landbridge-enroll` is idempotent and re-runnable.

### Cancellation

`stop` carries a TTL and a disposition enum (`preserve` / `discard` / `preserve_and_park`), plus optional free-text reason. Default is `preserve`.

TTL is set by the Lead per situation. `TTL=0` means kill immediately without waiting for ack.

Preservation is the agent's job — persist work in progress to the workspace substrate however that domain does it. The runner does not touch the workspace, so **the kill path is lossy by construction.** `preserve` and `preserve_and_park` are only as good as the harness's `stop` delivery (§10) — a signal-only profile cannot honour them, and the enroll conformance run makes that visible before it matters.

`discard` means removing this task's workspace instance, which is only safe *because* isolation is task-scoped. Under a shared checkout it would destroy a sibling's work. `discard` is deferred while the task is `verifying` — deleting a workspace mid-adjudication (the Lead may be running the criteria against it) turns a cancellation into a spurious verdict.

### Blocked on input

| Kind | Answered by | Resolution |
|---|---|---|
| `question` | Lead or human | answer text, delivered on the resumed worker's `get_session` |
| `spawn_request` | Lead | new task created, id returned |
| `auth_help` | human | credential provisioned |
| `endpoint_wait` | control plane | woken when service registers |
| `unreachable` | human | artifact or forward could not be reached |
| `permission` | Lead, or human on escalation | verdict (`allow`/`deny`) returned to the still-running worker |

Threaded on the originating session, provenance-tagged. Wait TTL is **off by default** (indefinite). A Lead who wants the machine back uses `park_session` (`desired=on_disk`). When a TTL is configured and expires, occupancy is released the same way; the answer later wakes `session/load`.

**The request carries its question and the answer carries its text.** The `kind` above only *routes* a request — who may answer it — so a request that carried nothing else would be a doorbell: an answerer would see that a task needs attention but not what for, and a resumed worker would know it had been unblocked but not with what. So `request_input` takes a bounded opaque `question` and `answer_input_request` a bounded opaque `answer` (§10), both stored verbatim on the task row beside the live `kind` and never parsed. **The answer reaches the resumed worker on its opening `get_session`, not through the resume prompt** — the profile's `resume.args` stays generic config, because argv is readable by any local process through `ps` and `/proc/<pid>/cmdline`, the same reason enrollment tokens never ride it (§13). `get_session` returns the question alongside the answer, which is what makes the pair legible after a cold start, where the transcript that held the question is on a machine that is gone. A *new* question overwrites the stored pair, so a task never presents one question next to the previous one's answer. Both are size-capped like the worker's report, over-cap is refused rather than truncated (a truncated question is a different question), and the refusal leaves the task where it was — `working` for a question, `blocked_on_input` for an answer — so the asker or answerer simply goes again, shorter.

**A prose question may end the turn; a permission wait does not.** A worker that has asked a question reports the request, persists, and may end its turn — occupancy can stay `running` idle for a follow-up `session/prompt`, or drop to `on_disk` if the process exits. Per-task no-progress is not a Lead-owed wait. Holding an open MCP call would strand the worker on client-side tool timeouts. `endpoint_wait` is the same shape: the consumer waits; the control plane wakes it when the service registers. The one wait that *must* be live is described next.

**The permission flavor: an approval dialog answered by Landbridge.** §10 names a permission-prompt tool as the middle path for a machine whose posture will not allow `bypassPermissions`. That path lands here: the harness — not the agent — posts when a tool call falls outside what the profile pre-approved, and the request carries the tool name plus the proposed arguments verbatim as untrusted agent-adjacent text (the same opacity as a `question`, size-capped the same way, and the tool name is required — "something wants permission" is not answerable). The message is `awaiting_permission`, so the inbox shows it. Wait TTL is off by default.

**It is answered in place.** The harness's permission contract has no resumed-answer seam: a prompt has nowhere to deliver a verdict to a process that exited, and the harness never times out a prompt on its own. So the asking process stays up inside the relaying tool call — occupancy `running`, workspace, registered services and instance token intact — and the verdict returns it to `idle` as the same incumbent, with no token revoked and no redispatch. The two answer paths refuse each other's requests: prose on a permission request would revoke a live worker's token and strand it, and a verdict on a prose request would treat a turn-ended question as a live wait. `park_session` is refused while a permission wait is live. A second concurrent request on the same session is refused, which serializes prompts rather than letting one overwrite another.

**Two tiers, with escalation as an explicit surrender of authority.** The Lead decides routine requests — work consistent with the task it wrote. It may instead mark one **human-only**, with a *required* reason: after that the Lead can no longer decide that request and it waits for a person, who sees the reason. A human may answer *any* pending request, escalated or not; escalation removes the Lead's authority rather than creating the human's, and does not extend the wait deadline. A denial always carries a message, which reaches the agent verbatim as the refusal reason — a refusal an agent cannot read is one it retries, so this is a rule and not a convention. Every decision writes a task event carrying the verdict and whether a Lead or a human made it: the question an operator asks after a surprising tool call is *who approved this*, and it has to be answerable after the fact.

**The allowlist is the volume control, and this is opt-in.** `bypassPermissions` remains correct for a fully-trusted machine. Per-call human approval as a *default* would be unusably slow — an hour of ordinary edits is hundreds of prompts — so a profile using the bridge keeps its routine baseline in `--allowedTools` and lets the bridge see only the exceptions.

**Park writes a record: `{machine, working directory, harness session ref, attempt}`.** Redispatch prefers that machine *and that directory*, because harness transcripts are machine- and directory-local — Claude Code resumes a session only from the directory that created it — and resuming that transcript is the cheap path that preserves the agent's accumulated context. The resumed turn's prompt is the profile's static `resume.args` text and carries no content; the *answer* it resumes for is fetched, not injected, on the worker's opening `get_session` (above). Two conditions guard the resume: the predecessor instance is *gone* first — the plane revokes its token (§5) and `landbridged` takes its process down when the superseding dispatch arrives, because revoking a token does not end a process, and resuming a transcript a zombie still holds interleaves two writers into one session file and corrupts the recovery substrate itself — and `landbridged` re-injects fresh MCP config, which resume does not restore (a resumed Claude Code session does read the fresh config and present the fresh credential; verified by isolation spike, 2026-08-08). A worker instance is not identifiable on the §10 wire — `exited` names only the task — so a superseded instance's exit is suppressed at the runner, the one place the two instances are distinguishable, rather than filtered at the plane. If the recorded machine is gone, redispatch falls back to a cold start elsewhere from the workspace plus the worker's persisted notes — which is why the worker skill treats "persist before asking" as protocol rather than etiquette, and why `attempt` is visible to the successor.

Auth failures report **structured facts** — operation, target, error code, missing scope. The control plane renders the remediation menu from a fixed set it owns.

### Session continuity across sessions

Park resume (above) recovers *one* task's transcript across a requeue. **Continuation** carries a transcript *to a new task*: `create_session(continues: <task-id>)` seeds the new task from the continued task — its `harness_session_ref` off that task's row, and the machine that last held/ran it, which for the ordinary case of a predecessor that has *finished* comes from its most recent `worker_instances` row rather than the row itself (a live lease is forgotten when the process exits, and a park machine only exists if it parked) — as a park-record-style affinity, so the first dispatch prefers that machine and reuses the very same resume seam (`--resume` on the recorded session id, `{session_id}` substituted into the profile's `resume.args`), under a new task id and a freshly minted worker token (§5). It is how a Lead says "talk to the agent that has the context" rather than re-briefing a cold worker.

The two resume guards still apply — the machine is preferred because harness transcripts are machine-local, and `landbridged` re-injects fresh MCP config, which resume does not restore — plus the machine-gone policy the Lead chose at creation. `degrade` (default) cold-starts a fresh session on any profile-matching machine and persists a `continuation-memory-lost` event so the Lead knows the conversation was dropped; `pin` waits in `submitted` for the recorded machine to return. Forking one task into several continuations is allowed and chains (a continuation of a continuation) are natural; each resumes the same inherited transcript and stamps its own new session ref after its first turn.

**A continuation runs in the continued task's working directory — always, resume or not.** This is a property of continuation itself rather than of transcript resume: the successor needs the worktree and artifacts its predecessor left, and the workspace *is* the work (§7). A cold-started continuation needs them just as much as a resumed one, so the directory follows the continuation even when no session is inherited — including a `degrade` cold-start, which drops the conversation but keeps the directory, so a task's directory does not move under it the moment a session is abandoned. (On that machine the directory starts empty; the `continuation-memory-lost` event is what says so.)

Transcript resume then *additionally* depends on it, which is why the two used to look like one thing. A harness session is directory-local as well as machine-local, and a continuation is a *new task id* — so the runner's own `work_root/<task-id>` for it has never held a session, and a resume aimed there fails outright rather than degrading to a cold start. The dispatch therefore names the directory-owning task (§10 — a task id, not a path) and `landbridged` runs the harness there. That task is resolved **transitively**, because continuing does not create a directory per link: B continuing A works in A's directory, so B never has one of its own, and C continuing B must still be sent to A's. Lineage names the immediate predecessor and so cannot say that, which is why the directory-owning task is recorded as its own fact at creation.

**So a continuation shares its predecessor's working directory, deliberately.** That is a real exception to §7's each-task-its-own-copy rule and it is the point rather than a leak: the continuation exists to keep working where the predecessor left off, and the workspace *is* that work. Two consequences follow and are honoured. The dispatched task keeps its own identity for everything else — supervision, per-task liveness, stray reaping by task id, its transcript, and its own injected credential (which cannot take the borrowed directory's usual config filename, since the task that owns the directory may still be running). And `discard` becomes unsafe on a shared directory in exactly the way §11's cancellation section already warns about for a shared checkout, so a discard must not delete a directory the task merely borrowed; nothing enacts workspace discard today, which is the moment to get this right rather than after.

**A resumed transcript is stale against a moving workspace.** The continuation's memory is the conversation as it *was*, not the repository as it *is* — commits may have landed, files changed, a sibling task moved on since. A continuation worker must re-verify remembered assumptions against the current workspace before acting, exactly as a visible `attempt > 1` already warns (§7): the transcript is context, never a substitute for looking.

### Partition

On lease-renewal failure the runner halts its agents. Prefer a stall over two machines doing the same expensive work with divergent results.

---

## 12. Observability

**Machine Group view** — machines, running tasks and back-pressure state, heartbeat age, tasks currently running with owning Team, subagent tree expandable beneath each task. Subagents are children in a tree, not peers: no lease, die with their parent, columns are duration and token spend.

Also, per machine, its **operator-declared services** (§10): name, state (`starting` / `running` / `failed` / `stopped` / `disabled` — the last is `enabled: false`, kept distinct because "the operator turned this off" and "this is not running and nobody meant that" are different facts), port, how long it has been up, restart count, last exit code, and when it last failed. This is the home for them because a service is declared per machine by the operator and is not Team-scoped, so it does not belong on a Team view. It arrives on the machine heartbeat and nowhere else; the plane stores what a machine last reported and **interprets none of it** — it forms no opinion about whether a service is healthy, and persists nothing, so a disconnected machine's services vanish along with the rest of its row. Human operator session only, consistent with the Machine Group view being a human surface and with machine enumeration remaining a non-goal for agents (§10 as-built). The Lead's `list_profiles` (§10) is carved out of that rule for **routing alone** and deliberately carries none of this: a declared service is per-machine operator config that no Team owns and that routing never consults, so knowing about it helps a Lead place exactly nothing. What a Lead needs a machine for is knowing where a profile can run, which is the whole of what that tool answers — "what is this machine running" stays here, with the person who can act on it.

**Service log *contents* are deliberately not served here.** `landbridged` captures them on the machine (below) and the operator reads the file there. Serving the bytes would be live tailing of a running process, which §16 open question 8 defers pending redaction — the terminal-task gate that makes transcript serving acceptable has no analogue for a service, which is never terminal. Status answers "is it up, did it die, why, how many times" without opening that question.

**The service list is an ordinary additive heartbeat field, and version skew is safe in both directions.** The heartbeat is deliberately outside §10's frozen command/event enum (`RunnerWire.Heartbeat` is in neither closed set, and the tripwire test that guards a vocabulary change asserts only those two sets), so adding to it is not a frozen-vocabulary change. Operationally that holds because the wire context sets `DefaultIgnoreCondition = WhenWritingNull` and **no type anywhere sets `UnmappedMemberHandling.Disallow`**: an older plane silently ignores a services field it does not know, and a newer plane reading an older runner's heartbeat simply gets null. So a fleet upgrades incrementally with no lockstep plane/runner ordering. **That property is load-bearing and would disappear silently** if strict deserialization were ever turned on — anyone considering it should know it converts every future additive field, and every mixed-version fleet, into a breaking change.

**The one write on this view is Revoke machine** (§5, §13), and it is the exception that proves the read-only rule below: it does not tell a machine to do anything — it un-trusts one. Per machine, human-only for the same reason the view itself is (a machine belongs to no Team, so a Lead revoking one would be a Team un-trusting shared infrastructure; there is no MCP twin for the same reason), and same-origin like every other mutating form here — it is the most valuable POST on the surface to forge, since it needs nothing but a machine id and the ids are on a page the session can already read. One action takes away the box's credentials, its live `/runner` channel, and every worker instance token on it, requeueing what it held; the confirmation reports each. This is what makes §5's "un-trusting a machine must take seconds" reachable by an operator rather than a claim about a database. Enrolled-but-disconnected machines are absent from the view, so revoking one that has already dropped goes through the JSON twin with its id.

**Read-only otherwise, and the reason is desired state.** There is deliberately no dashboard start/stop/restart. Services are config-declared, so a "stop" button would leave declared and actual state disagreeing with nothing recording why, and the next restart would silently undo it; making it honest means persisting desired state on the machine, which trades away the invariant restart-equals-reboot depends on (§10 runner restart). The honest stop is `enabled: false` in the config, where the declaration stays the single source of truth. A `restart` command is the one action that would be coherent — it does not change desired state — and it is a small additive follow-up rather than something to ride along here; note it would also be the first machine-scoped command in an otherwise entirely task-scoped vocabulary.

**Team view** — tasks by state, **measured usage** (tokens by type by model as the harness reported them, with its cost where it computed one — rendered in a visually distinct section labelled "reported by the harness", because these are a worker's claims about itself rather than anything the plane observed, §2 principle 2; a model appears only where the harness named one and reads "not reported" otherwise, never a model the plane supplied; an absent cost reads "not reported" and never `$0.00`, and a Team no harness has reported for reads "nothing reported yet" rather than zero), relay byte burn (measured, but best-effort and enforcing nothing — shown with the age of its last report, and "not reported" rather than zero when no relay has spoken, §9 check 10), registered services, open input requests, last activity, whether a Lead is attached and who, and **parks per task** — each park is a kill-and-resume of harness context, so this is the number that says whether decomposition is starving on human attention. Doubles as the reattachment surface (§4), so it must be consumable as structured data by a Lead. Sorted so idle Teams drift to the bottom.

**Human inbox** — everything waiting on a person across all Teams: permission requests, auth failures, questions, review confirmations (§7), and **tasks awaiting review**. Without it, `review` mode becomes a place work goes to die.

The **permission requests** row is the one section here whose items have somebody blocked behind them *right now* rather than eventually (§11): each is a live worker holding a tool call open. It carries the task and Team, the tool name, the proposed arguments (escaped, untrusted-styled — they travelled up through an agent's process), the age, and whether a Lead escalated it and why. Every pending row gets an allow/deny form with a message field, because a human may answer any of them and does not wait for an escalation; the form is human-only, since the Lead's own path is its MCP tool. These are deliberately *not* also listed as open questions — the same request answered by a different act would be two rows for one thing.

Lead takeovers, machine reboots, and eviction events all appear in the event log.

Views render as a plain web dashboard first; MCP Apps (SEP-1865) are a progressive enhancement where clients support them (§5).

### Retention

Contractual, not an ops preference.

| Tier | Retention |
|---|---|
| Full transcripts | Machine-local; operator-configured window (`logs.prune_after_days`, default 7 days). Never stored in the control plane. |
| Structured events | Weeks |
| Task records | Life of the Account |

Support access to transcripts is a data access problem.

**Transcripts are machine-local and served on demand.** The control plane stores no transcript bytes. `landbridged` captures each worker instance's harness stdout and stderr to `<state>/transcripts/<task>/<NNNN>` (opt-in per profile), and the dashboard reads one on demand: the plane asks the machine that ran that dispatch for a byte range, `landbridged` replies with one chunk, and the plane relays it into the operator's response without persisting it anywhere. A machine that is offline has no readable transcript, and the dashboard says so rather than hanging — the bytes exist only there. Retention is therefore that machine's own prune window, not a plane-side tier; the earlier "hours" figure assumed a streamed copy in Landbridge's database, which is not what was built.

**Service logs are captured to a separate root.** A supervised service's stdout and stderr go to `<state>/services/<name>/<NNNN>`, outside the transcripts root — not for tidiness, but because the transcript prune sweep deletes any top-level directory whose newest write is older than the retention window. That is safe for a task, which never writes again once terminal, and unsafe for a service: one idle longer than the window would have its **live** log directory unlinked from under an open handle. The write path is otherwise identical, byte cap and truncation marker included, and the service name is validated at config load precisely because it occupies the path slot a task's Guid fills.

**Readable only for terminal tasks, and only by a human.** A transcript is served verbatim (§13), so v1 narrows *when* and *to whom* rather than filtering *what*. When: only `completed`, `rejected`, or `canceled` — a task that can never run again, and whose worker instance token is already revoked. A `verifying` task is deliberately excluded: `report_result` does not revoke the reporting instance's token (the verdict does), so its transcript can still carry a live, replayable worker credential. To whom: a human operator session (§5) — not a Lead over MCP, and not a Lead token presented to the dashboard's structured-data twin, which is the one route that refuses one. Live tailing of a running task stays a machine-local operation for whoever administers that machine; exposing it, and any agent-facing read, is gated on resolving redaction (§13, §16).

Relay traffic is never persisted. Connection metadata is an event; payload is spliced and forgotten.

---

## 13. Security model

**Connected implies trusted, within an Instance.** A machine in the Machine Group is the customer's machine and their blast radius. Enrollment stays install-and-connect.

**But trusting a machine is not trusting its content.** The realistic attack is an agent on a legitimate machine that read a poisoned dependency file, an outside contributor's issue, or a fetched page, then acted with fleet privileges. Inter-agent messages arrive marked as data, never as instruction.

**Cheap eviction, not expensive admission.** Short-lived tokens with refresh held by `landbridged`.

**An agent-started process outlives its task, and the answer to that is orchestration plus visibility plus a hard outer bound — not enforcement.** `start_process` (§10) deliberately creates something the task state machine cannot reclaim, because the feature's whole purpose is to survive a worker exiting. So the authority story rests on three things instead. **Granted, never assumed:** whether a task may start a process at all is its profile's policy, declared by the machine's operator, and a machine that grants nothing is unaffected by the feature existing. **Visible:** every process a machine holds is on the §12 Machine Group view, with what started it, so an operator can always see what is running on their own hardware. **Bounded:** nothing outlives the machine generation — the restart sweep takes them all, and no restart policy revives them.

What this explicitly does *not* claim is that Landbridge will clean up after a Lead that forgets. It will not; the process leaks until the daemon restarts, and that is a documented property rather than a defect. The alternative — the plane reclaiming processes whose tasks have gone — needs the durable desired-state that restart-equals-reboot exists to avoid, and would trade a visible, bounded leak for an invisible reconciliation loop.

The **name arrives off the wire**, so it faces the same validator a config-declared name does: it becomes a filesystem path segment, and the path construction is only closed because nothing untrusted can steer it.

**The Instance boundary separates paying strangers, not colleagues.** Container-and-database-per-Instance. Keep it even when someone points out it is less efficient than a tenant column.

**Landbridge is a designed-in intermediary for customer service traffic.** `landbridge-relay` carries whatever runs on customer machines between them — the strongest argument for it staying a separate module.

**Landbridge sends `kill` to processes on customer machines.** Compromise of an Instance is not a data breach; it is remote execution across that customer's Machine Group.

**Forwards are the widest hole in the agent-facing model.** The registration requirement is the only thing between that and lateral movement.

### Credential storage

| Credential | Lives | Protection |
|---|---|---|
| Machine token + refresh | `/var/lib/landbridge/credentials`, 0600, dedicated service user | Filesystem perms; `systemd` `LoadCredential` where available |
| Worker token | Generated MCP config in `{work_root}/{session_id}`, 0600, removed on exit | Perms plus task-scoped expiry, instance-scoped revocation |
| Lead session | Held by the harness's MCP client, not by Landbridge | Whatever that client implements |
| Enrollment token | Transits once, stored nowhere | Single-use, short TTL |
| Forward grant | Memory only | Per-connection establishment, minutes |
| Signing key, database credentials | Container secret store | Never in the image |

**On the "signing key" row:** Landbridge's credentials are opaque random tokens, hashed (SHA-256) at rest and validated by lookup — not signed (§5, *Token format*). There is therefore **no per-Instance signing/HMAC key today**, and `landbridge-meta` generates and injects none when it provisions an Instance. The row is a reserved slot: if a future credential form needs a signing key, it lives in the container secret store, never in the image, and meta mints it per Instance then — not before there is a consumer. The per-Instance secrets that *do* exist today are the operator-passphrase hash, the database credentials, and the relay shared bearer.

**The machine token is the highest-value secret on a customer's machine**, because dispatch delivers worker tokens down the channel it authorizes. Compromise is not lateral movement — it is a supply of fresh credentials. Access tokens are short and the refresh token is the only long-lived secret.

**A copied credentials file is a working machine identity on any host** (as-built, 2026-08-14). The refresh token's "machine binding" is server-side only — the row records which machine it mints for — so it survives the copy rather than defeating it, and both `POST /machine/refresh` and the `/runner` upgrade authenticate the bearer and nothing about where it is. Nothing collects a host fingerprint, so nothing could compare one. **Revocation is therefore the only answer today**, and that is why un-trusting a machine has to be complete: credentials, the live command channel (auth runs once at upgrade and is never re-checked, so a revoked machine keeps an open socket otherwise), and every worker instance running on the box (a worker token carries no machine id, so a credential sweep by machine misses all of them). Un-trusting takes seconds and is one human-only dashboard action — see §13. The real fix is to bind refresh and connect to a secret derived from the host and sealed outside the credentials file (TPM, keychain), which is *not built*.

**Landbridge does not control Lead credential storage.** It is an OAuth token held by the harness's MCP client, and where that lands — keychain, plaintext file, elsewhere — varies by harness. A real dependency, not an assumption.

**The worker token's blast radius is small only if co-tenants cannot read it — and under one shared service user, they can.** Every task spawned by the same `landbridged` UID can read every sibling's `{work_root}/{session_id}` config and dial every sibling's loopback forward listener, across Teams. File modes do not create the boundary the token scoping implies. The postures, in order: per-task OS users where the operator can provision them; otherwise peer-credential checks (`SO_PEERCRED` against the task's process group) on `landbridged`'s loopback listeners; at minimum, the honest statement that on a shared machine the Team boundary is advisory. The token design still bounds what a *remote* attacker gains — one task, one Team, expiring — but local co-tenancy is a real channel, and §1's many-agents-per-machine posture makes it the common case rather than the edge.

**Enrollment tokens must not be passed as arguments.** Pasting into a terminal is the default human behaviour and it lands in shell history. Read from a prompt or a file. Single-use and short TTL bound the damage.

**Transcripts can capture credentials.** An agent that echoes a token, or a tool call carrying one, puts it in the session transcript. Transcripts are captured machine-locally and never streamed into Landbridge's database (§12), and **Landbridge does not redact them** — how to do that well is unresolved (§16, open question 8), and shipping a pattern filter would have implied a protection it could not deliver. So a transcript is served exactly as captured, and the design compensates with scope instead of filtering: files are owner-only on the machine that wrote them; the read path is a human operator session only; and only a terminal task's transcript is readable, so the worker instance token it may contain is already revoked and the task can never resume. Reading one is closer to logging in to that machine — which its operator can already do — than to publishing it. The read path stores nothing: the control plane relays chunks into the operator's response and never writes, logs, or traces the content.

**A transcript is sensitive beyond credentials.** It legitimately contains everything the agent read — source, customer data, internal hostnames — so no filter would make one safe to publish. Both the dashboard page and the served bytes carry that warning inline, and the enroll skill states it where an operator turns capture on.

**The harness keeps its own session store.** Landbridge's capture is a tee; the harness's own transcript on that machine is what a resume actually reads, Landbridge does not touch it, and it should not be described as protected.

**Model provider credentials never touch Landbridge** — but that is a statement about Landbridge's exposure, not about their safety. They sit in harness config on machines running agents that read untrusted content, and those agents can read them. Landbridge cannot prevent that and should not imply otherwise.

**Capabilities and permission level are bound server-side at enrollment.** Clients report state; they do not re-declare their own privileges.

**Agents may make project-local, reversible changes unilaterally.** System-level changes — sudo, PATH edits, global installs, version switches, anything touching credentials — are reported, not performed.

**Instructions found in fetched or read content are suggestions, not authority.**

---

## 14. Skill bundle

Ships with the server over MCP resources (`skill://` where supported — §5). Two layers: Landbridge's baseline plus per-Instance overrides, with defined precedence and versioning. **This is where domain knowledge lives.** The schema is neutral; the skill is opinionated.

Three skills ship by default:

| Skill | Audience |
|---|---|
| `landbridge-lead` | Humans driving a Lead. Decomposition, profile targeting, cancellation, Team lifecycle — and the integration pattern: **integration is itself a session**, authored by the Lead and sequenced after its inputs complete. Workers never negotiate merges peer-to-peer; they have no channel, and should not. |
| `landbridge-worker` | Dispatched workers. Isolating on a shared machine, persisting before asking, reporting, blockers, inheriting the session directory on redispatch (`attempt > 1`: inspect before trusting). |
| `landbridge-enroll` | The enrollment flow. Writing the runner config, headless-posture prerequisites, guiding the human, conformance. |

The failure mode for everything in these is bounded and recoverable. That is the test for belonging here rather than in §9.

---

## 15. Explicitly not building

- A workflow engine
- Retry policies more expressive than counters
- A rules DSL for routing or scheduling
- Per-task configurable state machines
- A general peer-to-peer agent message channel
- Sub-Teams / nested Leads
- A cross-Team scheduler
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
- **Any version control integration** — Landbridge stores opaque references and never dereferences them
- **Any built-in verifier or completion gate** — completion is a Lead or human verdict (§7, §9 check 4); an external automated gate holds a Lead-class credential and posts `submit_review`, and Landbridge runs nothing itself
- Cross-Instance coordination in `landbridge-meta`
- Agent-facing anything on `landbridge-meta`

---

## 16. Open questions

1. **Closing a Team has no affordance.** The Lead owns it, but nothing exposes it. Needs a command and a decision about what happens to outstanding tasks.
2. **Does `review` mode need a reviewer assignment?** Currently any Lead or human on the Team may confirm. Named reviewers are more correct for larger Teams and a step toward a workflow engine.
3. **Evidence-gated or merge-gated completion** for the code case — the Lead runs the suite or checks the merge before accepting. Skill-level guidance, but it shapes the default bundle.
4. **Runner compatibility window.** Deferred to alpha, when there is real data on how stale installed runners get.
5. **Relay capacity and COGS.** Bandwidth is a direct cost line.
6. **When does self-hosting arrive**, and does it ship the whole Instance or only `landbridge-relay`?
7. **Per-task OS users** (§13): provisioning them needs sudo the enroll flow currently requests once — is per-task user creation acceptable at enrollment, or does peer-credential checking carry v1?
8. **Transcript redaction is deliberately unresolved.** Transcripts are served verbatim to human operators for terminal tasks only (§12, §13). Pattern redaction (known token shapes, `Authorization` schemes, provider key formats) is the obvious first move and catches only shaped secrets — not a bare password, not a value the printing tool transformed, not anything whose only signal is meaning — so it is a mitigation that would be easy to mistake for a boundary. Deciding this gates four things now deferred: live tailing of a running task, reading a `verifying` task's transcript (whose worker token is still live), any agent-facing transcript read, and **serving a supervised service's log** (§10) — which is live tailing by definition, since a service is never terminal. The service case may be separable: the terminal gate's stated justification is that a non-terminal task still holds a live, revocable worker-instance token, and a service child has none. But its output is still unredacted and can carry env dumps and secrets in stack traces, so that is a new argument to be made and accepted on its own terms, not an existing one to inherit.

Two findings from designing the deferred service-log path, recorded so whoever resolves this inherits the analysis instead of re-deriving it:

**Build it as a sibling command, not as a nullable `service` field on `read-transcript`.** Almost everything below the identifier is already generic and should be shared rather than copied: the request-id correlation (the waiter table never reads the task id), the whole cursor protocol (offset / max_bytes / next_offset / eof, the range clamp, the UTF-8 boundary trim), the per-machine single-flight semaphore, the range timeout, the write-then-request streaming loop, and the two runner-side invariants that a reply bypasses the bounded outbound ring and the read runs detached off the receive loop. What must *not* be shared is the entry point. Adding a nullable service field makes the relay's ask path bimodal — one branch enforcing the terminal gate, one branch with no gate — and that is precisely the shape a later cleanup unifies. The gate lives inside the relay service specifically so no caller can forget it, and a bimodal entry point quietly reintroduces the possibility. A sibling command keeps the property structurally rather than by vigilance. (Secondary and smaller: the existing command's task field is a Guid-typed id with no converter, so a name cannot ride it anyway.)

**The plane can already answer "does this service exist here" without a new store.** Machine-declared services arrive on the heartbeat (§12), so the existence check that a task row provides for transcripts has a live-registry equivalent — no service table, and no need to push existence-checking down to the runner. Two other things a follow/tail mode must handle that the transcript path does not: the capture layout is per service run rather than per dispatch, and "eof" currently means "caught up", which for a growing log is not the same as "finished".

---

## 17. Build order

0. **Feasibility spikes, before any product code.** The three mechanics the design leans on hardest, against real Claude Code: (a) park→resume — `claude -p --resume` from the recorded directory with re-injected MCP config and a fresh instance token, including the transcript-interleaving hazard when a zombie process still holds the session; (b) `stop` delivered as an injected turn over `--input-format stream-json`, including disposition wind-down — **spike run, result negative**: that flag makes a `claude -p` worker hang instead, and no `-p` configuration consumes a mid-task turn, so stop there is a TTL'd kill (§10 as-built); (c) hook→HTTP tool-call events attributed by `LANDBRIDGE_SESSION_ID`, and what OTel actually yields with and without beta telemetry.
1. Control plane: state machine, Postgres schema, auth, the fourteen checks.
2. `landbridged` against Claude Code: dispatch, stop, kill, heartbeat, tool-call events, concurrent slots.
3. MCP server: Lead and worker tool sets, slash command prompts, skill serving.
4. `/landbridge-enroll` plus the conformance run.
5. Machine Group view, Team view, human inbox.
6. `landbridge-relay`: TCP primitive first, HTTP layer after.
7. `landbridge-meta`: Account records, Instance provisioning, image rollout.
8. **Chaos test.** Kill a runner mid-task with siblings running. SIGKILL `landbridged` and restart it. Partition a machine. Cancel with each disposition. Fail verification three times. Sever a forward mid-transfer. Evict a Lead mid-decomposition. Close a laptop and reattach. Park a task and answer it after the machine is gone. Replay a stale worker-instance token.
9. Second runner config against a different harness.
10. A non-code skill bundle — the forcing function separating coordination assumptions from software assumptions.
