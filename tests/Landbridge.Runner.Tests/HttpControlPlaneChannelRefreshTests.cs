using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Landbridge.Runner.Tests;

/// <summary>
/// The refresh/reconnect seam of the real control-plane channel (spec §5, §13): the
/// channel presents the CURRENT machine token on every request, and a 401 opening the
/// command stream triggers the refresh hook so the retry carries a fresh one. End to end
/// against a loopback plane that gates <c>/runner/events</c> on the presented bearer, so
/// the whole seam — token provider → 401 detection → hook → reconnect — runs for real.
/// </summary>
public class HttpControlPlaneChannelRefreshTests
{
    /// <summary>
    /// A loopback plane whose <c>/runner/events</c> is held open only when the presented
    /// bearer equals <see cref="Expected"/>; anything else is 401. Records every presented
    /// bearer so a test can assert what each attempt carried.
    /// </summary>
    private sealed class GatedStreamServer : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly object _gate = new();
        private readonly List<string> _presented = [];

        public volatile string Expected = "";

        public IReadOnlyList<string> Presented { get { lock (_gate) return _presented.ToArray(); } }

        private GatedStreamServer(WebApplication app) => _app = app;

        public static async Task<GatedStreamServer> StartAsync(string expected, CancellationToken ct)
        {
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var app = builder.Build();
            var server = new GatedStreamServer(app) { Expected = expected };
            app.MapGet("/runner/events", async (HttpContext http) =>
            {
                var auth = http.Request.Headers.Authorization.ToString();
                const string prefix = "Bearer ";
                var presented = auth.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    ? auth[prefix.Length..].Trim()
                    : "";
                lock (server._gate) server._presented.Add(presented);

                if (presented != server.Expected)
                {
                    http.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }

                http.Response.ContentType = "text/event-stream";
                await http.Response.StartAsync(http.RequestAborted);
                await http.Response.WriteAsync(": open\n\n", http.RequestAborted);
                await http.Response.Body.FlushAsync(http.RequestAborted);
                try
                {
                    // Hold it open: an accepted stream is what IsConnected reports.
                    await Task.Delay(Timeout.InfiniteTimeSpan, http.RequestAborted);
                }
                catch (OperationCanceledException) { }
            });
            await app.StartAsync(ct);
            return server;
        }

        public Uri PlaneUrl() => new(_app.Urls.First(u => u.StartsWith("http://", StringComparison.Ordinal)));

        public async ValueTask DisposeAsync() => await _app.DisposeAsync();
    }

    [Fact]
    public async Task A_401_on_reconnect_triggers_refresh_and_the_retry_presents_the_new_token()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = cts.Token;

        // The plane accepts only "access-1"; the daemon starts holding the stale
        // "access-0", so its first attempt is rejected 401.
        await using var server = await GatedStreamServer.StartAsync(expected: "access-1", ct);

        var clock = TimeProvider.System;
        var initial = new MachineCredentialFile(
            "m-1", "https://plane.example.com", "https://auth.example.com", "access-0",
            clock.GetUtcNow() + TimeSpan.FromHours(1), "refresh-0", clock.GetUtcNow() + TimeSpan.FromDays(90));
        var persisted = new List<MachineCredentialFile>();
        var persistLock = new object();

        await using var refresher = new MachineTokenRefresher(
            initial,
            // The refresh mints exactly the token the plane will accept.
            (_, _) => Task.FromResult<RefreshResponse?>(
                new RefreshResponse("access-1", clock.GetUtcNow() + TimeSpan.FromHours(1))),
            c => { lock (persistLock) persisted.Add(c); },
            clock);

        await using var channel = new HttpControlPlaneChannel(
            server.PlaneUrl(),
            () => refresher.CurrentAccessToken,
            clock,
            log: null,
            onAuthRejected: refresher.RefreshOnceAsync);
        channel.Start((_, _) => Task.CompletedTask);

        Assert.True(await TestKit.WaitUntilAsync(() => channel.IsConnected, TimeSpan.FromSeconds(15)),
            "the channel never connected after the 401 refresh");

        Assert.Equal("access-1", refresher.CurrentAccessToken);           // the hook swapped the token
        lock (persistLock) Assert.Contains(persisted, p => p.AccessToken == "access-1"); // and persisted it
        Assert.Contains("access-0", server.Presented);                    // the first attempt was stale
        Assert.Contains("access-1", server.Presented);                    // the retry carried the new one
    }

    [Fact]
    public async Task Env_token_mode_retries_the_same_token_and_never_refreshes()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = cts.Token;

        // The plane rejects everything, so the channel keeps retrying. With the string
        // constructor there is no refresh hook: every attempt must carry the one fixed
        // env token, unchanged.
        await using var server = await GatedStreamServer.StartAsync(expected: "the-only-acceptable-token", ct);

        await using var channel = new HttpControlPlaneChannel(
            server.PlaneUrl(), "env-token", TimeProvider.System);
        channel.Start((_, _) => Task.CompletedTask);

        Assert.True(await TestKit.WaitUntilAsync(() => server.Presented.Count >= 2, TimeSpan.FromSeconds(15)),
            "the channel did not retry after the 401");

        Assert.All(server.Presented, t => Assert.Equal("env-token", t)); // never refreshed, never changed
    }
}
