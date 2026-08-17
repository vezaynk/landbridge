using Landbridge.Meta.Data;
using Landbridge.Meta.Substrate;

namespace Landbridge.Meta.Provisioning;

/// <summary>The health rollup meta renders + the saga's readiness gate (design note §6).</summary>
public sealed record InstanceHealth(bool PgRunning, bool McpResponding, bool RelayRunning)
{
    /// <summary>An instance is healthy when Postgres runs, the plane answers HTTP, and the relay runs.</summary>
    public bool Ok => PgRunning && McpResponding && RelayRunning;
}

/// <summary>
/// Determines whether an Instance is healthy (design note §6): Postgres + relay by
/// container state, and — the real liveness signal — the plane answering an HTTP
/// request. The probe hits <c>/.well-known/oauth-protected-resource</c> because that
/// endpoint is anonymous and always mapped in Production (the Aspire <c>/health</c>
/// route is Development-only), so a 200 there proves the plane booted, migrated, and
/// is serving on its published port.
/// </summary>
public sealed class InstanceHealthProbe(HttpClient http)
{
    /// <summary>The always-mapped anonymous endpoint used as the plane's production liveness signal.</summary>
    public const string LivenessPath = "/.well-known/oauth-protected-resource";

    public async Task<InstanceHealth> CheckAsync(ISubstrate substrate, HostRow host, InstanceRow instance, CancellationToken ct)
    {
        var pg = await RunningAsync(substrate, InstanceNaming.PgContainer(instance.Name), ct);
        var relay = await RunningAsync(substrate, InstanceNaming.RelayContainer(instance.Name), ct);
        var mcp = instance.McpPublishedPort is int port && await PlaneRespondingAsync(host.PublishedHost, port, ct);
        return new InstanceHealth(pg, mcp, relay);
    }

    /// <summary>Waits (polling) until the plane responds on its published port, or the timeout elapses.</summary>
    public async Task<bool> WaitForPlaneAsync(HostRow host, int port, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (await PlaneRespondingAsync(host.PublishedHost, port, ct))
                return true;
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
        return false;
    }

    private static async Task<bool> RunningAsync(ISubstrate substrate, string container, CancellationToken ct)
    {
        var s = await substrate.InspectContainerAsync(container, ct);
        return s is { Exists: true, Running: true };
    }

    private async Task<bool> PlaneRespondingAsync(string hostAddr, int port, CancellationToken ct)
    {
        try
        {
            using var resp = await http.GetAsync($"http://{hostAddr}:{port}{LivenessPath}", ct);
            return resp.IsSuccessStatusCode;
        }
        catch (HttpRequestException) { return false; }
        catch (TaskCanceledException) { return false; }
    }
}
