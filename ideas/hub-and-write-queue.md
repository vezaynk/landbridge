# SSE hub, write queue, and runner transport

**Author:** Landbridge
**Date:** 2026-09-04
**Status:** Proposed
**Depends on:** occupancy and the message machine ([`spec.md`](spec.md) §6), Lead inbox SSE ([`lead-inbox-sse.md`](lead-inbox-sse.md)), datastore ([`spec.md`](spec.md) §3), dashboard ([`spec.md`](spec.md) §12), runner vocabulary ([`spec.md`](spec.md) §10).

Three independent workstreams that share a vocabulary. They are not a package.

| Part | Job | Unblocks |
|---|---|---|
| 1. SSE hub | committed reads + EventSource without Core | dashboard poll, Core restart of **reads** |
| 2. Write queue | accept without `Apply` | Core restart of **Lead/worker accepts** |
| 3. Runner transport | §10 over SSE + POST | Core restart of **machine command/event** path |

Postgres stays the source of truth **and** the queue **and** the tail. `SessionStateMachine.Apply` stays the only writer of session facts ([`spec.md`](spec.md) §3). `pg_notify` stays a **private doorbell** for whoever holds `LISTEN`. Consumers never `LISTEN`. Redis, WAL, and table CDC are non-goals.


## Two clocks

Every mutating call sits on two clocks. Collapsing them is how this design goes wrong.

| Clock | What moved | Who observes it | Today |
|---|---|---|---|
| **Plane commit** | `Apply` returned `Transitioned`; the row, event log, and `pg_notify` committed together (`SessionStore.CommitAsync`) | inbox, dashboard, `get_team_state` | the MCP/HTTP return |
| **Worker observe** | occupancy observed, `get_inbox` pull, harness cancel, process tree | `landbridged`, the worker | already async |

`stop_session` / answers already have **no** guarantee the harness has reacted. They **do** guarantee plane commit: the envelope is gone, occupancy desired moved, revoke effects ran. `watch_lead_inbox` and the §12 board mean that clock.

A write queue moves **plane commit** off the MCP return. Worker observe does not change. SSE decoupling does not change either clock.

## As-built (2026-09-04)

| Piece | Where | Notes |
|---|---|---|
| Session writes | `Landbridge.Mcp` → `SessionStore` → Core | `SaveChanges` + `hub_queue` outbox + `pg_notify(landbridge_session_events, sessionId)` in one transaction |

| Dispatch wake | `SessionEventListener` | one-consumer `LISTEN`; `SKIP LOCKED` claim |
| Inbox wake | `SessionEventFanout` | **second** `LISTEN` so a slow snapshot cannot stall dispatch; coalesced single-slot drop-write |
| Lead live read | `GET /lead/inbox/events`, `watch_lead_inbox` | full **snapshot** on wake, not a delta ([`lead-inbox-sse.md`](lead-inbox-sse.md)) |
| Dashboard live read | Blazor Server `DashboardRefresh` | **2s timer**, in-process with Core |
| Dashboard at-rest | `DashboardQueries` JSON twins | already HTTP |
| Machines | `RunnerConnectionRegistry` | **in-memory**; heartbeats do not `NOTIFY`; evaporate on Core restart; tasks rehydrate (`RehydrateMachineAsync`) |
| Event log | session-scoped rows + `EventLogDetail` | trail of Apply, not a live entity stream |
| Runner channel | `/runner` WebSocket | frozen §10 enum; `landbridged` dials out |
| Hub (started) | `Landbridge.Hub` | LISTENs as doorbell, tails `hub_queue`, SSE at `:5300`. Nothing consumes it yet. |



`Landbridge.Mcp` is one ASP.NET process: MCP tools, OAuth, `/runner`, dashboard, and `Apply`. The split below is that process cut into three.

## Target processes

| Process | Owns | Must not |
|---|---|---|
| **MCP gateway** | MCP HTTP, OAuth AS, `/enroll`, Lead/worker tool *accept* and *read* façade | `Apply`, dispatch, holding `/runner` sockets |
| **Core** | `Apply`, command drain, `SKIP LOCKED` dispatch, token **mint** at dispatch | long-lived EventSource, MCP accept |
| **Hub** | `LISTEN`, read-only `SELECT` of views, SSE sockets | writes, MCP, runner **apply** of occupancy |
| **`landbridged`** | spawn, §10 consume | — |

```
  harness / dashboard
           │  MCP / HTTP / EventSource
           ▼
   ┌──────────────┐  INSERT queued        ┌──────────────┐
   │ MCP gateway  │ ───────────────────►  │ Postgres     │
   │ auth, 202    │                       │ sessions     │
   └──────────────┘                       │ commands     │
          │                               │ event log    │
          │ EventSource                   │ runner_cmd   │
          ▼                               └──┬───────▲───┘
   ┌──────────────┐                          │       │ LISTEN + SELECT
   │ Hub          │──────────────────────────┘       │
   │ SSE sockets  │                                  │
   └──────────────┘                                  │
                                                     │ SKIP LOCKED Apply
                                        ┌────────────┴─┐
                                        │ Core         │
                                        │ dispatch,    │
                                        │ mint tokens  │
                                        └──────┬───────┘
                                               │  Part 3: runner SSE + POST
                                               ▼
                                          landbridged  (dials out)
```

Core restart: gateway and hub stay. New MCP accepts land as `queued` (Part 2). SSE stays up (Part 1). `landbridged` keeps its EventSource (Part 3) and POSTs events; new `dispatch` waits on Core mint+claim.

Worker tools are MCP, so they hit the **gateway**. Reads are SELECTs (gateway or hub). `/runner` is not MCP.

---

# Part 1 — SSE hub

## Problem

1. The §12 board polls every 2s, including to see machine liveness that never hits Postgres.
2. Lead inbox SSE already does the right thing (wake + snapshot) but **in Core**, so those sockets die with Core.
3. A dashboard that is only `EventSource` + `GET` should survive Core death: pings keep flowing, committed session state is still readable, reconnect is not required.

## Goals

- HTTP SSE of committed projections. Missed wakes are fine — the next snapshot is complete.
- Hub process owns the sockets. Core restart does not drop them.
- Hub reads **without calling Core** (read-only Postgres).
- Dashboard never `LISTEN`s, never imports `Landbridge.ControlPlane`.

- Session wakes and machine wakes are **separate doorbells**.

## Non-goals

- Redis, NATS, Kafka, WAL decoding, Debezium, triggers on all tables.

- Streaming `credentials`, tokens, permission argv, or anything §13.
- A custom multiplex subscribe protocol (v1 uses one EventSource per topic; HTTP/2 carries them).
- A write queue (Part 2) or replacing `/runner` (Part 3).
- Rewriting Blazor in the same change that extracts the process.

## Processes

Hub is the only long-lived SSE listener for humans/Leads. Postgres is the store that already survives Core death — that is what a read-only connection **accomplishes**: snapshot without Core.

A second connection to the same Instance database is enough. `LISTEN` is hub-internal.

## Views — one stream per row

**v1: one EventSource per row, plus a membership list per collection.** HTTP/2 (or HTTP/3) end-to-end; Caddy must not downgrade the hub to HTTP/1.1.

The client opens a stream for every row it cares about and aborts it when that row leaves the screen. A 200-session board is 200 session streams plus one membership stream. Cap later if that is loud; do not cap in this spec.

**Membership lists are mandatory.** A per-row stream cannot discover a row that did not exist when the page loaded (`create_session`, machine enroll, new forward). The list stream only says `{ "id", "op": "added"|"removed" }`. The client then opens or aborts `GET …/{id}/events`.

| Stream | Job |
|---|---|
| `GET /sessions/events` | ids appeared / disappeared (scope: instance or `teamId`) |
| `GET /sessions/{id}/events` | that session’s snapshot |
| same pattern for every topic below | |

Hub routes (wake-only, `event: change` `{ queueId, topic, entityId }`). Refetch is HTTP GET.

| Route | Topic | Entity |
|---|---|---|
| `GET /sessions/events` | `sessions` | session id |
| `GET /sessions/{id}/events` | `session` | session id |
| `GET /sessions/{id}/events/log` | `events` | session id |
| `GET /sessions/{id}/events/exchange` | `exchange` | session id |
| `GET /services/events` | `services` | session id |
| `GET /sessions/{id}/services/events` | `services` | session id |
| `GET /forwards/events` | `forwards` | `forward_id` |
| `GET /forwards/{id}/events` | `forwards` | `forward_id` |
| `GET /previews/events` | `previews` | mapping id |
| `GET /previews/{id}/events` | `previews` | mapping id |
| `GET /machines/events` | `machines` | machine id (heartbeat + enroll) |
| `GET /machines/{id}/events` | `machines` | machine id |
| `GET /machines/{id}/processes/events` | `processes` | machine id (same heartbeat) |


Outbox writers: `CommitAsync` (session/events/exchange), `RegisterServiceAsync` + `ClearServicesAndForwards` (services), `RelayGrantService.MintAsync` + clear (forwards), `PreviewMappingService` create/slide/revoke, `TokenService.ExchangeEnrollmentAsync` (machines, `landbridge_hub_events`), runner heartbeats (`HubOutbox.WriteHeartbeatAsync` → machines + processes).



Runner `/runner/events` (Part 3) is **not** this model: no coalesce, replay unacked.

### Topic catalog

Plane nouns. **Per-row doorbell only if the row mutates.** Append-only logs are a tail (one stream per session tail), not one stream per log line.

| Topic | Membership list | Per-row stream | Wakes on | Notes |
|---|---|---|---|---|
| **machines** | instance rail ids | one box | `machine(id)` | Heartbeat upsert. Does not wake **sessions**. |
| **sessions** | fleet / team ids | occupancy, message, pending | `session(id)` | Board rows. |
| **inbox** | outstanding ids per Team | — (the row is the session) | `session(id)` in Team | Derived. Exists today as a snapshot list; keep it. |
| **events** | — | **tail per session**, not per event | `session(id)` | `EventLogDetail` rows are **immutable**. |
| **exchange** | — | **tail per session** (Lead↔worker) | `session(id)` | Message machine, **not** harness NDJSON. Live wait *is* the session entity. |
| **registered services** (receiving ports) | Team / session ids | one name | register / update / clear | Producer loopback (`register_service`). |
| **forwards** (forwarded ports) | Team / session / consumer | one `forward_id` | issue / expiry / teardown | `open_forward` / `open_lead_forward`. |
| **previews** (preview ports) | Team / session | one label | mint / slide TTL / revoke | §8.4. Sliding TTL is a mutation. |
| **processes** | per machine | one process name | start / stop / exit | Agent-started; machine-scoped. |
| **commands** | `queued` ids | one `command_id` | insert / applied / rejected | Part 2. Omit until then. |

**Harness transcript** (`read-transcript`) is not an SSE. Flow-controlled byte pull (§10). “Capture exists” can ride the session entity.

### What the client opens

| UI | Streams |
|---|---|
| Fleet board | `sessions` membership + `sessions/{id}` for each visible row + `machines` membership + `machines/{id}` for each rail entry |
| Session drawer | keep the board streams; add that session’s `events` tail, `exchange` tail, port rows |
| Machine page | `machine/{id}` + `processes` membership + per-process rows |
| Inbox | `inbox?teamId=` (as today) and/or per-session entity streams for listed ids |
| Port admin | membership + per-row for visible forwards/previews |

### Event shape

- `event: change` — `{ queueId, topic, entityId }` only. **No snapshot.** The client `GET`s the JSON twin.
- `event: ping` — ~15s, empty. Clients ignore unknown types.
- Catch-up: `?after=<queueId>` / `Last-Event-ID`. Replay is “refetch these ids,” not bodies. After TTL gap: `GET` membership, then open row streams.
- Subscribe (waiter) **before** the first `SELECT` so an insert during catch-up still wakes.
- Auth is the JSON twin’s gate when a client is wired. Workers and machines are 403 on human/Lead feeds.
- `X-Accel-Buffering: no`.


NOTIFY payload stays an id. Hub maps one id onto every matching stream (session write → membership, that session entity, inbox, that session’s tails and ports). Each stream coalesces separately.

### Not streams

Credentials/tokens (§13), teams list (HTTP), friction (HTTP until it hurts), per-event / per-past-message / per-chunk streams (immutable), a mega fleet snapshot that heartbeats rebuild, HTTP/1.1 to the hub.

## Doorbells

### Sessions — already exists

`CommitAsync` stays: write + `hub_queue` + `pg_notify(landbridge_session_events, sessionId)` in one transaction. Hub `LISTEN`s on that channel as a doorbell and tails the outbox.


### Machines — missing

Heartbeats live in `RunnerConnectionRegistry`. A hub that only `LISTEN`s will not see connect/disconnect, and after a Core restart it will **keep showing ghost machines** until something publishes liveness.

Required before calling the hub split done:

- Persist a **machine liveness row** that Core (or the gateway ingest, Part 3) upserts on register / heartbeat / unregister, and `NOTIFY` a machine channel (payload: machine id), **or**
- Hub treats “Core HTTP health is down” as empty membership and emits that, then rebuilds when heartbeats restamp.

Rehydrate already restores *tasks*. It does not restore “which sockets are up.” The hub must not invent sockets from session rows.

### Events / exchange / ports

Not extra `LISTEN` channels in v1. Session (or machine) NOTIFY is the doorbell; the hub `SELECT`s the matching row/tail. Dedicated channels later if fan-out is too coarse.

## Resume and TTL

SSE is a **notification log** tailed from the Core outbox (`hub_queue`). Bodies live on HTTP GET.

`CommitAsync` inserts the outbox row in the **same transaction** as the session write and `pg_notify`. NOTIFY is a doorbell. Hub restart: `SELECT id > after` (or `0`) — LISTEN cannot lose those rows.

| Kind | Resume | Retention |
|---|---|---|
| **`hub_queue` wakes** | `id > after`. Replay names ids to refetch. | `DELETE` older than `Hub:Retain` (24h). After a gap: `GET` the list, then follow. |
| **Domain rows** | `GET` the JSON twin when `event: change` names them. | The row’s own life (`expires_at`, heartbeat age). |

Postgres, not Redis. Command / runner_command still finish on `applied`/`acked`, not expiry.

A second hub replica tails the same outbox (`LISTEN` + `SELECT`), not a Redis copy.



## Dashboard

- Live: EventSource per membership list + per visible row (HTTP/2).
- At-rest / click: HTTP GET (and cookie POSTs) on **Core** or the gateway, as today.
- Core 502 during restart: retry GET; **do not** tear down SSE. Committed session state did not change. Machine rail follows the machine doorbell / Core-down rule.

## Phases

1. **Same process, stop polling.** Per-row + membership SSE on `SessionEventFanout`. Machine wake from the registry **in-process**. Dashboard opens EventSources instead of `DashboardRefresh` 2s.
2. **Machine liveness in Postgres** + machine `NOTIFY`.
3. **Hub process.** Move `LISTEN` + `SELECT` + SSE out of Core. HTTP/2 at the edge. A second replica is another hub on the same Postgres, not a broker.


---

# Part 2 — Write queue

## Problem it actually solves

Accept a command while `Apply` is down.

It does **not** keep SSE sockets alive (Part 1). It does **not** let Core stop handling writes: Core is still the only `Apply`. It splits **accept** from **apply**. The accept process is the **MCP gateway**.

```
  Lead / worker / dashboard
           │  MCP / HTTP
           ▼
   ┌──────────────┐   INSERT command     ┌──────────────┐
   │ MCP gateway  │ ──────────────────►  │ command row  │
   │ auth, id,    │   NOTIFY drain       │ (queued)     │
   │ 202          │                      └──────┬───────┘
   └──────────────┘                             │ SKIP LOCKED
          │ EventSource                         ▼
          ▼                              Core.Apply → session row
        Hub                                     + event log
                                                + pg_notify (Part 1)
```

If the gateway is still in the Core process, a Core restart still drops accepts. The queue only pays for itself when the **gateway is a different process**. MCP retry on 502 already covers a few-second Core bounce while they are fused.

Pending leaves the snapshot when Core, in **one transaction**, `Apply`s (or records `Rejected`), sets `status = applied|rejected`, and `pg_notify`s. Failed create (no session row) must still notify (team/command id) or the command spins as pending forever. “No longer pending” is **absence from `queued`**, discovered by the same LISTEN → snapshot loop.

### Worker vs Lead on the gateway

Both classes authenticate at the gateway (hash lookup in Postgres).

| Surface | Gateway does | Queue? |
|---|---|---|
| Lead mutations (`create_session`, `stop_session`, answers) | insert `queued`, return `202` + ids | yes |
| Worker mutations (`report_result`, `request_input`, `start_process`, …) | same table, or **wait on Apply** and return the transition | optional wait; must not require Core’s HTTP process |
| Lead/worker reads | SELECT or EventSource on the hub | no |
| `watch_lead_inbox` / `watch_inbox` | hold until hub/fanout wakes | no — that wait is the hub |
| `/runner` | not MCP | Part 3 |

## Goals

- Gateway is request-reply **accept**: auth, idempotency key, allocated ids, `202`.
- Apply is async: `SKIP LOCKED` drain of a **Postgres command table**, then existing `Apply` + `CommitAsync`.
- Pending commands are **visible** on the same projections the hub snapshots.
- Apply rejection is an inbox/dashboard fact for that command id.
- Commands are idempotent on `(actor, idempotency_key)` / client session id.

## Non-goals

- Redis as command log or SoT. The command table is Postgres in the Instance database.
- WAL / CDC of session tables.
- Speculative `Apply`.
- Putting runner **heartbeats** on this table (Part 3: upsert).
- Changing worker-observe asynchrony; it is already true.

## Protocol change

Today every mutating MCP tool waits for plane commit and returns `Transitioned` or `Rejected(rule)`.

After a write queue, mutating tools return **accepted**:

| Call | Accept returns | Plane commit | Failure |
|---|---|---|---|
| `create_session` | `sessionId` (client- or gateway-minted Guid) + `commandId` | row appears | `command_rejected` on that id |
| `send_input_request` / permission answer | `commandId` | envelope moves | question stays until Apply; second accept is idempotent or `wrong_state` at Apply |
| `stop_session` | `commandId` | occupancy desired `none`, hide, revoke | worker still running until Apply |

Hub snapshots **must** list `pending` (and the session entity stream includes that command) or the Lead races.

This is a new protocol. Ship it only with pending-in-snapshot.

### Client-minted session ids, without the queue

Worth taking **first**:

- `create_session` may carry a Guid. Store PK is already a Guid; public slugs stay allocated at Apply.
- Retry after 502 with the same id does not double-create.
- Return stays request-reply.

That is the retry story for Core bounce. It does not need a queue.

## Command table (sketch)

| Column | Role |
|---|---|
| `command_id` | Guid, gateway-minted, returned |
| `idempotency_key` | caller-supplied or hash of canonical payload; unique per actor |
| `actor` | credential class + id — **not** a machine |
| `session_id` | existing sessions; for create, the minted Guid **before** Apply |
| `team_id` | scope |
| `kind` | `create_session` / `stop_session` / `answer` / … |
| `payload` | opaque to the hub; Core deserializes to `SessionCommand` |
| `status` | `queued` \| `applied` \| `rejected` |
| `rule` | `Rejected` rule name when rejected |
| `accepted_at` / `applied_at` | |

Gateway: authenticate (credential hash lookup — cannot invent authority), insert `queued`, `NOTIFY` the drain channel, return ids.

Core drain: `SELECT … FOR UPDATE SKIP LOCKED`, `Apply`, `CommitAsync`, mark `applied` / `rejected`. At-least-once: store command id on the event log / session row.

Hub: `SELECT status = queued` into membership + per-command (or session) streams.

## Auth

Authority stays structural ([`spec.md`](spec.md) §5). The gateway refuses workers for Lead commands at the door. It **reads** credentials from Postgres; it does not `Apply`. Signing/minting Lead tokens and worker tokens at dispatch stays Core/dashboard.

Dashboard POSTs: login is not a session command. Permission/preview either stay request-reply or become command kinds — do not 302 as if a queued form had applied.

## What stays out of the Lead queue

- Token mint at dispatch.
- Hub `SELECT`s.
- Runner heartbeats (Part 3 upsert).
- `read-transcript` (correlated request/reply).

Dispatch already has a queue: `submitted` + `SKIP LOCKED`. Do not put `Dispatch` on the Lead command table. The bytes to the machine are a **runner_command** after that claim (Part 3).

## Phases

0. Client-minted / idempotent `create_session` (no queue).
1. Do not start the queue until Part 1 phase 1 exists — pending must have a snapshot to live in.
2. Command table + same-process drain. Prove inbox honesty.
3. Split the MCP gateway only if Core bounce during accept still hurts after (0).

---

# Part 3 — Runner channel as SSE + ingest

The `/runner` WebSocket is bidirectional only because it is a socket. NAT already forces `landbridged` to dial **out**. That splits:

| Direction | Today | Proposed |
|---|---|---|
| Core → runner (`dispatch`, `stop`, `kill`, `prompt`, `open-forward`, `read-transcript`) | WS frames | **per-machine command outbox** + **SSE** (`GET /runner/events`) |
| Runner → Core (`started`, `exited`, `alive`, heartbeats, …) | WS frames | HTTP POST to the **gateway** → Postgres → Core drain |

Same idea as Parts 1–2. **Not** the same tables, **not** the same SSE as the dashboard. **Not** coalesced.

## Why not the Lead queue / row SSE

- Heartbeats are every ~15s × N machines. Mixing them makes `SKIP LOCKED` a heartbeat mill.
- Runner events are a machine credential (`lbr_m_`). Lead commands are a Lead factory.
- `Dispatch` (session command) is Core claiming `submitted`. `dispatch` (runner command) is the mailbox **after** the claim.
- A missed `kill` is a runaway harness. Replay **unacked outbox rows**. Inbox’s “missed wakes are fine” is false here.
- Spec §10: a transcript backlog must never delay `kill`. Separate channels (kill is outbox/SSE; chunks are a correlated POST).

## Core → runner: outbox + SSE

After `Apply(Dispatch)` / stop / kill, Core `INSERT`s a **runner_command** row (`machine_id`, `session_id`, `kind`, payload, `acked`). Hub (or gateway) streams pending rows to that machine’s EventSource.

`landbridged`:

1. Dials out: `GET /runner/events` with the machine token.
2. On connect, snapshot of unacked commands — replay.
3. Executes, POSTs ack.
4. Do **not** coalesce. Two `dispatch`es are two tasks. Last-write-wins is only legal on the **up** path for heartbeats.

`read-transcript`: outbound row with `request_id`; inbound `transcript-chunk` POST with the same id; Core does not issue the next range until the chunk is in.

**Core restart:** unacked `dispatch`/`kill` still sit in the outbox. Hub keeps the SSE. Core must come back to **insert new** ones (claim + mint worker token). The outbox row carries the already-minted token so a dead Core does not strand a claimed task that was already inserted.

## Runner → Core: ingest, not one queue

Gateway accepts POSTs (machine bearer). Split by kind:

| Kind | Store | Drain |
|---|---|---|
| Heartbeat / profiles / back-pressure | **Last-value upsert** on `machine_liveness` | none — hub `SELECT`s; Core reads for dispatch eligibility |
| `started` / `session-started` / `exited` / `auth-failed` / `rebooted` | append `runner_event` | Core → occupancy / `LivenessLost` |
| `alive` / `tool-call` / `subagent-spawned` | last-value per session **or** still droppable | liveness clocks; bound the table |
| `forward-*` | append or side table | relay grants |
| `transcript-chunk` | correlated to `request_id` | dashboard read path |

Heartbeats as append-only command rows are the failure mode.

Core down: upserts and appends still land (gateway + PG). Occupancy Apply waits. Hub can show last heartbeat. On Core return, drain `runner_event` in order per session.

## Frozen wire

§10 is the only frozen interface: `landbridged` may sit a year. This is a **new transport** of the same closed enum, not new verbs. Dual-stack until runners upgrade: WS still works; SSE+POST is opt-in on enroll.

## Phases

0. Machine liveness **row** (Part 1 phase 2) — heartbeat upsert is that row, WS or not.
1. Inbound lifecycle events via gateway POST **while WS still carries commands**.
2. Outbound outbox + runner SSE; `landbridged` prefers it when advertised.
3. Drop WS only when no supported runner speaks it.

---

# Independence and order

| | SSE hub | Write queue | Runner channel |
|---|---|---|---|
| Survives Core restart | sockets + **committed reads** | **unapplied accepts** (gateway up) | unacked **outbound** commands + last heartbeat; new `dispatch` waits on Core mint+claim |
| New process | hub | MCP gateway | gateway POST + hub SSE (`/runner/events`) |
| Protocol change | dashboard EventSource (HTTP/2, one per row) | mutating MCP tools | **transport** of §10; verbs frozen |
| Store | Instance Postgres | Instance Postgres | Instance Postgres |



Ship Part 1 without Part 2. Ship client-minted session ids without Part 2. Do not ship Part 2 without pending-in-snapshot. Part 3 is its own transport migration; do not reuse Part 1 coalescing or Part 2’s table.

## Open questions

1. Membership list: embed the id array vs `added`/`removed` events only after the first snapshot.
2. Machine liveness table shape vs Core-down ⇒ empty rail. The table is the honest one if the hub is split.
3. Mutating snapshot reads (`report_unread` cleared by per-session inbox fetch) — keep those on Core/gateway, even if the hub serves the unread view.
4. MCP 202 vs optional wait-for-Apply timeout (sync default, 202 if `Prefer: respond-async`). Default sync preserves today's Lead loop.
5. Whether `fork_session` (when it exists) uses a client-minted child id the same way as create.
6. Dual-stack duration for `/runner` WS vs SSE+POST.
7. Whether `alive`/`tool-call` are last-value or still droppable once they are HTTP — bound the table either way.
8. When a 200-row board of EventSources is too many: cap, or collapse to list-only. Not a v1 decision.
