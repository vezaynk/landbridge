using Docket.Contracts;
using Docket.ControlPlane.Auth;
using Docket.Core;
using Microsoft.Extensions.Logging;

namespace Docket.ControlPlane;

/// <summary>
/// Drives the two docketd ends of a relay forward from a freshly-issued grant
/// (spec §8.3). The relay has no docketd channel of its own in this deployment,
/// so the <b>control plane</b> relays <c>open-forward</c> to both ends over the
/// runner channel: it resolves each end's machine, sends the producer its dial
/// target and the consumer a bind instruction — both carrying the <em>same</em>
/// single-use-per-role grant — then waits for the consumer's
/// <c>forward-opened</c> to learn the loopback port to hand the worker.
///
/// <para>A singleton alongside <see cref="RunnerConnectionRegistry"/> and
/// <see cref="ForwardWaiters"/>; grant issuance stays in the scoped
/// <see cref="RelayGrantService"/>, and this only coordinates the already-issued
/// grant across the two live connections.</para>
/// </summary>
public sealed class ForwardOrchestrator(
    RunnerConnectionRegistry registry,
    ForwardWaiters waiters,
    ILogger<ForwardOrchestrator> logger)
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
    public async Task<ForwardEstablishResult> EstablishAsync(
        WorkerCaller consumer, RelayGrantResult.Issued issued, string serviceName, string relayUrl,
        CancellationToken ct = default)
    {
        // Both ends must be live right now: the plane sends over their runner
        // channels, and a forward to a machine that is gone can never splice.
        var consumerMachine = registry.MachineFor(consumer.Task);
        if (consumerMachine is null)
            return new ForwardEstablishResult.Failed("your machine is not connected to the control plane");

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
            consumer.Task, forwardId, serviceName,
            RelayTunnel.ConsumerRole, issued.Grant, relayUrl, Port: 0);

        if (!await registry.SendAsync(producerMachine, producerCommand, ct))
            return new ForwardEstablishResult.Failed(
                $"could not reach the machine hosting service '{serviceName}'");
        if (!await registry.SendAsync(consumerMachine, consumerCommand, ct))
            return new ForwardEstablishResult.Failed("could not reach your machine to open the forward");

        try
        {
            var port = await waiter.Opened.WaitAsync(OpenTimeout, ct);
            logger.LogInformation(
                "forward {ForwardId} opened: consumer {ConsumerMachine} bound 127.0.0.1:{Port}, producer {ProducerMachine}",
                forwardId, consumerMachine, port, producerMachine);
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
    /// no consumer <c>docketd</c> to command and no <c>forward-opened</c> to wait
    /// for — the preview frontend is the consumer and dials the relay itself — so
    /// this just resolves the producer's machine and sends it its dial target.
    /// The producer dials on demand: a fresh grant + forward id per browser
    /// connection, so this is called once per connection. Returns <c>false</c>
    /// (never throws) when the producer machine is gone or the send fails, so the
    /// endpoint renders a clean status rather than a hang.
    /// </summary>
    public async Task<bool> SendProducerDialAsync(
        TaskId producer, string forwardId, string serviceName, string grant, string relayUrl, int port,
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
/// transition — kept in <c>Docket.ControlPlane</c> so the plane takes no
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
