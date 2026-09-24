using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Landbridge.Contracts;

namespace Landbridge.Runner;

/// <summary>
/// The runner's link to the control plane over plain HTTP (§10). Outbound only, like
/// everything landbridged does: frames go up as <c>POST /runner/ingest</c>, and commands
/// come down <c>GET /runner/events</c>, a response the plane holds open.
///
/// <para>That open response is the machine's connection: the plane registers it, dispatches
/// onto it, and requeues what it held when it ends. Heartbeats are ordinary POSTs and do
/// not depend on it — a machine whose stream has dropped keeps reporting, and is visible
/// to an operator as connected-but-not-dispatchable rather than simply gone.</para>
///
/// <para>Delivery is at-least-once and acknowledged: a command is acked only after the
/// handler returns, so one interrupted by a crash or a dropped stream is replayed from
/// <c>runner_outbox</c> on the next connect. A handler must therefore tolerate seeing the
/// same outbox id twice — the previous WebSocket transport acked on write and could lose
/// a command instead, which is the trade this replaces.</para>
/// </summary>
public sealed class HttpControlPlaneChannel : IControlPlaneChannel, IAsyncDisposable
{
    private readonly Uri _controlUrl;
    private readonly Func<string> _tokenProvider;
    private readonly Func<CancellationToken, Task>? _onAuthRejected;
    private readonly TimeProvider _clock;
    private readonly Action<string>? _log;
    private readonly HttpClient _http;
    private readonly CancellationTokenSource _cts = new();

    private volatile bool _streaming;
    private Func<RunnerCommand, CancellationToken, Task>? _onCommand;
    private Task? _connectLoop;

    /// <summary>
    /// Env-token mode (the Aspire dev loop): a fixed bearer, never refreshed. No
    /// auth-rejected hook, so a 401 backs off and retries with the same token.
    /// </summary>
    public HttpControlPlaneChannel(Uri controlUrl, string machineToken, TimeProvider clock, Action<string>? log = null)
        : this(controlUrl, () => machineToken, clock, log) { }

    /// <summary>
    /// File-credential mode (§5, §13): the bearer is read fresh from
    /// <paramref name="tokenProvider"/> on every request, so a token refreshed mid-run is
    /// picked up without reconnecting. When the stream is rejected 401 and
    /// <paramref name="onAuthRejected"/> is supplied it runs before the backoff — the seam
    /// <see cref="MachineTokenRefresher"/> hangs its reactive refresh on.
    /// </summary>
    public HttpControlPlaneChannel(
        Uri controlUrl,
        Func<string> tokenProvider,
        TimeProvider clock,
        Action<string>? log = null,
        Func<CancellationToken, Task>? onAuthRejected = null,
        HttpMessageHandler? handler = null)
    {
        _controlUrl = controlUrl;
        _tokenProvider = tokenProvider;
        _onAuthRejected = onAuthRejected;
        _clock = clock;
        _log = log;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        // The command stream is meant to stay open; the default 100s would cut it.
        _http.Timeout = Timeout.InfiniteTimeSpan;
    }

    /// <summary>Whether the plane is currently holding this machine's command stream open.
    /// This is what decides whether the plane will dispatch here.</summary>
    public bool IsConnected => _streaming;

    /// <summary>
    /// Starts the command stream and its reconnect loop. <paramref name="onCommand"/>
    /// receives each decoded command — wire it to <c>daemon.HandleAsync</c>.
    /// </summary>
    public void Start(Func<RunnerCommand, CancellationToken, Task> onCommand)
    {
        _onCommand = onCommand;
        _connectLoop = Task.Run(() => ConnectLoopAsync(_cts.Token));
    }

    public Task<bool> PublishAsync(RunnerEvent evt, long gapBefore, CancellationToken ct) =>
        // The gap marker is carried locally by the ring; the frozen event vocabulary has
        // no gap field, so it is not transmitted (documented gap).
        IngestAsync(RunnerWire.EncodeEvent(evt), ct);

    public Task<bool> HeartbeatAsync(MachineHeartbeat heartbeat, CancellationToken ct) =>
        IngestAsync(RunnerWire.EncodeHeartbeat(heartbeat), ct);

    private async Task<bool> IngestAsync(string json, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Url("runner/ingest"))
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _tokenProvider());
            using var response = await _http.SendAsync(request, ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or ObjectDisposedException)
        {
            // §10: best-effort, never throw and never queue — the ring already buffered it.
            return false;
        }
    }

    private async Task ConnectLoopAsync(CancellationToken ct)
    {
        var backoff = TimeSpan.FromMilliseconds(200);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await StreamAsync(ct);
                // A clean end is the plane closing the stream; reconnect promptly.
                backoff = TimeSpan.FromMilliseconds(200);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (UnauthorizedAccessException)
            {
                if (_onAuthRejected is { } rejected)
                {
                    // Before the backoff, so the retry carries a refreshed token.
                    try { await rejected(ct); }
                    catch (Exception e) { _log?.Invoke($"token refresh after 401 failed: {e.Message}"); }
                }
            }
            catch (Exception e)
            {
                _log?.Invoke($"control plane connection lost: {e.Message}");
            }
            finally
            {
                _streaming = false;
            }

            try
            {
                await Task.Delay(backoff, _clock, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            backoff = TimeSpan.FromMilliseconds(Math.Min(backoff.TotalMilliseconds * 2, 10_000));
        }
    }

    private async Task StreamAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Url("runner/events"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _tokenProvider());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new UnauthorizedAccessException("the plane rejected this machine's token");
        response.EnsureSuccessStatusCode();

        _streaming = true;
        _log?.Invoke($"control plane connected: {_controlUrl}");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        // Minimal SSE: `data:` lines accumulate, a blank line ends the event, and a line
        // opening with ':' is a comment — which is all the keepalive is.
        var data = new StringBuilder();
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null)
                return; // the plane ended the stream

            if (line.Length == 0)
            {
                if (data.Length > 0)
                {
                    await HandleFrameAsync(data.ToString(), ct);
                    data.Clear();
                }
                continue;
            }
            if (line[0] == ':')
                continue;
            if (line.StartsWith("data: ", StringComparison.Ordinal))
                data.Append(line["data: ".Length..]);
        }
    }

    private async Task HandleFrameAsync(string data, CancellationToken ct)
    {
        long id;
        string frame;
        try
        {
            using var doc = JsonDocument.Parse(data);
            id = doc.RootElement.GetProperty("id").GetInt64();
            frame = doc.RootElement.GetProperty("frame").GetString() ?? "";
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            _log?.Invoke("control plane sent an unrecognized frame; ignoring");
            return;
        }

        if (RunnerWire.DecodeCommand(frame) is not { } command)
        {
            // Outside this runner's §10 vocabulary — a plane newer than this binary. Ack it
            // anyway: replaying it on every reconnect would wedge the stream on a command
            // this runner will never understand.
            _log?.Invoke("control plane sent a command outside this runner's vocabulary; acknowledging unrun");
            await AckAsync(id, ct);
            return;
        }

        if (_onCommand is { } handler)
        {
            try
            {
                // Ack only once the handler has returned, so a command interrupted by a
                // crash or a dropped stream is replayed rather than silently lost.
                await handler(command, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Unacked, so the next stream replays it. Left unacked deliberately:
                // acknowledging a command that threw would drop the work silently, and a
                // replay only recurs on reconnect rather than spinning here.
                _log?.Invoke($"command handler threw: {e.Message}");
                return;
            }
        }
        await AckAsync(id, ct);
    }

    private async Task AckAsync(long id, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Url($"runner/commands/{id}/ack"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _tokenProvider());
            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                _log?.Invoke($"ack for command {id} refused ({(int)response.StatusCode})");
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or ObjectDisposedException)
        {
            // Unacked means replayed, which the handler must already tolerate.
            _log?.Invoke($"ack for command {id} did not reach the plane: {e.Message}");
        }
    }

    private Uri Url(string path) =>
        new(_controlUrl.AbsoluteUri.TrimEnd('/') + "/" + path);

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        if (_connectLoop is { } loop)
        {
            try { await loop; }
            catch (OperationCanceledException) { }
        }
        _cts.Dispose();
        _http.Dispose();
    }
}
