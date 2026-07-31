using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;
using Microsoft.Extensions.Logging;

namespace Docket.Preview;

/// <summary>
/// The HTTP preview frontend's connection server (spec §8.4). It owns a raw
/// <see cref="TcpListener"/> and, per accepted browser connection: terminates TLS
/// on the wildcard origin (when a cert is configured), reads only the HTTP request
/// head to route by <c>Host</c>, asks the control plane to authorize + arm a fresh
/// forward, dials the relay's unchanged <c>/tunnel</c> as the consumer, and
/// byte-splices the connection through — HTTP and WebSocket upgrade alike, as
/// opaque bytes that are never rewritten (§8.4).
///
/// <para>Each browser connection mints its own forward id (one <c>/preview/connect</c>
/// call each), so N connections are N ordinary relay forwards. Raw sockets — not
/// Kestrel — because the frontend must not parse and re-emit the served app's HTTP
/// (that would rewrite cookies and absolute paths). Split out from the hosted
/// <see cref="PreviewListener"/> so tests drive it directly on a loopback port.</para>
/// </summary>
public sealed class PreviewServer : IAsyncDisposable
{
    private readonly IPEndPoint _endpoint;
    private readonly PreviewCertificateProvider? _certificates;
    private readonly PreviewOptions _options;
    private readonly PreviewControlPlaneClient _controlPlane;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<Task, byte> _connections = new();

    private TcpListener? _listener;
    private Task? _acceptLoop;

    public PreviewServer(
        IPEndPoint endpoint,
        PreviewCertificateProvider? certificates,
        PreviewOptions options,
        PreviewControlPlaneClient controlPlane,
        ILogger logger)
    {
        _endpoint = endpoint;
        _certificates = certificates;
        _options = options;
        _controlPlane = controlPlane;
        _logger = logger;
    }

    /// <summary>The port actually bound (useful when the endpoint asked for port 0).</summary>
    public int BoundPort { get; private set; }

    public void Start()
    {
        _listener = new TcpListener(_endpoint);
        _listener.Start();
        BoundPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _logger.LogInformation(
            "preview frontend listening on {Endpoint} ({Mode}) for *.{Domain}",
            _listener.LocalEndpoint, _certificates is null ? "plaintext" : "TLS", _options.Domain);
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var socket = await _listener!.AcceptSocketAsync(ct);
                var task = Task.Run(() => HandleConnectionAsync(socket, ct), ct);
                _connections[task] = 0;
                _ = task.ContinueWith(t => _connections.TryRemove(t, out _), TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) { /* stopped */ }
        catch (Exception e) when (e is SocketException or ObjectDisposedException) { /* listener stopped */ }
    }

    private async Task HandleConnectionAsync(Socket socket, CancellationToken ct)
    {
        using var connCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Stream browser = new NetworkStream(socket, ownsSocket: true);
        try
        {
            if (_certificates is not null)
            {
                var tls = new SslStream(browser, leaveInnerStreamOpen: false);
                browser = tls;
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    // Read the current cert per handshake so a hot-reloaded renewal is
                    // served to new handshakes with no restart; in-flight handshakes and
                    // established connections keep whatever cert they started with (§8.4).
                    ServerCertificateSelectionCallback = (_, _) => _certificates.Current,
                    ClientCertificateRequired = false,
                    // HTTP/1.1 only: no h2, so a browser can never coalesce two
                    // different Hosts onto one connection — which is what makes
                    // per-connection Host routing sound (§8.4).
                    ApplicationProtocols = [SslApplicationProtocol.Http11],
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                }, connCts.Token);
            }

            await ServeAsync(browser, connCts.Token);
        }
        catch (Exception e) when (e is IOException or OperationCanceledException
                                     or AuthenticationException or WebSocketException or SocketException)
        {
            // A dropped browser, a failed TLS handshake, or a torn tunnel — nothing
            // to do but let the connection close.
            _logger.LogDebug(e, "preview connection ended early");
        }
        finally
        {
            await browser.DisposeAsync();
        }
    }

    private async Task ServeAsync(Stream browser, CancellationToken ct)
    {
        // ── Read the request head (routing only; the rest is spliced) ───────────
        HttpRequestHead? head;
        using (var headCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            headCts.CancelAfter(_options.HeadReadTimeout);
            head = await HttpRequestHead.ReadAsync(browser, headCts.Token);
        }
        if (head is null)
            return; // closed / malformed / oversized head — nothing to answer

        var label = head.LabelUnder(_options.Domain);
        if (label is null)
        {
            await PreviewHttpResponses.NotFoundAsync(browser, ct);
            return;
        }

        // ── Authorize + arm a fresh forward with the control plane (§8.4) ───────
        var armed = await _controlPlane.ConnectAsync(label, head.OperatorSession, ct);
        if (armed is not PreviewConnect.Armed ok)
        {
            await WriteRefusalAsync(browser, armed, ct);
            return;
        }

        // ── Dial the relay's /tunnel as the consumer and splice ─────────────────
        WebSocket ws;
        try
        {
            ws = await PreviewTunnel.DialConsumerAsync(ok.RelayUrl, ok.ForwardId, ok.Grant, ct);
        }
        catch (Exception e) when (e is WebSocketException or IOException or OperationCanceledException)
        {
            _logger.LogWarning(e, "preview: dialing the relay tunnel failed for forward {ForwardId}", ok.ForwardId);
            await PreviewHttpResponses.BadGatewayAsync(browser, ct);
            return;
        }

        try
        {
            // Replay the head (+ any bytes already read past it), then splice.
            var initial = Concat(head.RawHead, head.Extra);
            var outcome = await PreviewTunnel.ProxyAsync(browser, ws, initial, _options.FirstByteTimeout, ct);
            switch (outcome)
            {
                case ProxyOutcome.NoUpstream:
                    await PreviewHttpResponses.BadGatewayAsync(browser, ct);
                    break;
                case ProxyOutcome.Timeout:
                    await PreviewHttpResponses.GatewayTimeoutAsync(browser, ct);
                    break;
            }
        }
        finally
        {
            ws.Dispose();
        }
    }

    private static async Task WriteRefusalAsync(Stream browser, PreviewConnect result, CancellationToken ct)
    {
        var write = result switch
        {
            PreviewConnect.NotFound => PreviewHttpResponses.NotFoundAsync(browser, ct),
            PreviewConnect.Gone => PreviewHttpResponses.GoneAsync(browser, ct),
            PreviewConnect.Unauthorized => PreviewHttpResponses.UnauthorizedAsync(browser, ct),
            PreviewConnect.Unavailable => PreviewHttpResponses.ServiceUnavailableAsync(browser, ct),
            _ => PreviewHttpResponses.BadGatewayAsync(browser, ct),
        };
        await write;
    }

    private static byte[] Concat(byte[] a, byte[] b)
    {
        if (b.Length == 0)
            return a;
        var result = new byte[a.Length + b.Length];
        Buffer.BlockCopy(a, 0, result, 0, a.Length);
        Buffer.BlockCopy(b, 0, result, a.Length, b.Length);
        return result;
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _listener?.Stop();
        if (_acceptLoop is not null)
        {
            try { await _acceptLoop; }
            catch (Exception) { /* stopped */ }
        }
        try { await Task.WhenAll(_connections.Keys); }
        catch (Exception) { /* teardown races expected */ }
        _cts.Dispose();
    }
}
