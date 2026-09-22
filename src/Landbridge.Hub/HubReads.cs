using Landbridge.ControlPlane;
using Landbridge.ControlPlane.Auth;
using Microsoft.EntityFrameworkCore;

namespace Landbridge.Hub;

/// <summary>
/// Postgres-only reads for the hub JSON twins. No registry, no Apply.
/// Live is <c>machines.last_spoke_at</c> within 90s. Caller already authorized.
/// </summary>
public sealed class HubReads(LandbridgeDbContext db, TimeProvider clock)
{
    public const int DefaultLimit = 200;
    public const int MaxLimit = 1000;

    public async Task<IReadOnlyList<SessionListItem>> SessionsAsync(
        HubCaller caller, Guid? teamId, bool includeHidden, CancellationToken ct)
    {
        var q = db.Sessions.AsNoTracking().AsQueryable();
        if (caller.Principal is Principal.Worker)
            q = q.Where(s => s.Id == caller.WorkerSession);
        else if (caller.Teams is { } teams)
            q = q.Where(s => teams.Contains(s.TeamId));
        if (teamId is { } team)
            q = q.Where(s => s.TeamId == team);
        if (!includeHidden)
            q = q.Where(s => !s.Hidden);
        var rows = await q.OrderBy(s => s.Slug).Take(MaxLimit).ToListAsync(ct);
        var slugs = await TeamSlugsAsync(rows.Select(s => s.TeamId), ct);
        return rows.Select(s => new SessionListItem(
            s.Id, s.Slug, s.TeamId, slugs.GetValueOrDefault(s.TeamId), s.Profile,
            s.State, s.OccupancyDesired, s.OccupancyObserved, s.Health, s.Hidden,
            s.MessageState, s.PendingSpawn, s.ReportUnread, s.MessageId, s.InputKind,
            s.BlockedAt, s.ParkMachine, s.CurrentInstanceId, s.MessageOpenedAt,
            s.LastMessageClosedAt, s.Namespace, s.Attempt,
            s.WorkerReport != null, s.BlockedAt != null && s.InputKind != null,
            s.ContinuesSessionId, s.CompletionProvenance,
            s.InfrastructureRequeues, s.LastRequeueReason)).ToList();
    }

    public async Task<SessionDocument?> SessionAsync(Guid id, CancellationToken ct)
    {
        var s = await db.Sessions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (s is null)
            return null;
        var teamSlug = await db.LeadTeams.AsNoTracking()
            .Where(t => t.TeamId == s.TeamId)
            .Select(t => t.Slug)
            .FirstOrDefaultAsync(ct);
        var instances = await db.Set<WorkerInstanceRow>().AsNoTracking()
            .Where(i => i.SessionId == id)
            .OrderBy(i => i.CreatedAt)
            .Select(i => new InstanceDocument(i.Id, i.SessionId, i.Revoked, i.CreatedAt, i.MachineId))
            .ToListAsync(ct);
        var usage = await db.SessionUsage.AsNoTracking()
            .Where(u => u.SessionId == id)
            .Select(u => new UsageDocument(
                u.SessionId, u.Model, u.InputTokens, u.OutputTokens,
                u.CacheReadTokens, u.CacheWriteTokens, u.ReasoningOutputTokens,
                u.CostUsd, u.ReportedAt))
            .ToListAsync(ct);
        return new SessionDocument(
            s.Id, s.Slug, s.TeamId, string.IsNullOrEmpty(teamSlug) ? null : teamSlug,
            s.Namespace, s.Profile, s.State, s.OccupancyDesired, s.OccupancyObserved,
            s.Health, s.Hidden, s.MessageState, s.MessageVerdict, s.ReportUnread,
            s.MessageId, s.LastMessageId, s.LastMessageTerminal, s.MessageOpenedAt,
            s.LastMessageClosedAt, s.PendingSpawn, s.Description, s.ResultReference, s.WorkerReport,
            s.InputKind, s.InputQuestion, s.InputAnswer, s.PermissionTool, s.PermissionOptions,
            s.PermissionOptionId, s.PermissionVerdict, s.PermissionEscalatedAt,
            s.PermissionEscalationReason, s.Attempt, s.InfrastructureRequeues,
            s.InfrastructureRequeueLimit, s.LastRequeueReason, s.CompletionProvenance,
            s.ContinuesSessionId, s.CurrentInstanceId, s.PreferredMachine, s.ParkMachine,
            s.BlockedAt, instances, usage);
    }

    public async Task<ExchangeDocument?> ExchangeAsync(Guid id, CancellationToken ct)
    {
        var s = await db.Sessions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (s is null)
            return null;
        return new ExchangeDocument(
            s.Id, s.Slug, s.MessageState, s.ReportUnread, s.InputKind, s.InputQuestion,
            s.InputAnswer, s.ResultReference, s.WorkerReport, s.PermissionTool,
            s.PermissionOptions, s.PermissionOptionId, s.PermissionVerdict);
    }

    public async Task<IReadOnlyList<EventDocument>?> LogAsync(Guid sessionId, int limit, CancellationToken ct)
    {
        if (!await db.Sessions.AsNoTracking().AnyAsync(s => s.Id == sessionId, ct))
            return null;
        limit = Clamp(limit);
        return await db.SessionEvents.AsNoTracking()
            .Where(e => e.SessionId == sessionId)
            .OrderByDescending(e => e.Seq)
            .Take(limit)
            .Select(e => new EventDocument(
                e.Seq, e.OccurredAt, e.Kind, e.FromState, e.ToState, e.Detail,
                e.SessionId, e.TeamId, e.InputKind, e.LivenessReason,
                e.PermissionVerdict, e.PermissionAnswerer,
                e.AuthOperation, e.AuthTarget, e.AuthErrorCode, e.AuthMissingScope,
                e.SubagentId, e.SubagentParentId))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<ServiceDocument>> ServicesAsync(
        HubCaller caller, Guid? teamId, Guid? sessionId, CancellationToken ct)
    {
        var q = db.RegisteredServices.AsNoTracking().AsQueryable();
        if (caller.Principal is Principal.Worker)
            q = q.Where(s => s.SessionId == caller.WorkerSession);
        else if (caller.Teams is { } teams)
            q = q.Where(s => teams.Contains(s.TeamId));
        if (teamId is { } team)
            q = q.Where(s => s.TeamId == team);
        if (sessionId is { } session)
            q = q.Where(s => s.SessionId == session);
        return await q.OrderBy(s => s.Seq)
            .Take(MaxLimit)
            .Select(s => new ServiceDocument(s.Seq, s.SessionId, s.TeamId, s.Name, s.Port, s.CreatedAt))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<ForwardDocument>> ForwardsAsync(
        HubCaller caller, Guid? teamId, CancellationToken ct)
    {
        var q = db.Set<RelayGrantRow>().AsNoTracking().AsQueryable();
        if (caller.Principal is Principal.Worker)
            q = q.Where(g => g.ProducerSessionId == caller.WorkerSession
                || g.ConsumerSessionId == caller.WorkerSession);
        else if (caller.Teams is { } teams)
            q = q.Where(g => teams.Contains(g.TeamId));
        if (teamId is { } team)
            q = q.Where(g => g.TeamId == team);
        var rows = await q.OrderByDescending(g => g.CreatedAt)
            .Take(MaxLimit)
            .ToListAsync(ct);
        return rows.Select(ToForward).ToList();
    }

    public async Task<ForwardDocument?> ForwardAsync(Guid forwardId, CancellationToken ct)
    {
        var g = await db.Set<RelayGrantRow>().AsNoTracking()
            .FirstOrDefaultAsync(x => x.ForwardId == forwardId, ct);
        return g is null ? null : ToForward(g);
    }

    public async Task<IReadOnlyList<PreviewDocument>> PreviewsAsync(
        HubCaller caller, Guid? teamId, CancellationToken ct)
    {
        var q = db.Set<PreviewMappingRow>().AsNoTracking().AsQueryable();
        if (caller.Principal is Principal.Worker)
            q = q.Where(p => p.SessionId == caller.WorkerSession);
        else if (caller.Teams is { } teams)
            q = q.Where(p => teams.Contains(p.TeamId));
        if (teamId is { } team)
            q = q.Where(p => p.TeamId == team);
        var rows = await q.OrderByDescending(p => p.ExpiresAt)
            .Take(MaxLimit)
            .ToListAsync(ct);
        return rows.Select(ToPreview).ToList();
    }

    public async Task<PreviewDocument?> PreviewAsync(Guid id, CancellationToken ct)
    {
        var p = await db.Set<PreviewMappingRow>().AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id, ct);
        return p is null ? null : ToPreview(p);
    }

    public async Task<IReadOnlyList<MachineDocument>> MachinesAsync(HubCaller caller, CancellationToken ct)
    {
        var rows = await db.Machines.AsNoTracking()
            .Where(m => !m.Revoked)
            .OrderBy(m => m.Name)
            .Take(MaxLimit)
            .ToListAsync(ct);
        return await AttachMachineAsync(rows, includeProcesses: caller.MayProcesses, ct);
    }

    public async Task<MachineDocument?> MachineAsync(HubCaller caller, Guid id, CancellationToken ct)
    {
        var row = await db.Machines.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id, ct);
        if (row is null)
            return null;
        var list = await AttachMachineAsync([row], includeProcesses: caller.MayProcesses, ct);
        return list[0];
    }

    public async Task<IReadOnlyList<ProcessDocument>> ProcessesAsync(
        HubCaller caller, Guid? machineId, CancellationToken ct)
    {
        if (!caller.MayProcesses && caller.Principal is not Principal.Worker)
            return [];
        var q = db.MachineProcesses.AsNoTracking().AsQueryable();
        if (machineId is { } machine)
            q = q.Where(p => p.MachineId == machine);
        var rows = await q.OrderBy(p => p.Name)
            .Take(MaxLimit)
            .ToListAsync(ct);
        return rows.Select(ToProcess).ToList();
    }

    public async Task<ProcessDocument?> ProcessAsync(Guid id, CancellationToken ct)
    {
        var p = await db.MachineProcesses.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        return p is null ? null : ToProcess(p);
    }

    public async Task<IReadOnlyList<TeamDocument>> TeamsAsync(HubCaller caller, CancellationToken ct)
    {
        var q = db.LeadTeams.AsNoTracking().AsQueryable();
        if (caller.Teams is { } owned)
            q = q.Where(t => owned.Contains(t.TeamId));
        var teams = await q
            .OrderBy(t => t.Slug)
            .Take(MaxLimit)
            .ToListAsync(ct);
        var ids = teams.Select(t => t.TeamId).ToArray();
        var usage = ids.Length == 0
            ? new Dictionary<Guid, TeamForwardUsageRow>()
            : await db.TeamForwardUsage.AsNoTracking()
                .Where(u => ids.Contains(u.TeamId))
                .ToDictionaryAsync(u => u.TeamId, ct);
        return teams.Select(t => ToTeam(t, usage.GetValueOrDefault(t.TeamId))).ToList();
    }

    public async Task<TeamDocument?> TeamAsync(Guid id, CancellationToken ct)
    {
        var t = await db.LeadTeams.AsNoTracking().FirstOrDefaultAsync(x => x.TeamId == id, ct);
        if (t is null)
            return null;
        var usage = await db.TeamForwardUsage.AsNoTracking()
            .FirstOrDefaultAsync(u => u.TeamId == id, ct);
        return ToTeam(t, usage);
    }

    public async Task<IReadOnlyList<FrictionDocument>> FrictionAsync(
        HubCaller caller, Guid? teamId, CancellationToken ct)
    {
        var q = db.FrictionReports.AsNoTracking().AsQueryable();
        if (caller.Teams is { } teams)
            q = q.Where(f => teams.Contains(f.TeamId));
        if (teamId is { } team)
            q = q.Where(f => f.TeamId == team);
        return await q.OrderByDescending(f => f.Seq)
            .Take(DefaultLimit)
            .Select(f => new FrictionDocument(f.Seq, f.At, f.Role, f.TeamId, f.SessionId, f.HumanId, f.Message))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<LeadEventDocument>> LeadEventsAsync(
        HubCaller caller, Guid? teamId, CancellationToken ct)
    {
        var q = db.Set<LeadEventRow>().AsNoTracking().AsQueryable();
        if (caller.Teams is { } teams)
            q = q.Where(e => teams.Contains(e.TeamId));
        if (teamId is { } team)
            q = q.Where(e => e.TeamId == team);
        var rows = await q.OrderByDescending(e => e.Seq)
            .Take(DefaultLimit)
            .ToListAsync(ct);
        return rows.Select(e => new LeadEventDocument(
            e.Seq, e.TeamId, e.Kind.ToString(), e.HumanId, e.PriorHumanId, e.OccurredAt)).ToList();
    }

    private async Task<IReadOnlyList<MachineDocument>> AttachMachineAsync(
        List<MachineRow> rows, bool includeProcesses, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var cutoff = now - WaitTtlSweeper.DefaultMachineLivenessWindow;
        var ids = rows.Select(m => m.Id).ToArray();
        var bound = ids.Length == 0
            ? new Dictionary<Guid, LeadMachineBindingRow>()
            : await db.LeadMachineBindings.AsNoTracking()
                .Where(b => !b.Revoked && ids.Contains(b.MachineId))
                .ToDictionaryAsync(b => b.MachineId, ct);
        Dictionary<Guid, List<ProcessDocument>> processes = [];
        if (includeProcesses && ids.Length > 0)
        {
            processes = (await db.MachineProcesses.AsNoTracking()
                    .Where(p => ids.Contains(p.MachineId))
                    .ToListAsync(ct))
                .GroupBy(p => p.MachineId)
                .ToDictionary(g => g.Key, g => g.Select(ToProcess).ToList());
        }

        return rows.Select(m =>
        {
            bound.TryGetValue(m.Id, out var b);
            var live = m.LastSpokeAt is { } at && at >= cutoff;
            return new MachineDocument(
                m.Id, m.Slug, m.Name, m.Os, m.EnrolledAt, m.Revoked, m.LastSpokeAt,
                m.Ready, m.UnderBackPressure, live, m.Profiles,
                b?.HumanId, b?.BoundAt,
                processes.GetValueOrDefault(m.Id) ?? []);
        }).ToList();
    }

    private async Task<Dictionary<Guid, string>> TeamSlugsAsync(IEnumerable<Guid> teamIds, CancellationToken ct)
    {
        var ids = teamIds.Distinct().ToArray();
        if (ids.Length == 0)
            return [];
        return await db.LeadTeams.AsNoTracking()
            .Where(t => ids.Contains(t.TeamId))
            .ToDictionaryAsync(t => t.TeamId, t => t.Slug, ct);
    }

    private static ForwardDocument ToForward(RelayGrantRow g) => new(
        g.ForwardId, g.TeamId, g.ProducerSessionId, g.ConsumerSessionId, g.ServiceName,
        g.CreatedAt, g.ExpiresAt, g.UsedByConsumerAt, g.UsedByProducerAt, g.Revoked,
        g.ConsumerMachine, g.ConsumerPort);

    private static PreviewDocument ToPreview(PreviewMappingRow p) => new(
        p.Id, p.Label, p.TeamId, p.SessionId, p.ServiceName, p.AuthPolicy, p.Ttl, p.ExpiresAt);

    private static ProcessDocument ToProcess(MachineProcessRow p) => new(
        p.Id, p.MachineId, p.Name, p.State, p.DeclaredBySession,
        p.StartedAt, p.ExitCode, p.ExitedAt, p.StdinOpen);

    private static TeamDocument ToTeam(LeadTeamRow t, TeamForwardUsageRow? usage) => new(
        t.TeamId, t.Slug, t.LeadCredentialId, t.CreatedAt,
        usage?.ForwardedBytes ?? 0, usage?.UpdatedAt);

    private static int Clamp(int limit) =>
        limit < 1 ? DefaultLimit : Math.Min(limit, MaxLimit);
}
