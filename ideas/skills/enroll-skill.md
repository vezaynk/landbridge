---
name: landbridge-enroll
description: How to enroll a machine into a Landbridge Machine Group — probing the local harness, writing the landbridged runner config, guiding the human through registering the daemon as a system service, and smoke-testing the machine before real work reaches it. Use this skill whenever the user is setting up a new machine for Landbridge, mentions adding a box to the Machine Group, needs to write or repair a landbridged config, or is debugging why a machine never receives dispatched work — even if they don't name Landbridge explicitly.
---

# Enrolling a machine

You are setting up this machine to accept dispatched Landbridge work. When you are done, `landbridged` runs as a service, holds an outbound connection to the control plane, and the machine shows up in the Machine Group. The connection is always dialled *out* from here — nothing needs to reach in.

A human is at this terminal. Some steps need them; do not attempt those yourself.

There is no `/landbridge-enroll` command on this build. No slash command or MCP prompt is registered, even though a few control-plane messages still tell people to run one. This skill *is* the flow: read it, probe, write the config, and walk the human through the parts that are theirs.

## What you are producing

A `landbridged` config that tells a generic daemon how to drive *this* machine's harness. The daemon knows nothing about harnesses, toolchains, or session content — everything specific lives in the config you write.

Name the first profile as `<harness>-<hostname>-<os>` — exact, lowercase, hyphens. Resolve those three from this machine: the harness you just probed (`goose`, `claude`, `codex`, `grok`, `opencode`), `hostname -s` (or the equivalent), and the OS as `linux` / `macos` / `windows`. Examples: `goose-devbox-linux`, `claude-mbp-macos`, `codex-builder-linux`. That name is unique to this box and this harness, so a Lead can target it without hitting a sibling.

Then **offer** a group profile that shares the same `spawn` / `prompt` / `follow_up` under a broader name. Ask the human; do not add one silently:

- Linux → `any-linux`
- macOS → `any-macos`
- Windows → `any-windows`

A Lead that does not care which Linux box runs the work sets `profile: "any-linux"`. A Lead that needs *this* machine or *this* harness uses the specific name. Do not invent a reserved `default` — `create_session` requires an exact name from `list_profiles`. Every name this machine declares must be exercised before real work uses it — a badge on the Machine Group is not a passing check.

The config must cover:

| Section | What it answers |
|---|---|
| Harness invocation | The ACP entry point (`spawn`) and the opening turn (`prompt`). An ACP agent takes no prompt on argv. |
| Follow-up | The wake-up turn (`follow_up`) when there is new input. Names landbridge tools the way *this* harness spells them. |
| Config options | Optional `config_options` map. Each pair is `session/set_config_option` after `session/new`, skipped unless the agent advertised that `configId` and value. OpenCode needs `{ "model": "<slug>" }`; most others advertise nothing. |
| Process control | `stop.wind_down_seconds` — grace after `session/cancel` before the tree-kill. There is no `mode` or `message`. |
| Resume | Not a profile field. Redispatch uses `session/load` on the live connection. |
| Transcript capture | Whether to record this worker's output locally (`logs.capture`) and the caps on it |
| Back-pressure | Thresholds for when this machine stops accepting dispatch — defaults are sensible (see Concurrency below) |

The full config schema and worked ACP profiles are in `references/runner-config.md`.
The plane's MCP server is handed over on `session/new` — do not write a bearer file.

**`landbridged` reads this file once, at start.** There is no reload, no `SIGHUP`,
and nothing the plane can push. Adding, renaming, editing, or deleting a
profile is invisible until you restart the daemon. The next heartbeat is what
publishes the new names to the Machine Group — the only channel the plane uses
for routing. Until that beat lands, a session aimed at a new name sits with
`desired=running` and `observed=none`, `Attempt` at 0, the same quiet failure as a typo. A restart
kills every agent on this machine and their sessions go `health=failed` (waiting
on the Lead); that is the cost of a profile change, not a bug. Say so to the
human before they edit a live box.

## Wiring the landbridge MCP server

A worker must reach the plane as an MCP client. Under ACP that server is a
**`session/new` parameter** — HTTP, bearer header, no file. Do not write
`{mcp_config}` or a `files[]` bearer unless this harness ignores
`mcpServers` on the session (none of the measured agents do).

The older additive-file path is still valid if you need it:

1. **`files[]` under `{work_dir}`** when the harness merges a project-local
   config with the user home. Grok does: `{cwd}/.grok/config.toml` plus
   `~/.grok/config.toml`. Write only the landbridge block; leave `GROK_HOME` unset.
   Use `{mcp_url}` for the plane URL. For the bearer, prefer the harness's own
   read-from-env field if it has one (Codex's `bearer_token_env_var =
   "LANDBRIDGE_WORKER_TOKEN"`) — that keeps the token off disk. Grok has no such field
   and does **not** expand `${LANDBRIDGE_WORKER_TOKEN}` in `config.toml`, so it must use
   the landbridged substitution `{worker_token}` (written verbatim). When a live token
   lands in the file this way, write the file `"mode": "600"`, as Claude's
   `mcp.json` does. Grok 1.0.4+ gates project-local MCP behind folder trust;
   a landbridged work dir is a throwaway folder, so also set
   `"env": { "GROK_FOLDER_TRUST": "0" }` or the worker lists the server and
   never handshakes it.
2. **`hooks.before_spawn` argv** when the only MCP surface is a user-global
   file (Codex / `CODEX_HOME`). The program must be idempotent
   (ensure-if-absent). Never invoke a shell — argv only, same as `spawn`.
   Omit `after_exit`: removing the block races a sibling worker and leaves
   interactive use with a 401ing landbridge server either way.
3. **`env.GROK_HOME` / `env.CODEX_HOME`** only when the operator asked for a
   **sealed** home (strict archetype). That replaces the directory; the worker
   will not see `~/.grok` MCP servers, skills, or `auth.json`.

Probe the harness before choosing. If you cannot tell whether it merges
project config, treat that as a question for the human, not a guess.

## Check the prerequisites first

Two bars, neither negotiable, neither degrading gracefully. Confirm both by running the harness once, headless, before writing any config.

**1. Is it an MCP client?** A worker's only channel to Landbridge is `landbridge-mcp` — claiming, reporting, blockers, service registration all happen there. A harness without MCP cannot participate at all, no matter how good it is. Most current agents qualify; Aider is the notable exception.

**2. Is it an ACP agent?** `landbridged` speaks Agent Client Protocol over stdio. Native: `grok agent stdio`, `opencode acp`, `goose acp`. Adapters: `claude-agent-acp`, `codex-acp`. A CLI that only has `-p` / `exec` / `run` is not enough. `goose serve` is a remote HTTP/WebSocket server, not this client's transport.

**3. Do not put bypass / always-approve / yolo in `spawn`.** Permissions are `session/request_permission`. landbridged posts the worker bearer at `POST /worker/permission` and a Lead or human decides. A bypass flag on argv skips that dialog.

Then test **park**: `park_session` a live session and confirm the process is gone. Wait TTL is off by default — a forgotten question holds the lease until you answer or deactivate. Permission waits cannot be parked.

If either bar fails, stop. Report to the human rather than working around it.

## Probe before you write

Do not assume the harness or its version. Find out:

1. Which harness is installed, and its version.
2. The ACP entry point (`claude-agent-acp`, `codex-acp`, `opencode acp`, `grok agent stdio`, `goose acp`). A CLI that only has `-p` / `exec` / `run` is not enough.
3. How this harness spells landbridge's MCP tools (`mcp__landbridge__get_session` / `landbridge_get_session` / `landbridge__get_session`). That spelling is what `prompt` and `follow_up` must use. Goose is expected to use `landbridge__get_session`.
4. Whether `initialize` declares `loadSession` and `mcpCapabilities.http`. Run `tools/acp-probe` against a harness this repo has not measured. `loadSession` defaults to false in the spec; without it every redispatch is a cold start.
5. Whether the agent asks the client for `fs/*` or `terminal/*`. This client declares those UNSUPPORTED. An agent that routes all I/O through the client cannot work here.

Version matters more than you'd expect. Adapter packages move on their own schedule; pin them the way you pin the harness.

You do not need to find where the harness writes its own log files. Transcript capture tees the stdout stream `landbridged` is already reading into a fixed layout under the state dir; it never goes looking for a harness-owned path.

## Concurrency is not something you set

There is no slot count. A declared limit is a guess that is wrong in both directions, and agents vary far too much in weight for a number to mean anything.

`landbridged` watches its own load, memory, and disk and stops claiming when the machine is under pressure. The thresholds in `machine.back_pressure` default to `0.90` / `0.90` / `0.95`; adjust them only if you know something specific about this box — a laptop someone actively works on may want to yield sooner than a dedicated server.

One caveat to pass on: where `landbridged` cannot observe CPU on the platform, `max_cpu_load` is inert and memory and disk carry the whole signal alone. It says so on its startup line, so read that rather than assuming all three are live.

There is no `max_concurrent`. Seat count is load, not a declared slot.

## Progress comes from the protocol

There is no `events.source` or `events.mapping` to declare. Tool calls arrive as
ACP `session/update` notifications with fixed field names. A worker that never
emits one is indistinguishable from a hung one until the no-progress ceiling
fires. That is a harness or prompt problem, not a mapping problem.

`telemetry.otel` is still visibility rather than metering — see `docs/TELEMETRY.md`.
`subagent-spawned` has no producer. The short aliveness window is satisfied by
`landbridged`'s own `alive` heartbeat; the no-progress ceiling is what fails a
worker that never emits a tool call (the Lead then decides whether to resume).

## Registering the daemon — hand this to the human

`landbridged` must run as a system service so it survives logout and restarts. On most systems that means systemd or launchd, and **it needs sudo**.

This is a system-level change, which you report rather than perform. Prepare the unit or plist, explain exactly what it does and where it goes, and have the human run the install and enable commands themselves. Confirm it came up before continuing.

## Smoke-test every profile before real work reaches it

**There is no automated gate that judges results.** Spec §11's conformance run would dispatch trivial work *and* decide pass/fail; that judging half is future work. What exists today is a dispatch check you run by hand. A machine that enrolls is otherwise simply a machine that connected — no unclaimable state, no probe. **Whatever you do not dispatch here, nobody checks** — including a second profile that only exists as a badge.

The failure you are hunting is the quiet one — a machine that heartbeats, reads as `ready`, accepts a dispatch, and produces nothing. `spawn`, `PATH`, credentials, and tool spelling are per profile, so a green `any-linux` says nothing about `goose-devbox-linux`.

**Restart `landbridged`** (or start it, if it is not running yet) after writing the config — editing the file under a running daemon changes nothing, and the plane will not list a badge until the post-restart heartbeat. Confirm every name you wrote appears on `/dashboard/machines` for this machine id. **A profile that is shared is claimed by any ready machine that declares it.** If this is the only ready box, the session lands here. If the Group already has others declaring the same name, drain or pause them first, or the check proves the fleet rather than this enrollment.

**Run `landbridged` in the foreground for this, or tail its journal.** Its stdout is the only place several of these failures appear at all. On start it prints one line — `landbridged up: machine=… profiles=[…] strays_reaped=… control=…`. A config that does not parse never gets that far; `landbridged` prints the error and exits non-zero before it connects. Set `logs.capture: true` on every profile you are about to check; the transcript is the only place a fast crash explains itself.

**Walk the list in the config, one profile at a time.** For each name, mint the dummy set against that profile (next section). Then follow that run:

| Watch | Where | Healthy |
|---|---|---|
| The machine is present at all | `/dashboard/machines` | a section for this machine id, `heartbeat Ns ago` inside your `heartbeat_seconds` |
| It declares this profile | same page, profile badges | the name is listed; `no profiles declared` means no heartbeat has landed yet |
| It will accept work | same page, badge | `ready` — `not ready` or `back-pressure` means nothing will dispatch |
| The session moves | `/dashboard/teams/{team}`, or the Lead's `get_team_state` | occupancy `running`/`running`, then `awaiting_report` after `report_result`, with `Attempt` reaching 1 and staying there |
| The work actually happened | the session's report | the value it was asked for, not a restatement of the ask |

Both views take `?format=json` if you would rather read them structured — the Team view with a Lead bearer token, the Machine Group view with an operator session only (machine enumeration is human-only by design, so a Lead token gets a 403 there). There is no MCP tool that lists machines and none that reads events — for those two the dashboard is the only surface.

The failures worth naming, and what each really looks like:

- **Nothing dispatches: occupancy stays `desired=running` / `observed=none` with `Attempt` at 0.** No reason is surfaced anywhere — this is the quietest failure in the system. It is a profile-name mismatch (exact string equality; check the spelling against the badges the machine actually published), or the machine is not `ready`, or it never connected. A machine that is not connected does not show as offline; it is absent from `/dashboard/machines` entirely.
- **Wrong `spawn` argv, or the harness binary is not on `landbridged`'s `PATH`.** `landbridged` prints `command handler threw: …` on its own stdout and nothing else happens — no event, no row, no change on any page. The session sits occupying until the per-session liveness window (60s) expires and `health=failed` (`LivenessTimeout`), and that record says nothing about the spawn. An unwritable `work_root` surfaces identically, since `landbridged` creates the work dir (and writes `mcp.json` when the profile names `{mcp_config}`). **If a session fails with no explanation, read `landbridged`'s stdout before anything else.**
- **The harness starts and exits immediately** — a rejected flag, a permission mode managed settings forbid, a missing credential. The exit code rides the `exited` event but is stored and displayed nowhere, so a fast crash is indistinguishable from a hang: same liveness timeout, same `health=failed`. The transcript is the only place the reason exists.
- **The worker cannot authenticate to the plane.** Do not wait for an `auth-failed` event. The plane can record one, but `landbridged` never emits one, so none will arrive. A rejected worker token appears as a 401 inside the harness's own output and `report_result` simply never lands — the transcript again.
- **`health=failed` after one attempt.** The plane does not requeue. Handshake flakes, spawn failures, and dead processes land as `health=failed` with a plane-authored reason and wait for the Lead. If you see `Attempt` climbing, a Lead is retrying those failures on purpose — read the last reason, not the count.

**Then test the kill path on that same profile, and do not skip it because dispatch worked.** Have the human stop the session mid-flight (`stop_session`) and confirm the process is actually gone — that is the assertion that matters, and spawn differs per profile, so a stop that worked on `any-linux` is not evidence for `goose-devbox-linux`. A machine that dispatches but cannot be stopped looks fine right up until someone needs to stop a runaway agent — the worst possible moment to find out. When you are done, `park_session` or `stop_session` anything still `awaiting_report` — a report keeps the process until you stop it.

**What to expect from stop.** A stop is `session/cancel` plus the wind-down deadline. The runner reports that the cancel was *sent*, never that the agent obeyed it (cancel is a notification with no reply). Confirm the process is gone after the deadline. There is no `stop.mode` to choose.

Two things you cannot verify by hand, so do not claim them either way: whether the runner refused a dispatch (it computes `BackPressure` / `UnknownProfile` refusals and then discards them — never sent upstream, never logged), and whether events were dropped under load (the outbound ring counts the gap, but the wire has no field to carry it).

**Failures here are configuration bugs, and they are worth fixing carefully rather than working around.** Fix the config, restart `landbridged`, and run the check again — remembering that a restart kills every agent on this machine, and that the file is not re-read in place.

## Profile check — dummy sessions from the plane

The control plane will mint a fixed set of dummy sessions aimed at **one profile you name** and expose their occupancy and message state. This is the stand-in for the unbuilt §11 conformance run. It does **not** judge the answers — a session that is `awaiting_report` is a worker that called `report_result`. A report keeps the process. One POST is one profile; walk the config and POST once per name.

After `landbridged` is up and the name is on this machine's badges, have the human (operator session, not a Lead token) start a run. `profile` is required — the exact string from the runner config (`goose-devbox-linux`, or `any-linux` if you added the group name):

```
POST /dashboard/conformance
Origin: <the plane's own origin>
Content-Type: application/json

{ "profile": "<exact name>" }
```

A browser can do the same from `/dashboard/conformance` — the form has a profile field. Same-origin only, human-only (a Lead token is 403). The response is `201` with a `runId`, a `progressUrl`, how many connected machines currently declare that profile (`machinesDeclaring`), and the three sessions:

| kind | What the worker must do |
|---|---|
| `identity` | Report hostname, cwd, and the first 8 hex of `$LANDBRIDGE_SESSION_ID` |
| `write` | Write `smoke.txt` in the workspace containing only the hostname |
| `shell` | `echo` a nonce (`lbr-smoke-` plus the run id prefix) and report that line |

Poll progress:

```
GET /dashboard/conformance/{runId}?format=json
```

`workerDone` is true when every session is `awaiting_report` or hidden (accepted) and none is `health=failed`. `pending` includes unclaimed occupancy (`desired=running`, `observed=none` — usually the profile name is not on a heartbeat yet) and live work. `failed` is `health=failed` (infrastructure gave up) or a hidden cancel/discard. A `machinesDeclaring` of `[]` with sessions stuck unclaimed is the restart-the-daemon miss from above.

Do not skip the kill-path check above because the dummy set reached `awaiting_report`. Dummy sessions never exercise `stop`. When you are done, `park_session` or accept each dummy — or those workers keep the machine. Then POST again with the next name in the config.

> **Future work (spec §11).** The conformance run automates the above and goes past it: per declared profile, the control plane would judge event attribution by session id, heartbeat cadence against the config, two concurrent sessions tracked independently, `stop` acknowledgement (and message delivery demonstrably reaching the agent as a turn), `TTL=0` killing one process while its sibling survives, a relay forward round-tripping and its listener closing on release, an approval-prone session completing without hanging, and a parked session resuming from its recorded directory with context intact — admitting the machine as `ready` on a pass, or leaving it registered-but-unclaimable with the failing step named. None of that exists yet. The manual per-profile pass above is its stand-in, not a preview of it.

## After enrollment

Nothing about this setup is meant to be hand-maintained. Re-running this flow — reprobing, rewriting the config, restarting the daemon, smoke-testing again — is the supported way to change it, and it is safe to repeat. A rewrite of the file is not enough: `landbridged` does not watch `--config`, so wait for the post-restart heartbeat before targeting a new name. The dashboard badges are the check that the plane saw it.

Spec §11 also wants the config stamped with the version of this skill, so the control plane can flag stale machines for a re-run. **That does not exist**: nothing writes a version, nothing serves one, and a `skill_version` key added by hand is silently dropped when the config parses. Until it lands, a machine's config is only as current as whoever last re-ran this — so when you notice a config written against older guidance, say so to the human rather than assuming the plane will catch it.

Note that `landbridged` keeps no state a restart would try to reconcile: no session ledger, no process re-adoption. If it restarts, every agent on this machine is killed and their sessions go `health=failed` — that is deliberate, not a fault. On start it also kills any stray harness processes it finds, which is what makes the guarantee survive an unclean shutdown. The state dir is the exception, and a narrow one: it holds the machine credentials and, where capture is on, the transcripts, which must outlive both a session teardown and a restart.

## A note on what this machine is

A name, a purpose, the OS string, and a permission level were declared once, when `landbridged` exchanged the enrollment token for credentials — not from the config, and not on the runner connection, which declares nothing beyond the token. Those four are recorded server-side and are not re-declarable from a config file, so a machine cannot promote its own privileges by editing one. If the machine's role changes, that is a human decision made through the control plane.

Two honest limits on that record. Hardware specs are **not** part of it despite what §11 says — nothing collects CPU or memory as declared capacity, only live load on the heartbeat. And the permission level is stored but not yet read by anything: no dispatch, forwarding, or tool decision consults it today. Treat it as a label the human is recording for later, not a control that is holding.
