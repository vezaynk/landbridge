# Docket

Docket coordinates AI coding agents across multiple machines. A human drives a
*Lead* agent that decomposes work into tasks; *worker* agents on other machines
are dispatched those tasks and execute them. The control plane keeps the record
and enforces procedure — it never reads the work.

Docket is the **communication, runner, and relay** layer, not a model provider.
It ships no models, resells no inference, and holds no provider credentials:
customers bring their own harness (Claude Code or any MCP-capable agent CLI) and
their own keys, which live on their own machines. The control plane is
deliberately thin in *logic* — it validates that fields exist, checks
identities, counts, and enforces state transitions — while being substantial in
*role*: `docket-relay` carries real service traffic between customer machines.

The task schema is domain-neutral. Docket knows a task has a completion mode and
a workspace, and nothing about what either contains. Coding is the primary use
case, but repositories, branches, and test suites appear only in the shipped
skill guidance, never in the data model.

> **Status: pre-alpha.** The spec (`ideas/spec.md`) is the source of truth and is
> ahead of the code in places. This README and the docs under `docs/` describe
> **what is actually implemented on this branch** and flag what is deferred. See
> [Status](#status) below and the honesty notes throughout `docs/`.

## Module map

`docket.slnx` groups the code under `src/` (shipping), `tests/`, and `spikes/`.

| Project | Role |
|---|---|
| `Docket.Core` | The pure task **state machine** and enforcement rules (spec §6, §9). No clock, no I/O, no Postgres, no ASP.NET — transitions are a function of a task record plus a command, returning a new record plus effects-as-data. |
| `Docket.Contracts` | The **frozen** control-plane ↔ runner wire vocabulary (spec §10): the closed set of commands and events, and the `RunnerWire` JSON codec. The one interface that must never break. |
| `Docket.ControlPlane` | The control-plane library: EF Core/Postgres store and dispatch (`SKIP LOCKED` + `LISTEN/NOTIFY`), opaque-token auth (`TokenService`), OAuth 2.1 authorization-server services, relay grants, the `DispatchService` and `WaitTtlSweeper` background loops, and the dashboard read models (`DashboardQueries`). |
| `Docket.Mcp` | The ASP.NET **host** (`docket` + `docket-mcp`): the MCP tool surface agents connect to, the `/runner` WebSocket, OAuth/enrollment/relay-validate HTTP endpoints, and the §12 web dashboard. One process, one Postgres, one Instance. |
| `Docket.Runner` | `docketd`, the per-machine **runner daemon**: process supervision, machine-credential enroll/refresh, heartbeat and event relay, stray-process cleanup, and the relay data planes. Config-driven; contains no harness knowledge. Outbound connections only. |
| `Docket.Relay` | `docket-relay`, a standalone authenticated **byte-splice relay** (spec §8.3). Pairs two tunnels by forward id and moves opaque bytes; validates grants against the control plane. Separately deployable. |
| `Docket.Preview` | The §8.4 **HTTP preview frontend**: wildcard TLS, Host-header routing to an opaque label, and a byte-splice through the *unchanged* relay so cookies and absolute paths are never rewritten. A separate module on top of the TCP primitive, not a change to it. |
| `Docket.Meta` | `docket-meta`, the human-only **provisioning control panel** (spec §3): a resumable saga that stands up an Instance — network, Postgres, `docket-mcp`, `docket-relay` — across a pool of Docker hosts, over its own Postgres. Structurally not an MCP server; no agent access. |
| `Docket.AppHost` | .NET Aspire orchestrator for the **local dev loop** — brings the whole system up with one command. Dev-time only; not a production path. |
| `Docket.ServiceDefaults` | Shared Aspire wiring: OpenTelemetry (traces/metrics/logs), health checks, service discovery, HTTP resilience. |

## Quickstart

Prerequisites: the **.NET 10 SDK** and **Docker** (Aspire runs Postgres in a
container).

```bash
dotnet run --project src/Docket.AppHost
```

One command brings up the full Lead → plane → runner → worker loop:

- a managed **Postgres** container (persistent volume, so data survives restarts),
- the **control plane / MCP host** (`Docket.Mcp`) at `http://127.0.0.1:5000`, migrated and dev-seeded,
- a real **`docketd`** runner, enrolled via a dev-seeded machine token and connected back to `/runner`,
- **`docket-relay`** at `http://127.0.0.1:5100`,
- the **preview frontend** (`Docket.Preview`), plaintext in the loop — minting a URL needs `open_preview` or the dashboard, so it idles until you use it.

`docket-meta` is **not** in the dev loop: it provisions whole Instances and runs
standalone with its own Postgres. See **[docs/META.md](docs/META.md)**.

Completion is Lead-adjudicated (spec §7, §9 check 4): a task reaches `verifying`, and a Lead completes it with `submit_review` — there is no verifier process in the loop.

Two dashboards:

- The **Aspire dashboard** (URL printed on the console) shows every resource,
  its logs, and the host's OpenTelemetry traces/metrics.
- The **Docket web dashboard** (spec §12) is served by the host at
  `http://127.0.0.1:5000/dashboard` — Machine Group, Team, inbox, and event-log
  views. It requires an operator passphrase (`Docket:Operator:PassphraseHash`);
  see `docs/RUNNING.md`.

The dev loop stands up a *standing fleet*: it does **not** auto-create a task. A
human Lead creates work over MCP, exactly as in production. The dispatched
worker is a scripted, no-LLM harness (`Docket.WorkerHarness`) that exercises the
full protocol; swapping in a real `claude -p` is a config-only change documented
in `docs/RUNNING.md`.

Guides: **[docs/RUNNING.md](docs/RUNNING.md)** (operator/developer, config
reference, running `docketd` as a service),
**[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)** (how the pieces fit),
**[docs/META.md](docs/META.md)** (provisioning Instances, images, secret-key
rotation), **[docs/PREVIEW-TLS.md](docs/PREVIEW-TLS.md)** (wildcard certs via
lego), **[docs/TELEMETRY.md](docs/TELEMETRY.md)** (seeing harness token/cost
usage). Runner config lives in
[`ideas/skills/references/runner-config.md`](ideas/skills/references/runner-config.md)
— read its **Event relay** section before writing a profile.

## Tests

```bash
dotnet build -c Release          # TreatWarningsAsErrors is on; this gates on warnings too
dotnet test tests/Docket.Core.Tests --no-build -c Release          # pure engine
dotnet test tests/Docket.Runner.Tests --no-build -c Release        # process supervision, per-OS reap
# ...and so on per project
```

CI runs in **three workflows** because they have different needs:

- **`.github/workflows/ci.yml`** (ubuntu only) builds the solution and runs the
  suites needing Postgres — `ControlPlane`, `Mcp`, `Meta`, `MultiMachine` — against
  a `postgres:16` service container, each with its own database via `DOCKET_TEST_PG`
  (`docket_cp`, `docket_mcp`, `docket_meta`, `docket_multimachine`). GitHub-hosted
  service containers are Linux-only. `Meta`'s Docker-gated E2E genuinely runs here:
  it publishes both images, provisions a real Instance, hits its health endpoint,
  and destroys it. This workflow also carries the **opt-in `real-claude-e2e` job**,
  gated to `workflow_dispatch` so a real `claude -p` fleet run (own database,
  `ANTHROPIC_KEY` → `ANTHROPIC_API_KEY`) never spends tokens on an ordinary push.
- **`.github/workflows/os-matrix.yml`** runs the platform-sensitive suites — Core,
  Contracts, Runner, Relay, Preview — on **ubuntu, macOS, and Windows**. The point
  is machinery whose behavior is genuinely per-OS: process supervision and stray
  cleanup (ProcFs on Linux, `KERN_PROCARGS2` on macOS, kill-on-close Job Objects on
  Windows), the CPU-load readers, and Preview's raw `SslStream` sockets. Tests gate
  themselves with `SkippableFact`, so each leg runs what its OS supports — a skip is
  a documented deferral, a failure is a real regression.
- **`.github/workflows/publish-images.yml`** builds and pushes the `docket-mcp` and
  `docket-relay` runtime images to GHCR (linux/amd64 + linux/arm64), on a pushed
  `v*` tag or manual dispatch — never on a push to master, because `docket-meta`
  pins an immutable tag per Instance. See [docs/META.md](docs/META.md).

Two things worth knowing when a matrix goes red. Suites that are individually green
can still break `master` **together** — a DI registration or shared fixture added by
one PR can be required by another — so after merging a batch that touches shared
composition, build and test `master` itself; GitHub's mergeability check is textual
only. And a matrix leg can wedge (a runner that hangs for its full timeout) — check
whether a *fresh* run on a *new* runner reproduces before treating it as a defect.

## Status

**The core loop is proven with real agents, not only scripted ones.** A CI job
dispatches two actual `claude -p` workers to two machines and has them complete a
task and hand a result between them, through real dispatch, MCP, and the relay.

Implemented: the task state machine and the §9 checks (check 4 is the doer/judge
split — a Lead or human adjudicates, never the task's own worker); Postgres store
with `SKIP LOCKED` dispatch and `LISTEN/NOTIFY` push; opaque-token auth across the
four credential classes; OAuth 2.1 authorization-code + PKCE (S256) + Client ID
Metadata Documents; machine enrollment and refresh; `docketd` supervision,
stop/kill with graceful wind-down, heartbeats, terminal-source tool-call events,
real per-OS CPU-load back-pressure, and stray cleanup; **two-clock per-task
liveness** (aliveness vs no-progress, so a ten-minute build is no longer mistaken
for a hang); §11 park → resume; **continuation tasks** (`continues:`) that resume a
prior task's transcript under a fresh token; **in-band worker reports** and the
**question/answer exchange** that makes blocking on a human actually carry words;
Lead-adjudicated completion (`lead`/`review` modes); **budget accounting** as a
ceiling on committed authorization plus an enforced forward rate limit; the relay
TCP splice with fail-closed grant validation; **`open_forward`, the Lead-facing
forward** for reaching a service from your own machine, and the **§8.4 preview
layer** (wildcard TLS, opaque labels, gated or public); **transcript capture and
on-demand serving**; **harness telemetry attribution**; **`docket-meta`** with
encrypted at-rest secrets; and the §12 web dashboard.

Deliberately deferred — do not assume these work:

- **Transcript redaction** (spec §16 open question 8). Transcripts are served
  **verbatim**, which is why serving is narrowed instead: human operator sessions
  only, and only for terminal tasks. This gates live tailing, reading a `verifying`
  task's transcript, and any agent-facing read.
- **Measured spend.** Nothing ingests token/cost telemetry, so the budget ceiling
  enforces *authorized* spend (the per-dispatch cap, committed at dispatch), never
  measured spend. Relay bytes are counted and reported but enforce nothing, because
  §8.3 forbids severing an established splice.
- **Event sources beyond `terminal`.** `hooks` and `otel` parse but are wired to
  nothing, so they behave as `none`; `docketd` warns loudly at startup for any
  profile declaring one. The subagent tree and `auth-failed` have no producer.
- **No cap on infrastructure requeues**, and `LivenessLossReason` isn't persisted,
  so a wedged task retries indefinitely and every requeue looks alike in the record.
- **SIGTERM does not wind workers down.** A signal hard-kills them; only a `stop`
  *from the plane* is graceful — so **drain a machine before restarting its service**.
- The enrollment **conformance run** and `/docket-enroll` wizard do not exist; the
  enroll skill carries a manual smoke test instead. Per-task OS isolation is
  deferred (§13): co-tenant tasks on a machine can reach each other's loopback.

Open decisions live in `ideas/spec.md` §16 and in the issue tracker.
`ideas/spec.md` remains authoritative for design; where it and the code disagree,
the code is what runs today — and the spec's "as-built reconciliation" notes record
where a claim was corrected rather than quietly left standing.

`ideas/spec.md` remains the authoritative design. Where this README or the code
and the spec disagree, the code is what runs today and the spec is where it is
going.
