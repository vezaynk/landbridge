# SSE hub, write queue, and runner transport

**Author:** Landbridge
**Date:** 2026-09-04
**Status:** Proposed (Part 1 started)
**Depends on:** occupancy and the message machine ([`spec.md`](spec.md) §6), Lead inbox SSE ([`lead-inbox-sse.md`](lead-inbox-sse.md)), datastore ([`spec.md`](spec.md) §3), dashboard ([`spec.md`](spec.md) §12), runner vocabulary ([`spec.md`](spec.md) §10).

Three independent workstreams that share a vocabulary. They are not a package.

| Part | Job | Unblocks |
|---|---|---|
| 1. SSE hub | committed reads + EventSource without Core | dashboard poll, Core restart of **reads** |
| 2. Write queue | accept without `Apply` | Core restart of **Lead/worker accepts** |
| 3. Runner transport | §10 over SSE + POST | Core restart of **machine command/event** path |

Postgres is the source of truth **and** the wake log. Last-value facts live in last-value rows. `hub_queue` is a doorbell, not a snapshot and not liveness. `SessionStateMachine.Apply` stays the only writer of session facts ([`spec.md`](spec.md) §3). `pg_notify` is a **private doorbell** for whoever holds `LISTEN`. Consumers never `LISTEN`. Redis, WAL, and table CDC are non-goals.

## Two clocks

Every mutating call sits on two clocks. Collapsing them is how this design goes wrong.

| Clock | What moved | Who observes it | Today |
|---|---|---|---|
| **Plane commit** | `Apply` returned `Transitioned`; the row, event log, `hub_queue`, and `pg_notify` committed together (`SessionStore.CommitAsync`) | inbox, dashboard, `get_team_state` | the MCP/HTTP return |
| **Worker observe** | occupancy observed, `get_inbox` pull, harness cancel, process tree | `landbridged`, the worker | already async |

`stop_session` / answers already have **no** guarantee the harness has reacted. They **do** guarantee plane commit. A write queue moves **plane commit** off the MCP return. Worker observe does not change. SSE decoupling does not change either clock.

## As-built (2026-09-04)

| Piece | Where | Notes |
|---|---|---|
| Session writes | `SessionStore.CommitAsync` | `SaveChanges` + `hub_queue` (`session`, `sessions`, `events`, `exchange`) + `pg_notify(landbridge_session_events, sessionId)` in one transaction |
| Services / forwards / previews | `RegisterServiceAsync`, `RelayGrantService`, `PreviewMappingService`, `ClearServicesAndForwards` | same pattern: mutate the domain row, `HubOutbox.Stage`, `NOTIFY` session channel |
| Machine enroll | `TokenService.ExchangeEnrollmentAsync` | `hub_queue` `machines` + `NOTIFY landbridge_hub_events` (payload is a machine id; dispatch must not LISTEN here) |
| Heartbeat | `HubOutbox.WriteHeartbeatAsync` after a successful `ApplyHeartbeat` | **upserts** `machines.last_spoke_at` / `ready` / `under_back_pressure` / `profiles` and replaces `machine_processes` by name. Then doorbells `machines`, `processes` (machine id), `process` (row id). No heartbeat blob in `hub_queue`. Non-guid test ids (`m1`) are a no-op |
| Liveness | `machines.last_spoke_at` within 90s (`Landbridge:MachineLivenessTtl`) | dashboard and wait-TTL sweeper. Not `hub_queue`. Not a separate liveness table |
| Processes | `machine_processes` | last-value, machine-scoped. `list_processes` reads the table when the machine id is a Guid |
| Socket | `RunnerConnectionRegistry` | send delegate, tracked dispatches, generation. Facts are the columns. Non-guid test ids keep a registry overlay |

| Dispatch / inbox wake | `SessionEventListener` / `SessionEventFanout` | LISTEN `landbridge_session_events` only |
| Lead live read | `GET /lead/inbox/events`, `watch_lead_inbox` | full **snapshot** on wake, still in Core ([`lead-inbox-sse.md`](lead-inbox-sse.md)) |
| Dashboard live read | Blazor `DashboardRefresh` | **still 2s poll**. Hub has no consumer yet |
| Hub | `Landbridge.Hub` `:5300` | LISTEN session + hub channels, wake in-process, tail `hub_queue` `id > after`, `event: change`. Unauthenticated. Retention `DELETE` older than `Hub:Retain` (24h) |
| Runner channel | `/runner` WebSocket | frozen §10; unchanged |

`Landbridge.Mcp` is still one process: MCP, OAuth, `/runner`, dashboard, `Apply`. The split below is that process cut into three.

## Target processes

| Process | Owns | Must not |
|---|---|---|
| **MCP gateway** | MCP HTTP, OAuth AS, `/enroll`, Lead/worker tool *accept* and *read* façade | `Apply`, dispatch, holding `/runner` sockets |
| **Core** | `Apply`, command drain, `SKIP LOCKED` dispatch, token **mint** at dispatch, heartbeat **upsert** of machine columns | long-lived EventSource, MCP accept |
| **Hub** | `LISTEN`, tail `hub_queue`, SSE sockets | domain writes, MCP, runner apply of occupancy. Retention `DELETE` on `hub_queue` is the exception |
| **`landbridged`** | spawn, §10 consume | — |

```
  harness / dashboard
           │  MCP / HTTP / EventSource
           ▼
   ┌──────────────┐  INSERT queued        ┌──────────────┐
   │ MCP gateway  │ ───────────────────►  │ Postgres     │
   │ auth, 202    │                       │ sessions     │
   └──────────────┘                       │ machines     │
          │                               │ processes    │
          │ EventSource                   │ hub_queue    │
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
                                        │ heartbeat    │
                                        │ upsert       │
                                        └──────┬───────┘
                                               │  Part 3: runner SSE + POST
                                               ▼
                                          landbridged  (dials out)
```

Core restart: gateway and hub stay. SSE stays up (Part 1). Committed session and machine **columns** are still readable. New MCP accepts need Part 2. New `dispatch` waits on Core mint+claim. `landbridged` keeps its socket until Part 3.

---

# Part 1 — SSE hub

## Problem

1. The §12 board polls every 2s.
2. Lead inbox SSE already does wake + snapshot but **in Core**, so those sockets die with Core.
3. A dashboard that is only `EventSource` + `GET` should survive Core death: pings keep flowing, committed rows are still readable.

## Goals

- HTTP SSE of **wakes**, not bodies. Missed wakes are fine — the next `GET` is complete.
- Hub process owns the sockets. Core restart does not drop them.
- Hub reads **without calling Core** (read-only Postgres, plus `hub_queue` tail).
- Dashboard never `LISTEN`s.
- Session wakes and machine wakes are **separate doorbells** (session channel vs `landbridge_hub_events`).
- Last-value facts (liveness, processes) live in last-value **rows**. The append log only names what to refetch.

## Non-goals

- Redis, NATS, Kafka, WAL decoding, Debezium, triggers on all tables.
- Storing the heartbeat document in `hub_queue`.
- A dedicated `machine_liveness` table — `machines.last_spoke_at` is that fact.
- Streaming `credentials`, tokens, permission argv, or anything §13.
- A custom multiplex subscribe protocol (v1 is one EventSource per topic; HTTP/2 carries them).
- A write queue (Part 2) or replacing `/runner` (Part 3).
- Rewriting Blazor in the same change that extracts the process.
- Collapsing dispatch off an in-memory `Fold` — done: `MachineLive.ReadyAsync` reads columns (overlay only for `"m1"` tests).


## Two stores

| Store | Shape | Job |
|---|---|---|
| Domain rows | last-value or append-only log of facts | `GET` the JSON twin |
| `hub_queue` | append-only `(id, topic, entity_id, created_at)` | `SELECT id > after`; SSE `event: change` |

`pg_notify` unblocks the waiter. It is not the log. A hub restart replays `hub_queue`; a LISTEN gap cannot drop a committed wake until the retain window deletes it.

## Views — one stream per row

**v1: one EventSource per row, plus a membership list per collection.** HTTP/2 (or HTTP/3) end-to-end; Caddy must not downgrade the hub to HTTP/1.1.

The client opens a stream for every row it cares about and aborts it when that row leaves the screen. Membership lists are mandatory: a per-row stream cannot discover a row that did not exist when the page loaded.

### Event shape

- `event: change` — `{ queueId, topic, entityId }` only. **No snapshot, no `op`.** The client `GET`s the JSON twin (or membership list). Absence after GET is `removed`.
- `event: ping` — ~15s, empty. Clients ignore unknown types.
- Catch-up: `?after=<queueId>` / `Last-Event-ID`. Replay names ids to refetch, not bodies. After TTL gap: `GET` membership, then open row streams.
- Subscribe (waiter) **before** the first `SELECT` so an insert during catch-up still wakes.
- Auth is the JSON twin’s gate when a client is wired. Today the hub is unauthenticated because nothing consumes it.
- `X-Accel-Buffering: no`.

NOTIFY payload stays an id. Hub wakes **every** subscriber (coalesce per stream); each stream’s catch-up filters by topic / entity.

### Routes (as-built)

| Route | Topic | Entity |
|---|---|---|
| `GET /sessions/events` | `sessions` | session id |
| `GET /sessions/{id}/events` | `session` | session id |
| `GET /sessions/{id}/events/log` | `events` | session id (tail, not per log line) |
| `GET /sessions/{id}/events/exchange` | `exchange` | session id (tail) |
| `GET /services/events` | `services` | session id |
| `GET /sessions/{id}/services/events` | `services` | session id |
| `GET /forwards/events` | `forwards` | `forward_id` |
| `GET /forwards/{id}/events` | `forwards` | `forward_id` |
| `GET /previews/events` | `previews` | mapping id |
| `GET /previews/{id}/events` | `previews` | mapping id |
| `GET /machines/events` | `machines` | machine id |
| `GET /machines/{id}/events` | `machines` | machine id |
| `GET /machines/{id}/processes/events` | `processes` | machine id (set) |
| `GET /processes/events` | `process` | process row id |
| `GET /processes/{id}/events` | `process` | process row id |

Inbox stays Core (`GET /lead/inbox/events`). Commands (Part 2) and `/runner/events` (Part 3) are not these routes.

### Topic catalog

Plane nouns. **Per-row doorbell only if the row mutates.** Append-only logs are a tail (one stream per session tail), not one stream per log line.

| Topic | Membership | Per-row | Writer | GET |
|---|---|---|---|---|
| **machines** | instance ids | one box | enroll; heartbeat upsert of columns | `/dashboard/machines` |
| **sessions** | fleet / team ids | occupancy, message, pending | `CommitAsync` | team / session JSON |
| **inbox** | outstanding ids per Team | — (the row is the session) | session NOTIFY (Core snapshot today) | `/lead/inbox` |
| **events** | — | **tail per session** | `CommitAsync` | `/dashboard/events?session=` |
| **exchange** | — | **tail per session** | `CommitAsync` | team session Q/A/report |
| **services** | session / team | session (name is not a Guid) | register / clear | team JSON `services[]` |
| **forwards** | instance | `forward_id` | mint / teardown | none yet (wake only) |
| **previews** | instance | mapping id | mint / slide / revoke | none yet as JSON twin |
| **processes** | per machine | process row Guid | heartbeat replace-by-name | machine JSON `processes` |
| **commands** | `queued` ids | `command_id` | Part 2 | omit until then |

**Harness transcript** (`read-transcript`) is not an SSE. Flow-controlled byte pull (§10).

### What the client opens

| UI | Streams |
|---|---|
| Fleet board | `sessions` membership + `sessions/{id}` for each visible row + `machines` membership + `machines/{id}` for each rail entry |
| Session drawer | board streams + that session’s `events` tail, `exchange` tail, port rows |
| Machine page | `machine/{id}` + `processes` membership + per-process rows |
| Inbox | `inbox?teamId=` (as today) and/or per-session entity streams for listed ids |
| Port admin | membership + per-row for visible forwards/previews |

### Not streams

Credentials/tokens (§13), teams list (HTTP), friction (HTTP until it hurts), per-event / per-past-message / per-chunk streams (immutable), a mega fleet snapshot that heartbeats rebuild, HTTP/1.1 to the hub.

## Doorbells

### Sessions

`CommitAsync`: domain write + `hub_queue` + `pg_notify(landbridge_session_events, sessionId)` in one transaction. Dispatch and inbox LISTEN that channel. Hub LISTENs it as a doorbell and tails the outbox.

### Machines and processes

Heartbeat is last-value, not a log.

On each applied beat (Guid machine, enrolled, unrevoked):

1. `UPDATE machines SET last_spoke_at, ready, under_back_pressure, profiles`.
2. If the beat carries `processes` (null means older runner — leave the table): upsert `machine_processes` by name, delete names that vanished, keep row ids stable.
3. `hub_queue`: `machines` (machine id), `processes` (machine id), `process` (each touched row id). Payload `{}`.
4. `pg_notify(landbridge_hub_events, machineId)`.

Live for the board and the wait-TTL sweeper: `last_spoke_at >= now() - 90s`. After that the box is absent even if the columns remain. `hub_queue` retain (24h) is unrelated — it is catch-up for SSE, not liveness.

The registry keeps the `/runner` socket and tracked dispatches. Ready / profiles / last-spoke are the columns. Dispatch (`MachineLive.ReadyAsync`) and `list_profiles` read those. Non-guid test ids (`m1`) overlay on the registry because they cannot be a `machines` PK.


### Events / exchange / ports

Not extra LISTEN channels in v1. Session (or hub) NOTIFY is the doorbell; catch-up filters `hub_queue` by topic.

## Resume and TTL

| Kind | Resume | Retention |
|---|---|---|
| **`hub_queue` wakes** | `id > after` | `DELETE` older than `Hub:Retain` (24h). After a gap: `GET` the list, then follow |
| **Domain rows** | `GET` the JSON twin when `event: change` names them | the row’s own life (`expires_at`, `last_spoke_at` window, revoke) |

Postgres, not Redis. Command / runner_command still finish on `applied`/`acked`, not expiry.

A second hub replica tails the same outbox (`LISTEN` + `SELECT`), not a Redis copy.

## Dashboard

- Live (target): EventSource per membership list + per visible row (HTTP/2) on the hub.
- Live (today): 2s poll. Hub is up; nothing dials it.
- At-rest / click: HTTP GET (and cookie POSTs) on Core, as today.
- Core 502 during restart: retry GET; **do not** tear down SSE. Committed session state did not change. Machine rail follows `last_spoke_at`.

## Phases

1. **Wake log + hub process** — done: `hub_queue`, `Landbridge.Hub`, session/port/machine topics, nothing consuming SSE.
2. **Last-value machine facts** — done: columns + `machine_processes`; doorbell only on `hub_queue`.
3. **Dashboard EventSource** instead of `DashboardRefresh`. Auth on the hub.
4. Split Lead inbox SSE onto the hub (still a snapshot stream, not this wake shape).


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
                                                + hub_queue
                                                + pg_notify (Part 1)
```

If the gateway is still in the Core process, a Core restart still drops accepts. The queue only pays for itself when the **gateway is a different process**. MCP retry on 502 already covers a few-second Core bounce while they are fused.

Pending leaves the snapshot when Core, in **one transaction**, `Apply`s (or records `Rejected`), sets `status = applied|rejected`, and `pg_notify`s. Failed create (no session row) must still notify (team/command id) or the command spins as pending forever. “No longer pending” is **absence from `queued`**, discovered by the same LISTEN → catch-up loop.

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
- Pending commands are **visible** on the same projections the hub wakes (client GETs).
- Apply rejection is an inbox/dashboard fact for that command id.
- Commands are idempotent on `(actor, idempotency_key)` / client session id.

## Non-goals

- Redis as command log or SoT. The command table is Postgres in the Instance database.
- WAL / CDC of session tables.
- Speculative `Apply`.
- Putting runner **heartbeats** on this table (they already upsert `machines` / `machine_processes`).
- Changing worker-observe asynchrony; it is already true.

## Protocol change

Today every mutating MCP tool waits for plane commit and returns `Transitioned` or `Rejected(rule)`.

After a write queue, mutating tools return **accepted**:

| Call | Accept returns | Plane commit | Failure |
|---|---|---|---|
| `create_session` | `sessionId` (client- or gateway-minted Guid) + `commandId` | row appears | `command_rejected` on that id |
| `send_input_request` / permission answer | `commandId` | envelope moves | question stays until Apply; second accept is idempotent or `wrong_state` at Apply |
| `stop_session` | `commandId` | occupancy desired `none`, hide, revoke | worker still running until Apply |

Hub wakes **must** name pending commands (and the session entity stream includes that command) or the Lead races. Ship it only with pending visible on GET.

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

Hub: `hub_queue` wakes for `queued` membership + per-command (or session) streams; bodies stay GET.

## Auth

Authority stays structural ([`spec.md`](spec.md) §5). The gateway refuses workers for Lead commands at the door. It **reads** credentials from Postgres; it does not `Apply`. Signing/minting Lead tokens and worker tokens at dispatch stays Core/dashboard.

Dashboard POSTs: login is not a session command. Permission/preview either stay request-reply or become command kinds — do not 302 as if a queued form had applied.

## What stays out of the Lead queue

- Token mint at dispatch.
- Hub `SELECT`s / SSE.
- Runner heartbeats (already `machines` upsert).
- `read-transcript` (correlated request/reply).

Dispatch already has a queue: `submitted` + `SKIP LOCKED`. Do not put `Dispatch` on the Lead command table. The bytes to the machine are a **runner_command** after that claim (Part 3).

## Phases

0. Client-minted / idempotent `create_session` (no queue).
1. Do not start the queue until Part 1 wakes exist — pending must have a GET to live in.
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
| Heartbeat / profiles / back-pressure / processes | **Last-value upsert** on `machines` + `machine_processes` (already as-built on the WS path) | none — GET the columns; Core reads for dispatch eligibility |
| `started` / `session-started` / `exited` / `auth-failed` / `rebooted` | append `runner_event` | Core → occupancy / `LivenessLost` |
| `alive` / `tool-call` / `subagent-spawned` | last-value per session **or** still droppable | liveness clocks; bound the table |
| `forward-*` | append or side table | relay grants |
| `transcript-chunk` | correlated to `request_id` | dashboard read path |

Heartbeats as append-only command rows (or `hub_queue` payloads) are the failure mode. The wake log only names the machine / process ids.

Core down (once ingest is the gateway): upserts and appends still land. Occupancy Apply waits. Hub can show `last_spoke_at`. On Core return, drain `runner_event` in order per session. Today the beat still hits Core over `/runner` WS.

## Frozen wire

§10 is the only frozen interface: `landbridged` may sit a year. This is a **new transport** of the same closed enum, not new verbs. Dual-stack until runners upgrade: WS still works; SSE+POST is opt-in on enroll.

## Phases

0. Machine last-value columns + process table (Part 1) — **done**, on the WS path.
1. Inbound lifecycle events via gateway POST **while WS still carries commands**.
2. Outbound outbox + runner SSE; `landbridged` prefers it when advertised.
3. Drop WS only when no supported runner speaks it.

---

# Independence and order

| | SSE hub | Write queue | Runner channel |
|---|---|---|---|
| Survives Core restart | sockets + **committed reads** (including `last_spoke_at` / processes) | **unapplied accepts** (gateway up) | unacked **outbound** commands + last heartbeat; new `dispatch` waits on Core mint+claim |
| New process | hub | MCP gateway | gateway POST + hub SSE (`/runner/events`) |
| Protocol change | dashboard EventSource (HTTP/2, one per row) | mutating MCP tools | **transport** of §10; verbs frozen |
| Store | Instance Postgres | Instance Postgres | Instance Postgres |

Ship Part 1 without Part 2. Ship client-minted session ids without Part 2. Do not ship Part 2 without pending-on-GET. Part 3 is its own transport migration; do not reuse Part 1 coalescing or Part 2’s table.

## Open questions

1. Membership: `GET` the list on every `change` vs later adding `op`. v1 is GET.
2. Mutating snapshot reads (`report_unread` cleared by per-session inbox fetch) — keep those on Core/gateway, even if the hub serves the unread view.
3. MCP 202 vs optional wait-for-Apply timeout (sync default, 202 if `Prefer: respond-async`). Default sync preserves today's Lead loop.
4. Whether `fork_session` (when it exists) uses a client-minted child id the same way as create.
5. Dual-stack duration for `/runner` WS vs SSE+POST.
6. Whether `alive`/`tool-call` are last-value or still droppable once they are HTTP — bound the table either way.
7. When a 200-row board of EventSources is too many: cap, or collapse to list-only. Not a v1 decision.
8. JSON twins for live forwards/previews (wakes exist; GET does not).

---
