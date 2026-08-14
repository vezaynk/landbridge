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
`profiles[].env` is a different map (harness homes, API keys, anything that is
not telemetry) and is stamped first; when `otel` is on, `telemetry.env` overlays
it. Neither map can set `DOCKET_MACHINE_ID`, `DOCKET_TASK_ID`,
`DOCKET_WORKER_TOKEN`, or `DOCKET_TRACEPARENT`.
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

Docket receives none of it and needs no network path to it: the worker talks to your
collector directly, and nothing about that traffic passes through the control plane.
There is still no OTLP receiver in Docket.

What Docket *does* now ingest arrives by a different route entirely — see
[What the dashboard shows](#what-the-dashboard-shows-and-what-it-still-does-not)
below.

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

**OpenCode is the third case, and it is neither of the first two.** Source-read at tag
`v1.18.17`, it lands in three places at once:

| | claude | `codex exec` | `opencode run` |
|---|---|---|---|
| opt-in variable | `CLAUDE_CODE_ENABLE_TELEMETRY=1` | — | **none needed** |
| OTLP signals | metrics + traces + logs | none documented | **traces + logs only** |
| token/cost channel | OTLP metrics | its stdout stream (tokens only) | **its stdout stream (tokens *and* USD)** |

It reads the vendor-neutral `OTEL_EXPORTER_OTLP_ENDPOINT`, `OTEL_EXPORTER_OTLP_HEADERS` and
`OTEL_RESOURCE_ATTRIBUTES` straight from the environment (`packages/core/src/flag/flag.ts:16-17`,
`observability/otlp.ts:20-34`), so `telemetry: { "otel": true }` works with an **empty**
`telemetry.env` — no harness-specific flag to carry as data. But its exporters are only
`${endpoint}/v1/logs` and `${endpoint}/v1/traces` (`observability/otlp.ts:50-77`); there is no
metrics exporter, so **no `claude_code.token.usage` analogue arrives and the table above stays
empty on an OpenCode profile no matter what you set.**

That is not a gap in what Docket measures, because the §12 measured view never depended on OTLP:
it reads the harness's own numbers off the stdout stream via `events.mapping`, and OpenCode states
both token counts and a USD cost there, per step, on the `step_finish` line it already emits for
the progress clock. So an OpenCode profile gets the *fuller* measured view of the three — Codex
reports no cost anywhere — while contributing nothing to this OTLP section. Two independent
channels, and it is worth not confusing them: an empty Aspire dashboard says nothing about whether
`docket_task_usage` has rows. See `ideas/skills/references/runner-config.md` for the usage mapping
that reads it, including the two nesting details specific to this harness.

One caveat on the cost figure: OpenCode computes it itself, client-side, by multiplying its token
counts against a price from the models.dev catalog
(`packages/opencode/src/session/session.ts:382-399`). It is genuinely harness-reported rather than
derived by Docket, which is what the §12 label claims — but it is the harness's arithmetic over a
catalog, not a figure the provider billed, so it can drift from an invoice in a way claude's
self-reported `total_cost_usd` also can.

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

## What the dashboard shows, and what it still does not

Since 2026-08-12 the §12 Team view has a **measured usage** section, and it is fed by
`docketd` reading the harness's own **stdout stream** — not by this OTLP path. The two
are independent: turning `telemetry.otel` off does not empty the dashboard, and turning
it on does not fill it.

**What lands there**, for both harnesses, as a new optional `usage-reported` event on the
§10 runner wire:

| | Claude Code | `codex exec` |
|---|---|---|
| tokens: input / output / cache-read / cache-write | yes | yes |
| by model | yes — from `modelUsage`, so subagents on another model are counted separately | **no** — Codex names none on its stream, so its rows read "not reported" |
| cost in USD | **reported** — `total_cost_usd` and per-model `costUSD` | none exists; renders "not reported" |
| reasoning-token breakdown | not exposed | carried (a portion *of* output) |

**The two harnesses disagree about "input", and `docketd` normalizes.** Claude's
`input_tokens` excludes cache; Codex's *includes* its `cached_input_tokens` as a subset.
A profile declares which by setting `usage_cached_is_subset`, and `docketd` subtracts so
the four buckets are disjoint and their sum counts the prompt once. Get this wrong and a
cache-heavy worker's total roughly doubles.

**What is deliberately not built** (each is a "could, here's what it'd take"):

- **Cost for a harness that reports none.** Deriving dollars from Codex's tokens needs a
  rate per model *and per bucket* — the four are priced differently — plus operator-owned
  rate config and an effective date, since re-pricing an old task at today's rate rewrites
  history. `ModelPricing` is the stub where that would live; it returns nothing, so the
  cell reads "not reported" rather than `$0.00`. Zero would claim the work was free.
- **A model for a harness that names none.** Codex reports tokens without a model, and Docket
  does not supply one. It could — a profile could declare the model it pins — but a model the
  *plane* asserted, rendered in a section that says "reported by the harness", would misattribute
  the claim. So a Codex row shows real token counts with "not reported" where the model goes.
- **Tool accept/reject counts, lines-of-code, commits, pull requests, active time, and
  subagent lineage.** These exist only as `claude_code.*` OTel metrics (lineage only in the
  trace beta), so surfacing them needs Docket to host an OTLP receiver. That path was
  considered and rejected — see below.

### Why Docket does not host an OTLP receiver

It would deliver strictly more data. It was rejected because it does not work on the
machines Docket runs on:

- **`codex exec` ignores `OTEL_EXPORTER_OTLP_ENDPOINT`.** Its exporter comes from its own
  `config.toml` `[otel]` block; the only `OTEL_*` variables it reads are the OTLP crate's
  timeout constants. Since `docketd` configures telemetry purely through spawn environment
  variables, it cannot point Codex at anything — that needs a per-machine Codex config edit.
- **Managed settings win.** On a machine whose MDM sets `OTEL_EXPORTER_OTLP_*`, Claude Code
  drops the conflicting developer values, so a plane-hosted receiver would sit empty while
  the config looked right. A managed `OTEL_RESOURCE_ATTRIBUTES` also threatens the
  `docket.task_id` attribution the whole scheme depends on.
- **Release-build Codex exports to OpenAI by default.** Its built-in metrics exporter is
  Statsig (`ab.chatgpt.com`) unless its `config.toml` disables it. Nothing Docket sets
  changes that; if you do not want it, configure Codex.

### Never turn on the content log opt-ins

`OTEL_LOG_USER_PROMPTS`, `OTEL_LOG_ASSISTANT_RESPONSES`, `OTEL_LOG_TOOL_DETAILS`,
`OTEL_LOG_TOOL_CONTENT` and `OTEL_LOG_RAW_API_BODIES` un-redact content that is redacted by
default. **A worker's prompt is the task description**, so setting any of them in a
profile's `telemetry.env` puts task content into a metrics pipeline that was never scoped
to hold it. Docket does not set them and a profile should not either.

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
