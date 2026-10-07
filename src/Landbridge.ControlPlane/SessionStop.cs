using Landbridge.Contracts;
using Landbridge.Core;

namespace Landbridge.ControlPlane;

/// <summary>
/// Commits <see cref="StopSession"/>, then sends <see cref="StopCommand"/> to the machine
/// that held the attempt (#127).
/// </summary>
/// <remarks>
/// The machine is the one recorded on the instance this stop revoked. Dispatch writes that
/// row, and the registry tracks the session, before <c>started</c> sets observed running, so
/// a stop in that window still has a process to wind down. A dropped socket removes the
/// registry entry and leaves the row; <see cref="RunnerConnectionRegistry.SendAsync"/> then
/// enqueues the stop for the runner's next connection. When the row never recorded a machine,
/// the live registry entry is the fallback. A session with no instance and no registry entry
/// has no process, so this sends nothing.
/// </remarks>
public static class SessionStop
{
    public static async Task<StoreResult> ApplyAndSignalAsync(
        SessionStore store,
        RunnerConnectionRegistry registry,
        SessionId session,
        Actor actor,
        TimeSpan ttl,
        CancellationToken ct)
    {
        var applied = await store.ApplyAsync(session, new StopSession(actor), ct);
        if (applied is not StoreResult.Applied ok)
            return applied;

        // Read the instance after the commit. Stop clears CurrentInstance and revokes the
        // row, so the pre-stop "current instance" query would miss the machine this transition
        // actually ended.
        Guid? machine = null;
        if (ok.Effects.OfType<RevokeWorkerInstanceToken>().FirstOrDefault() is { } revoked)
            machine = await store.InstanceMachineAsync(revoked.Instance, ct);
        machine ??= registry.MachineFor(session);
        if (machine is { } dest)
        {
            await registry.SendAsync(
                dest,
                new StopCommand(session, ttl, StopDisposition.Preserve, "stop"),
                ct);
        }
        return applied;
    }
}
