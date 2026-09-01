# Running Landbridge

The operator / developer guide: bring the system up locally, authenticate,
enroll a real machine, point a worker at a real harness, and the full config-key
reference. For putting real TLS on the preview frontend, see
[PREVIEW-TLS.md](PREVIEW-TLS.md). For the *why*, see [ARCHITECTURE.md](ARCHITECTURE.md) and the
authoritative [`ideas/spec.md`](../ideas/spec.md).

## Prerequisites

- **.NET 10 SDK** (`Directory.Build.props` targets `net10.0`; CI uses `10.0.x`).
- **Docker** — the dev loop runs Postgres in a container via .NET Aspire.
- No shell scripts are involved anywhere: `landbridged` invokes harnesses via argv
  through `execve`, never a shell (spec §10).

## The dev loop (one command)

```bash
dotnet run --project src/Landbridge.AppHost
```

`Landbridge.AppHost` is a .NET Aspire orchestrator. It is a **dev-time inner loop
only** — not a production deployment path (in production `landbridged` runs
standalone on each machine, and nothing couples the runtime to Aspire). It
brings up, in dependency order:

| Resource | What it is | Endpoint |
|---|---|---|
| `postgres` | Managed Postgres 16 with a persistent data volume (survives restarts) | container |
| `mcp` | The control plane + MCP host (`Landbridge.Mcp`), migrated on startup and dev-seeded | `http://127.0.0.1:5050` (fixed, un-proxied) |
| `relay` | `landbridge-relay` | `http://127.0.0.1:5100` (fixed, un-proxied) |
| `litellm` | Local LiteLLM gateway the classifier dials (`provider/model` slugs) | `http://127.0.0.1:4000` (fixed, un-proxied) |
| `classifier` | Permission classifier (simple argv allowlist, destroy-guard, two-stage LLM) | `http://127.0.0.1:5310` (fixed, un-proxied) |
| `landbridged-codex` / `-claude` / `-grok` | Three enrolled Linux containers, each dialing `ws://host.docker.internal:5050/runner` | outbound only |

The endpoints for `mcp` and `relay` are pinned to fixed loopback ports and *not*
proxied by Aspire's DCP, because the sibling `landbridged`/worker/relay processes
dial those addresses directly and would not see an ephemeral proxy port.

Two dashboards:

- **Aspire dashboard** — the URL is printed on the console at startup. It shows
  every resource, its console logs, and the host's OpenTelemetry traces, metrics,
  and logs (`Landbridge.ServiceDefaults` wires OTel; the host exports to the Aspire
  collector automatically in this loop).
- **Landbridge web dashboard** (spec §12) — served by the host at
  `http://127.0.0.1:5050/dashboard`. See [authentication](#authenticating-a-human)
  below. The Aspire / Development host uses the passphrase `dev`; production
  is fail-closed until you set `Landbridge:Operator:PassphraseHash`.

The classifier is a .NET API. It auto-allows a simple argv of an allowlisted
read-only program (`ls`, `git status`, `git --version`, …). Any shell
metacharacter (`|`, `&`, `;`, `$`, quotes, …) skips that gate. A small
destroy-guard list (`git reset --hard`, `git clean -f`, `terraform destroy`,
…) Asks without calling the model, so a model outage cannot wave those
through. Everything else is a two-stage LLM. Fail-closed is Ask / Lead, so
the allowlist stays small.

Each stage names a `provider/model` slug and a prompt template, in
`src/Landbridge.Classifier/appsettings.json` (or env). `anthropic/haiku` is a
valid Fast or Review model; a bare `gpt-5-nano` still means `openai/gpt-5-nano`.
The classifier sends those slugs to a local LiteLLM gateway (`litellm` on
`:4000`); provider keys stay on that container (`OPENAI_API_KEY` /
`ANTHROPIC_API_KEY` / `XAI_API_KEY`). A down gateway or a model error is Ask,
never Deny. Missing LiteLLM URL/key fails only the classifier resource; the
rest of the loop still starts.

The loop stands up a *standing fleet* and does **not** auto-create a task. Create
work as a Lead over MCP, exactly as in production. Each seeded box spawns the
real ACP harness (`codex-acp`, `claude-agent-acp`, `grok agent stdio`). Put the
provider keys in user secrets (or the environment); landbridged inherits them
and the child harness reads them. They never go in the runner config. The
scripted no-LLM `Landbridge.WorkerHarness` is still what the automated tests
drive — not this loop.

### Dev-seed shortcut

In the dev loop the host enrolls three linux boxes out of band (the real
enrollment handshake an operator performs — below — is skipped) and writes one
JSON file per box (`machineId`, `machineToken`) under a temp directory. The
AppHost starts one Linux `landbridged` container per file
(`docker/Landbridge.Landbridged.Dockerfile`, with the ACP harnesses on `PATH`)
and hands it that box's `LANDBRIDGE_MACHINE_TOKEN` / `LANDBRIDGE_MACHINE_ID`.
This is gated by `Landbridge:DevSeed:TokenDir` and **production never sets it**.

| Box | Declared OS | Profiles |
|---|---|---|
| `codex-apphost-linux` | linux | `codex-apphost-linux`, `any-linux` |
| `claude-apphost-linux` | linux | `claude-apphost-linux`, `any-linux` |
| `grok-apphost-linux` | linux | `grok-apphost-linux`, `any-linux` |

No Team is minted. A human Lead creates work and aims it at one of those
profile names. Spawn is the real ACP entry point for that harness. AppHost
generates each box's runner config at startup (prompt, `follow_up`,
`auth_method` for Codex, a sealed `CODEX_HOME` pinning `gpt-5.3-codex`,
`GROK_FOLDER_TRUST=0` for Grok). Override the Codex slug with
`LANDBRIDGE_CODEX_MODEL` if that catalog changes. The same keys the
paid MultiMachine e2e uses also feed this loop:

```bash
# Either store works. Process env wins if both are set.
dotnet user-secrets set ANTHROPIC_API_KEY '…' --project src/Landbridge.AppHost
dotnet user-secrets set CODEX_API_KEY     '…' --project src/Landbridge.AppHost
dotnet user-secrets set OPENAI_API_KEY    '…' --project src/Landbridge.AppHost
dotnet user-secrets set XAI_API_KEY       '…' --project src/Landbridge.AppHost
dotnet user-secrets set LANDBRIDGE_CLASSIFIER_FAST_MODEL   'openai/gpt-5-nano' --project src/Landbridge.AppHost
dotnet user-secrets set LANDBRIDGE_CLASSIFIER_REVIEW_MODEL 'anthropic/haiku' --project src/Landbridge.AppHost

# Already stored for the paid e2e? Leave them there — AppHost loads that
# secrets id too.
dotnet user-secrets set ANTHROPIC_API_KEY '…' --project tests/Landbridge.MultiMachine.Tests
```

`ANTHROPIC_KEY`, `OPENAI_KEY` / `OPENAI_API_KEY`, and `XAI_KEY` are accepted
and stamped as the names the CLIs actually read. A missing key is a failed
start — a box that enrolled without one would look ready and then die on the
first turn, which is the quiet failure the enroll skill already warns about.
The harness CLIs live in the Linux image; they do not need to be on the
host `PATH`. First `dotnet run` builds that image (SDK publish + npm + the
Grok installer) and later runs reuse the Docker layer cache.

## Authenticating a human

Two doors, both gated by an **operator passphrase**. The plane stores only the
SHA-256 hex of the passphrase, never the plaintext. When it is unset, both
`/oauth/authorize` and the dashboard login are **fail-closed (503)**.

The Aspire loop and `appsettings.Development.json` set the hash for the
passphrase `dev`, so local `/dashboard/login` works without extra config.
Production `appsettings.json` leaves the hash empty.

Generate the hash and set it (illustrative — any way of producing the SHA-256 hex
works):

```bash
printf '%s' 'your-passphrase' | shasum -a 256    # → the hex to store
```

Then supply it to the host as `Landbridge:Operator:PassphraseHash`, e.g. via user
secrets or the environment variable `Landbridge__Operator__PassphraseHash` (see the
[config reference](#control-plane-configuration-landbridgemcp) for the exact key).

- **Dashboard** — browse to `/dashboard`, land on `/dashboard/login`, enter the
  passphrase. On success you get a `landbridge_session` cookie (12h, HttpOnly,
  `Path=/dashboard`) and can view `/dashboard/machines` (Machine Group),
  `/dashboard/teams` + `/dashboard/teams/{id}` (Team views), `/dashboard/inbox`
  (human inbox), `/dashboard/events`, `/dashboard/conformance` (operator
  dummy-task check aimed at a named profile: `POST` mints the set,
  `GET /dashboard/conformance/{runId}` reports states), and `/dashboard/connect`
  (how to reach the plane as a Lead, and how to enroll a machine — including
  issuing an enrollment token, claiming a Team, and minting a one-time setup
  link whose first GET is markdown that contains the Lead bearer). Pages carry a
  5-second auto-refresh and each has a JSON twin (`?format=json` or an
  `Accept: application/json` request). A pasted human/Lead token is accepted as a
  secondary door — but a **Lead** token reads only its own Team: `/dashboard/teams`,
  `/dashboard/inbox` and `/dashboard/events` come back filtered to it, another
  Team's `/dashboard/teams/{id}` is a 403, and `/dashboard/machines` plus
  `/dashboard/conformance` are human-only (machine enumeration is a human surface
  by design, §12). `/dashboard/connect` is readable by a Lead; issuing an
  enrollment token or claiming a Team is human-only. The mutating forms (login,
  logout, the permission verdict, **Revoke machine**, the profile-check start,
  the Connect claims) are refused unless the request carries this dashboard's
  own `Origin` — so a scripted POST has to send one.
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

On production machines `landbridged` runs standalone. Enrollment exchanges a
**human-issued enrollment token** (single-use, short-lived — 15 minutes) for
persistent machine credentials. The token is read from a file or stdin,
**never from argv** (it would leak into shell history and `/proc/<pid>/cmdline` —
spec §13):

```bash
# token in a file (0600):
landbridged --enroll \
  --control-url https://plane.example.com \
  --enroll-token-file ./enroll.token \
  --state-dir /var/lib/landbridged \
  --name web-builder-01 --purpose "CI worker" --permission-level standard

# or piped on stdin (no --enroll-token-file):
landbridged --enroll --control-url https://plane.example.com < enroll.token
```

This POSTs to `POST /enroll` and persists the machine credentials — access token,
refresh token, machine id, and the control URL — to `credentials.json` under the
state dir, written `0600` in a `0700` directory. `--name`/`--purpose`/
`--permission-level` default to the hostname / `general` / `standard`; the OS
string is filled automatically.

State-dir resolution order: `--state-dir` → `$LANDBRIDGE_STATE_DIR` →
`$XDG_STATE_HOME/landbridge` → `~/.landbridge`.

Then run the daemon against a config (see below); it loads the stored
credentials, derives the `/runner` WebSocket URL from the saved control URL, and
connects:

```bash
landbridged --config /etc/landbridged/config.json --state-dir /var/lib/landbridged
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
> prompt: `landbridge-enroll` (`landbridge://skills/enroll`) walks an agent through probing the
> harness, writing the runner config, handing the service install to the human, and
> smoke-testing the machine. No MCP prompt is registered, so there is no `/landbridge-enroll`
> slash command to invoke it — the client reads the skill off `resources/list`. The
> control-plane **conformance run** — dispatching trivial tasks and judging the machine
> before it joins as `ready` — does not exist; nothing in the plane probes a new machine,
> and the skill's manual smoke test is its stand-in.

## The runner config

`landbridged` contains no harness knowledge — everything specific is data (spec §10).
`--config <path>` points at a JSON file with a `machine` section and one or more
`profiles` (no reserved `default`; `create_session` requires an exact name). A leftover
`services[]` block is refused — landbridged no longer supervises operator fixtures.
Session-scoped long work is `start_process`; something that must survive a restart
belongs to systemd or launchd. The full schema and a worked Claude Code profile live in
[`ideas/skills/references/runner-config.md`](../ideas/skills/references/runner-config.md).
The Aspire loop generates the same shape per box (see
[`src/Landbridge.AppHost/DevBoxConfig.cs`](../src/Landbridge.AppHost/DevBoxConfig.cs)).

`machine`: `work_root` (per-task scratch dirs — `landbridged` spawns each task in
`{work_root}/{session_id}`), `heartbeat_seconds`
(default 15s), and `back_pressure` thresholds (`max_cpu_load` / `max_memory_load`
/ `max_disk_usage` in `[0,1]`).

Each `profile` carries `spawn` (the ACP entry point), a required `prompt` (the
opening `session/prompt`), optional `follow_up`, `auth_method` (required if the agent
demands ACP authenticate; unset is a fail, not a guess),
optional `config_options` (ACP `session/set_config_option` pins; skipped unless
the agent advertised that `configId` and value), `stop.wind_down_seconds`,
`telemetry`, `logs`, and an optional `processes`
block. There is no `stdin`, `resume`, `events`, or `protocol` key.

- **stdin** is the JSON-RPC pipe. `landbridged` holds the write end for the task's
  whole life — ACP's shutdown rule and the §10 dead-man's switch are the same
  fact. There is no `closed` opt-out.
- **`processes`** — `agent_initiated` (default `false`) is the gate deciding whether
  tasks on this profile may call `start_process` at all, and `max` (default `8`) caps
  how many they may hold at once.

`landbridged` substitutes `{...}` tokens
into each `spawn` arg at dispatch: `{session_id}`, `{machine_id}`, `{work_dir}`
(`= {work_root}/{session_id}`, the cwd), and `{mcp_config}` (the path to the worker MCP config `landbridged`
writes to `{work_dir}/mcp.json`, mode `0600`). It also stamps `LANDBRIDGE_MACHINE_ID`,
`LANDBRIDGE_SESSION_ID`, and `LANDBRIDGE_WORKER_TOKEN` on the child.

### Pointing a worker at a real harness

The Aspire loop already does this. On a standalone `landbridged` (production, or
a box you enroll by hand) the same swap is config-only — no code change to
`landbridged` (spec §10). There is no reserved `default` profile. Abridged from
the worked example in the runner-config reference:

```json
"spawn": ["claude-agent-acp"],
"prompt": "You are a Landbridge worker on a live session. First call mcp__landbridge__get_inbox. When you think you are done, call mcp__landbridge__report_result and stay up; the Lead may reply. If blocked, call mcp__landbridge__request_input. You do not complete the session yourself.",
"follow_up": "There is new input on your assignment. Call mcp__landbridge__get_inbox to read it, then continue."
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

See [runner-config.md](../ideas/skills/references/runner-config.md) for the
worked profiles.

### Progress

Tool calls arrive as ACP `session/update` notifications. There is no
`events.source` or `events.mapping` to declare. `alive` is still emitted by
`landbridged`'s own heartbeat so a quiet process is not requeued for silence; the
no-progress ceiling is what requeues a worker that never emits a tool call.

## Running `landbridged` as a service

On a real machine `landbridged` must survive logout and reboot, which means the
platform's service manager. Two templates ship in [`deploy/`](../deploy):

| Template | Platform | Goes to |
|---|---|---|
| [`deploy/landbridged.service`](../deploy/landbridged.service) | Linux / systemd | `/etc/systemd/system/landbridged.service` |
| [`deploy/com.landbridge.landbridged.plist`](../deploy/com.landbridge.landbridged.plist) | macOS / launchd | `~/Library/LaunchAgents/` or `/Library/LaunchDaemons/` |

Both are heavily commented: every non-obvious setting says why it is there, and
the systemd unit ends with a list of hardening options that are deliberately
*absent* because they break a specific `landbridged` feature. Read the comments
before editing — `landbridged` spawns agent harnesses as its own children and is the
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
| Binary path | Where you published `landbridged` — `/opt/landbridged/landbridged` (Linux) or `/usr/local/libexec/landbridged/landbridged` (macOS) in the templates. |
| `--config` path | Your runner config, e.g. `/etc/landbridged/config.json`. Required; `landbridged` exits `2` without it. |
| `--state-dir` path | `/var/lib/landbridged` (Linux) or `/Users/<you>/.landbridge` (macOS). Must match the path you enrolled with. |
| `User=` / `Group=` (Linux) | The dedicated service account, created below. |
| `PATH` | The directories holding `claude`, `node`, `git`, `docker` — see the warning below. |
| `/Users/YOU` (macOS) | Your real home; launchd expands neither `~` nor `$HOME` in a plist. |

> **The `PATH` line is the one people get wrong.** `landbridged` copies its own
> environment onto every harness child at spawn, and both service managers hand a
> job a minimal `PATH` — systemd's compiled-in default, or launchd's
> `/usr/bin:/bin:/usr/sbin:/sbin` (no Homebrew, no `nvm`). If the harness binary
> is not on the `PATH` you set, the spawn fails with `ENOENT` and tasks fail on
> this machine only, which reads as a plane bug rather than a `PATH` bug.

Point `machine.work_root` at a path inside the state dir
(`/var/lib/landbridged/work`, as in the [runner-config
reference](../ideas/skills/references/runner-config.md)) so one directory covers
credentials, transcripts, and per-task scratch. `landbridged` creates each
`{work_root}/{session_id}` itself; the root needs to exist and be writable by the
service account.

### Linux (systemd)

```bash
# 1. A dedicated account with a REAL home — the harness reads ~/.claude,
#    ~/.gitconfig and ~/.ssh out of $HOME.
sudo useradd --system --create-home --home-dir /home/landbridged \
     --shell /usr/sbin/nologin landbridged

# 2. Publish the binary (AssemblyName is `landbridged`; add
#    -r linux-x64 --self-contained if the box has no .NET runtime).
sudo dotnet publish src/Landbridge.Runner -c Release -o /opt/landbridged

# 3. Config, owned by the service account.
sudo install -d -o landbridged -g landbridged -m 0750 /etc/landbridged
sudo install -o landbridged -g landbridged -m 0640 config.json /etc/landbridged/config.json

# 4. Enroll AS THE SERVICE USER, so credentials.json lands in the state dir it
#    will actually read (0600 in a 0700 dir).
sudo install -d -o landbridged -g landbridged -m 0700 /var/lib/landbridged
sudo -u landbridged /opt/landbridged/landbridged --enroll \
     --control-url https://plane.example.com \
     --enroll-token-file ./enroll.token \
     --state-dir /var/lib/landbridged

# 5. Install and enable.
sudo install -m 0644 deploy/landbridged.service /etc/systemd/system/landbridged.service
sudo systemd-analyze verify /etc/systemd/system/landbridged.service   # optional, catches typos
sudo systemctl daemon-reload
sudo systemctl enable --now landbridged
```

Verify:

```bash
systemctl status landbridged
journalctl -u landbridged -f
```

A healthy start logs one line naming the machine id, the declared profiles, the
stray count, and the control endpoint:

```
landbridged up: machine=<id> profiles=[default] strays_reaped=0 control=wss://plane.example.com/runner
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
sudo dotnet publish src/Landbridge.Runner -c Release -o /usr/local/libexec/landbridged

# Enroll first, into the same --state-dir the plist passes.
/usr/local/libexec/landbridged/landbridged --enroll \
  --control-url https://plane.example.com \
  --enroll-token-file ./enroll.token --state-dir ~/.landbridge

# LaunchAgent (runs as you, dies at logout):
cp deploy/com.landbridge.landbridged.plist ~/Library/LaunchAgents/
plutil -lint ~/Library/LaunchAgents/com.landbridge.landbridged.plist
launchctl bootstrap gui/$(id -u) ~/Library/LaunchAgents/com.landbridge.landbridged.plist

# LaunchDaemon (survives logout; add UserName/GroupName and a real HOME first):
sudo cp deploy/com.landbridge.landbridged.plist /Library/LaunchDaemons/
sudo chown root:wheel /Library/LaunchDaemons/com.landbridge.landbridged.plist
sudo chmod 644 /Library/LaunchDaemons/com.landbridge.landbridged.plist
sudo launchctl bootstrap system /Library/LaunchDaemons/com.landbridge.landbridged.plist
```

Verify, then restart or remove:

```bash
launchctl print gui/$(id -u)/com.landbridge.landbridged     # `system/…` for a daemon
tail -f ~/Library/Logs/landbridged.out.log              # the `landbridged up:` line
launchctl kickstart -k gui/$(id -u)/com.landbridge.landbridged
launchctl bootout gui/$(id -u)/com.landbridge.landbridged
```

launchd has no equivalent of systemd's `RestartPreventExitStatus`, so a bad
config exits `2` and respawns every 10 seconds forever. The reason is on the
first line of `landbridged.err.log` — read it there rather than wondering why the
machine never appears.

### Windows

**There is no template here, because there is nothing honest to put in one.**
`landbridged` is a plain console application with no Windows Service host
(`Microsoft.Extensions.Hosting.WindowsServices` is not referenced), so
`sc.exe create` pointed straight at the binary will not work: the process never
reports `SERVICE_RUNNING` to the Service Control Manager and the start fails with
error 1053. Windows also has no `SIGTERM`, so the SCM's stop request cannot reach
`landbridged`'s shutdown path.

Two pragmatic options, neither validated against a real Windows machine:

- **A service wrapper** — NSSM or WinSW hosts the console binary and speaks to
  the SCM on its behalf. Their default stop sequence sends a console `Ctrl+C`
  first, which .NET surfaces as `PosixSignal.SIGINT` — a signal `landbridged` *does*
  handle, giving the same ordered teardown as SIGTERM on Linux. Give the wrapper
  a stop timeout of at least 45 seconds, matching the other two templates.
- **A scheduled task** at startup, running as a dedicated account with "run
  whether the user is logged on or not". Simpler, no third-party dependency, and
  it gives up graceful shutdown entirely — the task is killed, not signalled.

Losing graceful shutdown costs less on Windows than it would elsewhere: each
worker is sealed at spawn into a kill-on-close Job Object, so `landbridged` dying by
any means — including `TerminateProcess` — makes the OS tear down the whole worker
tree. That is why the stray reaper's process inventory is empty by design on
Windows: there is nothing left to discover.

### What a restart does, and how to avoid it hurting

`landbridged` holds no state. **A restart kills every agent running on the machine
and their tasks requeue** — deliberate, not a fault. The templates lean into
this: `Restart=always` / `KeepAlive` bring the daemon straight back, and on start
it also kills any stray harness processes it finds, which is what makes the kill
guarantee survive an unclean shutdown.

A `SIGTERM` (`systemctl stop`, `launchctl bootout`) runs `landbridged`'s ordered
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
`__` (e.g. `Landbridge__PublicMcpUrl`). Every key below is grepped from the code on
this branch.

### Control plane / host (`Landbridge.Mcp`)

| Key | Default | Purpose |
|---|---|---|
| `ConnectionStrings:Landbridge` (or env `LANDBRIDGE_DB`) | `Host=localhost;Database=landbridge;Username=landbridge` | Postgres connection string. |
| `Landbridge:PublicMcpUrl` (or env `LANDBRIDGE_PUBLIC_MCP_URL`) | `http://127.0.0.1:5050` | The plane's public MCP endpoint for humans / OAuth 2.1 (canonical resource id / issuer). Set to the real public **https** URL in production. |
| `Landbridge:WorkerMcpUrl` (or env `LANDBRIDGE_WORKER_MCP_URL`) | *(same as PublicMcpUrl)* | URL stamped onto a worker at dispatch (`mcpServers` / `{mcp_url}`). The Aspire loop sets `http://host.docker.internal:5050` so Linux containers can reach the host plane. Unset in production. |
| `Landbridge:Classifier:Url` (or env `LANDBRIDGE_CLASSIFIER_URL`) | *(unset → Ask)* | Plane-side classifier sidecar (`POST /classify`). Unset or unreachable is Ask, never fail-open. Aspire sets `http://127.0.0.1:5310`. |
| `Landbridge:Classifier:TimeoutMs` | `45000` | How long the plane waits for one classify call (covers both LLM stages). Timeout is Ask. |
| `Landbridge:Operator:PassphraseHash` | *(empty → fail-closed)* | SHA-256 hex of the operator passphrase gating `/oauth/authorize` and dashboard login. Store the hash, never the plaintext. |
| `Landbridge:WaitTtl` | infinite | How long a `blocked_on_input` task waits before parking (spec §11). Off by default; a live ACP session is held until a Lead answers or `park_session`. Set a TimeSpan (e.g. `00:30:00`) to restore a timer. |
| `Landbridge:MachineLivenessTtl` | `00:01:30` | Heartbeat-age window past which a machine is treated as rebooted and its waiting tasks requeue (≈ six missed 15s heartbeats). |
| `Landbridge:WaitTtlSweepInterval` | `00:01:00` | How often the `WaitTtlSweeper` background loop runs. |
| `Landbridge:PerTaskLivenessWindow` | `00:01:00` | §10 clock one (**aliveness**): how long `landbridged` may go without asserting a task's harness process is alive before the task is requeued. `landbridged` asserts every heartbeat, so this is not gated on `events.source`. |
| `Landbridge:NoProgressCeiling` | `00:30:00` | §10 clock two (**progress**): how long an alive process may report no `tool-call` before it is treated as wedged. A task bearing a registered service (§8.2) is exempt from this one, never from the first. |
| `Landbridge:InfrastructureRequeueLimit` | `5` | §9 check 7: the infrastructure requeue cap stamped onto new tasks. Reaching it abandons the task as `canceled` (never `rejected`) with the workspace preserved. Non-positive means uncapped. |
| `Landbridge:PermissionPollIntervalMs` | `500` | How often `request_permission` re-reads the task row while a worker blocks on a §11 approval. One indexed primary-key read per tick, and only while a worker is genuinely blocked. |
| `Landbridge:RelayUrl` (or env `LANDBRIDGE_RELAY_URL`) | `http://127.0.0.1:5100` | The `landbridge-relay` URL the plane hands `landbridged` per `open_forward`, and the preview frontend per connect. Config wins, then the env var, then this default. |
| `Landbridge:PreviewUrlBase` | `http://preview.localhost` | The wildcard base §8.4 preview URLs are built from — the opaque label becomes its leftmost subdomain. Set to your real wildcard host (`https://preview.example.com`) in production. |
| `Landbridge:PreviewConnect:Bearer` | *(unset → 503)* | Shared bearer the preview frontend must present to `POST /preview/connect`. Fail-closed when unset, like `Landbridge:RelayValidation:Bearer`. |
| `Landbridge:RelayValidation:Bearer` | *(unset → 503)* | Shared bearer the relay must present to `POST /relay/validate`. Fail-closed when unset. |
| `Landbridge:Oauth:AllowInsecureClientMetadata` | `false` | DEV/TEST ONLY. Disables the CIMD SSRF address fence (accepts `http` `client_id` URLs and hosts resolving to private, loopback, or link-local addresses). Never enable in production. |
| `Landbridge:MigrateOnStartup` | `false` | Apply the checked-in EF migration on boot. Set by the dev loop; production migrates out of band. |
| `Landbridge:DevSeed:TokenDir` | *(unset)* | Dev-loop only: enroll the Codex/Claude/Grok linux boxes and write each seed file here. Never set in production. |
| env `OTEL_EXPORTER_OTLP_ENDPOINT` | *(unset)* | When set, the host exports OpenTelemetry via OTLP (the Aspire dashboard sets this in the dev loop). |

### Permission classifier (`Landbridge.Classifier`)

| Key | Default | Purpose |
|---|---|---|
| `Classifier:Fast:Model` (env `LANDBRIDGE_CLASSIFIER_FAST_MODEL` or `Classifier__Fast__Model`) | `openai/gpt-5-nano` | Stage-1 slug, `provider/model` (`anthropic/haiku`, `openai/gpt-5-nano`, `xai/grok-4`). A bare name is OpenAI. |
| `Classifier:Review:Model` (env `LANDBRIDGE_CLASSIFIER_REVIEW_MODEL`) | `openai/gpt-5-nano` | Stage-2 slug, same syntax. |
| `LANDBRIDGE_CLASSIFIER_MODEL` | *(unset)* | Fallback slug for both stages when Fast/Review are unset. |
| `Classifier:Fast:Prompt` / `Classifier:Review:Prompt` | *(file / compiled fallback)* | Full system prompt for that stage. Env `LANDBRIDGE_CLASSIFIER_FAST_PROMPT` / `_REVIEW_PROMPT`. |
| `Classifier:Fast:PromptFile` / `Classifier:Review:PromptFile` | `prompts/fast.txt` / `prompts/review.txt` | Path to a prompt template, relative to the classifier content root. Ignored when Prompt is set. |
| `Classifier:LiteLlm:Url` (env `LANDBRIDGE_CLASSIFIER_LITELLM_URL`) | *(required)* | OpenAI-compatible LiteLLM base (`http://127.0.0.1:4000/v1` in Aspire). |
| `Classifier:LiteLlm:ApiKey` (env `LANDBRIDGE_CLASSIFIER_LITELLM_KEY`) | *(required)* | LiteLLM master key. Aspire mints one per run. |
| `OPENAI_API_KEY` / `ANTHROPIC_API_KEY` / `XAI_API_KEY` | *(on the `litellm` resource)* | Provider keys LiteLLM uses to fulfill `openai/*` / `anthropic/*` / `xai/*` slugs. |

### Relay (`Landbridge.Relay`)

| Key | Default | Purpose |
|---|---|---|
| `Relay:ControlPlane:Url` | *(unset)* | When set, activates the real `ControlPlaneGrantValidator`, which validates each grant against `{Url}/relay/validate`. When unset, the fail-closed `StaticSecretGrantValidator` is used instead. |
| `Relay:ControlPlane:Bearer` | *(unset)* | Bearer presented to the plane's `/relay/validate`. Must match the plane's `Landbridge:RelayValidation:Bearer`. |
| `Relay:ControlPlane:Timeout` | `00:00:05` | Validation call timeout; a timeout refuses the tunnel (fail-closed). |
| `Relay:Grant:AllowAll` | `false` | DEV/SMOKE ONLY. Accept every grant (logs a loud warning). |
| `Relay:Grant:SharedSecret` | *(null)* | Static shared-secret grant for the stub validator. With neither this nor `AllowAll`, the static validator refuses everything. |
| `Relay:PairWaitTimeout` | `00:00:30` | How long a tunnel waits for its opposite end to arrive before giving up. |

The dev loop sets `Relay:ControlPlane:Url` and a freshly-minted shared
`Relay:ControlPlane:Bearer` on both `mcp` and `relay`, so the real
control-plane validator is active — not the static stub.

### Runner (`landbridged`)

Flags: `--config <path>` (required for a normal run), `--machine-id <id>`,
`--state-dir <dir>`; and for enrollment `--enroll --control-url <url>`
`[--enroll-token-file <path>]` `[--name <n>]` `[--purpose <p>]`
`[--permission-level <l>]`.

| Env var | Purpose |
|---|---|
| `LANDBRIDGE_CONTROL_URL` | The `ws(s)://…/runner` URL to dial. In the dev loop the AppHost sets it; with file credentials it is derived from the saved control URL. |
| `LANDBRIDGE_MACHINE_TOKEN` | A fixed machine bearer (dev-loop path — never refreshed). When unset, `landbridged` loads persisted credentials from the state dir and refreshes them. |
| `LANDBRIDGE_MACHINE_ID` | Machine id (else `--machine-id`, else a random id). |
| `LANDBRIDGE_STATE_DIR` / `XDG_STATE_HOME` | State-dir resolution (see enrollment). |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | Enables OTLP export from the runner when set. |

### Closing a session

There is no verifier process. A worker mails `report_result`; the session stays
occupied. A Lead (or a human) closes it with `stop_session` over MCP (§7, §9
check 4) when they are done with that worker. Default wind-down is 5 minutes,
then a kill. A session's own worker can never close it.

## Running the tests

See the [Tests section of the README](../README.md#tests). In short: `dotnet
build -c Release` gates on warnings (`TreatWarningsAsErrors`), then run each
suite with `dotnet test … --no-build -c Release`. The Postgres-backed suites
(`ControlPlane`, `Mcp`, `Meta`, `MultiMachine`, `Chaos`) honor `LANDBRIDGE_TEST_PG`; when
it is set they use that server instead of spinning a local cluster, and each gets its
**own database** on it so no suite's reset truncates another's fixtures. CI splits into
`ci.yml` (ubuntu + Postgres: the build-and-test matrix, the chaos job, and the two
opt-in real-harness tiers), `os-matrix.yml` (the platform-sensitive suites on
ubuntu/macOS/Windows), and `publish-images.yml` (GHCR runtime images on a `v*` tag).

Paid real-harness e2e (`Category=RealClaude` / `RealCodex` / `RealOpenCode` /
`RealGrok` / `RealGoose`) reads API keys from the environment. Locally, put
them in user secrets on the MultiMachine test project — they are loaded at
assembly start and published into the process so spawned CLIs inherit them.
Process env (including CI job secrets) is not overwritten. Goose defaults to
`GOOSE_PROVIDER=anthropic` / `GOOSE_MODEL=claude-haiku-4-5-20251001` and
opts in on the same Anthropic key as Claude; `LANDBRIDGE_REAL_GOOSE=1` is
the already-configured-CLI path (same role as `LANDBRIDGE_REAL_CLAUDE`).
The dispatch cell runs both the direct `goose acp` bar and the ACP-bridge facts.

```bash
dotnet user-secrets set ANTHROPIC_API_KEY      '…' --project tests/Landbridge.MultiMachine.Tests
dotnet user-secrets set CODEX_API_KEY          '…' --project tests/Landbridge.MultiMachine.Tests
dotnet user-secrets set XAI_API_KEY            '…' --project tests/Landbridge.MultiMachine.Tests
```

The Aspire loop loads this same secrets id, so one store feeds both the paid
e2e and the local fleet. `ANTHROPIC_KEY`, `OPENAI_KEY` / `OPENAI_API_KEY`, and
`XAI_KEY` are accepted and aliased to the names the CLIs actually read.
