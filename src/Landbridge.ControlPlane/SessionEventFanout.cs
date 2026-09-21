using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Landbridge.ControlPlane;

/// <summary>
/// A second LISTEN connection that fans session NOTIFYs out to Lead inbox
/// subscribers. Dispatch owns <see cref="SessionEventListener"/> as a
/// one-consumer pump; this type is a separate connection so inbox SSE
/// cannot stall dispatch, and so a slow snapshot cannot stall other Leads.
///
/// <para>Wakes are coalesced per subscriber (single-slot, drop-write). A
/// session-filtered subscription only wakes on that session's NOTIFY.</para>
/// </summary>
public sealed class SessionEventFanout : IHostedService, IAsyncDisposable
{
    private readonly SessionEventListener _listener;
    private readonly ILogger<SessionEventFanout> _logger;
    private readonly ConcurrentDictionary<Guid, Subscription> _subscribers = new();

    private CancellationTokenSource? _cts;
    private Task? _pump;

    public SessionEventFanout(string connectionString, ILogger<SessionEventFanout>? logger = null)
    {
        _listener = new SessionEventListener(connectionString);
        _logger = logger ?? NullLogger<SessionEventFanout>.Instance;
    }

    /// <summary>Completes once this instance's LISTEN has registered.</summary>
    public Task WhenListening => _listener.Listening;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = new CancellationTokenSource();
        _pump = Task.Run(() => PumpAsync(_cts.Token), _cts.Token);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Idempotent, because this type is both the hosted service and an
    /// <see cref="IAsyncDisposable"/> the container owns: a shutting-down host stops it
    /// and then disposes it, and <see cref="DisposeAsync"/> stops it again. Reading
    /// <c>_cts</c> twice let the second caller past the guard and onto a field the first
    /// had already cleared. Claiming it in one step means exactly one caller does the
    /// teardown and the rest return.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _cts, null) is not { } cts)
            return;
        await cts.CancelAsync();
        if (_pump is not null)
        {
            try { await _pump; }
            catch (OperationCanceledException) { }
        }
        foreach (var sub in _subscribers.Values)
            sub.Complete();
        _subscribers.Clear();
        await _listener.DisposeAsync();
        cts.Dispose();
    }

    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None);

    /// <summary>
    /// Registers immediately. Dispose to unregister. Wakes are coalesced
    /// (single-slot, drop-write). <paramref name="sessionId"/> limits wakes
    /// to that session so a filtered feed is not stolen by another row.
    /// </summary>
    public Subscription Subscribe(Guid sessionId) =>
        Subscribe((IReadOnlySet<Guid>)new HashSet<Guid> { sessionId });

    public Subscription Subscribe(Guid? sessionId = null) =>
        sessionId is { } id ? Subscribe(id) : Subscribe((IReadOnlySet<Guid>?)null);

    public Subscription Subscribe(IReadOnlySet<Guid>? sessionIds)
    {
        var channel = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
        });
        var id = Guid.NewGuid();
        var sub = new Subscription(this, id, channel, sessionIds);
        _subscribers[id] = sub;
        return sub;
    }

    private void Unsubscribe(Guid id)
    {
        if (_subscribers.TryRemove(id, out var sub))
            sub.Complete();
    }

    public sealed class Subscription : IDisposable
    {
        private readonly SessionEventFanout _owner;
        private readonly Guid _id;
        private readonly Channel<bool> _channel;
        private readonly IReadOnlySet<Guid>? _sessionIds;
        private int _disposed;

        internal Subscription(
            SessionEventFanout owner, Guid id, Channel<bool> channel, IReadOnlySet<Guid>? sessionIds)
        {
            _owner = owner;
            _id = id;
            _channel = channel;
            _sessionIds = sessionIds is { Count: > 0 } ? sessionIds : null;
        }

        public ChannelReader<bool> Reader => _channel.Reader;

        internal bool Matches(Guid sessionId) => _sessionIds is null || _sessionIds.Contains(sessionId);

        internal bool TryWake() => _channel.Writer.TryWrite(true);

        internal void Complete() => _channel.Writer.TryComplete();

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            _owner.Unsubscribe(_id);
        }
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var sessionId in _listener.ListenAsync(ct))
            {
                foreach (var sub in _subscribers.Values)
                {
                    if (sub.Matches(sessionId))
                        sub.TryWake();
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // expected on shutdown
        }
        catch (Exception e)
        {
            _logger.LogError(e, "lead inbox notify fan-out crashed");
        }
        finally
        {
            foreach (var sub in _subscribers.Values)
                sub.Complete();
        }
    }
}
