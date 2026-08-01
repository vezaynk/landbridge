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
| `Docket.AppHost` | .NET Aspire orchestrator for the **local dev loop** — brings the whole system up with one command. Dev-time only; not a production path. |
| `Docket.ServiceDefaults` | Shared Aspire wiring: OpenTelemetry (traces/metrics/logs), health checks, service discovery, HTTP resilience. |

`docket-meta` (the provisioning service, spec §3) is **not built** — it is a
future component with no code on this branch.

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
- **`docket-relay`** at `http://127.0.0.1:5100`.

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

See **[docs/RUNNING.md](docs/RUNNING.md)** for the operator/developer guide and
**[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)** for how the pieces fit.

## Tests

```bash
dotnet build -c Release          # TreatWarningsAsErrors is on; this gates on warnings too
dotnet test tests/Docket.Core.Tests --no-build -c Release          # pure engine
dotnet test tests/Docket.Runner.Tests --no-build -c Release        # process supervision, per-OS reap
# ...and so on per project
```

CI runs the suites in **two workflows** because they have different needs:

- **`.github/workflows/ci.yml`** (ubuntu only) builds the solution and runs every
  suite, including the two that need Postgres — `Docket.ControlPlane.Tests` and
  `Docket.Mcp.Tests` — against a `postgres:16` service container. GitHub-hosted
  service containers are Linux-only, and these suites are not platform-sensitive,
  so they live here. Each Postgres suite gets its own database
  (`docket_cp`, `docket_mcp`) via `DOCKET_TEST_PG`.
- **`.github/workflows/os-matrix.yml`** runs the platform-sensitive suites — Core,
  Contracts, Runner, Relay — on **ubuntu, macOS, and Windows**. The point is the
  process-supervision / stray-cleanup machinery, whose behavior is per-OS
  (ProcFs on Linux, `KERN_PROCARGS2` on macOS, kill-on-close Job Objects on
  Windows). Tests gate themselves with `SkippableFact`, so each leg runs what its
  OS supports and skips the rest — a skip is a documented deferral, a failure is
  a real regression.

## Status

Implemented and exercised end-to-end (scripted worker, no LLM): the task state
machine and the fourteen §9 checks (check 4 the doer/judge split: a Lead or human
adjudicates, never the task's own worker); Postgres store with `SKIP LOCKED`
dispatch and `LISTEN/NOTIFY` push; opaque-token auth across all four credential
classes;
OAuth 2.1 authorization-code + PKCE (S256) + Client ID Metadata Documents;
machine enrollment and token refresh; `docketd` process supervision, stop/kill,
heartbeats, terminal-source tool-call events, and per-OS stray cleanup; §11
park → resume, where a redispatch continues the parked harness transcript via
the profile's resume argv (the harness session ref round-trips through the
store); the relay TCP splice with fail-closed control-plane grant validation;
Lead-adjudicated completion (`submit_review`, `lead`/`review` modes, §9 check 4);
and the plain web dashboard.

Deliberately deferred or in progress on this branch — do not assume these work:

- **Budget accounting / byte metering** (spec §9.9, §12): the task's budget is
  handed to the harness at dispatch as the `{budget}` substitution (the intended
  hard-cap backstop), but token attribution and the per-Team byte counter/rate
  limit are not tracked — the dashboard shows them as empty states.
- **Event sources beyond `terminal`/`none`**: the `hooks` and `otel` event
  sources are defined in config but not wired; the subagent tree, `alive`, and
  `auth-failed` events are not yet produced.
- **`docket-meta`**, the relay **HTTP layer** (subdomain-per-service; TCP is the
  primitive), the enrollment **conformance run** and slash-command prompts, and
  cross-restart persistence of runner connections are **future work**.

`ideas/spec.md` remains the authoritative design. Where this README or the code
and the spec disagree, the code is what runs today and the spec is where it is
going.
