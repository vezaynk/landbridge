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

    /// <summary>
    /// Live streams, keyed by the machine they serve. Keyed rather than flat so one
    /// command wakes the one stream it is addressed to: a flat list wakes every
    /// connected runner and makes each of them ask the database whether the row was
    /// even theirs, which is a query per machine per command.
    /// </summary>
    private readonly Dictionary<Guid, List<Channel<RunnerOutboxRow>>> _subscribers = [];

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
        // A detached copy: `row` belongs to the scope disposed on the way out of this
        // method, and a subscriber reads it on another thread long after that.
        Publish(new RunnerOutboxRow
        {
            Id = row.Id,
            MachineId = row.MachineId,
            SessionId = row.SessionId,
            Kind = row.Kind,
            Payload = row.Payload,
            CreatedAt = row.CreatedAt,
        });
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

    /// <summary>
    /// Rows enqueued for <paramref name="machineId"/> after this call. Subscribe
    /// before taking the snapshot: a row landing between the two then arrives on both,
    /// which the caller can drop, where the reverse would lose it.
    /// </summary>
    public ChannelReader<RunnerOutboxRow> Subscribe(Guid machineId, out IDisposable unsubscribe)
    {
        var channel = Channel.CreateUnbounded<RunnerOutboxRow>();
        lock (_gate)
        {
            if (!_subscribers.TryGetValue(machineId, out var streams))
                _subscribers[machineId] = streams = [];
            streams.Add(channel);
        }
        unsubscribe = new Unsub(() =>
        {
            lock (_gate)
            {
                // Drop the list with its last stream, so the map does not keep one entry
                // for every machine that has ever connected.
                if (_subscribers.TryGetValue(machineId, out var streams)
                    && streams.Remove(channel) && streams.Count == 0)
                    _subscribers.Remove(machineId);
            }
            channel.Writer.TryComplete();
        });
        return channel.Reader;
    }

    /// <summary>True once the row has been acknowledged — by a runner, or by the
    /// socket write that carried the same command down the WebSocket.</summary>
    public async Task<bool> IsAckedAsync(long id, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LandbridgeDbContext>();
        return await db.Set<RunnerOutboxRow>().AsNoTracking()
            .AnyAsync(r => r.Id == id && r.AckedAt != null, ct);
    }

    private void Publish(RunnerOutboxRow row)
    {
        List<Channel<RunnerOutboxRow>>? copy;
        lock (_gate)
            copy = _subscribers.TryGetValue(row.MachineId, out var streams) ? [.. streams] : null;
        foreach (var channel in copy ?? [])
            channel.Writer.TryWrite(row);
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
