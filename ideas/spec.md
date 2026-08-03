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
| **Adjudication** | Not a module. Completion is a Lead-session or human verdict, never the task's own worker (§7, §9 check 4) — CI and tests are evidence a Lead gathers itself, not a verdict-issuing actor. External automated completion stays possible by holding a Lead-class credential (e.g. a CI webhook), but Docket requires no dedicated verifier role. |
| **Object storage / file transport** | Optional. Artifacts are URLs (§8.1). |

Every one of these is a place where a less disciplined design would grow an integration, a credential store, and an opinion.

### Deployment

Hosted. One Instance per customer deployment, as a container behind public TLS. Instances are completely independent: separate database, signing key, endpoint, skill bundle.

Because the control plane is operated rather than distributed, it can be fixed by deploying. **`docketd` cannot** — it runs on customer machines and may go a year without being touched. The runner contract in §10 is therefore the only frozen interface in the system.

### `docket-meta`

The human-only provisioning control panel: a server-rendered web panel (no MCP SDK reference, so it is structurally not an MCP server) behind an operator passphrase — hash-configured, fail-closed when unset, mirroring the dashboard's operator door. A single operator today; self-serve signup later. It owns Account labels (a name on an Instance; no billing yet), Instance lifecycle (create, suspend, resume, destroy), Instance provisioning, and image rollout.

It runs over a **pool of Docker hosts** behind an `ISubstrate` seam: the local unix socket (the pool-of-one), or a remote Docker Engine API over mutual TLS (the host record carries the client cert + CA). Placement picks a host at create — the operator chooses, defaulting to the least-loaded.

An **Instance is a per-host recipe**: a dedicated Docker network, a Postgres container on a named volume, a `docket-mcp` container, and a `docket-relay` container, co-located and independent. Lifecycle is a **resumable saga** — `provisioning → ready → suspended → destroyed`, plus `failed(step)` — where every step is idempotent (Docker objects are adopted by name/label) and checkpointed, so a meta restart mid-provision resumes from the first incomplete step or an operator retries a stall. Destroyed ≠ suspended: destroy removes containers, network, **and volume** (with a typed-name confirm) and tombstones the record; suspend stops the containers and drops the edge routes but keeps the volume, config, and secrets for resume.

Meta generates each Instance's secrets **before any side effect** and injects them into the containers: the operator-passphrase **hash** (the plaintext is shown once at create and never retained), the private Postgres connection string, the public MCP URL, and a relay bearer shared by the plane and relay. It sets `Docket:MigrateOnStartup` so a fresh Instance self-migrates and upgrades re-migrate, and it **never** sets the dev-only gates (dev-seed, insecure client metadata). It retains the secrets it must re-inject on resume/upgrade (DB password, relay bearer); only the passphrase is shown-once-and-discarded.

An edge **Caddy**, driven live over its admin API (pure HTTP, routes keyed by a stable `@id`), publishes **two routes per Instance** — the MCP endpoint at `<name>.docket.<domain>` and the relay endpoint at `relay-<name>.docket.<domain>`, since `docketd` dials the relay directly (§8.3). Wildcard DNS and Caddy's own TLS automation are operator-side prerequisites. A down edge never blocks suspend or destroy — route removal is best-effort and reconciled later.

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

A Team is a unit of human-authorized work. It owns a budget, a scope declaration, and a set of tasks. It terminates.

**The Lead is a harness client the human drives.** Not a dispatched agent, not a daemon. Someone opens their harness, attaches to a Team, and works. The human is the event loop — which is why the Lead needs no lease, no wake conditions, and no runner.

- A Lead's machine does not need `docketd`. Enrollment and attachment are independent choices — the one thing it buys is the §8.3 human path: only a Lead that has *bound* an enrolled machine can open a forward onto it for its human. Leading a Team never requires it.
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

Attaching gives a fresh Lead an empty context window. It must read its way back from control plane state: task states, open questions, registered services, recent results, budget burn. Takeover is not a handoff — the incumbent's context is not recoverable — so the reattachment path serves both cases and the Team view must be rich enough to reconstruct from.

---

## 5. Authentication and authorization

The control plane is both the OAuth 2.1 authorization server and the resource server for its Instance.

### Credential classes

| Identity | Obtained | Lifetime | Authorizes |
|---|---|---|---|
| **Human** | auth code / device flow | session | create Teams, approve permissions, confirm review verdicts, dashboard |
| **Lead** | human session, claimed against a Team | session or until evicted | create tasks, answer questions, **adjudicate completion** (lead mode autonomously; review mode human-confirmed, §7), read Team state, bind its human's machine and open forwards onto it (§8.3) |
| **Machine** (`docketd`) | enrollment token → client credentials | long, refreshed | runner channel, log stream, relay tunnels |
| **Worker** | minted at dispatch | task lifetime | MCP tools, scoped to `{team, task, worker, instance}` |

There is no verifier credential class. Completion is a Lead-session or human verdict, never the task's own worker (§7, §9 check 4); an external automated gate, if wanted, holds a Lead-class credential rather than a role of its own.

Two derivation paths, deliberately asymmetric: a Lead's authority comes down from a human directly; a worker's is minted by the control plane from the Lead's dispatch decision.

### Worker identity derives from dispatch

A worker never authenticates. The control plane dispatches task X to machine M, mints a token carrying exactly those claims, and `docketd` injects it into the harness's MCP config.

This makes the authority table in §6 structural rather than checked. A worker cannot create a task because its token carries no lead claim — not because an `if` statement rejected it.

**Each dispatch mints a distinct worker instance.** The token carries `{team, task, worker, instance}`; requeue or redispatch revokes the predecessor instance's token, and worker-triggered transitions are accepted only from the incumbent instance. An orphaned harness — SIGKILLed daemon, healed partition — holds a token that is already dead: it cannot flip the task's state under the new worker, and it cannot interleave into a resumed transcript (§11).

### Bootstrap

A human-issued enrollment token, single-use and short-lived, exchanged by `docketd` for machine credentials during `/docket-enroll`.

`docketd --enroll --control-url <plane>` performs the exchange at `POST /enroll`, reading the enrollment token from `--enroll-token-file` or stdin — never argv (§13). The short-lived access token is re-minted at `POST /machine/refresh` (proactively at ~50% of its lifetime and reactively on a 401 reconnect), and credentials persist 0600 under the state dir (`--state-dir`, else `$XDG_STATE_HOME/docket`, else `~/.docket`).

### The invariant

**No path from a worker credential to a lead claim or a human session.** Token exchange must be strictly narrowing.

### Token format

Opaque, not JWT. Revocation is the priority — un-trusting a machine must take seconds, and requeue must kill a worker-instance token just as fast.

### MCP alignment

Targets the released MCP spec (2025-11-25 as of this draft), with the 2026-07-28 release candidate tracked rather than assumed — the RC removes protocol sessions, server-initiated requests, and SSE resumability, and deprecates DCR in favour of Client ID Metadata Documents, so `docket-mcp` ships dual-version and every dependency below names its fallback.

Adopted where already real: Protected Resource Metadata (RFC 9728), Resource Indicators (RFC 8707) for audience-bound tokens, Client ID Metadata Documents (supported by Claude Code today), and `application_type` declaration (SEP-837) since harnesses are CLI clients. Speculative dependencies are progressive enhancement, never load-bearing: `skill://` (SEP-2640, open draft) falls back to plain MCP resources the dispatch prompt directs the worker to read; MCP Apps (SEP-1865, draft extension) sit behind the plain web dashboard, which is §12's primary surface.

MCP hardens *authentication*. Every authorization decision in §6 is Docket's own.

---

## 6. Task state machine

```
submitted ──► working ──► verifying ──► completed
    ▲          │    │          │
    │          │    ▼          └──► rejected
    └──────────┘  blocked_on_input    (retries exhausted)
      liveness      │        ▲
      lost          ▼        │ answer / wake
                  parked ────┘
```

Additional states: `blocked_on_input`, `parked`, `canceled`. `blocked_on_input` and `parked` hold a task whose harness process is *expected* to be gone — per-task liveness is suspended there, and process exit is not a failure (§11).

| Transition | Triggered by | Control plane checks |
|---|---|---|
| → `submitted` | Lead | session carries lead claim for this Team; completion criteria non-empty; namespace assigned; Team budget remains |
| `submitted` → `working` | control plane dispatch | the dispatch transaction *is* the claim: single dispatch per task (`SKIP LOCKED`), target machine `ready`, not under back-pressure, and declaring a profile matching the task's `profile`, if set. Workers do not claim; they are dispatched (§5). |
| `working` → `submitted` | control plane | ack timeout, per-task liveness loss, or machine reboot; increments the infrastructure counter; revokes the worker-instance token |
| `working` → `verifying` | working agent | result reference present; caller is the incumbent worker instance |
| `verifying` → `completed` | Lead or human | **caller is a Lead or human credential, never the task's own worker** (doer/judge split); in `review` mode the verdict carries human confirmation (§7); verdict provenance (`lead-session` \| `human`) is recorded on the completion event |
| `verifying` → `submitted` | Lead or human | verdict is `fail` and verification retries remain |
| `verifying` → `rejected` | Lead or human | verdict is `fail` and verification retries exhausted |
| `working` → `blocked_on_input` | working agent | typed request kind present; caller is the incumbent worker instance |
| `blocked_on_input` → `submitted` | Lead or human | answer landed; a park record is written (preferring the held-lease machine and the stamped harness session ref) and redispatch resumes the transcript (§11). The worker process is gone the moment the task blocked, so there is no in-place resume. Does **not** touch the infrastructure counter — a Lead answering is not an infrastructure requeue |
| `blocked_on_input` → `parked` | control plane | wait TTL expired; lease released; park record written (§11) |
| `blocked_on_input` → `submitted` | control plane | machine liveness lost while waiting; infrastructure counter |
| `working` → `parked` | Lead | `stop` with disposition `preserve_and_park`; park record written after the agent's wind-down |
| `parked` → `submitted` | control plane | the awaited answer or endpoint landed; redispatch runs the full `submitted → working` checks, preferring the park record's machine and directory for transcript resume (§11) |
| any → `canceled` | Lead, human, or control plane | disposition enum present; the control plane may cancel only on Team budget exhaustion (`disposition: budget`) — **as built the budget sweep sends `stop(preserve)` rather than cancelling**, so the task follows its ordinary wind-down and the exhausted ceiling then refuses its re-dispatch, leaving it visibly stalled instead of terminal; a human raising the ceiling resumes it, which cancelling would have made impossible |

No row requires reading a task description or interpreting its criteria.

**Two counters, not one.** Verification failures and infrastructure requeues are different things. A machine rebooting three times should not exhaust the budget a task has for failing its criteria. Only the verification counter drives `rejected`.

Terminal states — `completed`, `rejected`, `canceled` — are final and never resumed.

Leaving `working` clears the task's registered services and releases its relay forwards.

**Two targeting classes.** A task is dispatched one of two ways. *Profile targeting* (the default) routes a `submitted` task to any `ready` machine declaring its `profile` — the `submitted → working` claim above. *Continuation targeting* (`create_task(continues: <task-id>)`) instead resumes a prior task's harness session: the new task is seeded at creation from the continued task's row — `continues_task_id` (lineage), the machine that last held/ran it, that task's `harness_session_ref`, and an `on_machine_gone` policy — as park-record-style affinity, so its *first* dispatch prefers that machine and hands the runner the session ref to resume the transcript (§11), under a **new task id and a freshly minted worker token** (§5 credential descent is untouched; the addressable noun is the prior *task*, not a worker). Continuation is **same-Team only** — a `continues` referencing another Team's task is rejected at creation. Its `profile` defaults to the continued task's; supplying one the preferred machine does not declare is a creation-time error. If the preferred machine is gone at dispatch, `on_machine_gone` decides: `degrade` (default) cold-starts a fresh session on any profile-matching machine and records that conversational memory was lost, or `pin` waits in `submitted` for that machine to return (like a pinned profile). Forking is legal — several continuations of one task each resume the same transcript, and each stamps its own new session ref after its first turn (the existing session-start stamping handles this). Verification, budget, and event-log semantics are unchanged.

---

## 7. Task schema

**Typed**

| Field | Notes |
|---|---|
| `completion.mode` | `lead` (default) or `review`. Determines which credential may adjudicate completion, nothing else (§9 check 4). |
| `completion.criteria` | Opaque, non-empty string. In `lead` mode the Lead judges it against evidence it gathers; in `review` mode a person reads it. The control plane never parses it. |
| `completion.provenance` | Set on completion: `lead-session` or `human` (§9 check 4). Null until completed. |
| `namespace` | Server-assigned `team-{id}/task-{id}`. Guaranteed unique. What an agent maps it onto is convention. |
| `workspace` | Opaque blob assigned by the Lead. Where the work happens, how it is isolated, which ports it may use. Shape defined by the skill. |
| `team_id`, `parent_task` | Lineage. |
| `budget` | The per-dispatch harness-local hard cap (§10). Containment, not a meter (§9 check 9). **As built it is a Team-level setting, not a per-task field:** the Team's configured per-dispatch cap is what each dispatch commits against the ceiling and hands the harness, so every task in a Team gets the same cap. A per-task override would need its own commitment accounting and is not built. |
| `expected_duration` | Lead's guess. Distinguishes stuck-short from long-running. |
| `profile` | Optional runner profile name. Exact-match routing; the control plane never interprets it. |
| `attempt` | Server-maintained counter, incremented on every requeue and redispatch. Visible to the worker, so a dispatched agent knows it may be inheriting a dirty workspace and should inspect before trusting or overwriting (§11). |
| `author_identity` + `is_human` | **Provenance.** Lets the receiving side treat human- and agent-authored instructions differently. |

**Prose:** `description`, `result_summary`, `blocker_note`.

### Completion modes

Not all work has a mechanical check. What generalizes is not the *check* but the *authority*: the worker does not decide it is done — and a task's own worker can never complete it (§9 check 4, the doer/judge split, the same shape as a subagent that never accepts its own work).

| Mode | Verdict from | Typical use |
|---|---|---|
| `lead` (default) | the Lead session's verdict, autonomously | anything the Lead can check itself — run the suite, read the diff, inspect CI, re-verify the worker's claims |
| `review` | human-confirmed verdict via Lead or human session | judgment calls a human must own: written deliverables, research, design, recommendations |

Both land in `verifying` and take the same transitions; which credential may rule is the only difference, and completion records its provenance (`lead-session` \| `human`, §9 check 4). Tasks awaiting review appear in the human inbox (§12), which is what stops `review` from being a black hole.

**`lead` is the default because the orchestrator is the judge.** This is the Claude Code shape: the Lead decomposed the work and holds the plan, so it is the right judge of whether a task met its bar — but it judges *another* worker's output, never its own, and it judges against evidence it gathers itself. Docket hands it no verdict: CI and tests are that evidence, not a verdict-issuing actor, and the deterministic-verifier role is deliberately not something Docket runs (§15). Reject cheaply, accept carefully.

**`review` verdicts must carry human confirmation.** Some acceptance is a human's to give — and a Lead is a model that the untrusted text §13 warns about can argue into accepting. `submit_review` in `review` mode is therefore not honoured from an unattended Lead turn: the verdict is confirmed by the human through an elicitation prompt where the client supports it, or lands in the inbox for confirmation under the human session credential. A lead claim alone completes a `lead`-mode task but never a `review`-mode one — §9 check 4 applied at the door it would be easiest to forget.

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

A consumer finding nothing goes to `blocked_on_input` and is woken when the endpoint appears — the park path, like every other wait (§11).

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

**Implementation note (this deployment).** The relay holds no `docketd` channel of its own, so the **control plane** — which does — relays `open-forward` to *both* ends over the runner channel: the producer end (step 5) and the consumer end (which carries the bind instruction of step 3). Both ends receive the same single-use-per-role grant and dial the relay themselves. The consumer's bound loopback port returns to the control plane as a `forward-opened` event, and `open_forward` hands the worker a ready `{host, port}` — the grant and relay URL stay inside `docketd` and never reach the agent. v1 is one tunnel per forward id (the grant is single-use per role and the relay pairs exactly two ends), so the consumer listener accepts exactly one connection; the step-4 "one listener, N tunnels" generalization is deferred.

**Both ends authenticate independently. Neither authenticates to the other.** No peer key exchange, no service credentials, nothing to distribute.

**Only registered services are forwardable.** Otherwise it is a fleet-wide port scanner, and local-trust services like Postgres with `trust` in `pg_hba.conf` become reachable from any agent in the Team.

**Generic TCP is the primitive.** An HTTP layer sits on top: subdomain per service, never path prefix, wildcard cert, websocket upgrade from day one. Its justification is human-to-service access. Build TCP first; the HTTP layer is specified in §8.4.

**A human is a consumer too — the non-HTTP human path.** §8.4's preview covers a human reaching an *HTTP* service with no `docketd` on their side. It cannot cover `psql` against a worker's Postgres, and that is the case a person most often needs. The consumer end of a forward was never really a task: it is a **machine**, and a worker's is merely derived from its dispatch. So a **Lead** may be the consumer, with its human's own machine as that end — the same single grant presented once per role, the same two `open-forward` commands, the same splice.

Two gaps close for that to work:

- **A Lead binds a machine, explicitly.** A Lead is a harness client a human drives (§4) and its machine need not run `docketd` at all, so nothing is derivable — the human *says* which enrolled machine is theirs, and can revoke it. The binding keys on the **human**, not the Lead credential or the Team: the box on a person's desk outlives their session and spans the Teams they lead, and a takeover therefore does **not** inherit the evicted human's machine — the new Lead has its own binding or none. One live binding per human and per machine (moving desks is unbind-then-bind; two people never both claim one box, which would land one person's forward on the other's machine). Visible in `get_team_state` and on the §12 Machine Group view, since a bound machine is a Lead-facing forward target.
- **The grant binds no consumer task.** Exactly like an §8.4 preview mint: the consumer is not a worker instance, so the grant records only the producer and the Team. Check 11 is unchanged and Team-scoped — a Lead reaches nothing a worker in its Team could not already reach, and another Team's service reads as not-registered.

**The human's forward is one connection, briefly available.** v1 is one tunnel per forward id (§8.3 above), so the returned address carries exactly one connection — one `psql` session, not a pool — and the consumer listener closes if nobody connects within the bound accept window. A Lead therefore opens it *when its human is ready to connect* and opens another for another session. Once spliced, the ordinary rule holds: the connection survives grant expiry and lives until the owning task leaves `working`.

**A grant is a connection-establishment credential.** It is checked when a tunnel opens; an established splice persists until the owning task leaves `working`. A database session or websocket is never severed mid-flight by grant expiry, and no renewal path needs to exist.

Per-Team accounting alongside the token budget (§9 check 10): the forward rate limit is enforced at grant mint, and the relay counts the bytes it splices and reports them to the plane over the plane-facing HTTP contract. The counting gates nothing — an established splice is never severed, so there is no byte allowance to breach; §9's as-built note records why.

### 8.4 HTTP preview layer

The HTTP layer of §8.3 (subdomain per service, wildcard cert, websocket upgrade), specified now that the TCP primitive is built. Its sole justification is **human-to-service access** — a shareable preview URL for a registered service, needing no `docketd` install on the human's side. It supplements, and does not replace, the `docketd`-loopback path of §8.3, which remains the mechanism for non-HTTP services (e.g. Postgres) and machine-to-machine forwards.

**Topology — a separate module on top of the tunnel.** The preview frontend is its own separately-deployable module (§3), not a change to the pure byte-splicer of §8.3. It is a TLS frontend on a wildcard origin (`*.preview.<domain>`; domain and cert are its config — a provided PEM to start, ACME DNS-01 later) that terminates HTTPS and routes strictly by **Host header** — subdomain label, never path prefix, so cookies and absolute paths in the served app are never rewritten. The frontend *is* the consumer end (there is no consumer `docketd`): for each browser connection it dials the relay's tunnel as the **consumer** for a freshly-minted forward id and reverse-proxies the request — including a websocket upgrade — while the producer's `docketd` dials the matching **producer** end; the relay splices them by forward id exactly as §8.3, unchanged. The grant and splice rules of §8.3 hold (grant checked on connect; a live splice survives until the owning task leaves `working`).

**Subdomain labels are opaque and unguessable.** A label is a random token, never `service-task-team`; structure is never encoded in the hostname. The label is the lookup key into a preview mapping `{team, task, service, expiry, auth-policy}`.

**One forward id per browser connection — the splicer is unchanged.** A browser opens many parallel connections; rather than generalizing §8.3's one-connection-per-forward-id splice, the preview mints a **fresh forward id per browser connection**, so the pure relay pairs each exactly as it already does. N browser connections are N forward ids orchestrated by the control plane, and the producer `docketd` **dials on demand** — a fresh producer tunnel per connection — instead of the single pre-dial of the machine-to-machine path. (Per-connection orchestration cost is acceptable for preview traffic; a pooled generalization can come later.)

**Auth is per-mint, gated by default.** A mint may explicitly opt into a **public capability URL** — the unguessable label alone admits the request — which then carries a short mandatory TTL. Public is the exception and always time-boxed; gated is the default, and requires a §12 operator session.

The preview lives on `*.preview.<domain>`, a different origin from the dashboard, so the operator's `docket_session` cookie is **never** widened to reach it — because a browser's request bytes are spliced verbatim into team workload code, an operator session on the preview origin would be exfiltratable. Gated auth is therefore a **redirect + per-label cookie** handoff (an oauth2-proxy shape): a gated request with no preview session is 302'd to the dashboard origin (`/dashboard/preview-auth?label=…&return=…`), where the existing host-scoped `docket_session` confirms the operator may reach the label's Team and the control plane mints a **one-time, short-lived code** (in memory — no schema); the browser carries the code back to the preview origin, the frontend exchanges it at the plane for a per-label preview session and sets that as its own short-TTL `HttpOnly` cookie on the preview origin. Subsequent connects present that cookie, which the plane validates alongside the grant. A `Bearer` is the tooling path — a Lead validates directly and receives a plain 401 rather than a redirect. And on **every** spliced request the frontend strips the `Authorization` header and the `docket_session`/`docket_preview` cookies from the head before forwarding — the one deliberate exception to never-rewrite, so auth material never reaches the previewed service.

**Minted two ways (§10, §12).** The owning task's worker mints via `open_preview(name)`, receiving the URL to hand back in a report; a human mints from the §12 dashboard's registered-service list. Both produce the same mapping; the public/gated choice is set at mint. Only a registered service owned by a `working` task in the caller's Team is previewable (check 11).

Preview traffic rides the same per-Team accounting as any other forward (§9 check 10): each browser connection mints a grant, so preview load counts against the forward rate limit, and its bytes are counted and reported like any other splice.

---

## 9. Enforcement checks

1. `completion.criteria` is non-empty at task creation.
2. `namespace` is server-assigned; collision is structurally impossible.
3. Only a lead claim may create tasks.
4. Completion comes from a Lead or human credential, **never the task's own worker** (doer/judge split); `review` verdicts carry human confirmation; verdict provenance (`lead-session` | `human`) is recorded on the completion event.
5. Single dispatch per task; the dispatched machine is accepting work and declares a matching profile name.
6. One Lead per Team; takeover is explicit and logged.
7. Ack timeout and per-task liveness timeout → requeue.
8. Verification retries exhausted → `rejected`.
9. Team budget ceiling on **committed authorization** — containment, not metering: each dispatch commits the per-dispatch harness cap it is handed, and reaching the ceiling refuses new dispatch *and* `stop`s the Team's working tasks. Measured spend is not an input; nothing ingests it (see the as-built note below).
10. Forward rate limit per Team per window, enforced at grant mint — the enforcing half. Per-Team relay bytes are **measured and reported, but nothing is enforced on them**: there is no byte *allowance*, because §8.3 forbids severing an established splice (see the as-built note below).
11. Forwards and previews (§8.4) resolve only to registered services in the same Team owned by a `working` task.
12. Cancellation carries a disposition enum; `TTL=0` means immediate kill.
13. Token exchange is strictly narrowing.
14. Worker-triggered transitions are accepted only from the incumbent worker instance; requeue and redispatch revoke the predecessor's token first.

Nothing else. Any addition that requires knowing what a task is *about* should be rejected outright.

**Subagent depth is the harness's problem.** Fan-out cost is contained by check 9 instead — the budget ceiling bounds authorized spend regardless of tree shape, because a subagent tree runs inside its parent dispatch and therefore inside that dispatch's committed cap. Enforcement is refusing new dispatch plus `stop`/`kill`, since Docket does not hold the model keys and cannot stop an in-flight call.

**As-built reconciliation for checks 9 and 10 (2026-08-03).** Earlier drafts of both checks described enforcement against *measured* quantities. Neither quantity is measured, so this is what ships and why.

**Check 9 enforces committed authorization, not spend.** Docket ingests no spend telemetry at all: it does not sit between a harness and its model provider, the plane *exports* OTel and receives none, there is no OTLP receiver, and `tool-call` events carry no token counts. So a "spend so far" column would be permanently zero, and a ceiling enforced on it would be the same lie check 9 already told when `TeamBudgetRemains` was hardcoded `true`. What *is* knowable is what Docket authorized: every dispatch hands its harness a hard per-dispatch cap (§10), so committing that cap at dispatch bounds the Team's exposure without measuring anything — and cannot be defeated by a signal that never arrives, which is exactly how a metered ceiling fails. Three consequences, each deliberate:
- **Committed only rises.** It is never released when a task completes, because "unspent" is precisely the quantity that cannot be known, and releasing would turn a lifetime ceiling into a mere concurrency limiter — a $100 Team could run ten thousand sequential $10 tasks, which §4's "a Team owns a budget and terminates" rules out. A task that used a penny of its $10 cap still consumed $10 of ceiling. The escape valve is a human raising the ceiling, which is the control this exists to give them.
- **The one release** is a task cancelled before it ever dispatched: no process ran, so no spend was possible, and holding that commitment would overstate authorization as badly as releasing a finished task's understates it.
- **Charged per dispatch, not per task.** Each attempt is a fresh process that can burn the whole cap, so a requeued task commits twice. Conservative and true, and it makes requeue amplification visibly expensive rather than hidden.

A Team with no ceiling configured is **unbounded**, exactly as before any of this existed — the unconfigured default is unlimited, not zero. A ceiling set without a per-dispatch cap admits work but commits nothing and hands the harness no backstop, so it can never be reached; the dashboard says so rather than implying enforcement.

**The ceiling is set only by a human, from the §12 dashboard.** There is deliberately no MCP tool, and the dashboard's write refuses a Lead session even for its own Team — the one place that surface departs from "a Lead may act within its Team". A budget is the control that bounds the Lead itself, so a Lead able to raise it is enforcement living exactly where a model can reason past it (§2 principle 3). A Lead *reads* its own ceiling through `get_team_state`, because otherwise a refused `create_task` is an unexplained rejection it will simply retry against.

**Measured spend attribution remains future work, and nothing depends on it.** §10's telemetry-ingest section describes the intended attribution source; until a receiver exists, it feeds nothing. Adding it later *adds* a signal — it does not change how the ceiling is enforced, which is the point of enforcing on authorization.

**Check 10 ships as a forward rate limit only.** A grant is the one thing no forward can happen without, so the limit is enforced per Team per rolling window at grant mint in the control plane — the cheapest and strongest point: it needs no cooperation from a relay that may be unreachable, and it sits upstream of the thing it bounds, so no peer can outrun it. Like check 9 it counts *authorizations* (grants minted) rather than tunnels that opened, since what a peer did with a grant is not knowable. The default is deliberately generous — an §8.4 preview mints a fresh grant per browser connection, so a page load is legitimately several — because this bounds a runaway loop rather than rationing normal use.

**Bytes are measured; no byte allowance is enforced.** The relay counts every byte it splices, at the one choke point in its pump, and reports per-forward totals to the control plane over the plane-facing HTTP contract that already carries grant validation (`/relay/usage`, same shared bearer) — deliberately **not** the frozen runner wire of §10, which the relay does not speak. The plane attributes those bytes to Teams through the forward ids it minted, so the relay never learns whose traffic it is moving; it still moves opaque bytes and interprets nothing, it only says how many.

Two properties keep that number honest, and both are why it is a containment signal rather than an invoice:
- **Best-effort.** Reporting is periodic plus once on close, so the figure trails live traffic by up to one interval and a relay that dies loses its unsent tail. The Team's view therefore carries the timestamp of the last report, and an unmeasured Team is distinguishable from one measured at zero — an absence of measurement is not a measurement of no traffic.
- **Counted, never gated.** Nothing in the splice pump can refuse a frame. There is no byte ceiling, because §8.3 forbids severing an established splice mid-flight — a database session or websocket is never cut by policy once spliced — so what a reached byte ceiling should actually *do* remains unresolved, and guessing at the pump would break that promise. Bytes bound nothing today; the *forward rate limit* is what bounds a Team's relay use.

This is the one quantity in §9's budget story that is genuinely measured rather than authorized, which is exactly why it lives in its own table rather than beside check 9's committed ceiling: putting a measured number next to an authorized one invites the confusion this note exists to prevent.

---

## 10. Interfaces

### Agent → control plane (MCP)

**Lead:** `create_task` · `answer_input_request` · `submit_review` (lead-adjudicated; human-confirmed in `review` mode, §7) · `cancel_task` · `get_team_state` · `get_task_report` · `bind_machine` · `unbind_machine` · `open_lead_forward` (§8.3 human path)
**Worker:** `get_task` · `report_result` · `request_input` · `register_service` · `open_forward` · `open_preview` (§8.4)

There is no `claim_task`. Workers are dispatched, never claimants (§5, §6) — the first thing a worker does with its minted token is work, and its calls identify it.

**As-built reconciliation (2026-07-30).** The tool surface implemented deliberately differs from an earlier draft of this list, and this list now reflects what shipped:
- **No `claim_lead` / `release_lead` tools.** A Lead is not an agent that claims a tool — Lead identity *is* the credential: a human authenticates (§5 OAuth), claims the Team through the lead-claim flow, and holds a Lead session token. There is nothing for an MCP agent turn to call; claiming/releasing is the credential lifecycle, not a task action.
- **No `list_teams` / `get_machine_group_status` tools.** The cross-Team and machine-group views are a *human* surface, served by the §12 web dashboard (with a structured-data twin for a reattaching Lead) — not agent MCP tools. An agent sees only its own scope via `get_team_state`.
- **`report_blocker` folded into `request_input`.** Blocking is a single typed request (§6/§11), so the one `request_input` tool carries the kind; there is no separate blocker tool.
- **`get_task` added (worker).** A dispatched worker's opening move is to read its own assignment; this is that read, scoped to `{team, task, worker, instance}`.
- **`open_preview` added (worker, §8.4).** The reversed decision to build the HTTP preview layer adds a worker tool that mints a shareable preview URL for a service the task has registered; the human-facing mint is the §12 dashboard. Scoped like `open_forward` (worker owns the service); the public-vs-gated auth choice is set at mint.
- **`report_result` gains an optional in-band `report` (worker), read back by `get_task_report` (Lead).** The worker's summary of what it did and verified, evidence pointers, and proposals (e.g. "task X should run on profile Y") rides UP through the plane exactly as the Lead's `description` rides DOWN — opaque content the plane stores verbatim and never parses (§2 principle 1). It is the symmetric half of the brief: the plane already carries Lead→worker prose in-band, so a worker→Lead summary is the same shape in the other direction. The result *reference* stays the load-bearing artifact pointer; the report is **annotation, not authority** — a Lead reads it as untrusted agent claims (§13) and verifies before accepting (§7, §9 check 4). It is size-capped (16 KB); over-cap is refused so real detail goes to the workspace behind the reference, not the plane. Being prose, it never rides the bulk status read: `get_team_state` carries only a `has_report` flag per task, and the Lead pulls each report deliberately, one task at a time, with `get_task_report` (delimited as untrusted, §13). It surfaces to a successor worker on `get_task` (that task's own report to its next worker — not a fan-in surface) and to a human on the §12 dashboard. There is deliberately **no `await`/long-poll tool**: a Lead polls `get_team_state` on its own pacing, and a worker's dispatch *proposal* rides `request_input` (blocking) or this report (non-blocking) — Docket has no worker-creates-tasks machinery.
- **`bind_machine` / `unbind_machine` / `open_lead_forward` added (Lead, §8.3 human path).** The human path to a non-HTTP service. `open_lead_forward` is deliberately its own tool rather than making `open_forward` lead-callable: tool names are one flat namespace on this server, and the two differ in what "your machine" means (a task's dispatch machine vs. a human's bound machine) and in what a caller must be told (one connection, briefly available). Forking one tool on the caller's credential class would also make it the only tool that inspects *which* class called and changes meaning — authority stays structural either way (each tool refuses the other's credential at the door), so the split costs nothing and keeps both descriptions honest. `bind_machine` is an *action* by the human's session, which is why it is a tool rather than a §12 dashboard view; the dashboard shows the resulting binding.

This keeps §5's rule intact — authority is structural, from the credential, not from which tools exist — and moves human-facing reads to the human-facing surface (§12).

Status tools return counts and states — **never prose**. Free text is fetched deliberately, one item at a time, delimited as untrusted — including the worker's report, which `get_team_state` flags (`has_report`) but never carries, leaving `get_task_report` to pull it per task (§13). Responses are scoped by credential: a Lead gets full Team state, a worker gets its own task plus registered services and whether a Lead is attached.

**Slash commands are a convenience layer, not the API.** `/docket-teams`, `/docket-machines`, `/docket-lead`, `/docket-status`, `/docket-enroll` ship as MCP prompts. Surfacing prompts as slash commands is client behaviour and not universal, so every command must map onto an independently-reachable surface — nothing may be reachable *only* through a prompt. Per the as-built reconciliation above, that surface is a tool for agent actions (`/docket-status` → `get_team_state`), the §12 dashboard and its structured-data twin for the human cross-Team/machine views (`/docket-teams`, `/docket-machines`), the credential/lead-claim flow for `/docket-lead`, and the enrollment flow for `/docket-enroll`.

Skills ship as MCP resources, reaching every agent on connect where the client supports auto-discovery (`skill://`, SEP-2640, is draft — see §5); the guaranteed path is the dispatch prompt directing the worker to read the skill resource before starting.

### Control plane ↔ runner (closed enum)

**The only frozen interface in the system.** A runner rejects anything outside the vocabulary.

**Outbound:** `dispatch` · `stop(ttl, disposition)` · `kill` · `open-forward` · `read-transcript`
**Inbound:** `started` · `session-started` · `alive` · `tool-call` · `subagent-spawned` · `exited` · `auth-failed` · `forward-opened` · `forward-closed` · `rebooted` · `transcript-chunk`

`read-transcript`/`transcript-chunk` are a strict request/reply pair correlated by an opaque request id — the only place in the vocabulary where the control plane pulls and the runner answers, rather than the runner pushing. It exists so transcript bulk is flow-controlled by the reader: one chunk in flight, the next requested only once the reader has taken the last, and replies bypass the runner's bounded event ring entirely (a dropped chunk is a corrupted read, and a transcript backlog must never evict a liveness event or delay a `kill`). The runner sends the file's bytes and interprets nothing (§13). §12 has the read path.

**Every message carries a task id.** A machine runs many agents concurrently; there is no implied current task in either direction. This is the change most expensive to retrofit — get it right before anyone enrolls.

`started` (harness up) is distinct from dispatch ack (runner received). Cold start takes time, and their requeue semantics differ: a failed ack means nothing happened, so requeue is free; a death after `started` means side effects may exist.

Nothing in this vocabulary is domain-specific.

**`stop` is a message to the agent, not a signal, wherever the harness allows it.** A signal cannot carry a disposition: Claude Code's documented SIGTERM behaviour is abort-the-turn and exit, which silently turns `preserve` into `kill` for the flagship harness. Where the harness supports stdin message injection (Claude Code: `--input-format stream-json`), `docketd` delivers `stop` as an injected turn — the agent reads the disposition, winds down, persists, and exits — and signals are reserved for TTL expiry and `kill`. The runner config's `stop` section declares which delivery the profile supports; the frozen vocabulary names the command, never the transport.

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
| `profiles[].telemetry` | Per-profile opt-in (off by default) that points a harness's own OTel export at the operator's collector: `otel`, `endpoint`, and `env` for the harness's own enable flag. `docketd` sets only vendor-neutral `OTEL_*` — a harness's telemetry variables are data like everything else. Never enabled without a destination. |

**A daemon can drive several harnesses or postures.** Profiles exist for genuinely different setups on one machine: Claude Code alongside Codex, a restricted permission posture for sensitive work, a pinned version being canaried during an upgrade.

A task may carry an optional `profile` string, matched **by exact string equality** at dispatch. The control plane never learns what a profile name means — only whether a machine declares one. Absent a request, `default`. Requested-but-absent, the task sits visibly undispatchable. This is deliberately not a capability manifest, which §15 still excludes: profiles are identifiers a human chose, not descriptions Docket reasons over.

**Profiles describe how to run an agent, never what kind of work it does.** `profiles: {frontend, backend}` is task routing disguised as machine config, and it puts the control plane back in the business of meaning.

Three constraints are load-bearing rather than cosmetic:

**`docketd` never invokes a shell.** `spawn` is argv passed to `execve`, which is what makes it safe to deliver an agent-authored prompt as an argument — and most harnesses require that. There is no shell to inject into. If a harness genuinely needs shell interpretation, it gets wrapped in a script; a shell is never added to `docketd`.

**Two hard prerequisites, neither of which degrades gracefully.** A harness must be an MCP client, since that is a worker's only channel to Docket. And it must run to completion without prompting for approval — a headless agent waiting for a click nobody will make surfaces as a liveness timeout rather than an error, which is the most expensive way to find a misconfiguration. Headless posture is a named prerequisite with sharp edges the enroll skill enumerates: managed settings on corporate machines can forbid permission bypass outright; "don't ask" modes silently *deny* tools that require user interaction rather than prompting; and a permission-prompt tool is the middle path that turns approvals into `request_input` escalations instead of hangs.

**`docketd` sets `DOCKET_MACHINE_ID` and `DOCKET_TASK_ID` on everything it spawns**, not configurably. Stray-process cleanup on start scans for its own machine id, which is what makes the restart-equals-reboot guarantee survive a `SIGKILL`ed daemon. The same scan runs per task at exit, keyed by `DOCKET_TASK_ID`: a dev server that `setsid`s out of the task's process group survives group kill and keeps the task's assigned port — worse than a leak, since a later consumer's forward reaches a plausibly-alive stale service. Task-exit cleanup catches it while the port assignment is still known.

**`events.source: none` is a supported, honest answer.** Liveness degrades to process-alive and progress renders as "not reported." A fabricated event mapping produces a machine that looks healthy and is not, which is worse than a machine that admits what it cannot see.

**As-built reconciliation (2026-08-03).** `terminal` is the only source implemented: `hooks` and `otel` parse and are consumed nowhere, so they behave exactly as `none`. **The process-alive degradation above is not wired**, which makes `none` considerably more expensive than this section implies. `docketd` has the check (`ProcessSupervisor.IsTaskLive`) but nothing calls it, and the plane never learns process state — the machine heartbeat carries a running-task list without refreshing per-task activity, and a worker's own MCP calls do not refresh it either. Plane-side per-task activity is refreshed *only* by an inbound `started` / `session-started` / `alive` / `tool-call` / `subagent-spawned`, and a non-`terminal` profile emits exactly one of those, once, at spawn. The consequence is that such a profile loses and redispatches every task outliving the per-task liveness window, uncapped. `docketd` warns at startup for each profile declaring a non-`terminal` source. Closing this needs either the process-alive signal this section promises or a periodic `alive` event; until then `none` is honest about what it reports and misleading about what it costs.

Two smaller as-built notes on the same seam: `alive` and `subagent-spawned` are in the wire vocabulary but have no producer, and `events.mapping` overrides the stdout stream's *property names* (`type_key`, `tool_use_block_type`, …) rather than mapping harness event names onto that vocabulary — with unrecognized keys falling back silently, so a fabricated mapping does not fail at load.

`work_root` deserves a note: `docketd` spawns each task in `{work_root}/{task_id}`. This is *not* the task's workspace — the runner never interprets the opaque `workspace` blob. It is a unique machine-local scratch directory to start in; the agent constructs its real workspace from what the Lead assigned.

### Concurrency and back-pressure

Machines do not declare a concurrency limit. A declared number is a guess that is wrong in both directions, and agents vary too much in weight for it to mean anything.

Instead `docketd` observes its own load, memory, and disk, and **stops accepting dispatch when it is under pressure** — resuming when it clears. Derived, not asked for, consistent with principle 2. A saturated machine keeps running what it holds and appears as `back-pressure` in the Machine Group view. (As-built, 2026-08-03: the view renders `ready` / `not ready` / `back-pressure` from two heartbeat booleans; there is no `saturated` label and no machine state enum.)

This exists to break a feedback loop rather than to ration capacity. Without it, a thrashing machine misses heartbeats on every task it holds at once, all of them requeue, and nothing prevents the same machine from immediately being redispatched them. Back-pressure makes overload self-correcting instead of self-reinforcing.

A profile may declare `max_concurrent` for reasons unrelated to load — a licence limit, a rate-limited provider, a restricted posture kept to one at a time.

Liveness splits accordingly:

- **Machine heartbeat** — `docketd` on its own timer. Loss means every task on that machine is suspect.
- **Per-task liveness** — derived from `started` / `tool-call` / `exited` scoped to a task id, plus process-alive for that PID. Suspended while a task is `blocked_on_input` or `parked` (§11). (As-built, 2026-08-03: the process-alive half is not wired — see the event-relay reconciliation above. The window is 60s and is not configurable.)

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

OTel from harnesses, plus tool-call hooks where the harness has them — Claude Code's hooks can POST JSON to a loopback HTTP handler, a cleaner per-task event source for `docketd` than log scraping, and hook processes inherit `DOCKET_TASK_ID` for attribution. Per-subagent lineage (`agent_id` / `parent_agent_id`) currently arrives only on beta trace telemetry — treat the subagent tree as progressive enhancement, not a given. Harnesses without equivalent signals render as "not reported" — degraded telemetry is normal, not broken.

**Attribution is wired; ingestion is not.** A profile's `telemetry` block turns the harness's own exporter on and appends `docket.task_id` (plus `docket.machine_id`) to `OTEL_RESOURCE_ATTRIBUTES`, so every metric and event the harness emits reaches the *operator's* collector already bucketed per task — the id §10 requires, carried the only way available to a supervisor that never sees a token. Docket itself receives none of it: there is no OTLP receiver, no token or cost field in the schema, and joining spend to a Team still means joining on task id against the plane's records (`dispatch` carries no Team id). Operator guide: `docs/TELEMETRY.md`.

This is the intended *attribution* source for budgets — best-effort by construction, since Docket does not sit between the harness and the model provider and the machine is the customer's. **It feeds the operator's collector, and it feeds enforcement nothing:** per the paragraph above, no measured spend reaches the plane at all. Budget is therefore containment on *committed authorization* (§9 check 9 and its as-built note): the ceiling is charged the per-dispatch harness-local hard cap (Claude Code: `--max-budget-usd`, passed by `docketd` from the task's `budget`) at each dispatch, which is simultaneously the backstop that holds even when telemetry is off or gamed. Should an ingest path ever land, it adds a signal for humans to read; it does not become the thing enforced, precisely so enforcement cannot be defeated by turning telemetry off.

### Completion is adjudicated, not webhooked

There is no verifier webhook. A task in `verifying` is completed by the Lead (or a human) through `submit_review` over MCP — the Lead reads the result reference and the completion criteria it wrote, gathers its own evidence (a test run, a CI check), and rules (§7, §9 check 4). A verdict against an already-terminal task is refused with the state machine's "gone" rejection, never a silent success. External automated completion, where wanted, is a client holding a Lead-class credential (e.g. a CI webhook posting `submit_review`), not a role Docket runs.

### A2A (external boundary only)

Docket does **not** speak A2A internally. It is exposed at the outer boundary for inbound delegation from foreign orchestrators, outbound delegation to agents Docket doesn't own, and federation between Instances. The A2A data model is adopted internally regardless.

---

## 11. Lifecycles

### Machine enrollment

1. SSH to the box.
2. Start the harness.
3. Connect to `docket-mcp`. Present a human-issued enrollment token; exchange for machine credentials. Declare name, purpose, OS, permission level. (As-built, 2026-08-03: **specs are not collected** — the enrollment record is `name` / `purpose` / `os` / `permission_level` only, and CPU/memory exist solely as live load on the heartbeat, never as declared capacity. The same stale "specs" claim is mirrored in a code comment on `MachineRow`. `permission_level` is persisted but not yet consulted by dispatch, forwarding, or any tool decision.)
4. Run `/docket-enroll`, which reads the enrollment skill from the server and writes the `docketd` config.
5. Agent guides the human through registering `docketd` as a service. Registration needs sudo, so the human executes it.
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

**As-built reconciliation (2026-08-03).** Steps 4–7 do not exist. There is no `/docket-enroll` prompt (no MCP prompt is registered at all) and no conformance run: nothing in the plane dispatches trivial work to a new machine or judges it. Nor are `ready` and `unclaimable` machine *states* — there is no machine state enum; readiness is two per-heartbeat booleans, and an enrolled-but-disconnected machine is absent from the Machine Group view rather than shown as unclaimable. Today enrollment yields credentials, the operator authors the runner config with the `docket-enroll` skill, and the skill's manual smoke test stands in for the conformance run. The config-stamping below is likewise unbuilt.

The wizard *displays* results; the control plane *determines* them.

Configs are stamped with the generating skill version. `/docket-enroll` is idempotent and re-runnable.

### Cancellation

`stop` carries a TTL and a disposition enum (`preserve` / `discard` / `preserve_and_park`), plus optional free-text reason. Default is `preserve`.

TTL is set by the Lead per situation. `TTL=0` means kill immediately without waiting for ack.

Preservation is the agent's job — persist work in progress to the workspace substrate however that domain does it. The runner does not touch the workspace, so **the kill path is lossy by construction.** `preserve` and `preserve_and_park` are only as good as the harness's `stop` delivery (§10) — a signal-only profile cannot honour them, and the enroll conformance run makes that visible before it matters.

`discard` means removing this task's workspace instance, which is only safe *because* isolation is task-scoped. Under a shared checkout it would destroy a sibling's work. `discard` is deferred while the task is `verifying` — deleting a workspace mid-adjudication (the Lead may be running the criteria against it) turns a cancellation into a spurious verdict.

### Blocked on input

| Kind | Answered by | Resolution |
|---|---|---|
| `question` | Lead or human | answer text |
| `spawn_request` | Lead | new task created, id returned |
| `auth_help` | human | credential provisioned |
| `endpoint_wait` | control plane | woken when service registers |
| `unreachable` | human | artifact or forward could not be reached |

Threaded on the originating task, provenance-tagged. A wait TTL prevents indefinite lease holding: on expiry the task parks and is redispatched when the answer lands. This is what lets a Team survive its Lead's session ending.

**Waiting is always the park shape; there is no live blocking wait.** A headless worker that has asked a question has nothing left to do in-process: it reports the request, persists, and ends its turn — process exit while `blocked_on_input` or `parked` is expected, and per-task liveness is suspended there. Holding an open MCP call instead would strand the worker on client-side tool timeouts, and the MCP release candidate removes stream resumability, so a dropped wait would be unrecoverable. `endpoint_wait` is the same shape: the consumer parks; the control plane wakes it when the service registers.

**Park writes a record: `{machine, working directory, harness session ref, attempt}`.** Redispatch prefers that machine *and that directory*, because harness transcripts are machine- and directory-local — Claude Code resumes a session only from the directory that created it — and resuming with the answer as the next prompt is the cheap path that preserves the agent's accumulated context. Two conditions guard the resume: the predecessor instance's token is revoked first (§5 — resuming a transcript a zombie process still holds interleaves two writers into one session file and corrupts the recovery substrate itself), and `docketd` re-injects fresh MCP config, which resume does not restore. If the recorded machine is gone, redispatch falls back to a cold start elsewhere from the workspace plus the worker's persisted notes — which is why the worker skill treats "persist before asking" as protocol rather than etiquette, and why `attempt` is visible to the successor.

Auth failures report **structured facts** — operation, target, error code, missing scope. The control plane renders the remediation menu from a fixed set it owns.

### Session continuity across tasks

Park resume (above) recovers *one* task's transcript across a requeue. **Continuation** carries a transcript *to a new task*: `create_task(continues: <task-id>)` seeds the new task from the continued task's row — its `harness_session_ref` and the machine that last held/ran it — as a park-record-style affinity, so the first dispatch prefers that machine and reuses the very same resume seam (`--resume` on the recorded session id, `{session_id}` substituted into the profile's `resume.args`), under a new task id and a freshly minted worker token (§5). It is how a Lead says "talk to the agent that has the context" rather than re-briefing a cold worker.

The two resume guards still apply — the machine is preferred because harness transcripts are machine-local, and `docketd` re-injects fresh MCP config, which resume does not restore — plus the machine-gone policy the Lead chose at creation. `degrade` (default) cold-starts a fresh session on any profile-matching machine and persists a `continuation-memory-lost` event so the Lead knows the conversation was dropped; `pin` waits in `submitted` for the recorded machine to return. Forking one task into several continuations is allowed and chains (a continuation of a continuation) are natural; each resumes the same inherited transcript and stamps its own new session ref after its first turn.

**A resumed transcript is stale against a moving workspace.** The continuation's memory is the conversation as it *was*, not the repository as it *is* — commits may have landed, files changed, a sibling task moved on since. A continuation worker must re-verify remembered assumptions against the current workspace before acting, exactly as a visible `attempt > 1` already warns (§7): the transcript is context, never a substitute for looking.

### Partition

On lease-renewal failure the runner halts its agents. Prefer a stall over two machines doing the same expensive work with divergent results.

---

## 12. Observability

**Machine Group view** — machines, running tasks and back-pressure state, heartbeat age, tasks currently running with owning Team, subagent tree expandable beneath each task. Subagents are children in a tree, not peers: no lease, die with their parent, columns are duration and token spend.

**Team view** — tasks by state, budget (ceiling, committed authorization, remaining, and whether exhausted — never measured spend, §9 check 9; the human's set-limits form lives here and is refused to a Lead), relay byte burn (measured, but best-effort and enforcing nothing — shown with the age of its last report, and "not reported" rather than zero when no relay has spoken, §9 check 10), registered services, open input requests, last activity, whether a Lead is attached and who, and **parks per task** — each park is a kill-and-resume of harness context, so this is the number that says whether decomposition is starving on human attention. Doubles as the reattachment surface (§4), so it must be consumable as structured data by a Lead. Sorted so idle Teams drift to the bottom.

**Human inbox** — everything waiting on a person across all Teams: permission requests, auth failures, questions, review confirmations (§7), and **tasks awaiting review**. Without it, `review` mode becomes a place work goes to die.

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

**Transcripts are machine-local and served on demand.** The control plane stores no transcript bytes. `docketd` captures each worker instance's harness stdout and stderr to `<state>/transcripts/<task>/<NNNN>` (opt-in per profile), and the dashboard reads one on demand: the plane asks the machine that ran that dispatch for a byte range, `docketd` replies with one chunk, and the plane relays it into the operator's response without persisting it anywhere. A machine that is offline has no readable transcript, and the dashboard says so rather than hanging — the bytes exist only there. Retention is therefore that machine's own prune window, not a plane-side tier; the earlier "hours" figure assumed a streamed copy in Docket's database, which is not what was built.

**Readable only for terminal tasks, and only by a human.** A transcript is served verbatim (§13), so v1 narrows *when* and *to whom* rather than filtering *what*. When: only `completed`, `rejected`, or `canceled` — a task that can never run again, and whose worker instance token is already revoked. A `verifying` task is deliberately excluded: `report_result` does not revoke the reporting instance's token (the verdict does), so its transcript can still carry a live, replayable worker credential. To whom: a human operator session (§5) — not a Lead over MCP, and not a Lead token presented to the dashboard's structured-data twin, which is the one route that refuses one. Live tailing of a running task stays a machine-local operation for whoever administers that machine; exposing it, and any agent-facing read, is gated on resolving redaction (§13, §16).

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
| Worker token | Generated MCP config in `{work_root}/{task_id}`, 0600, removed on exit | Perms plus task-scoped expiry, instance-scoped revocation |
| Lead session | Held by the harness's MCP client, not by Docket | Whatever that client implements |
| Enrollment token | Transits once, stored nowhere | Single-use, short TTL |
| Forward grant | Memory only | Per-connection establishment, minutes |
| Signing key, database credentials | Container secret store | Never in the image |

**On the "signing key" row:** Docket's credentials are opaque random tokens, hashed (SHA-256) at rest and validated by lookup — not signed (§5, *Token format*). There is therefore **no per-Instance signing/HMAC key today**, and `docket-meta` generates and injects none when it provisions an Instance. The row is a reserved slot: if a future credential form needs a signing key, it lives in the container secret store, never in the image, and meta mints it per Instance then — not before there is a consumer. The per-Instance secrets that *do* exist today are the operator-passphrase hash, the database credentials, and the relay shared bearer.

**The machine token is the highest-value secret on a customer's machine**, because dispatch delivers worker tokens down the channel it authorizes. Compromise is not lateral movement — it is a supply of fresh credentials. Access tokens should be short, the refresh token the only long-lived secret, and the refresh bound to the machine id so a copied credential file fails on another host.

**Docket does not control Lead credential storage.** It is an OAuth token held by the harness's MCP client, and where that lands — keychain, plaintext file, elsewhere — varies by harness. A real dependency, not an assumption.

**The worker token's blast radius is small only if co-tenants cannot read it — and under one shared service user, they can.** Every task spawned by the same `docketd` UID can read every sibling's `{work_root}/{task_id}` config and dial every sibling's loopback forward listener, across Teams. File modes do not create the boundary the token scoping implies. The postures, in order: per-task OS users where the operator can provision them; otherwise peer-credential checks (`SO_PEERCRED` against the task's process group) on `docketd`'s loopback listeners; at minimum, the honest statement that on a shared machine the Team boundary is advisory. The token design still bounds what a *remote* attacker gains — one task, one Team, expiring — but local co-tenancy is a real channel, and §1's many-agents-per-machine posture makes it the common case rather than the edge.

**Enrollment tokens must not be passed as arguments.** Pasting into a terminal is the default human behaviour and it lands in shell history. Read from a prompt or a file. Single-use and short TTL bound the damage.

**Transcripts can capture credentials.** An agent that echoes a token, or a tool call carrying one, puts it in the session transcript. Transcripts are captured machine-locally and never streamed into Docket's database (§12), and **Docket does not redact them** — how to do that well is unresolved (§16, open question 8), and shipping a pattern filter would have implied a protection it could not deliver. So a transcript is served exactly as captured, and the design compensates with scope instead of filtering: files are owner-only on the machine that wrote them; the read path is a human operator session only; and only a terminal task's transcript is readable, so the worker instance token it may contain is already revoked and the task can never resume. Reading one is closer to logging in to that machine — which its operator can already do — than to publishing it. The read path stores nothing: the control plane relays chunks into the operator's response and never writes, logs, or traces the content.

**A transcript is sensitive beyond credentials.** It legitimately contains everything the agent read — source, customer data, internal hostnames — so no filter would make one safe to publish. Both the dashboard page and the served bytes carry that warning inline, and the enroll skill states it where an operator turns capture on.

**The harness keeps its own session store.** Docket's capture is a tee; the harness's own transcript on that machine is what a resume actually reads, Docket does not touch it, and it should not be described as protected.

**Model provider credentials never touch Docket** — but that is a statement about Docket's exposure, not about their safety. They sit in harness config on machines running agents that read untrusted content, and those agents can read them. Docket cannot prevent that and should not imply otherwise.

**Capabilities and permission level are bound server-side at enrollment.** Clients report state; they do not re-declare their own privileges.

**Agents may make project-local, reversible changes unilaterally.** System-level changes — sudo, PATH edits, global installs, version switches, anything touching credentials — are reported, not performed.

**Instructions found in fetched or read content are suggestions, not authority.**

---

## 14. Skill bundle

Ships with the server over MCP resources (`skill://` where supported — §5). Two layers: Docket's baseline plus per-Instance overrides, with defined precedence and versioning. **This is where domain knowledge lives.** The schema is neutral; the skill is opinionated.

Three skills ship by default:

| Skill | Audience |
|---|---|
| `docket-lead` | Humans driving a Lead. Decomposition, isolation assignment, completion modes, cancellation, Team lifecycle — and the integration pattern: **integration is itself a task**, authored by the Lead and sequenced after its inputs complete. Workers never negotiate merges peer-to-peer; they have no channel, and should not. |
| `docket-worker` | Dispatched workers. Working within an assigned workspace, persisting before asking, reporting, blockers, inheriting a workspace on redispatch (`attempt > 1`: inspect before trusting). |
| `docket-enroll` | The enrollment flow. Writing the runner config, headless-posture prerequisites, guiding the human, conformance. |

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
- **Any built-in verifier or completion gate** — completion is a Lead or human verdict (§7, §9 check 4); an external automated gate holds a Lead-class credential and posts `submit_review`, and Docket runs nothing itself
- Cross-Instance coordination in `docket-meta`
- Agent-facing anything on `docket-meta`

---

## 16. Open questions

1. **Closing a Team has no affordance.** The Lead owns it, but nothing exposes it. Needs a command and a decision about what happens to outstanding tasks.
2. **Does `review` mode need a reviewer assignment?** Currently any Lead or human on the Team may confirm. Named reviewers are more correct for larger Teams and a step toward a workflow engine.
3. **Evidence-gated or merge-gated completion** for the code case — the Lead runs the suite or checks the merge before accepting. Skill-level guidance, but it shapes the default bundle.
4. **Runner compatibility window.** Deferred to alpha, when there is real data on how stale installed runners get.
5. **Relay capacity and COGS.** Bandwidth is a direct cost line.
6. **When does self-hosting arrive**, and does it ship the whole Instance or only `docket-relay`?
7. **Per-task OS users** (§13): provisioning them needs sudo the enroll flow currently requests once — is per-task user creation acceptable at enrollment, or does peer-credential checking carry v1?
8. **Transcript redaction is deliberately unresolved.** Transcripts are served verbatim to human operators for terminal tasks only (§12, §13). Pattern redaction (known token shapes, `Authorization` schemes, provider key formats) is the obvious first move and catches only shaped secrets — not a bare password, not a value the printing tool transformed, not anything whose only signal is meaning — so it is a mitigation that would be easy to mistake for a boundary. Deciding this gates three things now deferred: live tailing of a running task, reading a `verifying` task's transcript (whose worker token is still live), and any agent-facing transcript read.

---

## 17. Build order

0. **Feasibility spikes, before any product code.** The three mechanics the design leans on hardest, against real Claude Code: (a) park→resume — `claude -p --resume` from the recorded directory with re-injected MCP config and a fresh instance token, including the transcript-interleaving hazard when a zombie process still holds the session; (b) `stop` delivered as an injected turn over `--input-format stream-json`, including disposition wind-down; (c) hook→HTTP tool-call events attributed by `DOCKET_TASK_ID`, and what OTel actually yields with and without beta telemetry.
1. Control plane: state machine, Postgres schema, auth, the fourteen checks.
2. `docketd` against Claude Code: dispatch, stop, kill, heartbeat, tool-call events, concurrent slots.
3. MCP server: Lead and worker tool sets, slash command prompts, skill serving.
4. `/docket-enroll` plus the conformance run.
5. Machine Group view, Team view, human inbox.
6. `docket-relay`: TCP primitive first, HTTP layer after.
7. `docket-meta`: Account records, Instance provisioning, image rollout.
8. **Chaos test.** Kill a runner mid-task with siblings running. SIGKILL `docketd` and restart it. Partition a machine. Cancel with each disposition. Fail verification three times. Sever a forward mid-transfer. Evict a Lead mid-decomposition. Close a laptop and reattach. Park a task and answer it after the machine is gone. Replay a stale worker-instance token.
9. Second runner config against a different harness.
10. A non-code skill bundle — the forcing function separating coordination assumptions from software assumptions.
