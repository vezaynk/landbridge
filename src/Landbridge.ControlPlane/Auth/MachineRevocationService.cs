using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Docket.ControlPlane.Auth;

/// <summary>
/// Un-trusting a machine, whole (§5: it must take seconds; §13). One call takes away
/// everything an enrolled box can still do with what it holds:
/// <list type="number">
/// <item><b>its credentials</b> — the machine row and every access/refresh token minted
/// for it (<see cref="TokenService.RevokeMachineCredentialsAsync"/>), so nothing it
/// holds authenticates and no further access can be refreshed;</item>
/// <item><b>its command channel</b> — the live <c>/runner</c> WebSocket is dropped from
/// the registry and hung up on, and the tasks it held requeue exactly as they do when a
/// machine's socket dies on its own;</item>
/// <item><b>its workers</b> — every unrevoked worker instance recorded as having run on
/// that machine, so the worker tokens sitting in those tasks' MCP configs stop
/// authenticating (§9 check 14: a worker token is valid exactly while its instance row
/// is).</item>
/// </list>
///
/// <para><b>Why this is a service and not one more method on
/// <see cref="TokenService"/>.</b> Each of the three lives somewhere different — the
/// credential store, an in-memory connection registry, the task store's own revoke
/// effect — and only two of them are credentials at all. What made the previous
/// arrangement dangerous was not that the pieces were separate but that the
/// credential half was named as though it were the whole: revoking a machine flipped
/// its token rows and left the box holding an authenticated socket that is never
/// re-checked (auth runs once, at upgrade) plus live worker tokens that carry no
/// machine id for the credential sweep to match. That is a revoked machine still
/// dispatching, reporting, and reading — so the composition is the unit callers get,
/// and the halves are documented as halves.</para>
///
/// <para><b>Order is load-bearing, and it is: credentials, channel, workers, requeue.</b>
/// Credentials first — a box whose socket is closed reconnects within its backoff, and a
/// still-live access token would buy it a fresh channel in the window before the
/// credential write; dead credentials make that reconnect a 401. The channel second,
/// which takes the machine out of <see cref="RunnerConnectionRegistry.ReadyMachines"/>
/// and so closes the only way a <em>new</em> worker instance could still be minted for
/// it — a dispatch pass claiming a task onto a still-registered machine would otherwise
/// mint one behind the sweep's back. Then the worker sweep, and only then the requeue:
/// the requeue is the one step whose work is recoverable (a liveness clock or a
/// reconnect reclaims a stranded task) while a missed worker token is not, so it goes
/// last and nothing that can fail runs between the revoke and the sweep.</para>
///
/// <para><b>Not one transaction, deliberately.</b> Two of the four steps are not database
/// writes at all — one is in-memory, one runs on its own scope and connection — so no
/// transaction could span them. The order above is the mitigation: it is chosen so that
/// stopping at any point leaves the machine strictly less trusted than before, never
/// half-trusted. A caller that sees an exception should retry; the operation is
/// idempotent.</para>
///
/// <para><b>Idempotent.</b> Every write is predicated on <c>!Revoked</c> and the
/// disconnect on a connection actually being registered, so revoking an
/// already-revoked or offline machine is a no-op that reports zero — which is what
/// lets a surface offer the action without first proving the machine is live.</para>
/// </summary>
public sealed class MachineRevocationService(
    DocketDbContext db,
    TokenService tokens,
    RunnerConnectionRegistry registry,
    RunnerEventSink sink,
    TimeProvider clock,
    ILogger<MachineRevocationService> logger)
{
    /// <summary>
    /// Revokes <paramref name="machineId"/> completely; see the type summary for what
    /// that covers and why in this order. Safe to call for a machine that is offline,
    /// unknown, or already revoked.
    /// </summary>
    public async Task<MachineRevocation> RevokeAsync(Guid machineId, CancellationToken ct = default)
    {
        // The registry keys machines by their id's string form (the runner endpoint
        // registers the authenticated machine id that way), so both halves of this
        // method are talking about the same box.
        var id = machineId.ToString();

        await tokens.RevokeMachineCredentialsAsync(machineId, ct);

        var teardown = await registry.DisconnectAsync(id, ct);

        // The sweep the credential predicate cannot express. A worker credential has no
        // MachineId — it is scoped to {team, task, instance} — but the instance row
        // records where the dispatch ran, and that is the join: revoking those rows
        // kills their tokens without inventing a second bookkeeping path. It reaches
        // exactly the instances still live on this machine, and no others: an instance
        // that ran here and was already superseded is revoked; an instance for the same
        // task now running elsewhere names that machine, not this one.
        var now = clock.GetUtcNow();
        var workers = await db.WorkerInstances
            .Where(w => w.MachineId == id && !w.Revoked)
            .ExecuteUpdateAsync(s => s
                .SetProperty(w => w.Revoked, true)
                .SetProperty(w => w.RevokedAt, now), ct);

        // Last, the same requeue a dropped socket gets (§10): the machine is gone, so
        // everything it held goes back in the queue. Each of those transitions also emits
        // RevokeWorkerInstanceToken for its incumbent, which the sweep above has already
        // done — a no-op, not a conflict, since that effect is itself predicated on
        // !Revoked.
        await sink.HandleDisconnectAsync(id, teardown.Held, ct);

        logger.LogWarning(
            "machine revoked: machine={Machine} channel={Closed} requeued={Requeued} workers={Workers}",
            id, teardown.Unregistered, teardown.Held.Count, workers);

        return new MachineRevocation(teardown.Unregistered, teardown.Held.Count, workers);
    }
}

/// <summary>
/// What a revoke actually took away, so the surface that asked can say so rather than
/// reporting a bare success.
/// </summary>
/// <param name="ChannelClosed">Whether the machine held a live command channel that this
/// revoke closed. False for an enrolled-but-offline machine, which is the ordinary case.</param>
/// <param name="SessionsRequeued">Tasks the channel held, now back in the queue for another
/// machine to pick up.</param>
/// <param name="WorkersRevoked">Every worker instance that was still live on the machine —
/// the requeued tasks' incumbents included, since the sweep runs before the requeue. So
/// this is the count of harnesses on that box whose token just died, which is the number
/// an operator is actually asking about.</param>
public sealed record MachineRevocation(bool ChannelClosed, int SessionsRequeued, int WorkersRevoked);
