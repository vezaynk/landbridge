# MCP Tasks projection

**Author:** Landbridge
**Date:** 2026-08-20
**Status:** Draft
**Depends on:** [`occupancy-and-messages.md`](occupancy-and-messages.md)
**Does not replace:** occupancy, the message machine, `get_team_state`, or Lead write tools.

Landbridge sessions are durable occupancy objects plus a message machine. MCP Tasks (`io.modelcontextprotocol/tasks`) wrap a request with `working | input_required | completed | failed | cancelled`. This file is the projection of the former onto the latter so a Lead client can poll `tasks/get` instead of only `get_team_state`.

The session row stays the source of truth. Task id = session id. `create_session` still returns that id as a string. This is not a `tools/call` wrapper and not the C# SDK `IMcpTaskStore`.

## Mapping

| Session | MCP `status` |
|---|---|
| `hidden` + accept or discard | `completed` |
| `hidden` otherwise (cancel) | `cancelled` |
| `awaiting_lead` / `awaiting_permission` / `awaiting_report` | `input_required` |
| everything else, including `health=failed` and `desired=on_disk` | `working` |

`health=failed` is **not** MCP `failed`. Same-id retry is legal; MCP terminal statuses must not move. The status message says to retry with `answer_input_request`.

## Methods

- `tasks/get` / `tasks/list` / `tasks/cancel` — Lead only, Team-scoped.
- `tasks/list` includes hidden rows (MCP: if gettable, listable). Cursor is the last session id.
- `tasks/cancel` is `Cancel(preserve)`. Already-terminal → `-32602`.
- `tasks/update` and `tasks/result` are not implemented. Answers stay `answer_input_request` / `answer_permission_request` / `submit_review`. Reports stay `get_session_report`.
- Polling is the subscription. `notifications/tasks/status` is not wired; `SessionEventListener` already NOTIFYs for dispatch/dashboard.

`ttl` is null (sessions are durable). `pollInterval` is 5000.
