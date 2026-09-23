using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace Landbridge.Runner.Tests;

/// <summary>
/// Loopback identity HTTP: GET answers with the machine id a Lead passes to
/// <c>bind_machine</c>. Port 19378 is the production well-known; tests bind
/// ephemeral loopback so they do not collide with a running landbridged.
/// One collection so HttpListener's process-wide prefix table is not mutated
/// by two tests at once — Close throws AddressAlreadyInUse out of that table, on
/// macOS as well as Linux, which is why every teardown here goes through Release.
/// </summary>
[Collection(nameof(LocalIdentityListenerTests))]
public class LocalIdentityListenerTests
{
    [Fact]
    public void Well_known_port_is_19378() =>
        Assert.Equal(19378, LocalIdentityListener.Port);

    [Fact]
    public async Task Get_returns_the_machine_id_as_plain_text()
    {
        var id = Guid.NewGuid().ToString("D");
        await using var listener = LocalIdentityListener.Bind(id, new IPEndPoint(IPAddress.Loopback, 0));
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        var body = await http.GetStringAsync($"http://127.0.0.1:{listener.BoundPort}/");

        Assert.Equal(id, body.Trim());
        Assert.True(Guid.TryParse(body, out var parsed));
        Assert.Equal(Guid.Parse(id), parsed);
    }

    [Fact]
    public async Task Get_any_path_returns_the_id()
    {
        var id = Guid.NewGuid().ToString("N");
        await using var listener = LocalIdentityListener.Bind(id, new IPEndPoint(IPAddress.Loopback, 0));
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        var body = await http.GetStringAsync($"http://127.0.0.1:{listener.BoundPort}/anything");

        Assert.Equal(id, body.Trim());
    }

    [Fact]
    public async Task Head_is_ok_with_no_body()
    {
        var id = Guid.NewGuid().ToString("D");
        await using var listener = LocalIdentityListener.Bind(id, new IPEndPoint(IPAddress.Loopback, 0));
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        using var response = await http.SendAsync(new HttpRequestMessage(
            HttpMethod.Head, $"http://127.0.0.1:{listener.BoundPort}/"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Post_is_method_not_allowed()
    {
        var id = Guid.NewGuid().ToString("D");
        await using var listener = LocalIdentityListener.Bind(id, new IPEndPoint(IPAddress.Loopback, 0));
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        using var response = await http.PostAsync($"http://127.0.0.1:{listener.BoundPort}/", new StringContent(""));

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Fact]
    public async Task Bound_address_is_loopback()
    {
        await using var listener = LocalIdentityListener.Bind(
            Guid.NewGuid().ToString("D"), new IPEndPoint(IPAddress.Loopback, 0));

        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await probe.ConnectAsync(new IPEndPoint(IPAddress.Loopback, listener.BoundPort));
        Assert.True(IPAddress.IsLoopback(((IPEndPoint)probe.LocalEndPoint!).Address));
    }

    [Fact]
    public async Task Two_gets_in_sequence_both_return_the_id()
    {
        var id = Guid.NewGuid().ToString("D");
        await using var listener = LocalIdentityListener.Bind(id, new IPEndPoint(IPAddress.Loopback, 0));
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var url = $"http://127.0.0.1:{listener.BoundPort}/";

        Assert.Equal(id, (await http.GetStringAsync(url)).Trim());
        Assert.Equal(id, (await http.GetStringAsync(url)).Trim());
    }

    [Fact]
    public async Task TryBindLoopback_returns_null_when_the_port_is_taken()
    {
        // Occupy the same HTTP.sys / managed listener stack HttpListener uses;
        // a raw TcpListener is a different bind on Windows and would not collide.
        var (occupied, port) = OccupyLoopback();
        try
        {
            var logs = new List<string>();
            var result = LocalIdentityListener.TryBindLoopback(
                Guid.NewGuid().ToString("D"), port, logs.Add);

            Assert.Null(result);
            Assert.Contains(logs, l => l.Contains("not bound", StringComparison.Ordinal));
        }
        finally
        {
            Release(occupied);
        }
    }

    /// <summary>
    /// Lets go of a listener without letting the teardown fail the test.
    ///
    /// <para><c>HttpListener.Close</c> re-enters the process-wide prefix table
    /// (<c>RemoveListener</c> → <c>RemovePrefixInternal</c> → <c>GetEPListener</c>) and
    /// throws <c>Address already in use</c> from there — the hazard this class's own
    /// summary notes for Linux, observed on macOS in CI. It happens after the assertions
    /// have passed, so an unguarded Close turns a green test red on the way out and the
    /// failure names a port rather than anything the test was about.</para>
    /// </summary>
    private static void Release(HttpListener listener)
    {
        try
        {
            listener.Stop();
            listener.Close();
        }
        catch (Exception e) when (e is HttpListenerException or ObjectDisposedException)
        {
            // Nothing to salvage: the listener is going away either way.
        }
    }

    /// <summary>
    /// A listener holding the loopback port, on a port nothing else has taken.
    ///
    /// <para>Retried because finding a free port means releasing it before the occupier
    /// can take it, and on a loaded runner something else can win that gap. That race is
    /// not what the test is about, so it is retried rather than failed on — it is the
    /// most likely cause of this test's intermittent CI failures (#280).</para>
    ///
    /// <para><b>IPv4 only, deliberately.</b> The obvious hardening — also occupy
    /// <c>[::1]</c>, since <see cref="LocalIdentityListener.TryBindLoopback"/> asks for
    /// both families before falling back to IPv4 — cannot be written and is not needed.
    /// <c>HttpListener</c>'s prefix parser on Unix rejects <c>http://[::1]:port/</c>
    /// outright with "Invalid port in prefix", so neither this test nor the code under
    /// test can hold that prefix. The dual-family attempt never succeeds there; the
    /// IPv4-only fallback is the one that runs, and that is what this occupies.</para>
    /// </summary>
    private static (HttpListener Listener, int Port) OccupyLoopback()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
                return (listener, port);
            }
            catch (Exception e) when (e is HttpListenerException or SocketException)
            {
                // Something took the port in the gap between the probe releasing it and
                // this taking it. Pick another rather than failing the test on it —
                // through Release, because a Close that throws here would escape the very
                // retry loop it is part of.
                Release(listener);
            }
        }

        throw new InvalidOperationException(
            "could not hold a free loopback port across ten attempts");
    }

    [Fact]
    public async Task Dispose_stops_accepting()
    {
        var listener = LocalIdentityListener.Bind(
            Guid.NewGuid().ToString("D"), new IPEndPoint(IPAddress.Loopback, 0));
        var port = listener.BoundPort;
        await listener.DisposeAsync();

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        try
        {
            var body = await http.GetStringAsync($"http://127.0.0.1:{port}/");
            Assert.Fail($"disposed listener still answered: {body}");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            // Linux/macOS reset the socket (HttpRequestException). Windows HTTP.sys
            // often accepts then hangs until the client timeout (TaskCanceledException).
        }
    }
}
