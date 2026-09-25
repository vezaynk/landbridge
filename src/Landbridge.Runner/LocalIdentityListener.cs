using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Landbridge.Runner;

/// <summary>
/// Loopback HTTP that answers with this machine's id. A Lead on the same box
/// GETs it to <c>bind_machine</c> without the dashboard. Bound to loopback only
/// — the id is how a Lead claims the box as theirs.
/// </summary>
/// <remarks>
/// Port <see cref="Port"/> is the well-known address
/// (<c>http://127.0.0.1:19378</c>). <see cref="HttpListener"/> is BCL (no ASP.NET)
/// and AOT-clean.
///
/// <para><b>The address is the IPv4 literal, everywhere it is published</b> — the
/// startup line, RUNNING, the enroll and lead skills, and the dashboard's Connect page
/// all say <c>http://127.0.0.1:19378</c>. That is deliberate rather than incidental:
/// <c>HttpListener</c> on Unix rejects an <c>[::1]</c> prefix at the parser ("Invalid
/// port in prefix"), so there is no IPv6 listener to reach on macOS or Linux, and
/// <c>localhost</c> resolves to <c>::1</c> first on both. Anyone improvising
/// <c>localhost:19378</c> there gets connection refused.</para>
///
/// <para>Windows is backed by http.sys and does take the prefix, so it is offered when
/// it will be accepted. Making <c>localhost</c> work on Unix would mean replacing
/// <c>HttpListener</c>, which is a large price for a spelling nothing documents.</para>
/// </remarks>
public sealed class LocalIdentityListener : IAsyncDisposable
{
    /// <summary>The well-known loopback port a Lead GETs for this machine's id.</summary>
    public const int Port = 19378;

    private readonly HttpListener _http = new();
    private readonly string _machineId;
    private volatile bool _stopping;
    private Task? _loop;

    private LocalIdentityListener(string machineId) => _machineId = machineId;

    /// <summary>The port actually bound (port 0 in tests becomes ephemeral).</summary>
    public int BoundPort { get; private set; }

    /// <summary>One prefix on <paramref name="endpoint"/>. Tests use loopback + port 0.</summary>
    public static LocalIdentityListener Bind(string machineId, IPEndPoint endpoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(machineId);
        var port = endpoint.Port == 0 ? FreePort(endpoint.Address) : endpoint.Port;
        var listener = new LocalIdentityListener(machineId);
        listener.Start([Prefix(endpoint.Address, port)], port);
        return listener;
    }

    /// <summary>
    /// Production bind: IPv4 loopback on <paramref name="port"/>, plus IPv6 loopback on
    /// Windows, where <c>HttpListener</c> accepts the prefix. Returns null when IPv4
    /// cannot bind — the daemon still runs; the Lead then uses enroll stdout or the
    /// dashboard.
    /// </summary>
    public static LocalIdentityListener? TryBindLoopback(
        string machineId, int port = Port, Action<string>? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(machineId);
        var v4 = $"http://127.0.0.1:{port}/";

        // Asked for only where it can be granted. On Unix this prefix is rejected by the
        // parser rather than by the network stack, so attempting it there is an exception
        // thrown and swallowed on every start — and, read as "when the OS allows", it
        // suggested the fallback was about a machine without IPv6 rather than about the
        // platform never taking the prefix at all.
        if (OperatingSystem.IsWindows()
            && TryStart(machineId, port, [v4, $"http://[::1]:{port}/"]) is { } both)
            return both;

        if (TryStart(machineId, port, [v4]) is { } ipv4)
            return ipv4;

        log?.Invoke($"landbridged: identity http://127.0.0.1:{port} not bound");
        return null;
    }

    private static LocalIdentityListener? TryStart(string machineId, int port, string[] prefixes)
    {
        var listener = new LocalIdentityListener(machineId);
        try
        {
            listener.Start(prefixes, port);
            return listener;
        }
        catch (Exception e) when (e is HttpListenerException or SocketException or ObjectDisposedException)
        {
            listener.DisposeAsync().AsTask().GetAwaiter().GetResult();
            return null;
        }
    }

    private void Start(string[] prefixes, int port)
    {
        _http.IgnoreWriteExceptions = true;
        foreach (var prefix in prefixes)
            _http.Prefixes.Add(prefix);
        _http.Start();
        BoundPort = port;
        _loop = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_stopping && _http.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await _http.GetContextAsync();
                }
                catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException)
                {
                    // Stop() races GetContextAsync: macOS throws InvalidOperationException
                    // ("Please call the Start() method") rather than HttpListenerException.
                    return;
                }

                _ = Task.Run(() => Handle(context));
            }
        }
        catch (Exception e) when (e is ObjectDisposedException or InvalidOperationException)
        {
            // stopped
        }
    }

    private void Handle(HttpListenerContext context)
    {
        try
        {
            var response = context.Response;
            response.ContentType = "text/plain; charset=utf-8";
            response.Headers["Cache-Control"] = "no-store";
            var method = context.Request.HttpMethod;
            if (method.Equals("GET", StringComparison.OrdinalIgnoreCase)
                || method.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
            {
                var body = Encoding.UTF8.GetBytes(_machineId + "\n");
                response.StatusCode = (int)HttpStatusCode.OK;
                response.ContentLength64 = body.Length;
                if (method.Equals("GET", StringComparison.OrdinalIgnoreCase))
                    response.OutputStream.Write(body);
                return;
            }

            response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
            response.AddHeader("Allow", "GET, HEAD");
            response.ContentLength64 = 0;
        }
        catch (Exception e) when (e is HttpListenerException or IOException or ObjectDisposedException)
        {
            // hung, reset, or shutdown
        }
        finally
        {
            try { context.Response.Close(); }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException) { }
        }
    }

    private static string Prefix(IPAddress address, int port) =>
        address.AddressFamily == AddressFamily.InterNetworkV6
            ? $"http://[{address}]:{port}/"
            : $"http://{address}:{port}/";

    private static int FreePort(IPAddress address)
    {
        var probe = new TcpListener(address, 0);
        probe.Start();
        try { return ((IPEndPoint)probe.LocalEndpoint).Port; }
        finally { probe.Stop(); }
    }

    public async ValueTask DisposeAsync()
    {
        _stopping = true;
        try { if (_http.IsListening) _http.Stop(); }
        catch (Exception e) when (e is ObjectDisposedException or InvalidOperationException or HttpListenerException) { }

        if (_loop is not null)
        {
            try { await _loop; }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException or OperationCanceledException) { }
        }

        // Managed HttpListener on Linux throws AddressAlreadyInUse from Close
        // while RemovePrefix races another listener (or this one's accept loop).
        try { _http.Close(); }
        catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException) { }
    }
}
