using Landbridge.Contracts;
using Landbridge.ControlPlane.Auth;
using Landbridge.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Landbridge.ControlPlane;

/// <summary>
/// Drives the two landbridged ends of a relay forward from a freshly-issued grant
/// (spec §8.3). The relay has no landbridged channel of its own in this deployment,
/// so the <b>control plane</b> relays <c>open-forward</c> to both ends over the
/// runner channel: it resolves each end's machine, sends the producer its dial
/// target and the consumer a bind instruction — both carrying the <em>same</em>
/// single-use-per-role grant — then waits for the consumer's
/// <c>forward-opened</c> to learn the loopback port to hand back.
///
/// <para>Two kinds of consumer share that one orchestration: a <b>worker</b>, whose
/// end is the machine its task was dispatched to, and a <b>human</b> — a Lead whose
/// end is the machine it bound as its own (§8.3 human path). The difference is only
/// how the consumer machine is resolved; the grant, the commands, and the splice are
/// the same.</para>
///
/// <para>A singleton alongside <see cref="RunnerConnectionRegistry"/> and
/// <see cref="ForwardWaiters"/>; grant issuance stays in the scoped
/// <see cref="RelayGrantService"/>, and this only coordinates the already-issued
/// grant across the two live connections.</para>
/// </summary>
public sealed class ForwardOrchestrator(
    RunnerConnectionRegistry registry,
    ForwardWaiters waiters,
    ILogger<ForwardOrchestrator> logger,
    IServiceScopeFactory? scopes = null)
{
    /// <summary>
    /// How long to wait for the consumer end to bind and report its port before
    /// giving up (§8.3). Generous relative to a loopback bind + two runner-channel
    /// round trips; a forward that misses it is genuinely stuck.
    /// </summary>
    public static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Establish the forward described by <paramref name="issued"/> for
    /// <paramref name="consumer"/>. Resolves both ends' machines, sends the two
    /// role-specific <c>open-forward</c> commands, and waits for the consumer's
    /// bound port. Every failure is a <see cref="ForwardEstablishResult.Failed"/>
    /// carrying a worker-facing reason — never a throw — so the tool surface
    /// renders it the same way as a store refusal.
    /// </summary>
    public Task<ForwardEstablishResult> EstablishAsync(
        WorkerCaller consumer, RelayGrantResult.Issued issued, string serviceName, string relayUrl,
        CancellationToken ct = default) =>
        // A worker's consumer end is the machine its own task was dispatched to.
        EstablishCoreAsync(
            registry.MachineFor(consumer.Session), consumer.Session, "your machine",
            issued, serviceName, relayUrl, ct);

    /// <summary>
    /// Establish the same §8.3 forward for a <b>human</b> consumer: a Lead whose
    /// bound machine (§8.3 human path) is <paramref name="consumerMachine"/>. Every
    /// mechanism is identical to <see cref="EstablishAsync"/> — the same single grant
    /// presented once per role, the same two <c>open-forward</c> commands over the
    /// runner channel, the same <c>forward-opened</c> wait for the loopback port —
    /// because the consumer end of a forward was never a task; it was always a
    /// machine, and a worker's was merely derived from its dispatch.
    ///
    /// <para>The consumer command's task field carries the <em>producer's</em> id: a
    /// Lead has no task, and that field is only correlation for the
    /// <c>forward-opened</c>/<c>forward-closed</c> events (the plane routes both by
    /// forward id), so naming the task the forward actually serves keeps the
    /// runner-side log honest.</para>
    /// </summary>
    public Task<ForwardEstablishResult> EstablishForLeadAsync(
        string consumerMachine, RelayGrantResult.Issued issued, string serviceName, string relayUrl,
        CancellationToken ct = default) =>
        EstablishCoreAsync(
            registry.SnapshotFor(consumerMachine) is null ? null : consumerMachine,
            issued.Producer, $"your bound machine {consumerMachine}",
            issued, serviceName, relayUrl, ct);

    /// <summary>
    /// The one orchestration both consumer kinds run. <paramref name="consumerLabel"/>
    /// is how a failure names the consumer end to whoever called ("your machine" for
    /// a worker, "your bound machine {id}" for a Lead);
    /// <paramref name="consumerCorrelation"/> is the task id the consumer command
    /// carries for event correlation only.
    /// </summary>
    private async Task<ForwardEstablishResult> EstablishCoreAsync(
        string? consumerMachine, SessionId consumerCorrelation, string consumerLabel,
        RelayGrantResult.Issued issued, string serviceName, string relayUrl,
        CancellationToken ct)
    {
        // Both ends must be live right now: the plane sends over their runner
        // channels, and a forward to a machine that is gone can never splice.
        if (consumerMachine is null)
            return new ForwardEstablishResult.Failed($"{consumerLabel} is not connected to the control plane");

        var producerMachine = registry.MachineFor(issued.Producer);
        if (producerMachine is null)
            return new ForwardEstablishResult.Failed(
                $"the machine hosting service '{serviceName}' is not connected to the control plane");

        var forwardId = issued.ForwardId.ToString();
        using var waiter = waiters.Register(forwardId);

        // Both ends present the SAME grant, each for its own role (single-use per
        // role, §8.3). Producer gets the service's loopback port to dial; consumer
        // binds its own and reports it back via forward-opened.
        var producerCommand = new OpenForwardCommand(
            issued.Producer, forwardId, serviceName,
            RelayTunnel.ProducerRole, issued.Grant, relayUrl, issued.Port);
        var consumerCommand = new OpenForwardCommand(
            consumerCorrelation, forwardId, serviceName,
            RelayTunnel.ConsumerRole, issued.Grant, relayUrl, Port: 0);

        if (!await registry.SendAsync(producerMachine, producerCommand, ct))
            return new ForwardEstablishResult.Failed(
                $"could not reach the machine hosting service '{serviceName}'");
        if (!await registry.SendAsync(consumerMachine, consumerCommand, ct))
            return new ForwardEstablishResult.Failed($"could not reach {consumerLabel} to open the forward");

        try
        {
            var port = await waiter.Opened.WaitAsync(OpenTimeout, ct);
            logger.LogInformation(
                "forward {ForwardId} opened: consumer {ConsumerMachine} bound 127.0.0.1:{Port}, producer {ProducerMachine}",
                forwardId, consumerMachine, port, producerMachine);
            if (scopes is not null)
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<RelayGrantService>()
                    .RecordConsumerBindAsync(issued.ForwardId, consumerMachine, port, ct);
            }
            return new ForwardEstablishResult.Established(port);
        }
        catch (TimeoutException)
        {
            return new ForwardEstablishResult.Failed("the forward did not open within the timeout");
        }
        catch (ForwardClosedBeforeOpenException e)
        {
            return new ForwardEstablishResult.Failed($"the forward closed before it opened: {e.Message}");
        }
    }

    /// <summary>
    /// Relay a single <c>open-forward</c> to the <b>producer</b> end only, for an
    /// §8.4 HTTP-preview connection. Unlike <see cref="EstablishAsync"/> there is
    /// no consumer <c>landbridged</c> to command and no <c>forward-opened</c> to wait
    /// for — the preview frontend is the consumer and dials the relay itself — so
    /// this just resolves the producer's machine and sends it its dial target.
    /// The producer dials on demand: a fresh grant + forward id per browser
    /// connection, so this is called once per connection. Returns <c>false</c>
    /// (never throws) when the producer machine is gone or the send fails, so the
    /// endpoint renders a clean status rather than a hang.
    /// </summary>
    public async Task<bool> SendProducerDialAsync(
        SessionId producer, string forwardId, string serviceName, string grant, string relayUrl, int port,
        CancellationToken ct = default)
    {
        var producerMachine = registry.MachineFor(producer);
        if (producerMachine is null)
        {
            logger.LogInformation(
                "preview forward {ForwardId}: producer task {Producer} is not connected", forwardId, producer);
            return false;
        }

        var command = new OpenForwardCommand(
            producer, forwardId, serviceName, RelayTunnel.ProducerRole, grant, relayUrl, port);
        var sent = await registry.SendAsync(producerMachine, command, ct);
        if (!sent)
            logger.LogInformation(
                "preview forward {ForwardId}: could not reach producer machine {Machine}", forwardId, producerMachine);
        return sent;
    }
}

/// <summary>
/// Outcome of <see cref="ForwardOrchestrator.EstablishAsync"/>. A plain result
/// (not <see cref="StoreResult"/>) — establishing a forward is not a task
/// transition — kept in <c>Landbridge.ControlPlane</c> so the plane takes no
/// dependency on the MCP layer; the tool surface maps <see cref="Failed"/> onto
/// its own exception type.
/// </summary>
public abstract record ForwardEstablishResult
{
    private ForwardEstablishResult() { }

    /// <summary>The consumer bound <c>127.0.0.1:<see cref="Port"/></c> and is ready.</summary>
    public sealed record Established(int Port) : ForwardEstablishResult;

    /// <summary>The forward could not be established; <see cref="Reason"/> is worker-facing.</summary>
    public sealed record Failed(string Reason) : ForwardEstablishResult;
}
