using System.Collections.Concurrent;
using System.Net.WebSockets;
using Docket.Contracts;
using static Docket.Relay.Tests.RelayTestKit;

namespace Docket.Relay.Tests;

/// <summary>
/// The §9 check 10 / §9.10 byte counter at the relay's splice pump, over the real wire.
///
/// <para>What these pin is as much about what the counter must <b>not</b> do as what it counts.
/// The relay still moves opaque bytes and interprets nothing (§8.3, design principle 1): every
/// frame is forwarded verbatim regardless of any count, nothing here can refuse traffic, and no
/// byte ceiling exists — §8.3 forbids severing an established splice mid-flight. Counting is the
/// whole feature.</para>
/// </summary>
public sealed class RelayByteCounterTests
{
    private static CancellationToken Timeout(out CancellationTokenSource cts)
    {
        cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        return cts.Token;
    }

    [Fact]
    public async Task Both_directions_are_counted_against_the_forward_id()
    {
        // Summed across directions and keyed by forward id — the only identifier the relay has.
        // It never learns which Team it is moving bytes for; the plane resolves that from the
        // forward id it minted (§8.3).
        var ct = Timeout(out var cts);
        using var _ = cts;
        var usage = new RecordingUsageReporter();
        await using var host = await RelayTestHost.StartAsync(DelegateGrantValidator.Accept, usage: usage);
        const string forwardId = "fwd-bytes";

        var consumer = await host.ConnectAsync(forwardId, GoodGrant, "consumer", ct);
        var producer = await host.ConnectAsync(forwardId, GoodGrant, "producer", ct);

        var up = RandomBytes(3000);
        var down = RandomBytes(5000);
        await SendAsync(consumer, up, ct);
        AssertBytesEqual(up, await ReceiveExactlyAsync(producer, up.Length, ct));
        await SendAsync(producer, down, ct);
        AssertBytesEqual(down, await ReceiveExactlyAsync(consumer, down.Length, ct));

        // Close and let teardown run: the on-close flush is what makes a short forward visible
        // at all, since it can finish entirely between two timer ticks.
        await consumer.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", ct);
        await WaitForAsync(() => usage.Flushes > 0, ct);

        Assert.Equal(up.Length + down.Length, usage.TotalFor(forwardId));
        consumer.Dispose();
        producer.Dispose();
    }

    [Fact]
    public async Task Each_forward_id_is_counted_separately()
    {
        // A preview mints a forward per browser connection (§8.4), so concurrent forwards are the
        // normal case and must not pool into one another's counts.
        var ct = Timeout(out var cts);
        using var _ = cts;
        var usage = new RecordingUsageReporter();
        await using var host = await RelayTestHost.StartAsync(DelegateGrantValidator.Accept, usage: usage);

        var first = RandomBytes(1024);
        var second = RandomBytes(4096);

        var c1 = await host.ConnectAsync("fwd-a", GoodGrant, "consumer", ct);
        var p1 = await host.ConnectAsync("fwd-a", GoodGrant, "producer", ct);
        var c2 = await host.ConnectAsync("fwd-b", GoodGrant, "consumer", ct);
        var p2 = await host.ConnectAsync("fwd-b", GoodGrant, "producer", ct);

        await SendAsync(c1, first, ct);
        AssertBytesEqual(first, await ReceiveExactlyAsync(p1, first.Length, ct));
        await SendAsync(c2, second, ct);
        AssertBytesEqual(second, await ReceiveExactlyAsync(p2, second.Length, ct));

        await c1.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", ct);
        await c2.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", ct);
        await WaitForAsync(() => usage.Flushes >= 2, ct);

        Assert.Equal(first.Length, usage.TotalFor("fwd-a"));
        Assert.Equal(second.Length, usage.TotalFor("fwd-b"));
        foreach (var socket in new[] { c1, p1, c2, p2 })
            socket.Dispose();
    }

    [Fact]
    public async Task A_relay_with_no_reporter_still_splices()
    {
        // The default posture: no control plane configured means nothing to report to, and the
        // NoOp reporter stands. Byte accounting gates nothing, so its absence must be invisible
        // to traffic — unlike grant validation, where absence means refuse.
        var ct = Timeout(out var cts);
        using var _ = cts;
        await using var host = await RelayTestHost.StartAsync(DelegateGrantValidator.Accept);
        const string forwardId = "fwd-noop";

        using var consumer = await host.ConnectAsync(forwardId, GoodGrant, "consumer", ct);
        using var producer = await host.ConnectAsync(forwardId, GoodGrant, "producer", ct);

        var payload = RandomBytes(2048);
        await SendAsync(consumer, payload, ct);
        AssertBytesEqual(payload, await ReceiveExactlyAsync(producer, payload.Length, ct));
    }

    [Fact]
    public async Task A_reporter_that_throws_on_flush_does_not_break_teardown()
    {
        // Best-effort means best-effort: a plane that is down, slow, or returning nonsense must
        // not be able to wedge a splice's teardown or leak its forward id. This asserts the
        // registry still empties, which is the leak invariant §8.3 cares about.
        var ct = Timeout(out var cts);
        using var _ = cts;
        await using var host = await RelayTestHost.StartAsync(
            DelegateGrantValidator.Accept, usage: new ThrowingUsageReporter());
        const string forwardId = "fwd-throws";

        var consumer = await host.ConnectAsync(forwardId, GoodGrant, "consumer", ct);
        var producer = await host.ConnectAsync(forwardId, GoodGrant, "producer", ct);

        var payload = RandomBytes(1500);
        await SendAsync(consumer, payload, ct);
        AssertBytesEqual(payload, await ReceiveExactlyAsync(producer, payload.Length, ct));

        await consumer.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", ct);
        await WaitForAsync(() => host.Registry.ActiveForwardCount == 0, ct);

        Assert.Equal(0, host.Registry.ActiveForwardCount);
        consumer.Dispose();
        producer.Dispose();
    }

    private static async Task WaitForAsync(Func<bool> condition, CancellationToken ct)
    {
        while (!condition())
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(20, ct);
        }
    }

    /// <summary>An <see cref="IForwardUsageReporter"/> that keeps what the pumps counted, draining
    /// on flush exactly as the real one does so the test sees the same totals the plane would.</summary>
    private sealed class RecordingUsageReporter : IForwardUsageReporter
    {
        private readonly ConcurrentDictionary<string, ForwardByteCounter> _counters = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, long> _drained = new(StringComparer.Ordinal);

        public int Flushes;

        public ForwardByteCounter CounterFor(string forwardId) =>
            _counters.GetOrAdd(forwardId, static _ => new ForwardByteCounter());

        public Task FlushAsync(CancellationToken ct)
        {
            foreach (var (forwardId, counter) in _counters)
            {
                var bytes = Interlocked.Exchange(ref counter.Bytes, 0);
                if (bytes > 0)
                    _drained.AddOrUpdate(forwardId, bytes, (_, total) => total + bytes);
            }
            Interlocked.Increment(ref Flushes);
            return Task.CompletedTask;
        }

        /// <summary>Everything flushed so far plus anything still pending, so the assertion does
        /// not depend on flush timing.</summary>
        public long TotalFor(string forwardId) =>
            _drained.GetValueOrDefault(forwardId)
            + (_counters.TryGetValue(forwardId, out var c) ? Interlocked.Read(ref c.Bytes) : 0);
    }

    private sealed class ThrowingUsageReporter : IForwardUsageReporter
    {
        public ForwardByteCounter CounterFor(string forwardId) => new();

        public Task FlushAsync(CancellationToken ct) =>
            throw new HttpRequestException("the plane is down");
    }
}
