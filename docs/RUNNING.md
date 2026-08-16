# Running Docket

The operator / developer guide: bring the system up locally, authenticate,
enroll a real machine, point a worker at a real harness, and the full config-key
reference. For putting real TLS on the preview frontend, see
[PREVIEW-TLS.md](PREVIEW-TLS.md). For the *why*, see [ARCHITECTURE.md](ARCHITECTURE.md) and the
authoritative [`ideas/spec.md`](../ideas/spec.md).

## Prerequisites

- **.NET 10 SDK** (`Directory.Build.props` targets `net10.0`; CI uses `10.0.x`).
- **Docker** — the dev loop runs Postgres in a container via .NET Aspire.
- No shell scripts are involved anywhere: `docketd` invokes harnesses via argv
  through `execve`, never a shell (spec §10).

## The dev loop (one command)

```bash
dotnet run --project src/Docket.AppHost
```

`Docket.AppHost` is a .NET Aspire orchestrator. It is a **dev-time inner loop
only** — not a production deployment path (in production `docketd` runs
standalone on each machine, and nothing couples the runtime to Aspire). It
brings up, in dependency order:

| Resource | What it is | Endpoint |
|---|---|---|
| `postgres` | Managed Postgres 16 with a persistent data volume (survives restarts) | container |
| `mcp` | The control plane + MCP host (`Docket.Mcp`), migrated on startup and dev-seeded | `http://127.0.0.1:5000` (fixed, un-proxied) |
| `relay` | `docket-relay` | `http://127.0.0.1:5100` (fixed, un-proxied) |
| `docketd` | A real runner, enrolled via a dev-seeded machine token, dialing `ws://127.0.0.1:5000/runner` | outbound only |

The endpoints for `mcp` and `relay` are pinned to fixed loopback ports and *not*
proxied by Aspire's DCP, because the sibling `docketd`/worker/relay processes
dial those addresses directly and would not see an ephemeral proxy port.

Two dashboards:

- **Aspire dashboard** — the URL is printed on the console at startup. It shows
  every resource, its console logs, and the host's OpenTelemetry traces, metrics,
  and logs (`Docket.ServiceDefaults` wires OTel; the host exports to the Aspire
  collector automatically in this loop).
- **Docket web dashboard** (spec §12) — served by the host at
  `http://127.0.0.1:5000/dashboard`. See [authentication](#authenticating-a-human)
  below; it needs an operator passphrase you must set yourself.

The loop stands up a *standing fleet* and does **not** auto-create a task. Create
work as a Lead over MCP, exactly as in production. The dispatched worker is
`Docket.WorkerHarness`, a scripted no-LLM MCP client that exercises the full
dispatch → `get_task` → `report_result` protocol; see
[running a real harness](#pointing-a-worker-at-a-real-harness) to swap it for
`claude -p`.

### Dev-seed shortcut

In the dev loop the host bootstraps a machine identity out of band (the real
enrollment handshake an operator performs — below — is skipped) and writes a
JSON file with `machineId` and `machineToken` to a temp path. The AppHost reads
it and hands `docketd` its `DOCKET_MACHINE_TOKEN` / `DOCKET_MACHINE_ID`. This is
gated by `Docket:DevSeed:TokenFile` and **production never sets it**. The dev
machine token is fixed and never refreshed. Completion is Lead-adjudicated
(§7, §9 check 4), so the loop seeds no verifier credential — a human-driven Lead
closes the task lifecycle with `submit_review`.

## Authenticating a human

Two doors, both gated by an **operator passphrase** you configure. The plane
stores only the SHA-256 hex of the passphrase, never the plaintext. When it is
unset, both `/oauth/authorize` and the dashboard login are **fail-closed (503)** —
so the dev loop ships with dashboard login disabled until you set it.

Generate the hash and set it (illustrative — any way of producing the SHA-256 hex
works):

```bash
printf '%s' 'your-passphrase' | shasum -a 256    # → the hex to store
```

Then supply it to the host as `Docket:Operator:PassphraseHash`, e.g. via user
secrets or the environment variable `Docket__Operator__PassphraseHash` (see the
[config reference](#control-plane-configuration-docketmcp) for the exact key).

- **Dashboard** — browse to `/dashboard`, land on `/dashboard/login`, enter the
  passphrase. On success you get a `docket_session` cookie (12h, HttpOnly,
  `Path=/dashboard`) and can view `/dashboard/machines` (Machine Group),
  `/dashboard/teams` + `/dashboard/teams/{id}` (Team views), `/dashboard/inbox`
  (human inbox), `/dashboard/events`, and `/dashboard/conformance` (operator
  dummy-task check aimed at `default`: `POST` mints the set,
  `GET /dashboard/conformance/{runId}` reports states). Pages carry a
  5-second auto-refresh and each has a JSON twin (`?format=json` or an
  `Accept: application/json` request). A pasted human/Lead token is accepted as a
  secondary door — but a **Lead** token reads only its own Team: `/dashboard/teams`,
  `/dashboard/inbox` and `/dashboard/events` come back filtered to it, another
  Team's `/dashboard/teams/{id}` is a 403, and `/dashboard/machines` plus
  `/dashboard/conformance` are human-only (machine enumeration is a human surface
  by design, §12). The mutating forms (login, logout, the permission verdict,
  **Revoke machine**, the profile-check start) are refused unless the request
  carries this dashboard's own `Origin` — so a scripted POST has to send one.
  Revoking is human-only for the same reason the Machine Group view is: a
  machine belongs to no Team.
- **MCP / harness (OAuth 2.1)** — a harness acting as a Lead authenticates via the
  authorization-code flow with PKCE (S256 only). The plane advertises
  `/.well-known/oauth-protected-resource` (RFC 9728) and
  `/.well-known/oauth-authorization-server` (RFC 8414), and identifies clients by
  **Client ID Metadata Document** (the `client_id` is a URL), not Dynamic Client
  Registration. The `/oauth/authorize` consent step verifies the same operator
  passphrase; `/oauth/token` mints an opaque human session.

Lead identity is then *derived from the authenticated token*, not claimed through
a tool call — a human-session/lead principal is what the MCP Lead tools require.
An earlier draft of spec §10 listed `claim_lead` / `release_lead` / `list_teams` /
`get_machine_group_status` tools; per §10's as-built reconciliation all four are
**deliberate non-goals, not pending work**. Claiming and releasing a Lead is the
credential lifecycle rather than a tool call, and Team / Machine Group enumeration
is a human surface served by the web dashboard (each page has a `?format=json`
twin a reattaching Lead can read with its own token). The slash-command prompts
are separately not built — no MCP prompt is registered on this branch.

## Enrolling a real second machine

On production machines `docketd` runs standalone. Enrollment exchanges a
**human-issued enrollment token** (single-use, short-lived — 15 minutes) for
persistent machine credentials. The token is read from a file or stdin,
**never from argv** (it would leak into shell history and `/proc/<pid>/cmdline` —
spec §13):

```bash
# token in a file (0600):
docketd --enroll \
  --control-url https://plane.example.com \
  --enroll-token-file ./enroll.token \
  --state-dir /var/lib/docketd \
  --name web-builder-01 --purpose "CI worker" --permission-level standard

# or piped on stdin (no --enroll-token-file):
docketd --enroll --control-url https://plane.example.com < enroll.token
```

This POSTs to `POST /enroll` and persists the machine credentials — access token,
refresh token, machine id, and the control URL — to `credentials.json` under the
state dir, written `0600` in a `0700` directory. `--name`/`--purpose`/
`--permission-level` default to the hostname / `general` / `standard`; the OS
string is filled automatically.

State-dir resolution order: `--state-dir` → `$DOCKET_STATE_DIR` →
`$XDG_STATE_HOME/docket` → `~/.docket`.

Then run the daemon against a config (see below); it loads the stored
credentials, derives the `/runner` WebSocket URL from the saved control URL, and
connects:

```bash
docketd --config /etc/docketd/config.json --state-dir /var/lib/docketd
```

The access token is short-lived and re-minted at `POST /machine/refresh` —
proactively at ~50% of its remaining lifetime and reactively on a 401 reconnect.
The long-lived refresh token is the only durable secret on the box.

> **Treat `credentials.json` as the machine.** The refresh token is bound to its
> machine only server-side — the row names which machine it mints for — so a copy of
> the file is a working machine identity on *any* host: nothing collects or checks a
> host fingerprint. Protect the file (it is `0600` in a `0700` dir for this reason),
> and if it leaks, revoke the machine — **Revoke machine** on the dashboard's Machine
> Group view closes its channel, requeues its tasks, kills its worker tokens, and
> makes it re-enroll to return.

> **Half built.** The agent-guided enrollment §11 describes ships as a *skill*, not a
> prompt: `docket-enroll` (`docket://skills/enroll`) walks an agent through probing the
> harness, writing the runner config, handing the service install to the human, and
> smoke-testing the machine. No MCP prompt is registered, so there is no `/docket-enroll`
> slash command to invoke it — the client reads the skill off `resources/list`. The
> control-plane **conformance run** — dispatching trivial tasks and judging the machine
> before it joins as `ready` — does not exist; nothing in the plane probes a new machine,
> and the skill's manual smoke test is its stand-in.

## The runner config

`docketd` contains no harness knowledge — everything specific is data (spec §10).
`--config <path>` points at a JSON file with a `machine` section, one or more
`profiles` (exactly one must be named `default`), and an optional `services` array —
the operator's own long-lived processes (§8.2), which `docketd` supervises as its own
children, keeps up, and verifies at dial. Those are the operator's, not an agent's: a
worker can see them in `list_processes` but cannot stop them. The full schema and a worked
Claude Code profile live in
[`ideas/skills/references/runner-config.md`](../ideas/skills/references/runner-config.md).
The dev-loop template is
[`src/Docket.AppHost/docketd.dev.json`](../src/Docket.AppHost/docketd.dev.json).

`machine`: `work_root` (per-task scratch dirs — `docketd` spawns each task in
`{work_root}/{task_id}`, which is *not* the workspace), `heartbeat_seconds`
(default 15s), and `back_pressure` thresholds (`max_cpu_load` / `max_memory_load`
/ `max_disk_usage` in `[0,1]`).

Each `profile` carries `spawn` (the ACP entry point), a required `prompt` (the
opening `session/prompt`), optional `follow_up`, optional `auth_method`,
optional `config_options` (ACP `session/set_config_option` pins; skipped unless
the agent advertised that `configId` and value), `stop.wind_down_seconds`,
`telemetry`, `logs`, an optional `max_concurrent`, and an optional `processes`
block. There is no `stdin`, `resume`, `events`, or `protocol` key.

- **stdin** is the JSON-RPC pipe. `docketd` holds the write end for the task's
  whole life — ACP's shutdown rule and the §10 dead-man's switch are the same
  fact. There is no `closed` opt-out.
- **`processes`** — `agent_initiated` (default `false`) is the gate deciding whether
  tasks on this profile may call `start_process` at all, and `max` (default `8`) caps
  how many they may hold at once.

`docketd` substitutes `{...}` tokens
into each `spawn` arg at dispatch: `{task_id}`, `{machine_id}`, `{work_dir}`
(`= {work_root}/{task_id}`, the cwd), and `{mcp_config}` (the path to the worker MCP config `docketd`
writes to `{work_dir}/mcp.json`, mode `0600`). It also stamps `DOCKET_MACHINE_ID`,
`DOCKET_TASK_ID`, and `DOCKET_WORKER_TOKEN` on the child.

> Note: `{work_root}` and `{worker_harness}` in `docketd.dev.json` are
> **AppHost** placeholders resolved *before* the file reaches `docketd` (the
> AppHost writes out a resolved copy); they are not `docketd` substitution tokens.
> `docketd`'s tokens are the five listed above.

### Pointing a worker at a real harness

This is the config-only swap the whole design turns on: to run a real agent
instead of the no-LLM harness, change the `default` profile's `spawn` argv — no
code change to `docketd` (spec §10). The dev template's

```json
"spawn": ["{worker_harness}", "--acp"],
"prompt": "Do the task you have been assigned."
```

becomes a real ACP entry point (abridged from the worked example in the
runner-config reference):

```json
"spawn": ["claude-agent-acp"],
"prompt": "You are a Docket worker. First call mcp__docket__get_task. When done, call mcp__docket__report_result. If blocked, call mcp__docket__request_input.",
"follow_up": "There is new input on your assignment. Call mcp__docket__get_task to read it, then continue."
```

The load-bearing parts:

- **`prompt` is required.** An ACP agent takes no prompt on argv. Without this
  field the worker connects, waits, and does nothing.
- **The plane's MCP server rides `session/new`.** No `{mcp_config}` file, no
  bearer on disk. Do not put bypass / `--always-approve` / `--auto` on `spawn` —
  permissions are `session/request_permission` and go through the plane.
- **Stop is `session/cancel`**, then `stop.wind_down_seconds` before the tree-kill.
  There is no `stop.mode`.
- **Resume is `session/load`.** There is no `resume.args`.

See [runner-config.md](../ideas/skills/references/runner-config.md) for the four
worked profiles.

### Progress

Tool calls arrive as ACP `session/update` notifications. There is no
`events.source` or `events.mapping` to declare. `alive` is still emitted by
`docketd`'s own heartbeat so a quiet process is not requeued for silence; the
no-progress ceiling is what requeues a worker that never emits a tool call.

## Running `docketd` as a service

On a real machine `docketd` must survive logout and reboot, which means the
platform's service manager. Two templates ship in [`deploy/`](../deploy):

| Template | Platform | Goes to |
|---|---|---|
| [`deploy/docketd.service`](../deploy/docketd.service) | Linux / systemd | `/etc/systemd/system/docketd.service` |
| [`deploy/com.docket.docketd.plist`](../deploy/com.docket.docketd.plist) | macOS / launchd | `~/Library/LaunchAgents/` or `/Library/LaunchDaemons/` |

Both are heavily commented: every non-obvious setting says why it is there, and
the systemd unit ends with a list of hardening options that are deliberately
*absent* because they break a specific `docketd` feature. Read the comments
before editing — `docketd` spawns agent harnesses as its own children and is the
containment boundary for them, so a sandbox setting applied to the daemon is also
applied to every dispatched agent.

**Installing a service is a privileged, system-level change, and the enroll skill
deliberately hands it to the human.** An enrolling agent prepares the file,
explains what it does and where it goes, and has the operator run the install and
enable commands (`ideas/skills/enroll-skill.md`, "Registering the daemon"). The
commands below are the ones to hand over.

### Substitutions

Both templates carry placeholders. Nothing else needs editing to get a working
service:

| Placeholder | What to put there |
|---|---|
| Binary path | Where you published `docketd` — `/opt/docketd/docketd` (Linux) or `/usr/local/libexec/docketd/docketd` (macOS) in the templates. |
| `--config` path | Your runner config, e.g. `/etc/docketd/config.json`. Required; `docketd` exits `2` without it. |
| `--state-dir` path | `/var/lib/docketd` (Linux) or `/Users/<you>/.docket` (macOS). Must match the path you enrolled with. |
| `User=` / `Group=` (Linux) | The dedicated service account, created below. |
| `PATH` | The directories holding `claude`, `node`, `git`, `docker` — see the warning below. |
| `/Users/YOU` (macOS) | Your real home; launchd expands neither `~` nor `$HOME` in a plist. |

> **The `PATH` line is the one people get wrong.** `docketd` copies its own
> environment onto every harness child at spawn, and both service managers hand a
> job a minimal `PATH` — systemd's compiled-in default, or launchd's
> `/usr/bin:/bin:/usr/sbin:/sbin` (no Homebrew, no `nvm`). If the harness binary
> is not on the `PATH` you set, the spawn fails with `ENOENT` and tasks fail on
> this machine only, which reads as a plane bug rather than a `PATH` bug.

Point `machine.work_root` at a path inside the state dir
(`/var/lib/docketd/work`, as in the [runner-config
reference](../ideas/skills/references/runner-config.md)) so one directory covers
credentials, transcripts, and per-task scratch. `docketd` creates each
`{work_root}/{task_id}` itself; the root needs to exist and be writable by the
service account.

### Linux (systemd)

```bash
# 1. A dedicated account with a REAL home — the harness reads ~/.claude,
#    ~/.gitconfig and ~/.ssh out of $HOME.
sudo useradd --system --create-home --home-dir /home/docketd \
     --shell /usr/sbin/nologin docketd

# 2. Publish the binary (AssemblyName is `docketd`; add
#    -r linux-x64 --self-contained if the box has no .NET runtime).
sudo dotnet publish src/Docket.Runner -c Release -o /opt/docketd

# 3. Config, owned by the service account.
sudo install -d -o docketd -g docketd -m 0750 /etc/docketd
sudo install -o docketd -g docketd -m 0640 config.json /etc/docketd/config.json

# 4. Enroll AS THE SERVICE USER, so credentials.json lands in the state dir it
#    will actually read (0600 in a 0700 dir).
sudo install -d -o docketd -g docketd -m 0700 /var/lib/docketd
sudo -u docketd /opt/docketd/docketd --enroll \
     --control-url https://plane.example.com \
     --enroll-token-file ./enroll.token \
     --state-dir /var/lib/docketd

# 5. Install and enable.
sudo install -m 0644 deploy/docketd.service /etc/systemd/system/docketd.service
sudo systemd-analyze verify /etc/systemd/system/docketd.service   # optional, catches typos
sudo systemctl daemon-reload
sudo systemctl enable --now docketd
```

Verify:

```bash
systemctl status docketd
journalctl -u docketd -f
```

A healthy start logs one line naming the machine id, the declared profiles, the
stray count, and the control endpoint:

```
docketd up: machine=<id> profiles=[default] strays_reaped=0 control=wss://plane.example.com/runner
```

Then confirm the machine appears in the dashboard's Machine Group. `strays_reaped=0`
is the normal result under systemd: the unit's cgroup is torn down before a
restart, so there is usually nothing left to reap. A non-zero count after an
unclean shutdown is the sweep doing its job.

### macOS (launchd): LaunchAgent or LaunchDaemon

This choice matters more than any other setting in the plist, and neither answer
is clean.

A **LaunchAgent** (`~/Library/LaunchAgents/`, `launchctl bootstrap gui/…`) runs
as you, inside your login session. The harness therefore gets the environment it
was installed into: your unlocked login keychain (where Claude Code keeps its
credentials on macOS, falling back to `~/.claude/.credentials.json`), your
`~/.claude` settings and installed MCP servers, your `~/.gitconfig` and
`~/.ssh`, your Homebrew and `nvm` toolchains, and TCC prompts that resolve
against your user record instead of failing silently. The cost is that it **does
not survive logout** — the job is bootstrapped into the GUI session and torn down
with it. A locked screen is fine; logout, fast user switching, and a reboot with
no auto-login are not. You also cannot bootstrap it purely over SSH before
someone has logged in.

A **LaunchDaemon** (`/Library/LaunchDaemons/`, root-owned `0644`,
`sudo launchctl bootstrap system`) genuinely survives logout and reboot with no
session at all. The cost is the mirror image: it runs outside any user session,
so the login keychain is locked and a keychain-backed harness login simply fails;
`$HOME` is whatever you set it to and `~/.claude` has to be provisioned there
deliberately; and TCC-protected resources (Desktop, Documents, Downloads, Screen
Recording, Automation) are denied silently rather than prompting. Setting
`UserName` to your own account recovers the home directory but *not* the
keychain — the session unlocks it, not the uid.

Pick by machine, not by preference: a workstation or Mac dev box you are logged
into anyway wants the LaunchAgent (enable auto-login so a reboot brings it back);
a headless Mac in a rack wants the LaunchDaemon, with a dedicated service account
whose `$HOME` you provision and a harness credential it can read without a
session.

```bash
# Publish (add -r osx-arm64 --self-contained if the box has no .NET runtime).
sudo dotnet publish src/Docket.Runner -c Release -o /usr/local/libexec/docketd

# Enroll first, into the same --state-dir the plist passes.
/usr/local/libexec/docketd/docketd --enroll \
  --control-url https://plane.example.com \
  --enroll-token-file ./enroll.token --state-dir ~/.docket

# LaunchAgent (runs as you, dies at logout):
cp deploy/com.docket.docketd.plist ~/Library/LaunchAgents/
plutil -lint ~/Library/LaunchAgents/com.docket.docketd.plist
launchctl bootstrap gui/$(id -u) ~/Library/LaunchAgents/com.docket.docketd.plist

# LaunchDaemon (survives logout; add UserName/GroupName and a real HOME first):
sudo cp deploy/com.docket.docketd.plist /Library/LaunchDaemons/
sudo chown root:wheel /Library/LaunchDaemons/com.docket.docketd.plist
sudo chmod 644 /Library/LaunchDaemons/com.docket.docketd.plist
sudo launchctl bootstrap system /Library/LaunchDaemons/com.docket.docketd.plist
```

Verify, then restart or remove:

```bash
launchctl print gui/$(id -u)/com.docket.docketd     # `system/…` for a daemon
tail -f ~/Library/Logs/docketd.out.log              # the `docketd up:` line
launchctl kickstart -k gui/$(id -u)/com.docket.docketd
launchctl bootout gui/$(id -u)/com.docket.docketd
```

launchd has no equivalent of systemd's `RestartPreventExitStatus`, so a bad
config exits `2` and respawns every 10 seconds forever. The reason is on the
first line of `docketd.err.log` — read it there rather than wondering why the
machine never appears.

### Windows

**There is no template here, because there is nothing honest to put in one.**
`docketd` is a plain console application with no Windows Service host
(`Microsoft.Extensions.Hosting.WindowsServices` is not referenced), so
`sc.exe create` pointed straight at the binary will not work: the process never
reports `SERVICE_RUNNING` to the Service Control Manager and the start fails with
error 1053. Windows also has no `SIGTERM`, so the SCM's stop request cannot reach
`docketd`'s shutdown path.

Two pragmatic options, neither validated against a real Windows machine:

- **A service wrapper** — NSSM or WinSW hosts the console binary and speaks to
  the SCM on its behalf. Their default stop sequence sends a console `Ctrl+C`
  first, which .NET surfaces as `PosixSignal.SIGINT` — a signal `docketd` *does*
  handle, giving the same ordered teardown as SIGTERM on Linux. Give the wrapper
  a stop timeout of at least 45 seconds, matching the other two templates.
- **A scheduled task** at startup, running as a dedicated account with "run
  whether the user is logged on or not". Simpler, no third-party dependency, and
  it gives up graceful shutdown entirely — the task is killed, not signalled.

Losing graceful shutdown costs less on Windows than it would elsewhere: each
worker is sealed at spawn into a kill-on-close Job Object, so `docketd` dying by
any means — including `TerminateProcess` — makes the OS tear down the whole worker
tree. That is why the stray reaper's process inventory is empty by design on
Windows: there is nothing left to discover.

### What a restart does, and how to avoid it hurting

`docketd` holds no state. **A restart kills every agent running on the machine
and their tasks requeue** — deliberate, not a fault. The templates lean into
this: `Restart=always` / `KeepAlive` bring the daemon straight back, and on start
it also kills any stray harness processes it finds, which is what makes the kill
guarantee survive an unclean shutdown.

A `SIGTERM` (`systemctl stop`, `launchctl bootout`) runs `docketd`'s ordered
teardown — hard-kill every worker's process tree, close every live relay tunnel,
join in-flight transcript reads, drain the event ring. It is **not** a graceful
agent wind-down: no stop turn is injected, and `stop.wind_down_seconds` /
`min(ttl, wind_down_seconds)` apply only to a `stop` command arriving from the
control plane, never to daemon shutdown. `TimeoutStopSec=45` / `ExitTimeOut=45`
covers the teardown itself, not an agent's last turn.

So for a planned restart — a config change, an upgrade, a reboot — **drain the
machine from the control plane first**, let the running tasks reach their own end,
and only then stop the service. Note what "let them finish" can and cannot mean on a
`claude -p` profile: the worker is never handed a wind-down turn, so a plane-side
`stop` buys it the granted TTL to finish and exit on its own, not a request to
report. Give a real TTL and wait it out rather than expecting a closing
`report_result` that will not come.

## Configuration reference

Keys are shown in `:`-separated form; as environment variables, replace `:` with
`__` (e.g. `Docket__PublicMcpUrl`). Every key below is grepped from the code on
this branch.

### Control plane / host (`Docket.Mcp`)

| Key | Default | Purpose |
|---|---|---|
| `ConnectionStrings:Docket` (or env `DOCKET_DB`) | `Host=localhost;Database=docket;Username=docket` | Postgres connection string. |
| `Docket:PublicMcpUrl` (or env `DOCKET_PUBLIC_MCP_URL`) | `http://127.0.0.1:5000` | The plane's public MCP endpoint dialed by workers; also the OAuth 2.1 canonical resource id / issuer. Set to the real public **https** URL in production. |
| `Docket:Operator:PassphraseHash` | *(empty → fail-closed)* | SHA-256 hex of the operator passphrase gating `/oauth/authorize` and dashboard login. Store the hash, never the plaintext. |
| `Docket:WaitTtl` | infinite | How long a `blocked_on_input` task waits before parking (spec §11). Off by default; a live ACP session is held until a Lead answers or `park_task`. Set a TimeSpan (e.g. `00:30:00`) to restore a timer. |
| `Docket:MachineLivenessTtl` | `00:01:30` | Heartbeat-age window past which a machine is treated as rebooted and its waiting tasks requeue (≈ six missed 15s heartbeats). |
| `Docket:WaitTtlSweepInterval` | `00:01:00` | How often the `WaitTtlSweeper` background loop runs. |
| `Docket:PerTaskLivenessWindow` | `00:01:00` | §10 clock one (**aliveness**): how long `docketd` may go without asserting a task's harness process is alive before the task is requeued. `docketd` asserts every heartbeat, so this is not gated on `events.source`. |
| `Docket:NoProgressCeiling` | `00:30:00` | §10 clock two (**progress**): how long an alive process may report no `tool-call` before it is treated as wedged. A task bearing a registered service (§8.2) is exempt from this one, never from the first. |
| `Docket:InfrastructureRequeueLimit` | `5` | §9 check 7: the infrastructure requeue cap stamped onto new tasks. Reaching it abandons the task as `canceled` (never `rejected`) with the workspace preserved. Non-positive means uncapped. |
| `Docket:PermissionPollIntervalMs` | `500` | How often `request_permission` re-reads the task row while a worker blocks on a §11 approval. One indexed primary-key read per tick, and only while a worker is genuinely blocked. |
| `Docket:RelayUrl` (or env `DOCKET_RELAY_URL`) | `http://127.0.0.1:5100` | The `docket-relay` URL the plane hands `docketd` per `open_forward`, and the preview frontend per connect. Config wins, then the env var, then this default. |
| `Docket:PreviewUrlBase` | `http://preview.localhost` | The wildcard base §8.4 preview URLs are built from — the opaque label becomes its leftmost subdomain. Set to your real wildcard host (`https://preview.example.com`) in production. |
| `Docket:PreviewConnect:Bearer` | *(unset → 503)* | Shared bearer the preview frontend must present to `POST /preview/connect`. Fail-closed when unset, like `Docket:RelayValidation:Bearer`. |
| `Docket:RelayValidation:Bearer` | *(unset → 503)* | Shared bearer the relay must present to `POST /relay/validate`. Fail-closed when unset. |
| `Docket:Oauth:AllowInsecureClientMetadata` | `false` | DEV/TEST ONLY. Disables the CIMD SSRF address fence (accepts `http` `client_id` URLs and hosts resolving to private, loopback, or link-local addresses). Never enable in production. |
| `Docket:MigrateOnStartup` | `false` | Apply the checked-in EF migration on boot. Set by the dev loop; production migrates out of band. |
| `Docket:DevSeed:TokenFile` | *(unset)* | Dev-loop only: bootstrap a machine identity and write the seed file here. Never set in production. |
| env `OTEL_EXPORTER_OTLP_ENDPOINT` | *(unset)* | When set, the host exports OpenTelemetry via OTLP (the Aspire dashboard sets this in the dev loop). |

### Relay (`Docket.Relay`)

| Key | Default | Purpose |
|---|---|---|
| `Relay:ControlPlane:Url` | *(unset)* | When set, activates the real `ControlPlaneGrantValidator`, which validates each grant against `{Url}/relay/validate`. When unset, the fail-closed `StaticSecretGrantValidator` is used instead. |
| `Relay:ControlPlane:Bearer` | *(unset)* | Bearer presented to the plane's `/relay/validate`. Must match the plane's `Docket:RelayValidation:Bearer`. |
| `Relay:ControlPlane:Timeout` | `00:00:05` | Validation call timeout; a timeout refuses the tunnel (fail-closed). |
| `Relay:Grant:AllowAll` | `false` | DEV/SMOKE ONLY. Accept every grant (logs a loud warning). |
| `Relay:Grant:SharedSecret` | *(null)* | Static shared-secret grant for the stub validator. With neither this nor `AllowAll`, the static validator refuses everything. |
| `Relay:PairWaitTimeout` | `00:00:30` | How long a tunnel waits for its opposite end to arrive before giving up. |

The dev loop sets `Relay:ControlPlane:Url` and a freshly-minted shared
`Relay:ControlPlane:Bearer` on both `mcp` and `relay`, so the real
control-plane validator is active — not the static stub.

### Runner (`docketd`)

Flags: `--config <path>` (required for a normal run), `--machine-id <id>`,
`--state-dir <dir>`; and for enrollment `--enroll --control-url <url>`
`[--enroll-token-file <path>]` `[--name <n>]` `[--purpose <p>]`
`[--permission-level <l>]`.

| Env var | Purpose |
|---|---|
| `DOCKET_CONTROL_URL` | The `ws(s)://…/runner` URL to dial. In the dev loop the AppHost sets it; with file credentials it is derived from the saved control URL. |
| `DOCKET_MACHINE_TOKEN` | A fixed machine bearer (dev-loop path — never refreshed). When unset, `docketd` loads persisted credentials from the state dir and refreshes them. |
| `DOCKET_MACHINE_ID` | Machine id (else `--machine-id`, else a random id). |
| `DOCKET_STATE_DIR` / `XDG_STATE_HOME` | State-dir resolution (see enrollment). |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | Enables OTLP export from the runner when set. |

### Completion (adjudication)

There is no verifier process. A task in `verifying` is completed by a Lead (or a
human) calling `submit_review` over MCP (§7, §9 check 4): the Lead reads the
reported result and rules on evidence it gathers itself (a test run, a CI check).
In `lead` mode (the default) the Lead's verdict completes the task with no human
confirmation; in `review` mode the verdict must carry it. A task's own worker can
never complete it.

## Running the tests

See the [Tests section of the README](../README.md#tests). In short: `dotnet
build -c Release` gates on warnings (`TreatWarningsAsErrors`), then run each
suite with `dotnet test … --no-build -c Release`. The Postgres-backed suites
(`ControlPlane`, `Mcp`, `Meta`, `MultiMachine`, `Chaos`) honor `DOCKET_TEST_PG`; when
it is set they use that server instead of spinning a local cluster, and each gets its
**own database** on it so no suite's reset truncates another's fixtures. CI splits into
`ci.yml` (ubuntu + Postgres: the build-and-test matrix, the chaos job, and the two
opt-in real-harness tiers), `os-matrix.yml` (the platform-sensitive suites on
ubuntu/macOS/Windows), and `publish-images.yml` (GHCR runtime images on a `v*` tag).

Paid real-harness e2e (`Category=RealClaude` / `RealCodex` / `RealOpenCode` /
`RealGrok`) reads API keys from the environment. Locally, put them in user
secrets on the MultiMachine test project — they are loaded at assembly start
and published into the process so spawned CLIs inherit them. Process env
(including CI job secrets) is not overwritten.

```bash
dotnet user-secrets set ANTHROPIC_API_KEY '…' --project tests/Docket.MultiMachine.Tests
dotnet user-secrets set CODEX_API_KEY     '…' --project tests/Docket.MultiMachine.Tests
dotnet user-secrets set XAI_API_KEY       '…' --project tests/Docket.MultiMachine.Tests
```

`ANTHROPIC_KEY`, `OPENAI_KEY` / `OPENAI_API_KEY`, and `XAI_KEY` are accepted
and aliased to the names the CLIs actually read.
