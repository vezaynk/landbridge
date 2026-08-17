using System.Net;
using Docker.DotNet;
using Docker.DotNet.Models;

namespace Landbridge.Meta.Substrate;

/// <summary>
/// The real <see cref="ISubstrate"/> over Docker.DotNet against one host's Engine API
/// (design note §3). Every operation is written check-before-act so it converges on
/// re-run: create-if-absent for networks/volumes/containers, adopt-and-start for an
/// existing container, and swallow 404 on teardown. Docker's own idempotence
/// (volume create by name, image pull when present) is relied on where it holds.
/// </summary>
public sealed class DockerSubstrate(IDockerClient client) : ISubstrate
{
    public async Task PingAsync(CancellationToken ct) => await client.System.PingAsync(ct);

    public async Task EnsureImageAsync(string image, CancellationToken ct)
    {
        // A locally-built image (the pool-of-one / E2E case) has no registry to pull
        // from — so present-locally short-circuits before any pull is attempted.
        var local = await client.Images.ListImagesAsync(
            new ImagesListParameters { Filters = Filter("reference", image) }, ct);
        if (local.Count > 0)
            return;

        var (repo, tag) = SplitImage(image);
        try
        {
            await client.Images.CreateImageAsync(
                new ImagesCreateParameters { FromImage = repo, Tag = tag },
                authConfig: null,
                progress: new Progress<JSONMessage>(),
                ct);
        }
        catch (DockerApiException ex)
        {
            // Docker.DotNet's message is unusable on its own: a denied GHCR pull surfaces
            // as `status code=InternalServerError, response={"message":"error from registry:
            // denied\ndenied"}` — it does not even name the image. This step's message is
            // what lands in the step log and on the instance detail page, so translate it
            // into something an operator can act on.
            throw new ImagePullException(image, (int?)ex.StatusCode, ex.ResponseBody, ex);
        }
    }

    public async Task EnsureNetworkAsync(string name, IReadOnlyDictionary<string, string> labels, CancellationToken ct)
    {
        var existing = await client.Networks.ListNetworksAsync(
            new NetworksListParameters { Filters = Filter("name", name) }, ct);
        if (existing.Any(n => n.Name == name))
            return;

        await client.Networks.CreateNetworkAsync(new NetworksCreateParameters
        {
            Name = name,
            Driver = "bridge",
            Labels = new Dictionary<string, string>(labels),
        }, ct);
    }

    // Docker volume-create is idempotent by name (an existing volume is returned),
    // so this is a single call — no pre-check needed.
    public async Task EnsureVolumeAsync(string name, IReadOnlyDictionary<string, string> labels, CancellationToken ct) =>
        await client.Volumes.CreateAsync(new VolumesCreateParameters
        {
            Name = name,
            Labels = new Dictionary<string, string>(labels),
        }, ct);

    public async Task<string> EnsureContainerAsync(ContainerSpec spec, CancellationToken ct)
    {
        var existing = await FindContainerAsync(spec.Name, ct);
        if (existing is not null && RunsImage(existing, spec.Image))
        {
            if (!string.Equals(existing.State, "running", StringComparison.OrdinalIgnoreCase))
                await client.Containers.StartContainerAsync(existing.ID, new ContainerStartParameters(), ct);
            return existing.ID;
        }
        // A container of this name on some other image is the upgrade case: converge on the
        // spec by replacing it rather than adopting something the recipe no longer describes.
        if (existing is not null)
            await RemoveContainerAsync(existing.ID, ct);

        var portKey = spec.PublishContainerPort is int p ? $"{p}/tcp" : null;

        var create = new CreateContainerParameters
        {
            Name = spec.Name,
            Image = spec.Image,
            Env = spec.Env.Select(kv => $"{kv.Key}={kv.Value}").ToList(),
            Labels = new Dictionary<string, string>(spec.Labels),
            HostConfig = new HostConfig
            {
                NetworkMode = spec.NetworkName,
                RestartPolicy = new RestartPolicy { Name = RestartPolicyKind.UnlessStopped },
                Mounts = spec.Mounts
                    .Select(m => new Mount { Type = "volume", Source = m.VolumeName, Target = m.ContainerPath })
                    .ToList(),
            },
        };

        if (portKey is not null)
        {
            create.ExposedPorts = new Dictionary<string, EmptyStruct> { [portKey] = default };
            create.HostConfig.PortBindings = new Dictionary<string, IList<PortBinding>>
            {
                [portKey] = new List<PortBinding>
                {
                    // HostPort "0" lets Docker pick a free port, which the saga reads
                    // back via InspectContainerAsync — the source of truth for the port.
                    new() { HostIP = "0.0.0.0", HostPort = spec.PublishHostPort > 0 ? spec.PublishHostPort.ToString() : "0" },
                },
            };
        }

        if (spec.HealthCmd is { Count: > 0 })
        {
            // HealthConfig mixes units in this build: Interval/Timeout are TimeSpan,
            // StartPeriod/Retries are long (StartPeriod in nanoseconds).
            const long ns = 1_000_000_000L;
            create.Healthcheck = new HealthConfig
            {
                Test = spec.HealthCmd.ToList(),
                Interval = TimeSpan.FromSeconds(3),
                Timeout = TimeSpan.FromSeconds(3),
                Retries = 20,
                StartPeriod = 2 * ns,
            };
        }

        var created = await client.Containers.CreateContainerAsync(create, ct);
        await client.Containers.StartContainerAsync(created.ID, new ContainerStartParameters(), ct);
        return created.ID;
    }

    public async Task<ContainerStatus> InspectContainerAsync(string nameOrId, CancellationToken ct)
    {
        try
        {
            var r = await client.Containers.InspectContainerAsync(nameOrId, ct);
            var health = (r.State?.Health?.Status?.ToLowerInvariant()) switch
            {
                "healthy" => HealthState.Healthy,
                "starting" => HealthState.Starting,
                "unhealthy" => HealthState.Unhealthy,
                _ => HealthState.None,
            };
            int? published = null;
            if (r.NetworkSettings?.Ports is { } ports)
            {
                foreach (var binding in ports.Values)
                {
                    var host = binding?.FirstOrDefault()?.HostPort;
                    if (int.TryParse(host, out var hp)) { published = hp; break; }
                }
            }
            return new ContainerStatus(true, r.State?.Running ?? false, health, published);
        }
        catch (DockerContainerNotFoundException)
        {
            return new ContainerStatus(false, false, HealthState.None, null);
        }
    }

    public async Task StartContainerAsync(string nameOrId, CancellationToken ct)
    {
        try { await client.Containers.StartContainerAsync(nameOrId, new ContainerStartParameters(), ct); }
        catch (DockerContainerNotFoundException) { /* idempotent */ }
    }

    public async Task StopContainerAsync(string nameOrId, CancellationToken ct)
    {
        try { await client.Containers.StopContainerAsync(nameOrId, new ContainerStopParameters { WaitBeforeKillSeconds = 10 }, ct); }
        catch (DockerContainerNotFoundException) { /* idempotent */ }
    }

    public async Task RemoveContainerAsync(string nameOrId, CancellationToken ct)
    {
        try
        {
            await client.Containers.RemoveContainerAsync(nameOrId,
                new ContainerRemoveParameters { Force = true, RemoveVolumes = false }, ct);
        }
        catch (DockerContainerNotFoundException) { /* idempotent */ }
    }

    public async Task RemoveNetworkAsync(string name, CancellationToken ct)
    {
        var existing = await client.Networks.ListNetworksAsync(
            new NetworksListParameters { Filters = Filter("name", name) }, ct);
        var match = existing.FirstOrDefault(n => n.Name == name);
        if (match is null)
            return;
        try { await client.Networks.DeleteNetworkAsync(match.ID, ct); }
        catch (DockerApiException e) when (e.StatusCode == HttpStatusCode.NotFound) { /* idempotent */ }
    }

    public async Task RemoveVolumeAsync(string name, CancellationToken ct)
    {
        try { await client.Volumes.RemoveAsync(name, force: true, ct); }
        catch (DockerApiException e) when (e.StatusCode == HttpStatusCode.NotFound) { /* idempotent */ }
    }

    /// <summary>
    /// Whether an existing container is running the image the spec asks for. Docker reports
    /// the reference the container was created from, which is the same string the recipe
    /// builds, so this compares like with like. A report we cannot read as a reference
    /// (empty, or an id left behind by a tag since re-pointed) counts as a match: churning a
    /// running container on an unreadable signal is worse than leaving it alone, and a
    /// genuine tag change is always legible.
    /// </summary>
    private static bool RunsImage(ContainerListResponse existing, string wanted) =>
        string.IsNullOrEmpty(existing.Image)
        || existing.Image.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
        || existing.Image == wanted;

    private async Task<ContainerListResponse?> FindContainerAsync(string name, CancellationToken ct)
    {
        var matches = await client.Containers.ListContainersAsync(new ContainersListParameters
        {
            All = true,
            Filters = Filter("name", name),
        }, ct);
        // The name filter is a substring/regex match, so re-check for the exact
        // "/name" Docker records, never a prefix collision.
        return matches.FirstOrDefault(c => c.Names.Any(n => n == "/" + name));
    }

    private static IDictionary<string, IDictionary<string, bool>> Filter(string key, string value) =>
        new Dictionary<string, IDictionary<string, bool>> { [key] = new Dictionary<string, bool> { [value] = true } };

    private static (string Repo, string Tag) SplitImage(string image)
    {
        // Split only on a tag colon, not a registry-port colon: the tag is after the
        // LAST colon and only if that segment has no '/'.
        var idx = image.LastIndexOf(':');
        if (idx > 0 && !image[(idx + 1)..].Contains('/'))
            return (image[..idx], image[(idx + 1)..]);
        return (image, "latest");
    }
}
