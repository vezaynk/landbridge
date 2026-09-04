using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Landbridge.Hub;

/// <summary>
/// In-process doorbell after a <c>hub_queue</c> insert. Coalesce per subscriber
/// (single-slot drop-write); the queue rows themselves are not coalesced — SSE
/// catch-up SELECTs every id since <c>after</c>.
/// </summary>
public sealed class HubWaiters
{
    private readonly ConcurrentDictionary<Guid, Subscription> _subscribers = new();

    public Subscription Subscribe(string topic, Guid? entityId = null)
    {
        var channel = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
        });
        var id = Guid.NewGuid();
        var sub = new Subscription(this, id, channel, topic, entityId);
        _subscribers[id] = sub;
        return sub;
    }

    public void Wake(string topic, Guid? entityId)
    {
        foreach (var sub in _subscribers.Values)
        {
            if (sub.Matches(topic, entityId))
                sub.TryWake();
        }
    }

    private void Unsubscribe(Guid id)
    {
        if (_subscribers.TryRemove(id, out var sub))
            sub.Complete();
    }

    public sealed class Subscription : IDisposable
    {
        private readonly HubWaiters _owner;
        private readonly Guid _id;
        private readonly Channel<bool> _channel;
        private readonly string _topic;
        private readonly Guid? _entityId;
        private int _disposed;

        internal Subscription(
            HubWaiters owner, Guid id, Channel<bool> channel, string topic, Guid? entityId)
        {
            _owner = owner;
            _id = id;
            _channel = channel;
            _topic = topic;
            _entityId = entityId;
        }

        public ChannelReader<bool> Reader => _channel.Reader;

        internal bool Matches(string topic, Guid? entityId) =>
            topic == _topic && (_entityId is null || _entityId == entityId);

        internal bool TryWake() => _channel.Writer.TryWrite(true);

        internal void Complete() => _channel.Writer.TryComplete();

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            _owner.Unsubscribe(_id);
        }
    }
}
