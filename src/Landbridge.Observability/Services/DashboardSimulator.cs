using Landbridge.Observability.Models;
using Microsoft.Extensions.Hosting;

namespace Landbridge.Observability.Services;

/// <summary>
/// Owns the fake fleet and advances it on a timer: elapsed time, a sliding
/// timeline window, jittered token rates and occasional state transitions.
/// One instance is shared by every viewer (a fleet has one truth), so it is
/// registered as a singleton hosted service.
/// </summary>
public sealed class DashboardSimulator : BackgroundService
{
    private const int WindowSeconds = 15 * 60;
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(1200);

    private readonly object _gate = new();
    private readonly Random _rng = new();
    private readonly List<AgentSession> _agents;
    private readonly List<Machine> _machines;
    private int _relayMb = 412;

    public DashboardSimulator()
    {
        _machines = SeedData.BuildMachines();
        _agents = SeedData.BuildAgents();
    }

    /// <summary>Raised on every tick so subscribers (Blazor components) know to re-render.</summary>
    public event Action? Changed;

    public void RunWithLock(Action<IReadOnlyList<AgentSession>, IReadOnlyList<Machine>> action)
    {
        lock (_gate) action(_agents, _machines);
    }

    public T RunWithLock<T>(Func<IReadOnlyList<AgentSession>, IReadOnlyList<Machine>, T> selector)
    {
        lock (_gate) return selector(_agents, _machines);
    }

    public FleetSummary GetSummary()
    {
        lock (_gate)
        {
            var s = new FleetSummary
            {
                Working = _agents.Count(a => a.State == SessionState.Working),
                Waiting = _agents.Count(a => a.State is SessionState.Permission or SessionState.Question or SessionState.Parked),
                Failed = _agents.Count(a => a.State == SessionState.Failed),
                Submitted = _agents.Count(a => a.State == SessionState.Submitted),
                MachineCount = _machines.Count,
                ForwardsOpen = _agents.SelectMany(a => a.Ports).Count(p => p.Live),
                RelayMb = _relayMb,
            };
            return s;
        }
    }

    /// <summary>Clears the unread badge for a session — called when a viewer selects its row.</summary>
    public void MarkRead(int id)
    {
        lock (_gate)
        {
            var a = _agents.FirstOrDefault(x => x.Id == id);
            if (a is not null) a.Unread = 0;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TickInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            lock (_gate)
            {
                Tick();
            }
            Changed?.Invoke();
        }
    }

    private void Tick()
    {
        var tickSeconds = TickInterval.TotalSeconds;
        var scrollPct = tickSeconds / WindowSeconds * 100.0;
        _relayMb = Math.Max(180, _relayMb + _rng.Next(-6, 7));

        foreach (var a in _agents)
        {
            var frozen = a.State is SessionState.Completed or SessionState.Submitted;
            if (!frozen)
            {
                a.ElapsedSeconds++;
                ScrollTimeline(a, scrollPct);

                if (a.Meta.Live && a.HasDispatch)
                {
                    MaybeAddToolMark(a);
                    JitterUsage(a);
                    MaybeGrowTranscript(a);
                }
                else
                {
                    a.MinutesSinceReport = Math.Min(59, a.ElapsedSeconds / 60 + 1);
                }
            }

            MaybeToggleForward(a);
            MaybeTransition(a);
        }
    }

    private void ScrollTimeline(AgentSession a, double scrollPct)
    {
        foreach (var m in a.Events) m.PositionPct -= scrollPct;
        a.Events.RemoveAll(m => m.PositionPct < -2);
    }

    private void MaybeAddToolMark(AgentSession a)
    {
        // Busier lanes (lower seed mod) tick more often — gives the board visual variety.
        var chance = 0.10 + (a.Seed % 7) * 0.02;
        if (_rng.NextDouble() < chance)
        {
            a.Events.Add(new TimelineMark { Type = MarkType.Tool, PositionPct = 98 + _rng.NextDouble() * 2 });
            a.ToolCalls++;
        }
    }

    private void JitterUsage(AgentSession a)
    {
        var baseRate = 1 + (a.Seed % 9) / 2.0;
        var jitter = (_rng.NextDouble() - 0.5) * 0.6;
        a.RateTokPerMin = Math.Max(0.1, baseRate + jitter);
        a.Tokens += (long)((a.RateTokPerMin.Value * 1000 / 60) * TickInterval.TotalSeconds);
        if (a.ReportsDollars) a.CostUsd = a.Tokens * 0.0061 / 1000.0;
    }

    private void MaybeGrowTranscript(AgentSession a)
    {
        if (_rng.NextDouble() >= 0.18) return;
        a.Transcript.Add(SeedData.RandomTranscriptLine(_rng));
        while (a.Transcript.Count > 40) a.Transcript.RemoveAt(0);
    }

    private void MaybeToggleForward(AgentSession a)
    {
        if (a.Ports.Count == 0) return;
        if (_rng.NextDouble() >= 0.01) return;
        var p = a.Ports[_rng.Next(a.Ports.Count)];
        p.Live = !p.Live;
    }

    private void MaybeTransition(AgentSession a)
    {
        switch (a.State)
        {
            case SessionState.Working when _rng.NextDouble() < 0.0015:
                a.SavedNow = a.Now;
                a.Now = SeedData.RandomPermissionAsk(_rng);
                a.State = SessionState.Permission;
                a.Unread++;
                a.Events.Add(new TimelineMark { Type = MarkType.Ask, PositionPct = 99 });
                break;

            case SessionState.Permission when _rng.NextDouble() < 0.012:
                a.Now = a.SavedNow ?? "Resumed after approval";
                a.State = SessionState.Working;
                a.Events.Add(new TimelineMark { Type = MarkType.Answer, PositionPct = 99 });
                break;

            case SessionState.Question when _rng.NextDouble() < 0.006:
                a.Now = a.SavedNow ?? "Resumed — Lead answered";
                a.State = SessionState.Working;
                a.Events.Add(new TimelineMark { Type = MarkType.Answer, PositionPct = 99 });
                break;

            case SessionState.Failed when _rng.NextDouble() < 0.004:
                a.Attempt = "a" + (int.Parse(a.Attempt.TrimStart('a')) + 1);
                a.Now = "Retrying — " + a.Attempt;
                a.State = SessionState.Working;
                a.Events.Add(new TimelineMark { Type = MarkType.Dispatch, PositionPct = 99 });
                break;

            case SessionState.Submitted when _rng.NextDouble() < 0.003:
                a.Now = "Dispatched — cold start";
                a.State = SessionState.Working;
                a.Events.Add(new TimelineMark { Type = MarkType.Dispatch, PositionPct = 99 });
                break;
        }
    }
}
