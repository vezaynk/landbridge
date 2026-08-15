---
name: docket-enroll
description: How to enroll a machine into a Docket Machine Group — probing the local ACP agent, writing the docketd runner config, guiding the human through registering the daemon as a system service, and smoke-testing the machine before real work reaches it. Use this skill whenever the user is setting up a new machine for Docket, mentions adding a box to the Machine Group, needs to write or repair a docketd config, or is debugging why a machine never receives dispatched work — even if they don't name Docket explicitly.
---

# Enrolling a machine

You are setting up this machine to accept dispatched Docket work. When you are done, `docketd` runs as a service, holds an outbound connection to the control plane, and the machine shows up in the Machine Group. The connection is always dialled *out* from here — nothing needs to reach in.

A human is at this terminal. Some steps need them; do not attempt those yourself.

There is no `/docket-enroll` command on this build. This skill *is* the flow.

## What you are producing

A `docketd` config that tells a generic daemon how to drive *this* machine's **ACP agent**. The daemon is the ACP client. The profile `spawn` is the agent argv. stdin/stdout are JSON-RPC.

The first profile — and the one dummy tasks land on — is named `default`. Extra profiles are fine later. Do not invent a machine-specific name for the first one.

| Section | What it answers |
|---|---|
| Harness invocation | The ACP agent argv (`spawn`) |
| Stop | Wind-down budget before `session/cancel` is followed by a tree-kill |
| Transcript capture | Whether to record stderr locally (`logs.capture`) |
| Back-pressure | When this machine stops accepting dispatch |

The schema and worked ACP recipes (Grok, OpenCode, Claude adapter, Codex adapter) are in `references/runner-config.md`.

**`docketd` reads this file once, at start.** There is no reload. Adding, renaming, editing, or deleting a profile is invisible until you restart the daemon. A restart kills every agent on this machine and their tasks requeue. Say so to the human before they edit a live box.

## Wiring the docket MCP server

You do **not** write `mcp.json`, a `files[]` bearer, or a `CODEX_HOME` hook for Docket. `docketd` injects an HTTP MCP server named `docket` on `session/new` / `session/load`, with the per-instance Bearer. The worker's first move is `get_task`.

`files[]` is still for harness-local config that is *not* the plane server (a project TOML, a trust flag). Prefer additive project files over replacing the operator's home.

## Check the prerequisites first

**1. Is it an ACP agent?** `docketd` speaks Agent Client Protocol over stdio. Native: `grok agent stdio`, `opencode acp`. Adapters: `@zed-industries/claude-agent-acp`, `@zed-industries/codex-acp`. A CLI that only has `-p` / `exec` / `run` is not enough.

**2. Can it take MCP servers on `session/new`?** The plane is injected there. If the agent cannot attach an HTTP MCP server with a Bearer header, it cannot participate.

**3. Do not put bypass / always-approve / yolo in `spawn`.** Permissions are `session/request_permission`. The client answers. A bypass flag skips the dialog Docket is now the one answering.

If any bar fails, stop. Report to the human rather than working around it.

## Probe before you write

Do not assume the harness or its version. Find out:

1. Which ACP agent is installed, and its version.
2. The exact argv that speaks ACP on stdio (`grok agent stdio`, `opencode acp`, the adapter binary).
3. That `session/new` accepts `mcpServers` of type `http` with headers.
4. That `session/load` exists if this machine should resume parked tasks (most adapters do).
5. How it names MCP tools (`get_task` vs `mcp__docket__get_task` vs `docket__get_task`). The worker skill tells the agent to call the docket tools; a prompt that uses the wrong spelling sends it hunting.

You do not need a stdout event mapping. Progress is `session/update` `tool_call`. You do not need `stdin: closed`. The RPC pipe *is* stdin.

## Concurrency is not something you set

`docketd` watches load, memory, and disk and stops claiming when the machine is under pressure. Thresholds default to `0.90` / `0.90` / `0.95`. `max_cpu_load` is inert where CPU cannot be observed. `max_concurrent` is only for a licence or posture cap.

## Progress

ACP `tool_call` updates are the progress clock. The periodic `alive` still satisfies the short aliveness window. There is no `events.source` to declare. `telemetry.otel` is still visibility to *your* collector, not a Docket meter.

## Registering the daemon — hand this to the human

`docketd` must run as a system service so it survives logout and restarts. On most systems that means systemd or launchd, and **it needs sudo**. Prepare the unit or plist, explain it, and have the human install it. Confirm it came up before continuing.

## Smoke-test the machine before real work reaches it

**`default` is shared.** Drain or pause other ready boxes if you need the dummy set to land here. **Restart `docketd`** after writing the config.

Run `docketd` in the foreground for this, or tail its journal. On start it prints `docketd up: machine=… profiles=[…]`. A config that does not parse never gets that far.

Then mint the dummy-task set:

```
POST /dashboard/conformance
Origin: <the plane's own origin>
```

| kind | What the worker must do |
|---|---|
| `identity` | Report hostname, cwd, and the first 8 hex of `$DOCKET_TASK_ID` |
| `write` | Write `smoke.txt` in the workspace containing only the hostname |
| `shell` | `echo` a nonce (`dkt-smoke-` plus the run id prefix) and report that line |

Poll `GET /dashboard/conformance/{runId}?format=json`. `workerDone` is true when every task is `verifying` or `completed`.

**Then test park and kill.** Have the human `park_task` or `cancel_task` (disposition `preserve`) a live task and confirm the process is gone. Park is a command, not a timer. Wait TTL is off by default.

The quiet failures:

- **Nothing dispatches: `Submitted`, `Attempt` 0.** Profile-name mismatch, machine not `ready`, or never connected.
- **ACP handshake fails.** `docketd` prints `ACP handshake failed` and the task stays `Working` until liveness requeues it. Wrong `spawn`, agent not on `PATH`, agent does not speak ACP.
- **The worker cannot authenticate.** A 401 inside the agent's own output; `report_result` never lands. Set `logs.capture: true` and read stderr.
- **`Attempt` climbing.** Handshake or first-turn crash. Cap (5) then `canceled`.

## After enrollment

Re-run this flow to change the config, then restart. `docketd` keeps no task ledger: a restart kills every agent and their tasks requeue. The state dir holds credentials and, if capture is on, transcripts.

Hardware specs are not declared. Permission level is stored and not yet consulted. Treat it as a label.
