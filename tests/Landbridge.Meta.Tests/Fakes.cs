using Landbridge.Meta.Data;
using Landbridge.Meta.Edge;
using Landbridge.Meta.Substrate;

namespace Landbridge.Meta.Tests;

/// <summary>
/// An in-memory <see cref="ISubstrate"/> for the saga tests (design note §7): records
/// every network/volume/container/image it is asked to realize, so tests assert on
/// what meta injects (the credential surface) and on idempotent convergence, and can
/// inject a failure at any operation via <see cref="BeforeOp"/> to exercise recovery.
/// </summary>
public sealed class FakeSubstrate : ISubstrate
{
    public sealed class ContainerRecord(ContainerSpec spec)
    {
        public ContainerSpec Spec { get; } = spec;
        public bool Running { get; set; } = true;
    }

    public Dictionary<string, string> Networks { get; } = new();
    public HashSet<string> Volumes { get; } = new();
    public Dictionary<string, ContainerRecord> Containers { get; } = new();
    public HashSet<string> PulledImages { get; } = new();
    public List<string> Removed { get; } = new();

    /// <summary>Health inspect returns this; default None so "running" is the readiness signal.</summary>
    public HealthState ContainerHealth { get; set; } = HealthState.None;

    /// <summary>Optional per-operation hook (label like "run:landbridge-x-mcp"); throw to inject a failure.</summary>
    public Func<string, Task>? BeforeOp { get; set; }

    public Task PingAsync(CancellationToken ct) => Task.CompletedTask;

    public async Task EnsureImageAsync(string image, CancellationToken ct)
    { await Hook("pull:" + image); PulledImages.Add(image); }

    public async Task EnsureNetworkAsync(string name, IReadOnlyDictionary<string, string> labels, CancellationToken ct)
    { await Hook("net:" + name); Networks.TryAdd(name, "netid-" + name); }

    public async Task EnsureVolumeAsync(string name, IReadOnlyDictionary<string, string> labels, CancellationToken ct)
    { await Hook("vol:" + name); Volumes.Add(name); }

    public async Task<string> EnsureContainerAsync(ContainerSpec spec, CancellationToken ct)
    {
        await Hook("run:" + spec.Name);
        if (Containers.TryGetValue(spec.Name, out var existing))
        {
            if (existing.Spec.Image == spec.Image)
            {
                existing.Running = true;
                return "id-" + spec.Name;
            }
            // Mirrors the real substrate: a container on another image is the upgrade case,
            // so it is replaced rather than adopted.
            await RemoveContainerAsync(spec.Name, ct);
        }
        Containers[spec.Name] = new ContainerRecord(spec);
        return "id-" + spec.Name;
    }

    public Task<ContainerStatus> InspectContainerAsync(string nameOrId, CancellationToken ct)
    {
        if (Containers.TryGetValue(nameOrId, out var r))
            return Task.FromResult(new ContainerStatus(true, r.Running, ContainerHealth,
                r.Spec.PublishHostPort > 0 ? r.Spec.PublishHostPort : null));
        return Task.FromResult(new ContainerStatus(false, false, HealthState.None, null));
    }

    public Task StartContainerAsync(string nameOrId, CancellationToken ct)
    { if (Containers.TryGetValue(nameOrId, out var r)) r.Running = true; return Task.CompletedTask; }

    public Task StopContainerAsync(string nameOrId, CancellationToken ct)
    { if (Containers.TryGetValue(nameOrId, out var r)) r.Running = false; return Task.CompletedTask; }

    public async Task RemoveContainerAsync(string nameOrId, CancellationToken ct)
    { await Hook("rm:" + nameOrId); Containers.Remove(nameOrId); Removed.Add("container:" + nameOrId); }

    public Task RemoveNetworkAsync(string name, CancellationToken ct)
    { Networks.Remove(name); Removed.Add("network:" + name); return Task.CompletedTask; }

    public Task RemoveVolumeAsync(string name, CancellationToken ct)
    { Volumes.Remove(name); Removed.Add("volume:" + name); return Task.CompletedTask; }

    private Task Hook(string op) => BeforeOp?.Invoke(op) ?? Task.CompletedTask;
}

/// <summary>Hands the same <see cref="FakeSubstrate"/> to every host (single-pool tests).</summary>
public sealed class FakeSubstrateFactory(FakeSubstrate substrate) : ISubstrateFactory
{
    public ISubstrate For(HostRow host) => substrate;
}

/// <summary>An in-memory <see cref="ICaddyAdmin"/> recording the route table (design note §7).</summary>
public sealed class FakeCaddyAdmin : ICaddyAdmin
{
    public Dictionary<string, (string Host, string Upstream)> Routes { get; } = new();

    /// <summary>When true, mutating calls throw — simulates an unreachable edge (best-effort teardown).</summary>
    public bool Unreachable { get; set; }

    public Task PingAsync(CancellationToken ct) =>
        Unreachable ? throw new HttpRequestException("caddy down") : Task.CompletedTask;

    public Task<bool> HasRouteAsync(string routeId, CancellationToken ct) =>
        Task.FromResult(Routes.ContainsKey(routeId));

    public Task UpsertRouteAsync(string routeId, string host, string upstream, CancellationToken ct)
    {
        if (Unreachable) throw new HttpRequestException("caddy down");
        Routes[routeId] = (host, upstream);
        return Task.CompletedTask;
    }

    public Task RemoveRouteAsync(string routeId, CancellationToken ct)
    {
        if (Unreachable) throw new HttpRequestException("caddy down");
        Routes.Remove(routeId);
        return Task.CompletedTask;
    }
}
