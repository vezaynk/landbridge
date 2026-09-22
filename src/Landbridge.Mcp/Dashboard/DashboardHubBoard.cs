using Landbridge.ControlPlane;
using Landbridge.ControlPlane.Auth;
using Landbridge.Contracts;
using Landbridge.Core;

namespace Landbridge.Mcp.Dashboard;

/// <summary>
/// Compose the fleet board from Hub nouns. LastProgress is dropped (registry).
/// Event marks/tail wait on per-session logs; this pass uses ports + exchange
/// from the session document.
/// </summary>
internal static class DashboardHubBoard
{
    public static async Task<ObservabilitySnapshot?> TrySnapshotAsync(
        HubClient hub, string bearer, DateTimeOffset now, TimeSpan window,
        IReadOnlyCollection<Guid>? teamScope, Guid? teamId, CancellationToken ct)
    {
        if (!hub.Enabled)
            return null;

        var machinesTask = hub.GetAsync<List<HubMachine>>("/machines", bearer, ct);
        var sessionsTask = hub.GetAsync<List<HubSessionListItem>>("/sessions?hidden=true", bearer, ct);
        var servicesTask = hub.GetAsync<List<HubServiceDocument>>("/services", bearer, ct);
        var previewsTask = hub.GetAsync<List<HubPreviewDocument>>("/previews", bearer, ct);
        var forwardsTask = hub.GetAsync<List<HubForwardDocument>>("/forwards", bearer, ct);
        var teamsTask = hub.GetAsync<List<HubTeamDocument>>("/teams", bearer, ct);
        await Task.WhenAll(machinesTask, sessionsTask, servicesTask, previewsTask, forwardsTask, teamsTask);

        var machines = await machinesTask;
        var sessions = await sessionsTask;
        if (machines is null || sessions is null)
            return null;

        var services = await servicesTask ?? [];
        var previews = await previewsTask ?? [];
        var forwards = await forwardsTask ?? [];
        var teams = await teamsTask ?? [];
        var relayBytes = teams.Sum(t => t.ForwardedBytes);

        if (teamScope is { } scope)
            sessions = sessions.Where(s => scope.Contains(s.TeamId)).ToList();
        if (teamId is { } tid)
            sessions = sessions.Where(s => s.TeamId == tid).ToList();

        var windowStart = now - window;
        var liveBySession = new Dictionary<Guid, Guid>();
        foreach (var m in machines.Where(m => m.Live))
        {
            foreach (var p in m.Processes ?? [])
            {
                if (string.Equals(p.State, "running", StringComparison.OrdinalIgnoreCase))
                    liveBySession[p.DeclaredBySession] = m.Id;
            }
        }

        var rows = sessions.Where(s =>
            s.State is SessionState.Submitted or SessionState.Working
                or SessionState.BlockedOnInput or SessionState.Parked or SessionState.Failed
            || liveBySession.ContainsKey(s.Id)
            || (s.MessageOpenedAt is { } opened && opened >= windowStart)
            || (s.LastMessageClosedAt is { } closed && closed >= windowStart)).ToList();

        var docs = await LoadDocumentsAsync(hub, bearer, rows.Select(s => s.Id), ct);

        var servicesBySession = services.GroupBy(s => s.SessionId).ToDictionary(g => g.Key, g => g.ToList());
        var previewsBySession = previews.Where(p => p.ExpiresAt > now)
            .GroupBy(p => p.SessionId).ToDictionary(g => g.Key, g => g.ToList());
        var receipts = forwards
            .Where(g => !g.Revoked && g.ConsumerPort is not null && g.ConsumerMachine is not null)
            .Select(g => new ObservabilityReceipt(
                g.ForwardId, g.ConsumerMachine!.Value.ToString("D"), g.ServiceName,
                g.ConsumerPort!.Value, g.ProducerSessionId, g.CreatedAt))
            .ToList();
        var receiptsByMachine = receipts
            .GroupBy(r => r.MachineId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var machineSlug = machines.ToDictionary(m => m.Id.ToString("D"), m => m.Slug, StringComparer.Ordinal);
        var heartbeat = machines.Where(m => m.LastSpokeAt is not null)
            .ToDictionary(m => m.Id.ToString("D"), m => m.LastSpokeAt, StringComparer.Ordinal);

        var lanes = new List<ObservabilityLane>(rows.Count);
        foreach (var s in rows.OrderBy(r => r.Namespace, StringComparer.Ordinal))
        {
            docs.TryGetValue(s.Id, out var doc);
            var lastInst = doc?.Instances.Where(i => !i.Revoked && i.MachineId is not null)
                .OrderBy(i => i.CreatedAt).LastOrDefault();
            var live = liveBySession.TryGetValue(s.Id, out var liveMachine);
            var machine = liveMachine != default
                ? liveMachine.ToString("D")
                : s.ParkMachine?.ToString("D")
                    ?? doc?.PreferredMachine?.ToString("D")
                    ?? lastInst?.MachineId?.ToString("D")
                    ?? "—";
            DateTimeOffset? beat = live ? heartbeat.GetValueOrDefault(machine) : null;

            long input = 0, output = 0, cacheRead = 0, cacheWrite = 0;
            decimal? cost = null;
            DateTimeOffset? reportedAt = null;
            if (doc?.Usage is { Count: > 0 } u)
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
            var sessionPreviews = previewsBySession.GetValueOrDefault(s.Id)?
                .Select(p => new ObservabilityPreview(p.ServiceName, p.AuthPolicy, p.ExpiresAt, p.Id, p.Label))
                .ToList() ?? [];

            var question = doc?.InputQuestion;
            var answer = doc?.InputAnswer;
            var perm = doc?.PermissionTool;
            var report = doc?.WorkerReport;
            var exchange = BuildExchange(s.InputKind, question, answer, perm, s.BlockedAt);
            var marks = ports.Select(p =>
            {
                var start = p.CreatedAt < windowStart ? windowStart : p.CreatedAt;
                var left = Pct(start, windowStart, now);
                return new ObservabilityMark(ObservabilityMarkKind.Forward, left, Math.Max(0.5, 100 - left),
                    $"forward {p.Name}:{p.Port}");
            }).ToList();

            var occupancy = SessionRecord.DeriveState(new SessionRecord
            {
                Id = new SessionId(s.Id),
                Team = new TeamId(s.TeamId),
                Namespace = s.Namespace,
                Hidden = s.Hidden,
                Health = s.Health,
                OccupancyDesired = s.OccupancyDesired,
                OccupancyObserved = s.OccupancyObserved,
                MessageState = s.MessageState,
                CurrentInstance = s.CurrentInstanceId is { } inst ? new WorkerInstanceId(inst) : null,
            });

            lanes.Add(new ObservabilityLane(
                s.Id, s.TeamId, s.Namespace, s.Profile, occupancy, s.MessageState, s.Attempt,
                s.ReportUnread, s.MessageOpenedAt, s.BlockedAt, s.InputKind, question,
                answer, perm, report, s.LastRequeueReason, machine,
                live, beat, LastProgress: null, input, output, cacheRead, cacheWrite, cost, reportedAt,
                ports, marks, exchange, Tail: [], s.Slug, s.TeamSlug ?? "",
                machineSlug.GetValueOrDefault(machine) ?? "", sessionPreviews,
                receiptsByMachine.GetValueOrDefault(machine)));
        }

        var obsMachines = machines.Select(m => new ObservabilityMachine(
            m.Id.ToString("D"), m.Ready, m.UnderBackPressure, m.LastSpokeAt,
            m.Processes?.Count(p => string.Equals(p.State, "running", StringComparison.OrdinalIgnoreCase)) ?? 0,
            m.Processes?.Count ?? 0, m.Slug, m.Name)).ToList();

        var waiting = lanes.Count(l =>
            l.State is SessionState.Parked or SessionState.BlockedOnInput
            || (l.State == SessionState.Working && l.BlockedAt is not null));

        var summary = new ObservabilitySummary(
            Working: lanes.Count(l => l.State == SessionState.Working && l.BlockedAt is null),
            Waiting: waiting,
            Failed: lanes.Count(l => l.State == SessionState.Failed),
            Submitted: lanes.Count(l => l.State == SessionState.Submitted),
            MachineCount: obsMachines.Count,
            ForwardsOpen: lanes.Sum(l => l.Ports.Count),
            RelayBytes: relayBytes,
            OldestHeartbeat: obsMachines.Select(m => m.LastHeartbeat).OfType<DateTimeOffset>().DefaultIfEmpty().Max()
                is var oldest && oldest != default ? oldest : null);

        return new ObservabilitySnapshot(obsMachines, lanes, summary, receipts);
    }

    public static async Task<IReadOnlyList<MachineView>?> TryMachinesAsync(
        HubClient hub, string bearer, CancellationToken ct)
    {
        var machines = await hub.GetAsync<List<HubMachine>>("/machines", bearer, ct);
        if (machines is null)
            return null;
        var sessions = await hub.GetAsync<List<HubSessionListItem>>("/sessions?hidden=true", bearer, ct) ?? [];
        var byId = sessions.ToDictionary(s => s.Id);
        return machines.Select(m =>
        {
            var running = (m.Processes ?? [])
                .Where(p => string.Equals(p.State, "running", StringComparison.OrdinalIgnoreCase))
                .Select(p => byId.TryGetValue(p.DeclaredBySession, out var s)
                    ? new MachineSessionView(s.Id, s.TeamId, s.Namespace, s.State)
                    : new MachineSessionView(p.DeclaredBySession, Guid.Empty, "(unknown)", SessionState.Working))
                .GroupBy(s => s.SessionId)
                .Select(g => g.First())
                .ToList();
            var processes = (m.Processes ?? [])
                .Select(p => new ProcessStatus(
                    p.Name,
                    Enum.TryParse<ProcessState>(p.State, ignoreCase: true, out var st) ? st : ProcessState.Exited,
                    p.DeclaredBySession, p.StartedAt, p.ExitCode, p.ExitedAt, p.StdinOpen))
                .ToList();
            return new MachineView(
                m.Id.ToString("D"), m.Ready, m.UnderBackPressure, m.LastSpokeAt,
                m.Profiles.OrderBy(p => p, StringComparer.Ordinal).ToList(),
                running, m.BoundHumanId, m.BoundAt, processes, m.Slug, m.Name);
        }).ToList();
    }

    public static async Task<IReadOnlyList<TeamOverview>?> TryTeamsAsync(
        HubClient hub, string bearer, IReadOnlyCollection<Guid>? teamScope, CancellationToken ct)
    {
        var teams = await hub.GetAsync<List<HubTeamDocument>>("/teams", bearer, ct);
        var sessions = await hub.GetAsync<List<HubSessionListItem>>("/sessions?hidden=true", bearer, ct);
        var services = await hub.GetAsync<List<HubServiceDocument>>("/services", bearer, ct);
        if (teams is null || sessions is null)
            return null;
        services ??= [];
        if (teamScope is { } scope)
        {
            teams = teams.Where(t => scope.Contains(t.TeamId)).ToList();
            sessions = sessions.Where(s => scope.Contains(s.TeamId)).ToList();
            services = services.Where(s => scope.Contains(s.TeamId)).ToList();
        }

        var sessionsByTeam = sessions.GroupBy(s => s.TeamId).ToDictionary(g => g.Key, g => g.ToList());
        var servicesByTeam = services.GroupBy(s => s.TeamId).ToDictionary(g => g.Key, g => g.Count());
        return teams.Select(t =>
        {
            var rows = sessionsByTeam.GetValueOrDefault(t.TeamId) ?? [];
            var counts = rows.GroupBy(s => s.State).ToDictionary(g => g.Key, g => g.Count());
            var last = rows.Select(s => s.LastMessageClosedAt ?? s.MessageOpenedAt).OfType<DateTimeOffset>().DefaultIfEmpty().Max();
            var lastN = last == default ? (DateTimeOffset?)null : last;
            var open = rows.Count(s => s.HasQuestion);
            var parks = rows.Count(s => s.State == SessionState.Parked || s.ParkMachine is not null);
            var idle = rows.All(s => s.State is SessionState.Completed or SessionState.Canceled
                or SessionState.Rejected or SessionState.Failed);
            return new TeamOverview(
                t.TeamId, rows.Count, counts, parks, servicesByTeam.GetValueOrDefault(t.TeamId),
                open, LeadHumanId: null, LeadSince: t.CreatedAt, lastN, idle,
                t.ForwardedBytes > 0 ? new TeamForwardUsageView(t.ForwardedBytes, t.BytesReportedAt ?? t.CreatedAt) : null,
                t.Slug);
        }).ToList();
    }

    public static async Task<LeadMachineBinding?> TryBindingAsync(
        HubClient hub, string bearer, Guid humanId, CancellationToken ct)
    {
        var machines = await hub.GetAsync<List<HubMachine>>("/machines", bearer, ct);
        var match = machines?.FirstOrDefault(m => m.BoundHumanId == humanId);
        return match is null
            ? null
            : new LeadMachineBinding(match.Id, match.Name, match.BoundAt ?? match.EnrolledAt);
    }

    private static async Task<Dictionary<Guid, HubSessionDocument>> LoadDocumentsAsync(
        HubClient hub, string bearer, IEnumerable<Guid> ids, CancellationToken ct)
    {
        var result = new Dictionary<Guid, HubSessionDocument>();
        await Parallel.ForEachAsync(ids, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct },
            async (id, token) =>
            {
                var doc = await hub.GetAsync<HubSessionDocument>($"/sessions/{id:D}", bearer, token);
                if (doc is null)
                    return;
                lock (result)
                    result[id] = doc;
            });
        return result;
    }

    private static IReadOnlyList<ObservabilityChat> BuildExchange(
        InputRequestKind? inputKind, string? question, string? answer, string? permissionTool,
        DateTimeOffset? blockedAt)
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
            list.Add(new ObservabilityChat("Lead", null, answer));
        return list;
    }

    private static double Pct(DateTimeOffset at, DateTimeOffset windowStart, DateTimeOffset now)
    {
        var window = (now - windowStart).TotalSeconds;
        if (window <= 0)
            return 0;
        var p = (at - windowStart).TotalSeconds / window * 100;
        return Math.Clamp(p, 0, 100);
    }
}
