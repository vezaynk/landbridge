# Landbridge Observability Center — Blazor implementation

Implements the "1a — lane board" design from `project/Observability Center.dc.html`
(the Claude Design handoff in the repo root) as Blazor components, plus a sample
page (`/`) that reproduces the dashboard with a simulated fake fleet.

## Run

```
dotnet run
```

Then open the URL printed in the console (e.g. `http://localhost:5265`).

## Layout

- `Models/` — `AgentSession`, `SessionState`/`StateMeta`, `TimelineMark`, `Machine`, etc.
- `Services/DashboardSimulator.cs` — a singleton `BackgroundService` that owns the fake
  fleet and advances it every ~1.2s: elapsed time, a sliding 15-minute timeline window,
  jittered token/cost rates, and occasional state transitions (permission → working,
  submitted → dispatched, failed → retry, …).
- `Services/SeedData.cs` — the initial fake fleet (same session names/machines as the mockup).
- `Components/Observability/` — `ObservabilityDashboard`, `MachineRail`, `SessionRow`,
  `TimelineTrack`, `UsageMeter`, `DetailPanel` — the reusable pieces of the lane board.
- `Components/Pages/ObservabilityCenter.razor` — the sample page, routed at `/`.
- `wwwroot/css/tokens.css` — the Nocturne design tokens ported from the handoff's
  `nocturne.css`, plus the dashboard-local tokens (`--state-live`, `--state-wait`, …)
  defined in the `.dc.html` file.
- `wwwroot/css/observability.css` — the lane board layout.

Clicking a session row selects it (clearing its unread badge) and updates the detail
panel's exchange + transcript tail.
