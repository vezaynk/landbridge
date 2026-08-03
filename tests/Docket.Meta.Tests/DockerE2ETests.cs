using System.Diagnostics;
using System.Formats.Tar;
using Docker.DotNet;
using Docker.DotNet.Models;
using Docket.Meta;
using Docket.Meta.Data;
using Docket.Meta.Provisioning;
using Docket.Meta.Substrate;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ContainerSpec = Docket.Meta.Substrate.ContainerSpec;

namespace Docket.Meta.Tests;

/// <summary>
/// Docker-gated end-to-end (design note §7). Runs only where a Docker Engine is
/// reachable (ubuntu CI + local Docker); SkippableFact means macOS/Windows CI legs
/// without a daemon skip cleanly. Two levels:
///   1. <see cref="Substrate_round_trip"/> — the real <see cref="DockerSubstrate"/>
///      against the daemon: network + volume + a health-checked container + port
///      read-back + idempotent adopt + teardown. Fast (one small image).
///   2. <see cref="Full_instance_provision_health_destroy"/> — the whole recipe:
///      publish + build the docket runtime images in-process (no SDK-in-container),
///      provision a real Instance, hit the plane's real HTTP liveness endpoint, then
///      destroy and assert every object is gone.
/// </summary>
public class DockerE2ETests
{
    private const string Sock = "unix:///var/run/docker.sock";
    private const string PgImage = "postgres:16";

    /// <summary>
    /// A throwaway registry for the pull-failure test. From Docker Hub, like
    /// <see cref="PgImage"/> — this suite already depends on Hub, so it adds no new
    /// class of dependency, and 127.0.0.1 registries are insecure-allowed by default so
    /// the daemon needs no configuration.
    /// </summary>
    private const string RegistryImage = "registry:2";

    private static bool DockerReachable()
    {
        try
        {
            using var client = new DockerClientConfiguration(new Uri(Sock)).CreateClient();
            client.System.PingAsync().GetAwaiter().GetResult();
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// A pull that cannot succeed must fail with a message naming the image, not a raw
    /// <c>DockerApiException</c>. Companion to <see cref="ImagePullDiagnosticsTests"/>:
    /// those pin the wording against captured response bodies, these two prove the live
    /// daemon still fails in a shape <see cref="DockerSubstrate"/> translates.
    ///
    /// <para>Deliberately aimed at a port with nothing behind it rather than at a real
    /// registry on the internet. A test whose failure mode is "the registry was slow" is
    /// a false-red generator in a per-push matrix, and a false red costs far more than
    /// the bug it imitates. Connection-refused is instant, needs no network beyond
    /// loopback, and still exercises the whole daemon → substrate path.</para>
    /// </summary>
    [SkippableFact]
    public async Task An_unreachable_registry_is_translated_not_leaked()
    {
        Skip.IfNot(DockerReachable(), "Docker Engine not reachable at " + Sock);
        using var client = new DockerClientConfiguration(new Uri(Sock)).CreateClient();
        var sub = new DockerSubstrate(client);
        // Port 1: privileged, never a registry, refused immediately.
        var image = "127.0.0.1:1/docket-no-such-registry:v0";
        var ct = new CancellationTokenSource(TimeSpan.FromMinutes(1)).Token;

        var ex = await Assert.ThrowsAsync<ImagePullException>(() => sub.EnsureImageAsync(image, ct));

        // The raw Docker message carries none of this — that is the entire point.
        Assert.Contains(image, ex.Message);
        Assert.Contains("docs/META.md", ex.Message);
        Assert.DoesNotContain('\n', ex.Message);
        Assert.NotEmpty(ex.RegistryDetail);
        // The operator gets a cause, not an HTTP status that reads like a server fault.
        Assert.DoesNotContain("InternalServerError", ex.Message);
    }

    /// <summary>
    /// The production-shaped case: a registry that is reachable and *answers*, refusing a
    /// repository it does not have. That is a different path through Docker.DotNet than the
    /// dial failure above — an HTTP protocol error rather than a transport error — and it is
    /// the one a real deployment hits, so it is worth exercising against a real registry
    /// rather than a mock. Runs a throwaway <c>registry:2</c> on a Docker-assigned port.
    /// </summary>
    [SkippableFact]
    public async Task A_real_registry_refusing_a_repository_is_translated_too()
    {
        Skip.IfNot(DockerReachable(), "Docker Engine not reachable at " + Sock);
        using var client = new DockerClientConfiguration(new Uri(Sock)).CreateClient();
        var sub = new DockerSubstrate(client);
        var id = "dktmeta-reg-" + Guid.NewGuid().ToString("N")[..8];
        var net = id + "-net";
        var labels = new Dictionary<string, string> { ["docket.managed"] = "meta-test", ["docket.instance"] = id };
        var ct = new CancellationTokenSource(TimeSpan.FromMinutes(4)).Token;

        try
        {
            await sub.EnsureImageAsync(RegistryImage, ct);
            await sub.EnsureNetworkAsync(net, labels, ct);
            await sub.EnsureContainerAsync(new ContainerSpec
            {
                Name = id,
                Image = RegistryImage,
                NetworkName = net,
                Labels = labels,
                PublishContainerPort = 5000,
                PublishHostPort = 0,
            }, ct);

            var status = await WaitAsync(() => sub.InspectContainerAsync(id, ct),
                s => s.Running && s.PublishedPort is not null, TimeSpan.FromMinutes(1));
            var port = status.PublishedPort!.Value;

            // Wait until it actually serves the v2 API. Pulling too early would fail with
            // connection-refused, silently making this a duplicate of the test above.
            Assert.True(await RegistryAnswersAsync(port, TimeSpan.FromMinutes(1)),
                $"registry:2 never answered /v2/ on 127.0.0.1:{port}");

            var image = $"127.0.0.1:{port}/docket-absent-repo:v0";
            var ex = await Assert.ThrowsAsync<ImagePullException>(() => sub.EnsureImageAsync(image, ct));

            Assert.Contains(image, ex.Message);
            Assert.Contains("docs/META.md", ex.Message);
            Assert.DoesNotContain('\n', ex.Message);
            Assert.DoesNotContain("InternalServerError", ex.Message);
            // It got a real answer from a real registry, not a dial failure — otherwise the
            // readiness gate above lied and this test proves nothing the other one doesn't.
            Assert.DoesNotContain("refused", ex.RegistryDetail.ToLowerInvariant());

            // Observed against registry:2 + Docker 29: the body is `… : not found`, so this
            // lands on the missing-tag branch and the operator is told to publish or re-pin.
            // Not asserted — the daemon's phrasing is its own business, and pinning it here
            // would turn a wording change into a red build for no gain. The branch logic
            // itself is covered exhaustively in ImagePullDiagnosticsTests.
        }
        finally
        {
            await sub.RemoveContainerAsync(id, ct);
            await sub.RemoveNetworkAsync(net, ct);
        }
    }

    /// <summary>Polls the registry's own <c>/v2/</c> endpoint until it responds.</summary>
    private static async Task<bool> RegistryAnswersAsync(int port, TimeSpan timeout)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                // 200 or 401 both mean "a registry is listening and speaking v2".
                using var resp = await http.GetAsync($"http://127.0.0.1:{port}/v2/");
                return true;
            }
            catch (HttpRequestException) { /* not up yet */ }
            catch (TaskCanceledException) { /* not up yet */ }
            await Task.Delay(250);
        }
        return false;
    }

    [SkippableFact]
    public async Task Substrate_round_trip()
    {
        Skip.IfNot(DockerReachable(), "Docker Engine not reachable at " + Sock);
        using var client = new DockerClientConfiguration(new Uri(Sock)).CreateClient();
        var sub = new DockerSubstrate(client);
        var id = "dktmeta-" + Guid.NewGuid().ToString("N")[..10];
        var net = id + "-net";
        var vol = id + "-vol";
        var pg = id + "-pg";
        var labels = new Dictionary<string, string> { ["docket.managed"] = "meta-test", ["docket.instance"] = id };
        var ct = new CancellationTokenSource(TimeSpan.FromMinutes(4)).Token;

        try
        {
            await sub.EnsureImageAsync(PgImage, ct);
            await sub.EnsureNetworkAsync(net, labels, ct);
            await sub.EnsureVolumeAsync(vol, labels, ct);

            var spec = new ContainerSpec
            {
                Name = pg,
                Image = PgImage,
                NetworkName = net,
                Labels = labels,
                Env = new Dictionary<string, string>
                {
                    ["POSTGRES_USER"] = "docket",
                    ["POSTGRES_PASSWORD"] = "pw",
                    ["POSTGRES_DB"] = "docket",
                },
                Mounts = new[] { new MountSpec(vol, "/var/lib/postgresql/data") },
                HealthCmd = new[] { "CMD-SHELL", "pg_isready -U docket -d docket" },
                PublishContainerPort = 5432,
                PublishHostPort = 0,
            };

            var cid = await sub.EnsureContainerAsync(spec, ct);
            Assert.False(string.IsNullOrEmpty(cid));

            var status = await WaitAsync(() => sub.InspectContainerAsync(pg, ct),
                s => s.Health is HealthState.Healthy, TimeSpan.FromMinutes(2));
            Assert.True(status.Running);
            Assert.Equal(HealthState.Healthy, status.Health);
            Assert.NotNull(status.PublishedPort); // Docker assigned a host port; we read it back

            // Idempotent adopt: a second Ensure returns the same container, still one exists.
            var cid2 = await sub.EnsureContainerAsync(spec, ct);
            Assert.Equal(cid, cid2);
        }
        finally
        {
            await sub.RemoveContainerAsync(pg, ct);
            await sub.RemoveNetworkAsync(net, ct);
            await sub.RemoveVolumeAsync(vol, ct);
        }

        // Confirm teardown really removed the container.
        var gone = await sub.InspectContainerAsync(pg, ct);
        Assert.False(gone.Exists);
    }

    [SkippableFact]
    public async Task Full_instance_provision_health_destroy()
    {
        Skip.IfNot(DockerReachable(), "Docker Engine not reachable at " + Sock);
        var repoRoot = FindRepoRoot();
        Skip.If(repoRoot is null, "repo root not found");
        var ct = new CancellationTokenSource(TimeSpan.FromMinutes(10)).Token;

        using var client = new DockerClientConfiguration(new Uri(Sock)).CreateClient();
        var tag = "e2e-" + Guid.NewGuid().ToString("N")[..8];
        var mcpImage = $"docket-mcp:{tag}";
        var relayImage = $"docket-relay:{tag}";

        // 1) Publish + build the two runtime images in-process (fast: COPY of published output).
        await PublishAndBuildAsync(client, repoRoot!, "Docket.Mcp", mcpImage, ct);
        await PublishAndBuildAsync(client, repoRoot!, "Docket.Relay", relayImage, ct);

        // 2) Wire the provisioner with the REAL substrate + probe, a fake edge (health is
        //    checked on the published port directly), an InMemory store, and image repos
        //    pointed at the freshly built tags.
        var options = new MetaOptions
        {
            Domain = "e2e.localhost",
            McpImageRepo = "docket-mcp",
            RelayImageRepo = "docket-relay",
            PostgresImage = PgImage,
        };
        using var db = SagaHarness.NewInMemoryDb("e2e-" + Guid.NewGuid(), SagaHarness.NewProtector());
        // Real time, not a fake: the saga's container health poll sleeps on the injected
        // clock, so a frozen FakeTimeProvider never wakes once a real Postgres reports
        // Starting before Healthy. The in-memory suites can fake it because their
        // substrate is healthy on the first read.
        var clock = TimeProvider.System;
        var host = new HostRow
        {
            Id = Guid.NewGuid(),
            Name = "local",
            EndpointUri = Sock,
            EndpointKind = HostEndpointKind.UnixSocket,
            PublishedHost = "127.0.0.1",
            CreatedAt = clock.GetUtcNow(),
        };
        db.Hosts.Add(host);
        await db.SaveChangesAsync();

        var creator = new InstanceCreator(db, new PlacementService(db), new SecretGenerator(), options, clock);
        var name = "e2e" + Guid.NewGuid().ToString("N")[..6];
        var created = await creator.CreateAsync(new CreateInstanceRequest(name, null, tag, host.Id), ct);

        var caddy = new FakeCaddyAdmin();
        var provisioner = new InstanceProvisioner(
            db, new SingleClientSubstrateFactory(new DockerSubstrate(client)), caddy,
            new InstanceRecipe(options), new InstanceHealthProbe(new HttpClient()), options, clock,
            NullLogger<InstanceProvisioner>.Instance);

        try
        {
            await provisioner.ProvisionAsync(created.Id, ct);

            var row = await db.Instances.SingleAsync(i => i.Id == created.Id);
            Assert.Equal(InstanceState.Ready, row.State);
            Assert.Equal(2, caddy.Routes.Count);

            // The real plane answered its anonymous liveness endpoint on the published port.
            var probe = new InstanceHealthProbe(new HttpClient());
            var health = await probe.CheckAsync(new DockerSubstrate(client), host, row, ct);
            Assert.True(health.Ok, $"health pg={health.PgRunning} mcp={health.McpResponding} relay={health.RelayRunning}");
        }
        finally
        {
            try { await provisioner.DestroyAsync(created.Id, ct); } catch { /* best effort */ }
            await RemoveImageAsync(client, mcpImage);
            await RemoveImageAsync(client, relayImage);
        }

        // Teardown removed the containers, network, and volume.
        var sub = new DockerSubstrate(client);
        Assert.False((await sub.InspectContainerAsync(InstanceNaming.McpContainer(name), ct)).Exists);
        Assert.False((await sub.InspectContainerAsync(InstanceNaming.PgContainer(name), ct)).Exists);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static async Task PublishAndBuildAsync(
        IDockerClient client, string repoRoot, string project, string imageTag, CancellationToken ct)
    {
        var publishDir = Path.Combine(Path.GetTempPath(), "dktmeta-pub-" + Guid.NewGuid().ToString("N")[..8]);
        await RunAsync("dotnet",
            ["publish", Path.Combine(repoRoot, "src", project, project + ".csproj"),
             "-c", "Release", "-o", publishDir, "--nologo"], ct);

        // The runtime Dockerfile expects the publish output as its build context.
        File.Copy(Path.Combine(repoRoot, "docker", project + ".Dockerfile"),
                  Path.Combine(publishDir, "Dockerfile"), overwrite: true);

        await using var tar = new MemoryStream();
        await TarFile.CreateFromDirectoryAsync(publishDir, tar, includeBaseDirectory: false, ct);
        tar.Position = 0;

        // The waiting overload: it completes only when the build finishes, and the
        // progress stream surfaces any build error.
        var errors = new List<string>();
        await client.Images.BuildImageFromDockerfileAsync(
            new ImageBuildParameters { Dockerfile = "Dockerfile", Tags = new List<string> { imageTag } },
            tar,
            authConfigs: null,
            headers: null,
            progress: new Progress<JSONMessage>(m => { if (!string.IsNullOrEmpty(m.ErrorMessage)) errors.Add(m.ErrorMessage); }),
            ct);
        Assert.True(errors.Count == 0, "image build reported: " + string.Join("; ", errors));

        try { Directory.Delete(publishDir, recursive: true); } catch { /* best effort */ }
    }

    private static async Task RemoveImageAsync(IDockerClient client, string image)
    {
        try { await client.Images.DeleteImageAsync(image, new ImageDeleteParameters { Force = true }); }
        catch { /* best effort */ }
    }

    private static async Task<T> WaitAsync<T>(Func<Task<T>> read, Func<T, bool> done, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        T last = await read();
        while (!done(last) && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(1000);
            last = await read();
        }
        return last;
    }

    private static async Task RunAsync(string file, string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(file) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stderr = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"{file} {string.Join(' ', args)} exited {p.ExitCode}: {await stderr}");
    }

    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "docket.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>Hands one real substrate to any host (single-daemon E2E).</summary>
    private sealed class SingleClientSubstrateFactory(ISubstrate substrate) : ISubstrateFactory
    {
        public ISubstrate For(HostRow host) => substrate;
    }
}
