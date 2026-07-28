---
name: docket-enroll
description: How to enroll a machine into a Docket Machine Group — probing the local harness, writing the docketd runner config, guiding the human through registering the daemon as a system service, and passing the conformance run. Use this skill whenever the user runs /docket-enroll, is setting up a new machine for Docket, mentions adding a box to the Machine Group, needs to write or repair a docketd config, or is debugging why a machine shows as unclaimable — even if they don't name Docket explicitly.
---

# Enrolling a machine

You are setting up this machine to accept dispatched Docket work. When you are done, `docketd` runs as a service, the control plane can reach it, and the machine joins the Machine Group.

A human is at this terminal. Some steps need them; do not attempt those yourself.

## What you are producing

A `docketd` config that tells a generic daemon how to drive *this* machine's harness. The daemon knows nothing about harnesses, toolchains, or task content — everything specific lives in the config you write.

The config must cover:

| Section | What it answers |
|---|---|
| Harness invocation | How to start the harness headlessly with a task, and where it reads its MCP config |
| Process control | How `stop` is delivered — an injected message turn where the harness supports one, a signal otherwise — what kills it hard, how long wind-down normally takes, how to confirm exit |
| Event sources | How lifecycle events are observed — hooks, structured output, OTel endpoint |
| Event mapping | How this harness's event names map to `started` / `tool-call` / `subagent-spawned` / `exited` |
| Log tail | Path and format of the session transcript |
| Back-pressure | Thresholds for when this machine stops accepting dispatch — defaults are sensible (see Concurrency below) |

The full config schema and a worked Claude Code profile — including the exact
headless `spawn` argv, the `{mcp_config}` injection, and the generated MCP config
a worker dials the plane with — are in `references/runner-config.md`.

## Check the prerequisites first

Two bars, neither negotiable, neither degrading gracefully. Confirm both by running the harness once, headless, before writing any config.

**1. Is it an MCP client?** A worker's only channel to Docket is `docket-mcp` — claiming, reporting, blockers, service registration all happen there. A harness without MCP cannot participate at all, no matter how good it is. Most current agents qualify; Aider is the notable exception.

**2. Does it run to completion without asking permission?** A headless agent that waits for approval hangs until the liveness timeout, which is the most expensive way to find a misconfiguration — it looks like a hung task, not a bad config. The bypass flag differs per harness and is the most important line in `command`. Find it, and check what else it disables while you are there; several bypass sandboxing along with approvals.

Three sharp edges to check at the same time: managed settings on corporate machines can forbid the bypass mode outright — confirm it is actually permitted before writing it into `command`; "don't ask" postures typically *deny* tools that require user interaction rather than prompting, which silently removes capabilities instead of hanging; and where the harness supports a permission-prompt tool, that is the middle path — approvals become `request_input` escalations to the Lead instead of hangs or a blanket bypass.

If either bar fails, stop. Report to the human rather than working around it.

## Probe before you write

Do not assume the harness or its version. Find out:

1. Which harness is installed, and its version.
2. The exact headless invocation, and how it takes a prompt — positional argument, flag value, or stdin.
3. What structured output it produces: a stream of events, a single object at exit, or plain text.
4. What lifecycle hooks it offers, and their names *in this version*.
5. Whether it emits OTel, and with what attributes. Per-subagent attribution is valuable if available.
6. Where it writes session transcripts, and whether that path is static or only knowable once running.
7. Whether it can resume a prior session, and how — including whether resume is scoped to the directory that created the session, since parked-task resume depends on returning to it.
8. Whether a running session accepts injected input — a stdin message stream or equivalent — because that is how a graceful `stop` reaches the agent as a turn rather than a signal.

Version matters more than you'd expect. Hook names and event payloads change between releases, and a config written against last quarter's names will silently produce a machine that looks alive and reports nothing.

## Concurrency is not something you set

There is no slot count. A declared limit is a guess that is wrong in both directions, and agents vary far too much in weight for a number to mean anything.

`docketd` watches its own load, memory, and disk and stops claiming when the machine is under pressure. The thresholds in `machine.backpressure` have sensible defaults; adjust them only if you know something specific about this box — a laptop someone actively works on may want to yield sooner than a dedicated server.

If a profile needs a hard cap for reasons unrelated to load — a licence limit, a rate-limited provider, a restricted posture you want singular — that is `max_concurrent` on the profile, not a machine setting.

## Degraded telemetry is acceptable

If the harness has no subagent lifecycle events, or no OTel, say so in the config rather than inventing a mapping. The control plane renders missing signals as "not reported," which is honest and useful. A fabricated mapping produces a machine that appears healthy and isn't.

Two specific degradations worth declaring accurately:

- **`events.source: terminal`** — the harness emits one structured result at exit rather than a stream. `started` and `exited` work; there is no progress signal, so a hung agent cannot be told from a busy one until it times out. Common for harnesses whose JSON output is one object per invocation.
- **`telemetry.otel: false`** — token spend is invisible for this profile. The Team ceiling cannot meter what it cannot see; only the harness-local hard cap `docketd` passes at dispatch still binds. Surface this to the human at enrollment rather than letting them find it in a bill.

## Registering the daemon — hand this to the human

`docketd` must run as a system service so it survives logout and restarts. On most systems that means systemd or launchd, and **it needs sudo**.

This is a system-level change, which you report rather than perform. Prepare the unit or plist, explain exactly what it does and where it goes, and have the human run the install and enable commands themselves. Confirm it came up before continuing.

## The conformance run

The control plane dispatches trivial work and judges the results. You do not decide whether this passed — you report what the control plane says and help fix what failed.

It checks:

- `started` and tool-call events arrive, attributed to the right task id
- Heartbeat cadence matches what the config claims
- Two concurrent tasks are independently trackable
- `stop` with a short TTL is acknowledged — and where the profile declares message delivery, the stop demonstrably reaches the agent as a turn
- `TTL=0` kills one process and leaves its sibling running
- A relay forward round-trips and the local listener closes on release
- A task that would normally prompt for approval completes without hanging
- A parked task resumes on this machine from its recorded directory with context intact

**Failures here are configuration bugs, and they are worth fixing carefully.** A machine that passes dispatch but fails kill looks fine right up until someone needs to stop a runaway agent — the worst possible time to discover it. Same for task attribution: a machine that reports events without task ids will misattribute every event once it runs two agents.

If a check fails, the machine stays registered but unclaimable. Fix the config and re-run — `/docket-enroll` is idempotent.

## After enrollment

The config is stamped with the version of this skill. When the skill changes, the control plane can flag stale machines for a re-run. Nothing about this setup is meant to be hand-maintained; re-running the flow is the supported way to change it.

Note that `docketd` holds no state. If it restarts, every agent on this machine is killed and their tasks requeue — that is deliberate, not a fault. On start it also kills any stray harness processes it finds, which is what makes the guarantee survive an unclean shutdown.

## A note on what this machine is

You declared a purpose, OS, specs, and permission level at connect. Those are recorded server-side and are not re-declarable from the config — a machine cannot promote its own privileges by editing a file. If the machine's role changes, that is a human decision made through the control plane.
