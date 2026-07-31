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

            await ServeAsync(browser, _certificates is not null, connCts.Token);
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

    private async Task ServeAsync(Stream browser, bool isTls, CancellationToken ct)
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

        // ── Gated flow: the browser came back from the dashboard with a one-time
        // code. Redeem it, set our own per-label cookie on the preview origin, and
        // redirect to the clean URL (§8.4). No connect/splice this hop.
        if (head.PreviewCode is { } code)
        {
            await HandleCodeAsync(browser, isTls, label, code, head, ct);
            return;
        }

        // ── Authorize + arm a fresh forward with the control plane (§8.4) ───────
        var armed = await _controlPlane.ConnectAsync(label, head.OperatorSession, head.PreviewCookie, ct);
        if (armed is PreviewConnect.Unauthorized && head.Bearer is null)
        {
            // A browser (no bearer) on a gated preview without a valid session:
            // bounce it to the dashboard confirm, which mints a code and returns.
            // Tooling (a bearer) instead gets a flat 401 below.
            await PreviewHttpResponses.RedirectAsync(
                browser, DashboardAuthUrl(isTls, head.Host, head.Target, label), setCookie: null, ct);
            return;
        }
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
            // HEAD-STRIP before the splice (§8.4): drop the operator's Authorization
            // and the docket_session/docket_preview cookies so they never reach the
            // team's workload code, which receives these bytes verbatim. The one
            // deliberate exception to never-rewrite — auth material, not app content.
            var initial = Concat(head.StripAuthMaterial(), head.Extra);
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

    /// <summary>
    /// Redeem the one-time code for a per-label preview session, set it as our own
    /// HttpOnly cookie on the preview origin, and 302 to the clean URL (§8.4). On a
    /// bad/expired code, bounce back to the dashboard confirm for a fresh one.
    /// </summary>
    private async Task HandleCodeAsync(
        Stream browser, bool isTls, string label, string code, HttpRequestHead head, CancellationToken ct)
    {
        var cleanTarget = head.TargetWithoutCode();
        switch (await _controlPlane.ExchangeAsync(label, code, ct))
        {
            case PreviewExchange.Ok ok:
                var secure = isTls ? "; Secure" : "";
                var cookie = $"{HttpRequestHead.PreviewCookieName}={ok.Session}; HttpOnly; Path=/; " +
                             $"Max-Age={ok.MaxAgeSeconds}; SameSite=Lax{secure}";
                await PreviewHttpResponses.RedirectAsync(browser, cleanTarget, cookie, ct);
                break;
            default:
                // Expired/replayed code — send them back through the dashboard confirm.
                await PreviewHttpResponses.RedirectAsync(
                    browser, DashboardAuthUrl(isTls, head.Host, cleanTarget, label), null, ct);
                break;
        }
    }

    /// <summary>
    /// The dashboard confirm URL a gated browser is bounced to (§8.4):
    /// <c>{DashboardUrl}/dashboard/preview-auth?label=&amp;return=</c>, where return is
    /// this exact browser URL on the preview origin so the confirm can redirect back.
    /// </summary>
    private string DashboardAuthUrl(bool isTls, string? host, string target, string label)
    {
        var scheme = isTls ? "https" : "http";
        var self = $"{scheme}://{host}{target}";
        var dashboard = (_options.DashboardUrl ?? "").TrimEnd('/');
        return $"{dashboard}/dashboard/preview-auth" +
               $"?label={Uri.EscapeDataString(label)}&return={Uri.EscapeDataString(self)}";
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
