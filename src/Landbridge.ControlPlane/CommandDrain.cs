using System.Text.Json;
using Landbridge.Contracts;
using Landbridge.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Landbridge.ControlPlane;

/// <summary>
/// Core-only SKIP LOCKED drain of <see cref="CommandRow"/>. Registry sends stay
/// here because <c>/runner</c> lives on Core.
/// </summary>
public sealed class CommandDrain(
    IServiceScopeFactory scopes,
    ILogger<CommandDrain> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await DrainOneAsync(stoppingToken))
                    await Task.Delay(TimeSpan.FromMilliseconds(200), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "command drain failed");
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
        }
    }

    public async Task<bool> DrainOneAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LandbridgeDbContext>();
        var store = scope.ServiceProvider.GetRequiredService<SessionStore>();
        var ids = scope.ServiceProvider.GetRequiredService<FriendlyIds>();
        var registry = scope.ServiceProvider.GetRequiredService<RunnerConnectionRegistry>();
        var clock = scope.ServiceProvider.GetRequiredService<TimeProvider>();

        var stale = clock.GetUtcNow() - TimeSpan.FromMinutes(1);
        await db.Commands
            .Where(c => c.Status == CommandRow.Running && c.ClaimedAt != null && c.ClaimedAt < stale)
            .ExecuteUpdateAsync(
                s => s.SetProperty(c => c.Status, CommandRow.Queued)
                    .SetProperty(c => c.ClaimedAt, (DateTimeOffset?)null),
                ct);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var claimedId = await db.Database
            .SqlQuery<Guid>(
                $"""
                 SELECT id AS "Value" FROM command_queue
                 WHERE status = {CommandRow.Queued}
                 ORDER BY accepted_at
                 FOR UPDATE SKIP LOCKED
                 LIMIT 1
                 """)
            .FirstOrDefaultAsync(ct);
        if (claimedId == Guid.Empty)
        {
            await tx.CommitAsync(ct);
            return false;
        }
        var row = await db.Commands.FirstAsync(c => c.Id == claimedId, ct);
        row.Status = CommandRow.Running;
        row.ClaimedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        try
        {
            await ApplyAsync(store, ids, registry, db, clock, row, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "command {Id} {Kind} failed", row.Id, row.Kind);
            var failed = await db.Commands.FirstAsync(c => c.Id == row.Id, ct);
            failed.Status = CommandRow.Rejected;
            failed.Reason = ex.Message;
            failed.AppliedAt = clock.GetUtcNow();
            HubOutbox.Stage(db, clock, HubQueueRow.CommandsTopic, failed.Id);
            await HubOutbox.SaveAndNotifyAsync(db, failed.SessionId, ct);
            return true;
        }

        return true;
    }

    private static async Task ApplyAsync(
        SessionStore store,
        FriendlyIds ids,
        RunnerConnectionRegistry registry,
        LandbridgeDbContext db,
        TimeProvider clock,
        CommandRow row,
        CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<CommandPayload>(row.Payload, CommandQueue.Json)
            ?? new CommandPayload();
        var session = new SessionId(row.SessionId);
        var team = new TeamId(row.TeamId);
        StoreResult result = row.Kind switch
        {
            CommandRow.CreateSession => await store.CreateAsync(
                new CreateSession(
                    new LeadClaim(team), team,
                    payload.Description ?? "", payload.Profile ?? "default",
                    session),
                ct),
            CommandRow.StopSession => await StopAsync(store, registry, session, new LeadClaim(team), payload, ct),
            CommandRow.ParkSession => await ParkAsync(store, registry, session, new LeadClaim(team), ct),
            CommandRow.InputResponse => await store.SendInputResponseAsync(
                new LeadClaim(team), session, registry.MachineFor(session), payload.Answer,
                registry.HasLiveProcess(session), ct),
            CommandRow.InputRequest => await store.SendInputRequestAsync(
                new LeadClaim(team), session, registry.MachineFor(session), payload.Text,
                registry.HasLiveProcess(session), ct),
            CommandRow.Permission => await store.AnswerPermissionAsync(
                row.ActorKind == CommandRow.HumanActor
                    ? new HumanSession()
                    : new LeadClaim(team),
                session, payload.Option ?? "", payload.Message, ct),
            CommandRow.Report => await store.ApplyAsync(
                session,
                new ReportResult(
                    new WorkerCaller(team, session, new WorkerInstanceId(payload.InstanceId ?? Guid.Empty)),
                    payload.ResultReference ?? "", payload.Report),
                ct),
            CommandRow.Ask => await AskAsync(store, session, team, payload, ct),
            CommandRow.RegisterService => await store.RegisterServiceAsync(
                new WorkerCaller(team, session, new WorkerInstanceId(payload.InstanceId ?? Guid.Empty)),
                payload.Name ?? "", payload.Port ?? 0, ct),
            CommandRow.PullReceipt => await store.ApplyAsync(
                session,
                new PullReceipt(new WorkerCaller(
                    team, session, new WorkerInstanceId(payload.InstanceId ?? Guid.Empty))),
                ct),
            _ => new StoreResult.Rejected(Rule.InvalidSourceState, $"unknown command kind {row.Kind}"),
        };

        row = await db.Commands.FirstAsync(c => c.Id == row.Id, ct);
        switch (result)
        {
            case StoreResult.Applied a:
                row.Status = CommandRow.Applied;
                row.Reason = a.Session.State.ToString();
                if (row.Kind == CommandRow.CreateSession)
                    row.Slug = await ids.SessionAsync(a.Session.Id.Value, ct);
                break;
            case StoreResult.Rejected r:
                row.Status = CommandRow.Rejected;
                row.Rule = r.Rule.ToString();
                row.Reason = r.Reason;
                break;
            case StoreResult.NotFound n:
                row.Status = CommandRow.Rejected;
                row.Reason = n.Reason;
                break;
            case StoreResult.Conflict c:
                row.Status = CommandRow.Rejected;
                row.Reason = c.Reason;
                break;
        }
        row.AppliedAt = clock.GetUtcNow();
        HubOutbox.Stage(db, clock, HubQueueRow.CommandsTopic, row.Id);
        await HubOutbox.SaveAndNotifyAsync(db, row.SessionId, ct);
    }

    private static async Task<StoreResult> StopAsync(
        SessionStore store, RunnerConnectionRegistry registry, SessionId session, LeadClaim lead,
        CommandPayload payload, CancellationToken ct)
    {
        var ttl = payload.TtlSeconds is null ? TimeSpan.FromMinutes(5)
            : TimeSpan.FromSeconds(Math.Max(0, payload.TtlSeconds.Value));
        var machine = registry.MachineFor(session);
        var applied = await store.ApplyAsync(session, new StopSession(lead), ct);
        if (applied is StoreResult.Applied ok
            && ok.Session.OccupancyObserved == Occupancy.Running
            && machine is not null)
        {
            await registry.SendAsync(machine.Value,
                new StopCommand(session, ttl, StopDisposition.Preserve, "stop"), ct);
        }
        return applied;
    }

    private static async Task<StoreResult> ParkAsync(
        SessionStore store, RunnerConnectionRegistry registry, SessionId session, LeadClaim lead,
        CancellationToken ct)
    {
        var machine = registry.MachineFor(session);
        if (machine is not { } parked)
            return new StoreResult.Rejected(Rule.InvalidSourceState,
                "this task is not tracked on any machine, so it cannot be parked");
        var result = await store.ApplyAsync(session, new Park(lead, new ParkRecord(parked)), ct);
        if (result is StoreResult.Applied)
        {
            await registry.SendAsync(parked,
                new StopCommand(session, TimeSpan.FromSeconds(30), StopDisposition.PreserveAndPark, "park"), ct);
        }
        return result;
    }

    private static async Task<StoreResult> AskAsync(
        SessionStore store, SessionId session, TeamId team, CommandPayload payload, CancellationToken ct)
    {
        if (!Enum.TryParse<InputRequestKind>(payload.Kind, ignoreCase: true, out var kind))
            return new StoreResult.Rejected(Rule.InvalidSourceState, "unknown input kind");
        return await store.ApplyAsync(
            session,
            new RequestInput(
                new WorkerCaller(team, session, new WorkerInstanceId(payload.InstanceId ?? Guid.Empty)),
                kind, payload.Text ?? payload.Answer),
            ct);
    }
}

public sealed record CommandPayload(
    string? Description = null,
    string? Profile = null,
    int? TtlSeconds = null,
    string? Answer = null,
    string? Text = null,
    string? Option = null,
    string? Message = null,
    string? ResultReference = null,
    string? Report = null,
    string? Kind = null,
    string? Name = null,
    int? Port = null,
    Guid? InstanceId = null,
    Guid? Nonce = null);
