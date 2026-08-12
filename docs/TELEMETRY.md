# Harness telemetry: what a worker's tokens actually cost

Operator guide to the `telemetry` block in a runner profile (spec §10 telemetry
ingest). Turning it on makes each worker export **its own** token, cost, and
activity telemetry to **your** OTLP collector, with the Docket task that caused
the spend stamped on every metric and event.

> **Visibility, not enforcement.** Nothing in Docket ingests, meters, or caps any
> of this: **the control plane never sees a token count.** Docket does not sit between
> the harness and the model provider (§10), and the machine is the customer's, so these
> are the harness's own self-reported numbers going to your own collector: useful,
> attributable, and best-effort by construction.
>
> **There is no spend limit anywhere in Docket.** A Team dollar ceiling existed until
> 2026-08-12 and was removed (spec §9's note keeps its design); the `{budget}`
> substitution and the `--max-budget-usd` it filled went with it, because their value
> came from that ceiling. What bounds a runaway now is time (the §10 no-progress
> ceiling), attempts (the §9 check 7 requeue cap), whatever caps you write into a
> profile's own argv, and an operator reading numbers like these. Nothing on this page
> is enforced on — by design, since a figure a harness self-reports can be switched off
> or reported wrong.

## Turning it on

Per profile, in docketd's config, off by default:

```json
{
  "name": "default",
  "spawn": ["/usr/local/bin/claude", "-p", "...", "--mcp-config", "{mcp_config}"],
  "telemetry": {
    "otel": true,
    "endpoint": "http://127.0.0.1:4318",
    "env": { "CLAUDE_CODE_ENABLE_TELEMETRY": "1" }
  }
}
```

That is the whole feature. `otel` is the opt-in, `endpoint` is where OTLP goes,
and `env` carries whatever variables *this harness* needs to start exporting.

**Why the harness flag lives in config.** docketd contains no harness knowledge
(§10) — supporting a new harness is a config file, never a code change. So
docketd sets only vendor-neutral OTel SDK variables and takes the harness's own
opt-in as data. For Claude Code that is `CLAUDE_CODE_ENABLE_TELEMETRY=1`; without
it, Claude Code exports nothing no matter what else is set.

### What docketd sets on the spawn

When `otel` is true **and** a destination resolves:

| Variable | Value |
|---|---|
| `OTEL_EXPORTER_OTLP_ENDPOINT` | `telemetry.endpoint`, else the one docketd inherited |
| `OTEL_EXPORTER_OTLP_PROTOCOL` | only when nothing upstream named one — `grpc` for a `:4317` endpoint, `http/protobuf` otherwise |
| `OTEL_METRICS_EXPORTER` | `otlp` |
| `OTEL_LOGS_EXPORTER` | `otlp` |
| `OTEL_RESOURCE_ATTRIBUTES` | whatever was already there, plus `docket.task_id=<task>,docket.machine_id=<machine>` |
| everything in `telemetry.env` | verbatim |

Then `telemetry.env` is applied over those defaults, so you can override any of
them — a gRPC-only collector, events without metrics, a shorter export interval.
The one thing it cannot override is attribution: an `OTEL_RESOURCE_ATTRIBUTES`
you set becomes the *base* that `docket.task_id` is appended to, never a
replacement for it.

**Everything else you don't see here arrives by inheritance.** A worker is
spawned with `UseShellExecute=false`, so it gets a copy of docketd's whole
environment. Headers, TLS material, compression, export intervals, the tracing
beta — set them once on docketd and every worker has them. That is also why
`endpoint` is optional: a docketd that already exports its own traces to a
collector has a destination, and a profile need only opt in.

### Two rules worth knowing

- **`otel: false` (the default) sets nothing at all.** Not the exporters, not the
  attribution, not `telemetry.env`. This is your data going to your collector, so
  it is opt-in per profile rather than something a Docket upgrade turns on.
- **Telemetry is never enabled without a destination.** `otel: true` with no
  `endpoint` and none inherited sets *nothing* and logs one warning per profile
  to docketd's stderr. An exporter aimed at nowhere costs a worker retry loops
  against a dead socket and buys no visibility.

## The dev loop: the Aspire dashboard is already a collector

`src/Docket.AppHost` runs an Aspire dashboard that is a real OTLP receiver, so
the inner loop needs no extra infrastructure — harness token and cost metrics
land beside the plane's own traces and logs.

The dashboard's OTLP endpoint is fixed in
`src/Docket.AppHost/Properties/launchSettings.json`:

| Launch profile | Dashboard UI | OTLP endpoint (gRPC) |
|---|---|---|
| `http` | `http://localhost:15183` | `http://localhost:19015` |
| `https` | `https://localhost:17067` | `https://localhost:21104` |

docketd runs as an Aspire resource, so Aspire hands it the OTLP endpoint (and,
where the dashboard requires one, the API-key header) in its environment — which
a worker inherits. In the dev loop a profile therefore usually needs no
`endpoint` at all:

```json
"telemetry": {
  "otel": true,
  "env": {
    "CLAUDE_CODE_ENABLE_TELEMETRY": "1",
    "OTEL_EXPORTER_OTLP_PROTOCOL": "grpc",
    "OTEL_METRIC_EXPORT_INTERVAL": "5000"
  }
}
```

`grpc` is spelled out because that endpoint is gRPC and its port looks like
nothing in particular, so docketd's port-based fallback would otherwise guess
HTTP. The short export interval matters more than it looks: the default is 60s
and a worker that finishes a small task in 20s may exit before its first export
window. If a dashboard shows a worker's spans but no `claude_code.*` metrics,
suspect the interval and the API-key header, in that order.

Swap the dev profile's `spawn` for a real `claude -p` (see the comments in
`src/Docket.AppHost/docketd.dev.json`) and the metrics are real too.

## Production: point it at your own collector

Set `endpoint` to your collector, or set `OTEL_EXPORTER_OTLP_ENDPOINT` once on
docketd and let every profile inherit it. Under the shipped service templates
that is `/etc/docketd/docketd.env` (`EnvironmentFile` in `deploy/docketd.service`,
which already names this variable as typical contents) or the
`EnvironmentVariables` dict in `deploy/com.docket.docketd.plist` — a machine-wide
destination that every profile can then opt into with `otel` alone.

Docket receives none of it and needs no
network path to it: the worker talks to your collector directly, and nothing
about that traffic passes through the control plane. There is no OTLP receiver in
Docket and no token or cost field in its schema, so a §12 dashboard that shows what
work cost is still ahead of this document (§10's telemetry-ingest section states the
two candidate shapes and that the choice is open).

A `docker run` collector with a `debug` exporter is enough to see what a worker
emits before you wire it anywhere permanent.

## What Claude Code actually emits

Verified against the official telemetry documentation and the shipped Claude Code
binary (2.1.220). All of it requires `CLAUDE_CODE_ENABLE_TELEMETRY=1`.

**Metrics** (`OTEL_METRICS_EXPORTER=otlp`):

| Metric | Unit | Attributes beyond the standard set |
|---|---|---|
| `claude_code.token.usage` | tokens | `type` (`input`, `output`, `cacheRead`, `cacheCreation`), `model` |
| `claude_code.cost.usage` | USD | `model`, `query_source` (`main`, `subagent`, `auxiliary`) |
| `claude_code.session.count` | — | — |
| `claude_code.active_time.total` | s | `type` (`user`, `cli`) |
| `claude_code.lines_of_code.count` | — | `type` (`added`, `removed`), `model` |
| `claude_code.commit.count` | — | — |
| `claude_code.pull_request.count` | — | — |
| `claude_code.code_edit_tool.decision` | — | `tool_name`, `decision`, `source`, `language` |

`claude_code.token.usage` and `claude_code.cost.usage` are the two that answer
"what did this task cost". Cache reads and cache creation are separate `type`
values, so a cache-heavy worker's real spend is visible rather than averaged
away.

Every metric also carries a standard set — `session.id`, `user.id`,
`terminal.type`, the account/organization ids, and your custom resource
attributes — each gated by an `OTEL_METRICS_INCLUDE_*` variable you can set in
`telemetry.env` to trade cardinality for granularity.
`OTEL_METRICS_INCLUDE_RESOURCE_ATTRIBUTES` defaults to on, which is what puts
`docket.task_id` on each datapoint and not only in the OTLP resource block.

**Events** (`OTEL_LOGS_EXPORTER=otlp`) are OTel log records, and are where
per-request detail lives. `claude_code.api_request` carries `model`,
`input_tokens`, `output_tokens`, `cache_read_tokens`, `cache_creation_tokens`,
`cost_usd`, `duration_ms`, and `request_id`; there are also `api_error`,
`api_refusal`, `tool_result`, `tool_decision`, `user_prompt`,
`assistant_response`, and a `prompt.id` that correlates everything from one
prompt across the API calls and tool results it caused. Prompts, responses, and
tool parameters are **redacted by default** and only included if you opt in
(`OTEL_LOG_USER_PROMPTS`, `OTEL_LOG_ASSISTANT_RESPONSES`, `OTEL_LOG_TOOL_DETAILS`)
— worth thinking about twice, since a worker's prompt contains the task
description.

**Traces** are a separate beta (`CLAUDE_CODE_ENHANCED_TELEMETRY_BETA=1` plus
`OTEL_TRACES_EXPORTER=otlp`) and are the only place per-subagent lineage appears
(§10 treats the subagent tree as progressive enhancement for exactly this
reason). Both variables can go in `telemetry.env`, or on docketd for the whole
machine.

### Other harnesses may emit none of this

Everything in this section is Claude Code's. The names are `claude_code.*`, and all of
them are gated on `CLAUDE_CODE_ENABLE_TELEMETRY=1` — which docketd carries as
`telemetry.env` data precisely because it holds no harness knowledge (§10). A second
harness has its own metric names, its own opt-in variable, or no OTLP export at all, and
none of that is something a profile can configure into existence.

Codex is the worked case: its published documentation describes no OTLP telemetry and no
equivalent enable flag, so `telemetry: { "otel": true }` on a Codex profile sets the
vendor-neutral `OTEL_*` variables and appends `docket.task_id`, on a process that may
ignore all of it. That is the "a harness that exports nothing is normal, not broken" case
in [Caveats](#caveats) rather than a wiring bug — so before reading an empty dashboard as
one, check what your harness documents it emits, and under which variable. The table
above is not a contract any harness signed.

## How attribution works

§10: *"Token attribution must carry a task id"* — otherwise a machine running
several tasks at once produces one undifferentiated pile of spend.

`docket.task_id` is that id. It rides `OTEL_RESOURCE_ATTRIBUTES`, so it lands on
every metric datapoint and every event the harness emits, and grouping by it in
your collector gives spend per Docket task — on a machine running several tasks
at once, in whatever mix of profiles.

`docket.machine_id` rides along too, because one Team's tasks span machines and
one machine serves many Teams.

**Task → Team is the plane's half, and it is not in this data.** A profile has no
Team, and `dispatch` carries no Team id, so docketd cannot stamp one. The control
plane owns that mapping; joining spend to a Team means joining on task id against
the plane's own records. Adding a Team id here would mean a new wire field, and
there is nothing built that would read it yet.

The task id also reaches a worker as `DOCKET_TASK_ID` (§10, on every spawn,
telemetry or not). Same id, different consumers: the environment variable is for
hooks and stray-process cleanup, the resource attribute is for your collector.

## Caveats

- **Cost numbers are estimates.** Claude Code's own documentation says so; use
  your provider's billing for anything that has to reconcile.
- **Managed settings can override the endpoint.** Claude Code gives
  managed-settings environment variables the highest precedence and drops
  conflicting developer-set `OTEL_EXPORTER_OTLP_*` values at startup. On a
  corporate machine whose MDM already points Claude Code at a company collector,
  a profile's `endpoint` will **not** redirect it — the telemetry goes where the
  policy says, and `docket.task_id` rides along there instead. Verified on a
  managed macOS machine: a worker spawned with a local `endpoint` sent nothing to
  the local collector. Check
  `/Library/Application Support/ClaudeCode/managed-settings.json` before
  concluding the wiring is broken.
- **A harness that exports nothing is normal, not broken** (§10). Profiles
  without an OTel-emitting harness render as "not reported"; nothing degrades.
- **Cardinality is your collector's problem.** `docket.task_id` is unbounded over
  time — one value per task, forever. That is the point for attribution and a
  real cost in a long-lived metrics store, so aggregate and expire on your side.
  `OTEL_METRICS_INCLUDE_RESOURCE_ATTRIBUTES=false` keeps the id in the resource
  block only, if you want it off the datapoint labels.
- **`OTEL_*` reaches the harness, not the harness's children.** Claude Code does
  not pass its `OTEL_*` variables down to the subprocesses it spawns (Bash tool,
  hooks, MCP servers), so their activity is not separately attributed.
- **Self-reported and machine-local.** A harness could report nothing, or report
  wrong. That is the standing reason nothing in Docket is enforced on these
  numbers, and it did not change when the dollar ceiling was removed.
