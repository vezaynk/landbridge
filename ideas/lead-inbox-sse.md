# Lead inbox SSE

**Author:** Landbridge
**Date:** 2026-08-20
**Status:** Implemented
**Depends on:** occupancy and the message machine ([`spec.md`](spec.md) §6).

The Lead inbox is a snapshot of outstanding items. HTTP SSE and `watch_lead_inbox` wake on session NOTIFY. Postgres LISTEN/NOTIFY is the bus. The human dashboard's 5s poll is a separate surface.

## Surface

| Route | Auth | Body |
|---|---|---|
| `GET /lead/inbox?teamId=` | Lead bearer | JSON snapshot |
| `GET /lead/inbox/events?teamId=` | Lead bearer | SSE of the same snapshot |
| `get_lead_inbox` | Lead MCP | JSON snapshot (`teamId` required) |
| `watch_lead_inbox` | Lead MCP | waits until at least one item, then the snapshot (`teamId` required) |

`teamId` is required: a Team this factory owns. `?sessionId=` (repeatable) / `sessionId` or `sessionIds` limits the snapshot. Team-wide is identifiers only. A session filter carries bodies and marks unread report mail as read. Workers, machines, and humans are 403. Unauthenticated is 401. Another Team's sessions never appear.

Each snapshot lists **every outstanding fact**, not one row per session. Team-wide: `sessionId`, `kind`, `messageId`, `namespace`. Per-session: result reference, report, question, permission options, infrastructure account. A question or permission wait stays until answered. `get_team_state` remains the full occupancy view. Worker pull is `get_inbox` / `watch_inbox`.

## Kind

A session is in the inbox when `hidden = false` and something is still outstanding. A failed row still lists a leftover envelope as a second item.

| Kind | Source |
|---|---|
| `failed` | `health = failed` |
| `permission` | `message_state = awaiting_permission` |
| `report` | `report_unread` (not an envelope wait; worker stays idle) |
| `question` / `spawnRequest` / `authHelp` / `endpointWait` / `unreachable` | `message_state = awaiting_lead` (typed by `input_kind`) |
| `pull` | `message_state = awaiting_pull` (worker-owed) |

Hidden rows are omitted. Deactivated occupancy (`desired = on_disk`) is not a wait and is not in this feed.

Triage order: failed, permission, report, Lead-owed asks, pull; then oldest envelope.

## SSE

- `event: snapshot` with the full JSON view. Not a delta. Missed events are fine — the next snapshot is complete.
- `event: ping` keepalive (~15s). Clients ignore unknown event types; this one carries no payload.
- No `Last-Event-ID` resume.
- Subscribe to NOTIFY, then snapshot, so a write during the first read coalesces into a follow-up snapshot.
- NOTIFYs coalesce per connection (single-slot drop-write).

MCP Tasks `notifications/tasks/status` is not wired. Envelope status is `tasks/get` / `tasks/list`. The inbox feed is `watch_lead_inbox` and `/lead/inbox/events`.

## Bus

`SessionEventListener` is one-consumer; dispatch owns that instance. `SessionEventFanout` is a second LISTEN connection that broadcasts wakes to inbox subscribers, so snapshots cannot stall dispatch.
