using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Landbridge.Relay;

/// <summary>
/// Reports per-forward byte counts to the control plane (spec §9 check 10 / §9.10), over the
/// plane-facing HTTP contract that already carries grant validation —
/// <c>{Url}/relay/usage</c>, same shared bearer, same options section. Deliberately <b>not</b>
/// the frozen runner wire (§10): the relay is not a runner, does not speak that vocabulary, and
/// that closed enum must not grow a member for a component outside it.
///
/// <para><b>Best-effort, and the opposite posture to
/// <see cref="ControlPlaneGrantValidator"/>.</b> Validation fails closed because a wrong answer
/// admits a tunnel; this fails <em>open and silent</em> because a lost report costs a human some
/// visibility and nothing else. A relay that dies loses whatever it had not sent, an unreachable
/// plane drops that interval's bytes rather than buffering forever, and neither is allowed to
/// interfere with splicing. That is precisely why §9.10 calls this a containment signal and not
/// an invoice.</para>
///
/// <para>Runs as a hosted service so the flush timer belongs to the host's lifetime and the
/// final flush happens on shutdown, which is the one moment a graceful relay can still report
/// its tail.</para>
/// </summary>
public sealed class ControlPlaneForwardUsageReporter : IForwardUsageReporter, IHostedService
{
    private readonly ConcurrentDictionary<string, ForwardByteCounter> _counters = new(StringComparer.Ordinal);
    private readonly IHttpClientFactory _httpFactory;
    private readonly ControlPlaneGrantValidatorOptions _options;
    private readonly ILogger<ControlPlaneForwardUsageReporter> _logger;
    private readonly string _usageUrl;
    private readonly TimeProvider _clock;

    private CancellationTokenSource? _cts;
    private ITimer? _timer;

    public ControlPlaneForwardUsageReporter(
        IHttpClientFactory httpFactory,
        IOptions<ControlPlaneGrantValidatorOptions> options,
        ILogger<ControlPlaneForwardUsageReporter> logger,
        TimeProvider? clock = null)
    {
        _httpFactory = httpFactory;
        _options = options.Value;
        _logger = logger;
        _clock = clock ?? TimeProvider.System;
        _usageUrl = (_options.Url ?? "").TrimEnd('/') + "/relay/usage";
    }

    /// <summary>
    /// How often accumulated bytes are sent. Frequent enough that a long-lived splice is
    /// visible while it runs, infrequent enough that a busy relay is not chattering at the
    /// plane — this is a number a human reads on a dashboard, not a control loop.
    /// </summary>
    public static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(30);

    public ForwardByteCounter CounterFor(string forwardId) =>
        _counters.GetOrAdd(forwardId, static _ => new ForwardByteCounter());

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _timer = _clock.CreateTimer(_ => _ = FlushSafeAsync(ct), null, FlushInterval, FlushInterval);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_timer is not null)
            await _timer.DisposeAsync();
        // The tail: a graceful shutdown is the last chance to report what is still buffered.
        // Uses the caller's token, not the cancelled one, so shutdown does not cancel its own
        // final flush.
        await FlushSafeAsync(cancellationToken);
        if (_cts is not null)
        {
            await _cts.CancelAsync();
            _cts.Dispose();
        }
    }

    private async Task FlushSafeAsync(CancellationToken ct)
    {
        try { await FlushAsync(ct); }
        catch (Exception e)
        {
            // Unreachable in practice — FlushAsync swallows its own failures — but a throw out
            // of a timer callback would take the process down, and losing byte counts must
            // never do that.
            _logger.LogWarning(e, "relay usage flush failed");
        }
    }

    public async Task FlushAsync(CancellationToken ct)
    {
        // Drain by exchange, so traffic arriving mid-flush accumulates into the next batch
        // instead of being double-counted or lost. Entries are kept rather than removed: a live
        // forward's counter is held by its pump, and removing it would orphan that reference.
        var batch = new List<object>();
        foreach (var (forwardId, counter) in _counters)
        {
            var bytes = Interlocked.Exchange(ref counter.Bytes, 0);
            if (bytes > 0)
                batch.Add(new { forwardId, bytes });
        }
        if (batch.Count == 0)
            return;

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_options.Timeout);

            var client = _httpFactory.CreateClient(ControlPlaneGrantValidator.HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Post, _usageUrl)
            {
                Content = JsonContent.Create(new { reports = batch }),
            };
            if (!string.IsNullOrEmpty(_options.Bearer))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.Bearer);

            using var response = await client.SendAsync(request, timeoutCts.Token);
            if (!response.IsSuccessStatusCode)
                _logger.LogWarning(
                    "control-plane usage report returned {Status}; {Count} forward counts dropped",
                    (int)response.StatusCode, batch.Count);
        }
        catch (Exception e)
        {
            // Dropped, not retried, and never rethrown (§9.10 best-effort). Retrying would mean
            // buffering unboundedly against an unreachable plane, and the whole point of this
            // number is that nothing depends on it.
            _logger.LogWarning(e, "control-plane usage report failed; {Count} forward counts dropped", batch.Count);
        }
    }
}
