using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace Landbridge.Runner.Tests;

/// <summary>
/// Loopback identity HTTP: GET answers with the machine id a Lead passes to
/// <c>bind_machine</c>. Port 19378 is the production well-known; tests bind
/// ephemeral loopback so they do not collide with a running landbridged.
/// One collection so HttpListener's process-wide prefix table is not mutated
/// by two tests at once (Close throws AddressAlreadyInUse on Linux).
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
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        using var occupied = new HttpListener();
        occupied.Prefixes.Add($"http://127.0.0.1:{port}/");
        occupied.Start();
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
            occupied.Stop();
            occupied.Close();
        }
    }

    [Fact]
    public async Task Dispose_stops_accepting()
    {
        var listener = LocalIdentityListener.Bind(
            Guid.NewGuid().ToString("D"), new IPEndPoint(IPAddress.Loopback, 0));
        var port = listener.BoundPort;
        await listener.DisposeAsync();

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        await Assert.ThrowsAnyAsync<HttpRequestException>(
            () => http.GetStringAsync($"http://127.0.0.1:{port}/"));
    }
}
