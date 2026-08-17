using Landbridge.Contracts;
using Landbridge.Core;
using Microsoft.Extensions.Logging;

namespace Landbridge.ControlPlane;

/// <summary>
/// One live forward a leaving-<c>working</c> task is taking with it (§8.3), as read off
/// its unrevoked grants at the moment <c>ClearServicesAndForwards</c> fires.
/// </summary>
/// <param name="Producer">
/// The task whose registered service this forward reaches — the one leaving
/// <c>working</c>, and the task id the <em>producer</em> end's
/// <see cref="OpenForwardCommand"/> carried.
/// </param>
/// <param name="ForwardId">The forward id both ends and the relay key their pairing on.</param>
/// <param name="Consumer">
/// The consumer end's task, when the consumer <em>is</em> a task — a worker that called
/// <c>open_forward</c>. Null for the two consumer kinds that are not: an §8.4 preview
/// (the browser, with no <c>landbridged</c> at all) and the §8.3 human path (a Lead's own
/// bound machine, which the grant does not record). Both are still torn down, one hop
/// later: the relay splices exactly two ends per forward id, so closing the producer's
/// tunnel ends theirs.
/// </param>
public readonly record struct ForwardTeardown(SessionId Producer, string ForwardId, SessionId? Consumer);

/// <summary>
/// Tells the machines holding a task's relay forwards to close them, at the moment the
/// task leaves <c>working</c> (spec §8.3: "an established splice persists <b>until the
/// owning task leaves <c>working</c></b>"). Driven from <see cref="SessionStore"/>'s
/// <c>ClearServicesAndForwards</c> effect, alongside the service-row delete and the grant
/// revoke that were already there.
///
/// <para><b>Why the revoke was not enough.</b> A grant gates <em>open</em> and nothing
/// else — revoking it stops the next tunnel and says nothing to a splice already running.
/// Where the plane killed the worker the splice ended by accident (the tree-kill takes the
/// producer's sockets with it, so the tunnels die by RST); where nothing was killed —
/// <c>report_result</c>, <c>cancel_session</c> — the splice and the consumer end's idle
/// loopback listener outlived the task that authorized them. This is the command that
/// makes the spec's bound true rather than incidental.</para>
///
/// <para><b>Best-effort, like every §10 command</b> (<see cref="RunnerConnectionRegistry.SendAsync"/>):
/// a machine with no live channel is not sent to and nothing is queued for it, which is
/// correct rather than lossy — a forward is a live socket on a live connection, so a
/// machine whose channel is gone has already lost its half of every splice it held.</para>
///
/// <para>A singleton alongside the registry it sends through: it holds no state of its own
/// and takes no <c>DbContext</c> — the grant rows are read inside the store's transaction
/// and handed here as values, so nothing touches the database after the commit.</para>
/// </summary>
public sealed class ForwardTeardownService(
    RunnerConnectionRegistry registry, ILogger<ForwardTeardownService> logger)
{
    /// <summary>
    /// Close both ends of every forward in <paramref name="forwards"/>. Never throws: a
    /// send that fails is logged and the rest still go, because this runs after the
    /// transition has committed and a failed close must not surface as a failed
    /// <c>report_result</c>.
    /// </summary>
    public async Task CloseAsync(IReadOnlyList<ForwardTeardown> forwards, CancellationToken ct = default)
    {
        foreach (var forward in forwards)
        {
            await CloseEndAsync(forward.Producer, forward, "producer", ct);
            // The consumer end is on its own machine and holds its own half — the loopback
            // listener the consumer's client connected to. Closing the producer's tunnel
            // would end its splice through the relay, but only once the relay observed the
            // close; telling it directly is what closes a listener that never accepted.
            if (forward.Consumer is { } consumer)
                await CloseEndAsync(consumer, forward, "consumer", ct);
        }
    }

    private async Task CloseEndAsync(SessionId end, ForwardTeardown forward, string role, CancellationToken ct)
    {
        // The end's own task id, not the producer's: a worker's consumer end received
        // open-forward under its own task, and close-forward's task field is correlation
        // (§10 — every message carries one), so it must name the same one.
        if (registry.MachineFor(end) is not { } machine)
            return;
        if (!await registry.SendAsync(machine, new CloseForwardCommand(end, forward.ForwardId), ct))
            logger.LogInformation(
                "forward {ForwardId}: could not reach {Role} machine {Machine} to close it",
                forward.ForwardId, role, machine);
    }
}
