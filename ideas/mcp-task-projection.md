# MCP Tasks projection

**Author:** Landbridge
**Date:** 2026-08-20
**Status:** Draft
**Depends on:** [`occupancy-and-messages.md`](occupancy-and-messages.md)
**Does not replace:** occupancy, the message machine, `get_team_state`, or Lead write tools.

Landbridge sessions are durable occupancy objects; they have no terminal state. MCP Tasks (`io.modelcontextprotocol/tasks`) wrap one request with `working | input_required | completed | failed | cancelled`, and those terminals must not move. The honest projection is therefore the **message envelope**, not the session.

At most one outstanding envelope per session. Leaving `idle` mints `message_id` (the MCP `taskId`). Returning to `idle` closes that task onto `last_message_id`. The next exchange is a new id on the same session. Occupancy, `health=failed`, and `hidden` stay session facts; `get_team_state` still polls those.

`create_session` still returns the session id. It does not open an envelope, so it is not a task.

This is not a `tools/call` wrapper and not the C# SDK `IMcpTaskStore`.

## Mapping

| Envelope | MCP `status` |
|---|---|
| `awaiting_lead` / `awaiting_permission` / `awaiting_report` | `input_required` |
| `awaiting_pull` | `working` |
| closed by pull receipt, permission verdict, or accept/discard | `completed` |
| closed by `cancel_session` or a failed-session retry that drops the wait | `cancelled` |

Idle with no `last_message_id` means there is no task to get. Mechanical `health=failed` does not close the envelope.

## Methods

- `tasks/get` / `tasks/list` — Lead only, Team-scoped. Live `message_id` plus the last closed envelope per session.
- `tasks/cancel` is refused: closing an envelope is answering, reviewing, or `cancel_session`.
- `tasks/update` and `tasks/result` are not implemented. Answers stay `answer_input_request` / `answer_permission_request` / `submit_review`.
- Polling is the subscription. `notifications/tasks/status` is not wired.

`ttl` is null. `pollInterval` is 5000.
