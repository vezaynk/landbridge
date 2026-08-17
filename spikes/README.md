# Step-0 feasibility spikes (spec §17.0)

Throwaway experiments against a real Claude Code install — the three mechanics
the design leans on hardest. **These consume tokens and require a logged-in
`claude` CLI**; they are operator-run, never CI. Each prints PASS/FAIL per
criterion, exits non-zero on failure, and leaves evidence under `spikes/out/`.

One .NET console project, no shell scripts anywhere — `claude` is spawned as
argv, and the s3 hook command is this same binary in `hook-relay` mode
(matching landbridged's own no-shell constraint, spec §10).

```sh
dotnet run --project spikes/Landbridge.Spikes -- s1   # resume-after-park
dotnet run --project spikes/Landbridge.Spikes -- s2   # stop as an injected turn
dotnet run --project spikes/Landbridge.Spikes -- s3   # hook events with task attribution
```

Model defaults to `haiku` to bound spend; override with `LANDBRIDGE_SPIKE_MODEL`.

| Spike | Proves | Pass criterion |
|---|---|---|
| `s1` | Park→resume: a session resumes headlessly, with context, from the directory that created it — and only from there | Resume in the original directory recalls session context; resume from a different directory fails |
| `s2` | `stop` as a message: a disposition delivered over `--input-format stream-json` reaches the agent as a turn and produces a wind-down, not an abort | Agent acknowledges the stop, persists progress to a file, and exits before the hard timeout |
| `s3` | Tool-call event sourcing: hooks fire per tool call with `LANDBRIDGE_SESSION_ID` attribution intact | Every tool call in the run POSTs an event carrying the task id and tool name to a loopback listener |

Record findings (pinned flags, versions, measured timings) in
`spikes/FINDINGS.md` — that document is the spike deliverable, not the code.

Not yet scripted (needs a landbridge-mcp stub): the transcript-interleaving hazard
(resuming a session a zombie process still holds) and MCP-config re-injection
on resume. Fold both into the s1 protocol once a stub MCP server exists.
