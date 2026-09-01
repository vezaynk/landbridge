using Landbridge.Core;
using Microsoft.EntityFrameworkCore;

namespace Landbridge.ControlPlane;

/// <summary>
/// The §12 fleet board: every live machine plus every session a human (or a scoped
/// Lead) should see right now, with the last 15 minutes of marks, usage, forwards,
/// and the current exchange. Built as one read so the Blazor board can refresh as a
/// unit rather than N Team pages.
/// </summary>
public sealed record ObservabilitySnapshot(
    IReadOnlyList<ObservabilityMachine> Machines,
    IReadOnlyList<ObservabilityLane> Lanes,
    ObservabilitySummary Summary);

public sealed record ObservabilitySummary(
    int Working,
    int Waiting,
    int Failed,
    int Submitted,
    int MachineCount,
    int ForwardsOpen,
    long RelayBytes,
    DateTimeOffset? OldestHeartbeat);

public sealed record ObservabilityMachine(
    string MachineId,
    bool Ready,
    bool UnderBackPressure,
    DateTimeOffset? LastHeartbeat,
    int RunningSessionCount,
    int ProcessCount);

public sealed record ObservabilityLane(
    Guid SessionId,
    Guid TeamId,
    string Namespace,
    string? Profile,
    SessionState State,
    MessageState MessageState,
    int Attempt,
    bool ReportUnread,
    DateTimeOffset? OpenedAt,
    DateTimeOffset? BlockedAt,
    InputRequestKind? InputKind,
    string? Question,
    string? Answer,
    string? PermissionTool,
    string? WorkerReport,
    LivenessLossReason? LastRequeueReason,
    string? Machine,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    decimal? CostUsd,
    DateTimeOffset? UsageReportedAt,
    IReadOnlyList<ObservabilityPort> Ports,
    IReadOnlyList<ObservabilityMark> Marks,
    IReadOnlyList<ObservabilityChat> Exchange,
    IReadOnlyList<ObservabilityTailLine> Tail);

public sealed record ObservabilityPort(string Name, int Port, bool Live, DateTimeOffset CreatedAt);

public sealed record ObservabilityMark(
    ObservabilityMarkKind Kind,
    double PositionPct,
    double WidthPct,
    string Label);

public enum ObservabilityMarkKind
{
    Tool,
    Ask,
    Answer,
    Forward,
    Error,
    Park,
    Dispatch,
    Done,
}

public sealed record ObservabilityChat(string From, DateTimeOffset? At, string Text);

public sealed record ObservabilityTailLine(DateTimeOffset At, string Kind, string Text);

public sealed partial class DashboardQueries
{
    public static readonly TimeSpan ObservabilityWindow = TimeSpan.FromMinutes(15);

    public async Task<ObservabilitySnapshot> GetObservabilityAsync(
        DateTimeOffset now, IReadOnlyCollection<Guid>? teamScope = null, CancellationToken ct = default)
    {
        var windowStart = now - ObservabilityWindow;

        IReadOnlyList<MachineView> machineViews = teamScope is null
            ? await GetMachinesAsync(ct)
            : [];

        var liveBySession = new Dictionary<Guid, string>();
        foreach (var m in machineViews)
        {
            foreach (var t in m.RunningSessions)
                liveBySession[t.SessionId] = m.MachineId;
        }

        var sessions = db.Sessions.AsNoTracking();
        var events = db.SessionEvents.AsNoTracking();
        var usage = db.SessionUsage.AsNoTracking();
        var services = db.RegisteredServices.AsNoTracking();
        var forwards = db.TeamForwardUsage.AsNoTracking();
        var instances = db.WorkerInstances.AsNoTracking();
        if (teamScope is not null)
        {
            sessions = sessions.Where(s => teamScope.Contains(s.TeamId));
            events = events.Where(e => teamScope.Contains(e.TeamId));
            usage = usage.Where(u => teamScope.Contains(u.TeamId));
            services = services.Where(s => teamScope.Contains(s.TeamId));
            forwards = forwards.Where(u => teamScope.Contains(u.TeamId));
        }

        var liveIds = liveBySession.Keys.ToArray();
        var rows = await sessions
            .Where(s =>
                s.State == SessionState.Submitted
                || s.State == SessionState.Working
                || s.State == SessionState.BlockedOnInput
                || s.State == SessionState.Parked
                || s.State == SessionState.Failed
                || liveIds.Contains(s.Id)
                || (s.MessageOpenedAt != null && s.MessageOpenedAt >= windowStart)
                || (s.LastMessageClosedAt != null && s.LastMessageClosedAt >= windowStart))
            .Select(s => new
            {
                s.Id,
                s.TeamId,
                s.Namespace,
                s.Profile,
                s.State,
                s.MessageState,
                s.Attempt,
                s.ReportUnread,
                s.MessageOpenedAt,
                s.BlockedAt,
                s.InputKind,
                s.InputQuestion,
                s.InputAnswer,
                s.PermissionTool,
                s.WorkerReport,
                s.LastRequeueReason,
                s.ParkMachine,
                s.PreferredMachine,
            })
            .ToListAsync(ct);

        var sessionIds = rows.Select(s => s.Id).ToArray();

        var instanceRows = sessionIds.Length == 0
            ? []
            : await instances
                .Where(w => sessionIds.Contains(w.SessionId) && w.MachineId != null)
                .Select(w => new { w.SessionId, w.MachineId, w.CreatedAt })
                .ToListAsync(ct);
        var lastMachine = instanceRows
            .GroupBy(w => w.SessionId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(x => x.CreatedAt).Select(x => x.MachineId).First());

        var eventRows = sessionIds.Length == 0
            ? new List<EventSlice>()
            : (await events
                .Where(e => sessionIds.Contains(e.SessionId) && e.OccurredAt >= windowStart)
                .Select(e => new
                {
                    e.SessionId, e.Kind, e.FromState, e.ToState, e.Detail,
                    e.OccurredAt, e.AuthErrorCode, e.LivenessReason,
                })
                .ToListAsync(ct))
            .Select(e => new EventSlice(
                e.SessionId, e.Kind, e.FromState, e.ToState, e.Detail,
                e.OccurredAt, e.AuthErrorCode, e.LivenessReason))
            .ToList();

        var usageRows = sessionIds.Length == 0
            ? []
            : await usage.Where(u => sessionIds.Contains(u.SessionId)).ToListAsync(ct);
        var usageBySession = usageRows
            .GroupBy(u => u.SessionId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var serviceRows = await services
            .Select(s => new { s.SessionId, s.Name, s.Port, s.CreatedAt })
            .ToListAsync(ct);
        var servicesBySession = serviceRows
            .GroupBy(s => s.SessionId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var relayBytes = await forwards.SumAsync(u => (long?)u.ForwardedBytes, ct) ?? 0;

        var lanes = new List<ObservabilityLane>(rows.Count);
        foreach (var s in rows.OrderBy(r => r.Namespace, StringComparer.Ordinal))
        {
            var machine = liveBySession.GetValueOrDefault(s.Id)
                ?? s.ParkMachine
                ?? s.PreferredMachine
                ?? lastMachine.GetValueOrDefault(s.Id)
                ?? "—";

            var u = usageBySession.GetValueOrDefault(s.Id);
            long input = 0, output = 0, cacheRead = 0, cacheWrite = 0;
            decimal? cost = null;
            DateTimeOffset? reportedAt = null;
            if (u is { Count: > 0 })
            {
                input = u.Sum(x => x.InputTokens);
                output = u.Sum(x => x.OutputTokens);
                cacheRead = u.Sum(x => x.CacheReadTokens);
                cacheWrite = u.Sum(x => x.CacheWriteTokens);
                var costs = u.Where(x => x.CostUsd is not null).Select(x => x.CostUsd!.Value).ToList();
                if (costs.Count > 0)
                    cost = costs.Sum();
                reportedAt = u.Max(x => x.ReportedAt);
            }

            var ports = servicesBySession.GetValueOrDefault(s.Id)?
                .Select(p => new ObservabilityPort(p.Name, p.Port, Live: true, p.CreatedAt))
                .ToList() ?? [];

            var sessionEvents = eventRows.Where(e => e.SessionId == s.Id).OrderBy(e => e.OccurredAt).ToList();
            var marks = BuildMarks(sessionEvents, ports, windowStart, now);
            var exchange = BuildExchange(s.InputKind, s.InputQuestion, s.InputAnswer, s.PermissionTool, s.BlockedAt, sessionEvents);
            var tail = sessionEvents
                .TakeLast(14)
                .Select(e => new ObservabilityTailLine(
                    e.OccurredAt, e.Kind, TailText(e.Kind, e.Detail, e.ToState, e.AuthErrorCode, e.LivenessReason)))
                .ToList();

            lanes.Add(new ObservabilityLane(
                s.Id, s.TeamId, s.Namespace, s.Profile, s.State, s.MessageState, s.Attempt,
                s.ReportUnread, s.MessageOpenedAt, s.BlockedAt, s.InputKind, s.InputQuestion,
                s.InputAnswer, s.PermissionTool, s.WorkerReport, s.LastRequeueReason, machine,
                input, output, cacheRead, cacheWrite, cost, reportedAt,
                ports, marks, exchange, tail));
        }

        var machines = machineViews
            .Select(m => new ObservabilityMachine(
                m.MachineId, m.Ready, m.UnderBackPressure, m.LastHeartbeat,
                m.RunningSessions.Count, m.Processes?.Count ?? 0))
            .ToList();

        var waiting = lanes.Count(l =>
            l.State is SessionState.Parked or SessionState.BlockedOnInput
            || (l.State == SessionState.Working && l.BlockedAt is not null));

        var summary = new ObservabilitySummary(
            Working: lanes.Count(l => l.State == SessionState.Working && l.BlockedAt is null),
            Waiting: waiting,
            Failed: lanes.Count(l => l.State == SessionState.Failed),
            Submitted: lanes.Count(l => l.State == SessionState.Submitted),
            MachineCount: machines.Count,
            ForwardsOpen: lanes.Sum(l => l.Ports.Count),
            RelayBytes: relayBytes,
            OldestHeartbeat: machines.Select(m => m.LastHeartbeat).Where(h => h is not null).Min());

        return new ObservabilitySnapshot(machines, lanes, summary);
    }

    private static IReadOnlyList<ObservabilityMark> BuildMarks(
        IReadOnlyList<EventSlice> events,
        IReadOnlyList<ObservabilityPort> ports,
        DateTimeOffset windowStart,
        DateTimeOffset now)
    {
        var marks = new List<ObservabilityMark>();
        var window = (now - windowStart).TotalSeconds;
        if (window <= 0)
            return marks;

        double Pct(DateTimeOffset at)
        {
            var p = (at - windowStart).TotalSeconds / window * 100;
            return Math.Clamp(p, 0, 100);
        }

        foreach (var e in events)
        {
            var mapped = MapMark(e.Kind, e.FromState, e.ToState);
            if (mapped is null)
                continue;
            marks.Add(new ObservabilityMark(mapped.Value.Kind, Pct(e.OccurredAt), 0, mapped.Value.Label));
        }

        foreach (var p in ports)
        {
            var start = p.CreatedAt < windowStart ? windowStart : p.CreatedAt;
            var left = Pct(start);
            var width = Math.Max(0.5, 100 - left);
            marks.Add(new ObservabilityMark(ObservabilityMarkKind.Forward, left, width, $"forward {p.Name}:{p.Port}"));
        }

        return marks;
    }

    private static (ObservabilityMarkKind Kind, string Label)? MapMark(
        string kind, SessionState? from, SessionState? to)
    {
        if (kind == SessionEventRow.AuthFailedKind)
            return (ObservabilityMarkKind.Error, "auth-failed");
        if (kind == nameof(RequestInput))
            return (ObservabilityMarkKind.Ask, "worker → Lead");
        if (kind is nameof(AnswerInput) or nameof(AnswerPermission) or nameof(WakeParked))
            return (ObservabilityMarkKind.Answer, "Lead → worker");
        if (to == SessionState.Parked)
            return (ObservabilityMarkKind.Park, "parked");
        if (kind == nameof(Dispatch) || (from == SessionState.Submitted && to == SessionState.Working))
            return (ObservabilityMarkKind.Dispatch, "dispatch");
        if (kind == nameof(ReportResult) || to is SessionState.Completed or SessionState.Canceled or SessionState.Rejected)
            return (ObservabilityMarkKind.Done, "report_result");
        return null;
    }

    private static IReadOnlyList<ObservabilityChat> BuildExchange(
        InputRequestKind? inputKind,
        string? question,
        string? answer,
        string? permissionTool,
        DateTimeOffset? blockedAt,
        IReadOnlyList<EventSlice> events)
    {
        var list = new List<ObservabilityChat>();
        if (!string.IsNullOrWhiteSpace(question) || !string.IsNullOrWhiteSpace(permissionTool))
        {
            var text = inputKind == InputRequestKind.Permission
                ? string.IsNullOrWhiteSpace(permissionTool)
                    ? question
                    : string.IsNullOrWhiteSpace(question) ? permissionTool : $"{permissionTool}: {question}"
                : question;
            if (!string.IsNullOrWhiteSpace(text))
                list.Add(new ObservabilityChat("Worker", blockedAt, text));
        }
        if (!string.IsNullOrWhiteSpace(answer))
        {
            DateTimeOffset? at = events
                .LastOrDefault(e => e.Kind is nameof(AnswerInput) or nameof(AnswerPermission) or nameof(WakeParked))
                ?.OccurredAt;
            list.Add(new ObservabilityChat("Lead", at, answer));
        }
        return list;
    }

    private sealed record EventSlice(
        Guid SessionId,
        string Kind,
        SessionState? FromState,
        SessionState? ToState,
        string? Detail,
        DateTimeOffset OccurredAt,
        string? AuthErrorCode,
        LivenessLossReason? LivenessReason);

    private static string TailText(
        string kind, string? detail, SessionState? to, string? authError, LivenessLossReason? reason)
    {
        if (!string.IsNullOrWhiteSpace(detail))
            return detail;
        if (kind == SessionEventRow.AuthFailedKind && !string.IsNullOrWhiteSpace(authError))
            return authError;
        if (reason is { } r)
            return r.ToString();
        if (to is { } state)
            return state.ToString();
        return kind;
    }
}
