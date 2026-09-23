using System.Threading.Channels;
using Landbridge.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Landbridge.ControlPlane;

/// <summary>
/// Outbound runner commands. One row per send. Unacked rows replay on
/// <c>GET /runner/events</c>. A successful WebSocket write acks the row so
/// today's socket runners are not asked to run the command twice.
/// </summary>
public sealed class RunnerOutboxRow
{
    public long Id { get; set; }
    public Guid MachineId { get; set; }
    public Guid? SessionId { get; set; }
    public string Kind { get; set; } = "";
    public string Payload { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? AckedAt { get; set; }
}

public sealed class RunnerOutbox(IServiceScopeFactory scopes, TimeProvider clock)
{
    private readonly object _gate = new();
    private readonly List<Channel<long>> _subscribers = [];

    public async Task<long> EnqueueAsync(Guid machineId, RunnerCommand command, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LandbridgeDbContext>();
        var row = new RunnerOutboxRow
        {
            MachineId = machineId,
            SessionId = SessionOf(command),
            Kind = KindOf(command),
            Payload = RunnerWire.EncodeCommand(command),
            CreatedAt = clock.GetUtcNow(),
        };
        db.Set<RunnerOutboxRow>().Add(row);
        await db.SaveChangesAsync(ct);
        Publish(row.Id);
        return row.Id;
    }

    public async Task<bool> AckAsync(Guid machineId, long id, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LandbridgeDbContext>();
        var row = await db.Set<RunnerOutboxRow>().FirstOrDefaultAsync(r => r.Id == id, ct);
        if (row is null || row.MachineId != machineId)
            return false;
        if (row.AckedAt is null)
        {
            row.AckedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
        }
        return true;
    }

    public async Task<IReadOnlyList<RunnerOutboxRow>> UnackedAsync(Guid machineId, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LandbridgeDbContext>();
        return await db.Set<RunnerOutboxRow>().AsNoTracking()
            .Where(r => r.MachineId == machineId && r.AckedAt == null)
            .OrderBy(r => r.Id)
            .ToListAsync(ct);
    }

    public async Task<RunnerOutboxRow?> FindAsync(long id, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LandbridgeDbContext>();
        return await db.Set<RunnerOutboxRow>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
    }

    /// <summary>Ids enqueued after this call. Subscribe before the snapshot.</summary>
    public ChannelReader<long> Subscribe(out IDisposable unsubscribe)
    {
        var channel = Channel.CreateUnbounded<long>();
        lock (_gate)
            _subscribers.Add(channel);
        unsubscribe = new Unsub(() =>
        {
            lock (_gate)
                _subscribers.Remove(channel);
            channel.Writer.TryComplete();
        });
        return channel.Reader;
    }

    private void Publish(long id)
    {
        List<Channel<long>> copy;
        lock (_gate)
            copy = [.. _subscribers];
        foreach (var channel in copy)
            channel.Writer.TryWrite(id);
    }

    private static Guid? SessionOf(RunnerCommand command) => command switch
    {
        DispatchCommand c => c.Session.Value,
        StopCommand c => c.Session.Value,
        KillCommand c => c.Session.Value,
        PromptCommand c => c.Session.Value,
        OpenForwardCommand c => c.Session.Value,
        CloseForwardCommand c => c.Session.Value,
        ReadTranscriptCommand c => c.Session.Value,
        StartProcessCommand c => c.Session.Value,
        StopProcessCommand c => c.Session.Value,
        WriteProcessCommand c => c.Session.Value,
        _ => null,
    };

    private static string KindOf(RunnerCommand command) => command switch
    {
        DispatchCommand => RunnerWire.Dispatch,
        StopCommand => RunnerWire.Stop,
        KillCommand => RunnerWire.Kill,
        PromptCommand => RunnerWire.Prompt,
        OpenForwardCommand => RunnerWire.OpenForward,
        CloseForwardCommand => RunnerWire.CloseForward,
        ReadTranscriptCommand => RunnerWire.ReadTranscript,
        StartProcessCommand => RunnerWire.StartProcess,
        StopProcessCommand => RunnerWire.StopProcess,
        WriteProcessCommand => RunnerWire.WriteProcess,
        _ => command.GetType().Name,
    };

    private sealed class Unsub(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
