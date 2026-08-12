---
name: docket-enroll
description: How to enroll a machine into a Docket Machine Group — probing the local harness, writing the docketd runner config, guiding the human through registering the daemon as a system service, and smoke-testing the machine before real work reaches it. Use this skill whenever the user is setting up a new machine for Docket, mentions adding a box to the Machine Group, needs to write or repair a docketd config, or is debugging why a machine never receives dispatched work — even if they don't name Docket explicitly.
---

# Enrolling a machine

You are setting up this machine to accept dispatched Docket work. When you are done, `docketd` runs as a service, holds an outbound connection to the control plane, and the machine shows up in the Machine Group. The connection is always dialled *out* from here — nothing needs to reach in.

A human is at this terminal. Some steps need them; do not attempt those yourself.

There is no `/docket-enroll` command on this build. No slash command or MCP prompt is registered, even though a few control-plane messages still tell people to run one. This skill *is* the flow: read it, probe, write the config, and walk the human through the parts that are theirs.

## What you are producing

A `docketd` config that tells a generic daemon how to drive *this* machine's harness. The daemon knows nothing about harnesses, toolchains, or task content — everything specific lives in the config you write.

The config must cover:

| Section | What it answers |
|---|---|
| Harness invocation | How to start the harness headlessly with a task (`spawn`), and where it reads its MCP config |
| Process control | How `stop` is delivered (`mode: message` only where the harness demonstrably reads mid-task stdin turns — **not `claude -p`**, which takes `signal`), the message template, and how long wind-down gets before the hard tree-kill |
| Resume | The argv that reattaches to a parked task's transcript (`resume.args`), which is how a parked task comes back with its context |
| Event relay | Whether the harness streams structured output docketd can read (`events.source`), and the property names it is keyed by (`events.mapping`) |
| Transcript capture | Whether to record this worker's output locally (`logs.capture`) and the caps on it |
| Back-pressure | Thresholds for when this machine stops accepting dispatch — defaults are sensible (see Concurrency below) |

The full config schema and a worked Claude Code profile — including the exact
headless `spawn` argv, the `{mcp_config}` injection, and the generated MCP config
a worker dials the plane with — are in `references/runner-config.md`.

## Check the prerequisites first

Two bars, neither negotiable, neither degrading gracefully. Confirm both by running the harness once, headless, before writing any config.

**1. Is it an MCP client?** A worker's only channel to Docket is `docket-mcp` — claiming, reporting, blockers, service registration all happen there. A harness without MCP cannot participate at all, no matter how good it is. Most current agents qualify; Aider is the notable exception.

**2. Does it run to completion without asking permission?** A headless agent that waits for approval hangs until the liveness timeout, which is the most expensive way to find a misconfiguration — it looks like a hung task, not a bad config. The bypass flag differs per harness and is the most important line in `spawn`. Find it, and check what else it disables while you are there; several bypass sandboxing along with approvals.

Three sharp edges to check at the same time: managed settings on corporate machines can forbid the bypass mode outright — confirm it is actually permitted before writing it into `spawn`; "don't ask" postures typically *deny* tools that require user interaction rather than prompting, which silently removes capabilities instead of hanging; and where the harness supports a permission-prompt tool, that is the middle path — approvals become `request_input` escalations to the Lead instead of hangs or a blanket bypass. Docket implements that path (`--permission-prompt-tool mcp__docket__request_permission` in place of the bypass flag); the runner-config reference has the worked profile and the caveats, the main one being that `--allowedTools` still has to carry the routine baseline or the machine will ask about everything.

If either bar fails, stop. Report to the human rather than working around it.

## Probe before you write

Do not assume the harness or its version. Find out:

1. Which harness is installed, and its version.
2. The exact headless invocation, and how it takes a prompt — positional argument, flag value, or stdin.
3. What structured output it writes to **stdout**, and whether that is one line per event or a single object at exit. This is the only event source `docketd` actually reads, so the answer decides whether this machine can hold a task longer than a minute — read "The event source is not a telemetry preference" below before you settle it.
4. The property names inside that stream: the top-level discriminator, the value marking an assistant turn, how a tool call appears within a turn, and where the session id is. Those are what `events.mapping` overrides; its defaults are Claude Code's `stream-json` shape.
5. Whether the session id appears early in the stream rather than at exit, since that is the ref a parked task resumes from.
6. Whether it can resume a prior session, and how — including whether resume is scoped to the directory that created the session, since parked-task resume depends on returning to it.
7. Whether a **running** session accepts injected input — a stdin message stream or equivalent, read continuously rather than once at startup — because that is how a graceful `stop` reaches the agent as a turn rather than as a kill deadline. Answer this from an observed run, not from the flag list: `docketd` cannot verify the answer, so declaring `stop.mode: message` on a harness that ignores mid-task stdin makes `preserve` a promise this machine will break. For `claude -p` the answer is already known and it is **no** — declare `signal` (see the runner-config reference).

Version matters more than you'd expect. Output shapes and key names change between releases, and `events.mapping` silently falls back to its default for any key it does not recognise — so a config written against last quarter's names produces a machine that looks alive and reports nothing, with no complaint at load time.

You do not need to find where the harness writes its own log files. Transcript capture tees the stdout stream `docketd` is already reading into a fixed layout under the state dir; it never goes looking for a harness-owned path.

## Concurrency is not something you set

There is no slot count. A declared limit is a guess that is wrong in both directions, and agents vary far too much in weight for a number to mean anything.

`docketd` watches its own load, memory, and disk and stops claiming when the machine is under pressure. The thresholds in `machine.back_pressure` default to `0.90` / `0.90` / `0.95`; adjust them only if you know something specific about this box — a laptop someone actively works on may want to yield sooner than a dedicated server.

One caveat to pass on: where `docketd` cannot observe CPU on the platform, `max_cpu_load` is inert and memory and disk carry the whole signal alone. It says so on its startup line, so read that rather than assuming all three are live.

If a profile needs a hard cap for reasons unrelated to load — a licence limit, a rate-limited provider, a restricted posture you want singular — that is `max_concurrent` on the profile, not a machine setting.

## The event source is not a telemetry preference

**Get this one wrong and the machine cannot report progress.** `events.source` looks like a choice about how much detail you get. On this build it decides whether anyone can tell a working agent from a wedged one, so it is worth more care than anything else in the config except the headless posture.

Per-task liveness on the control plane is refreshed **only** by an inbound event. A profile that produces no `tool-call` events emits three: `started` at spawn, `exited` at the end, and the periodic `alive` `docketd` sends for every live process on its own heartbeat timer. `alive` is not gated on `events.source`, so the short aliveness window (60 seconds by default, a control-plane setting rather than anything you declare here) stays satisfied and such a task is **not** requeued merely for being quiet.

What it loses is the progress clock. With no `tool-call` ever arriving, the no-progress ceiling (30 minutes by default) is the only clock governing the task: work that legitimately runs that long without a tool call is requeued, and in the meantime a hung agent is indistinguishable from a busy one — which is the real cost, because it is the cost you cannot see. Such requeues are capped (5 by default): at the cap the task is abandoned as `canceled`, with the reason that reclaimed it recorded and its workspace preserved, rather than being redispatched forever.

If the harness has no readable event stream, say so in the config rather than inventing a mapping — but say it to the human too, because it is a limit on what the machine can be asked to do, not a cosmetic gap. A fabricated mapping produces a machine that appears healthy and isn't.

Three of the four declared `events.source` values are the same answer today:

- **`events.source: terminal`** is the only source `docketd` reads. It drains the harness's stdout line by line, which is what produces `tool-call` events and the liveness refresh that rides them. If the harness can stream NDJSON on stdout, this is the value you want, and you almost certainly want it. For any harness other than Claude Code, treat `events.mapping` as **mandatory rather than optional**: the built-in defaults are claude's `stream-json` property names, and against a different stream they match nothing at all, so `terminal` with no mapping reads a whole task's output and extracts neither the resume ref nor a single `tool-call`. What `mapping` can express is renamed properties plus one alternative shape — a flat event object per tool call, via `tool_event_type` and `tool_name_path` — and nothing beyond that, so a stream that hides its tool calls anywhere else (a nested array of a different arity, a name that must be computed) is a code change, not a config one; check the shapes against a real run before promising the human this machine reports progress.
- **`hooks` and `otel` parse but are not wired.** Declaring either gets you exactly what `none` gets: `started` at spawn, `exited` at the end, the periodic `alive` in between, and no progress signal at all — the cost described above. Do not write `hooks` because the harness happens to offer hooks; it buys nothing on this build and reads as a progress signal the machine does not have. `docketd` prints a warning at startup for every profile declaring a non-`terminal` source — if you see it, treat it as a defect to fix, not a notice to acknowledge.
- **The process-alive fallback does exist — it is the `alive` event, and it is the whole safety net.** Spec §10 says liveness degrades to process-alive when there is no event source, and that is what `docketd`'s heartbeat timer does: one `alive` per live process, ungated by `events.source`. So a source-less profile is not requeued merely for being quiet. What it does *not* buy is progress: nothing else refreshes per-task activity — not the machine heartbeat, not the worker's own MCP calls — so the no-progress ceiling above is the only clock left.
- **`subagent-spawned` has no producer.** It exists in the wire vocabulary, and the dashboard has a slot for it that always reads "no subagents reported." Nothing you put in the config will fill it, so do not spend probe time on subagent attribution.
- **`telemetry.otel` works, and it is visibility rather than metering.** Turning it on makes docketd set the vendor-neutral `OTEL_*` variables on the spawn and append `docket.task_id`/`docket.machine_id`, so the harness's own token and cost numbers go to **your** collector, attributed per task (`docs/TELEMETRY.md` has the worked block and what Claude Code emits). Two things to say plainly at enrollment. Nothing in Docket ingests any of it — the plane never sees a token count, so what the Team ceiling enforces is *authorized* spend committed at dispatch, never measured spend. And a harness that exports nothing is normal rather than broken: the variables are set on a process that may ignore all of them, which is the Codex case today. The `{budget}` token carries the per-dispatch cap when the Team has one configured, and substitutes empty when it does not.

If the harness genuinely cannot stream anything readable on stdout, the honest declaration is `none` — and then say plainly what that costs. Such a machine is *not* limited to work finishing inside the aliveness window; `alive` covers that. It is limited to work that never goes longer than the no-progress ceiling without a tool call, and to a fleet where nobody can distinguish its hung agents from its busy ones. That is a real constraint on what the human just built, and it belongs in the conversation rather than in a config file nobody rereads.

## Registering the daemon — hand this to the human

`docketd` must run as a system service so it survives logout and restarts. On most systems that means systemd or launchd, and **it needs sudo**.

This is a system-level change, which you report rather than perform. Prepare the unit or plist, explain exactly what it does and where it goes, and have the human run the install and enable commands themselves. Confirm it came up before continuing.

## Smoke-test the machine before real work reaches it

**There is no automated gate.** Spec §11 describes a conformance run — the control plane dispatching trivial work and judging the results — and it is future work. Nothing in the plane probes a new machine, and there is no unclaimable state for it to hold a failure in: a machine that enrolls is simply a machine that connected. Whatever you do not check here, nobody checks.

So check it once, by hand, with the human. The failure you are hunting is the quiet one — a machine that heartbeats, reads as `ready`, accepts a dispatch, and produces nothing.

**Give the test somewhere it can only land.** A task carries a profile name matched by exact string equality, so add a temporary uniquely-named profile alongside `default` (`smoke-<hostname>`) and target that. Aim a task at `default` and it may be served by some other machine in the Group, proving nothing about this one.

**Run `docketd` in the foreground for this, or tail its journal.** Its stdout is the only place several of these failures appear at all. On start it prints one line — `docketd up: machine=… profiles=[…] strays_reaped=… control=…`. A config that does not parse never gets that far; `docketd` prints the error and exits non-zero before it connects.

Then have the human's Lead create one trivial task against that profile (`create_task`, with `profile` set to the smoke name) — "report this machine's hostname and working directory" is enough — and follow it:

| Watch | Where | Healthy |
|---|---|---|
| The machine is present at all | `/dashboard/machines` | a section for this machine id, `heartbeat Ns ago` inside your `heartbeat_seconds` |
| It declares the profile | same page, profile badges | the smoke profile is listed; `no profiles declared` means no heartbeat has landed yet |
| It will accept work | same page, badge | `ready` — `not ready` or `back-pressure` means nothing will dispatch |
| The task moves | `/dashboard/teams/{team}`, or the Lead's `get_team_state` | `Submitted` → `Working` → `Verifying`, with `Attempt` reaching 1 and staying there |
| The work actually happened | the task's report | the value it was asked for, not a restatement of the ask |

Both views take `?format=json` with a Lead bearer token if you would rather read them structured. There is no MCP tool that lists machines and none that reads events — for those two the dashboard is the only surface.

The failures worth naming, and what each really looks like:

- **Nothing dispatches: the task sits in `Submitted` with `Attempt` at 0.** No reason is surfaced anywhere — this is the quietest failure in the system. It is a profile-name mismatch (exact string equality; check the spelling against the badges the machine actually published), or the machine is not `ready`, or it never connected. A machine that is not connected does not show as offline; it is absent from `/dashboard/machines` entirely.
- **Wrong `spawn` argv, or the harness binary is not on `docketd`'s `PATH`.** `docketd` prints `command handler threw: …` on its own stdout and nothing else happens — no event, no row, no change on any page. The task stays `Working` until the per-task liveness window (60s) expires and requeues it, and the requeue record says nothing about the spawn. An unwritable `work_root` surfaces identically, since `docketd` writes `{work_dir}/mcp.json` itself. **If a task requeues with no explanation, read `docketd`'s stdout before anything else.**
- **The harness starts and exits immediately** — a rejected flag, a permission mode managed settings forbid, a missing credential. The exit code rides the `exited` event but is stored and displayed nowhere, so a fast crash is indistinguishable from a hang: same liveness timeout, same requeue. Set `logs.capture: true` on the smoke profile; the transcript is the only place the reason exists.
- **The worker cannot authenticate to the plane.** Do not wait for an `auth-failed` event. The plane can record one, but `docketd` never emits one, so none will arrive. A rejected worker token appears as a 401 inside the harness's own output and `report_result` simply never lands — the transcript again.
- **`Attempt` climbing on its own.** The task is being dispatched, failing, and redispatched. Infrastructure requeues are capped (5 by default), so this does not run forever: at the cap the task is abandoned as `canceled` — not `rejected`, since nothing is wrong with the work — with the reason that reclaimed it on the record and the workspace left intact. Read that reason, not the attempt count.

**Then test the kill path, and do not skip it because dispatch worked.** Have the human cancel the task mid-flight (`cancel_task`, disposition `preserve`) and confirm the process is actually gone — that is the assertion that matters, and it holds on every profile. A machine that dispatches but cannot be stopped looks fine right up until someone needs to stop a runaway agent — the worst possible moment to find out.

**What to expect depends on the stop mode, and one of the two expectations is a trap.** On a `signal` profile — which is what `claude -p` gets — the worker is killed at the TTL with no closing `report_result`, and that is correct behaviour, not a failure. Only on a `stop.mode: message` profile should you expect the injected turn to produce a final report before a voluntary exit. `docketd` cannot tell the difference: it reports that it *wrote* the turn, never that the harness read it (the line on its stdout says `written, not confirmed read`). So this run is the only check that a `message` declaration is true, and if you declared `message` and the worker still had to be killed at the deadline, the declaration is wrong — change it to `signal` rather than leaving a profile that promises `preserve` and delivers a kill.

Two things you cannot verify by hand, so do not claim them either way: whether the runner refused a dispatch (it computes `BackPressure` / `UnknownProfile` / `MaxConcurrent` refusals and then discards them — never sent upstream, never logged), and whether events were dropped under load (the outbound ring counts the gap, but the wire has no field to carry it).

**Failures here are configuration bugs, and they are worth fixing carefully rather than working around.** Fix the config, restart `docketd`, and run the check again — remembering that a restart kills every agent on this machine. Then delete the temporary smoke profile, so it does not sit in the config as a target a Lead might one day name.

> **Future work (spec §11).** The conformance run automates the above and goes past it: per declared profile, the control plane would judge event attribution by task id, heartbeat cadence against the config, two concurrent tasks tracked independently, `stop` acknowledgement (and message delivery demonstrably reaching the agent as a turn), `TTL=0` killing one process while its sibling survives, a relay forward round-tripping and its listener closing on release, an approval-prone task completing without hanging, and a parked task resuming from its recorded directory with context intact — admitting the machine as `ready` on a pass, or leaving it registered-but-unclaimable with the failing step named. None of that exists yet. The manual pass above is its stand-in, not a preview of it.

## After enrollment

Nothing about this setup is meant to be hand-maintained. Re-running this flow — reprobing, rewriting the config, restarting the daemon, smoke-testing again — is the supported way to change it, and it is safe to repeat.

Spec §11 also wants the config stamped with the version of this skill, so the control plane can flag stale machines for a re-run. **That does not exist**: nothing writes a version, nothing serves one, and a `skill_version` key added by hand is silently dropped when the config parses. Until it lands, a machine's config is only as current as whoever last re-ran this — so when you notice a config written against older guidance, say so to the human rather than assuming the plane will catch it.

Note that `docketd` keeps no state a restart would try to reconcile: no task ledger, no process re-adoption. If it restarts, every agent on this machine is killed and their tasks requeue — that is deliberate, not a fault. On start it also kills any stray harness processes it finds, which is what makes the guarantee survive an unclean shutdown. The state dir is the exception, and a narrow one: it holds the machine credentials and, where capture is on, the transcripts, which must outlive both a task teardown and a restart.

## A note on what this machine is

A name, a purpose, the OS string, and a permission level were declared once, when `docketd` exchanged the enrollment token for credentials — not from the config, and not on the runner connection, which declares nothing beyond the token. Those four are recorded server-side and are not re-declarable from a config file, so a machine cannot promote its own privileges by editing one. If the machine's role changes, that is a human decision made through the control plane.

Two honest limits on that record. Hardware specs are **not** part of it despite what §11 says — nothing collects CPU or memory as declared capacity, only live load on the heartbeat. And the permission level is stored but not yet read by anything: no dispatch, forwarding, or tool decision consults it today. Treat it as a label the human is recording for later, not a control that is holding.
