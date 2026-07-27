# Step-0 feasibility spikes (spec §17.0)

Throwaway experiments against a real Claude Code install — the three mechanics
the design leans on hardest. **These consume tokens and require a logged-in
`claude` CLI**; they are operator-run, never CI. Each script prints PASS/FAIL
per criterion and leaves its evidence under `spikes/out/`.

| Spike | Proves | Pass criterion |
|---|---|---|
| `s1-resume-after-park.sh` | Park→resume: a session resumes headlessly, with context, from the directory that created it — and only from there | Resume in the original directory recalls session context; resume from a different directory fails with "no conversation found" |
| `s2-stop-injection.sh` | `stop` as a message: a disposition delivered over `--input-format stream-json` reaches the agent as a turn and produces a wind-down, not an abort | Agent acknowledges the stop, persists progress to a file, and exits before the hard timeout |
| `s3-hook-events.sh` | Tool-call event sourcing: hooks fire per tool call with `DOCKET_TASK_ID` attribution intact | Every tool call in the run appends a line carrying the task id and tool name |

Record findings (pinned flags, versions, measured timings) in
`spikes/FINDINGS.md` — that document is the spike deliverable, not the
scripts.

Not yet scripted (needs a docket-mcp stub): the transcript-interleaving hazard
(resuming a session a zombie process still holds) and MCP-config re-injection
on resume. Fold both into the s1 protocol once a stub MCP server exists.
