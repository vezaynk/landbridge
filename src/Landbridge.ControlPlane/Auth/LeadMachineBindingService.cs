using Microsoft.EntityFrameworkCore;

namespace Landbridge.ControlPlane.Auth;

/// <summary>
/// The lead↔machine binding (spec §8.3 human path), alongside
/// <see cref="RelayGrantService"/> and in the same clock/db-injection style. It
/// answers exactly one question the rest of the plane cannot: <em>which enrolled
/// machine is this human sitting at?</em> — the consumer end a Lead-facing forward
/// binds its loopback listener on.
///
/// <para>A Lead's machine is not derivable. Machines are enrolled runners (§11); a
/// Lead is a human's session with no machine of its own, and §4 is explicit that a
/// Lead's box need not run <c>landbridged</c> at all. So the human says so, once,
/// explicitly — and can revoke it. Nothing is inferred from a dispatch, a
/// heartbeat, or an IP.</para>
///
/// <para>Keyed on the human rather than the Lead credential or the Team: the box on
/// a person's desk outlives their session, spans the Teams they lead, and does
/// <em>not</em> transfer to whoever takes a Team over (§4). One live binding per
/// human and per machine, and both are the database's invariant (two partial
/// unique indexes), not a read's — the advisory reads below exist only to produce a
/// message that names what is in the way.</para>
/// </summary>
public sealed class LeadMachineBindingService(LandbridgeDbContext db, TimeProvider clock)
{
    /// <summary>
    /// Binds <paramref name="machineId"/> to <paramref name="humanId"/>. Refuses an
    /// unknown or revoked machine, a human who already holds a binding (rebinding is
    /// unbind-then-bind, so moving desks is never silent), and a machine another
    /// human already claims (two people cannot both call one box theirs — a forward
    /// for one of them would bind a port on the other's machine).
    /// </summary>
    public async Task<LeadMachineBindResult> BindAsync(
        Guid humanId, Guid machineId, CancellationToken ct = default)
    {
        // Enrolled and not un-trusted (§13). Bind does NOT require a live runner
        // connection: the statement "this is my box" holds while the laptop is shut,
        // and connectivity is open_lead_forward's problem at forward time.
        var machine = await db.Machines.AsNoTracking()
            .Where(m => m.Id == machineId && !m.Revoked)
            .Select(m => new { m.Id, m.Name })
            .FirstOrDefaultAsync(ct);
        if (machine is null)
            return new LeadMachineBindResult.Refused(
                $"no enrolled machine {machineId:D} (or it has been revoked); enroll it with " +
                "/landbridge-enroll and take the machine id from the enrollment result or the " +
                "dashboard Machine Group view");

        if (await GetAsync(humanId, ct) is { } mine)
            return new LeadMachineBindResult.Refused(
                $"you are already bound to machine {mine.MachineName} ({mine.MachineId:D}); " +
                "call unbind_machine first if you have moved to a different machine");

        if (await LiveHolderOfAsync(machineId, ct) is { } holder)
            return new LeadMachineBindResult.Refused(
                $"machine {machine.Name} ({machineId:D}) is already bound by human {holder:D}; " +
                "a machine belongs to one person");

        var now = clock.GetUtcNow();
        db.LeadMachineBindings.Add(new LeadMachineBindingRow
        {
            Id = Guid.NewGuid(),
            HumanId = humanId,
            MachineId = machineId,
            BoundAt = now,
        });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
            when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" } pg)
        {
            // A concurrent binder won the race between the reads above and this
            // insert. The unique index made the loss safe; report it as the same
            // refusal a sequential second caller would have got, naming which slot
            // was taken from the constraint itself.
            db.ChangeTracker.Clear();
            return new LeadMachineBindResult.Refused(
                pg.ConstraintName == LandbridgeDbContext.OneLiveBindingPerMachineIndex
                    ? $"machine {machine.Name} ({machineId:D}) was bound by someone else just now; " +
                      "a machine belongs to one person"
                    : "you bound a machine in a concurrent session just now; " +
                      "read your current binding with get_team_state");
        }

        return new LeadMachineBindResult.Bound(new LeadMachineBinding(machine.Id, machine.Name, now));
    }

    /// <summary>
    /// Revokes this human's binding. Returns the binding that was released, or null
    /// when there was none — an unbind with nothing to unbind is not an error, it is
    /// already the desired state.
    /// </summary>
    public async Task<LeadMachineBinding?> UnbindAsync(Guid humanId, CancellationToken ct = default)
    {
        var current = await GetAsync(humanId, ct);
        if (current is null)
            return null;

        var now = clock.GetUtcNow();
        // Soft delete: the row keeps the bind/unbind history and frees both unique
        // slots (the indexes filter on revoked = false).
        await db.LeadMachineBindings
            .Where(b => b.HumanId == humanId && !b.Revoked)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.Revoked, true)
                .SetProperty(b => b.RevokedAt, (DateTimeOffset?)now), ct);
        return current;
    }

    /// <summary>
    /// This human's live binding, or null when they have bound no machine. The read
    /// behind both <c>open_lead_forward</c>'s consumer-end resolution and the
    /// binding's visibility in <c>get_team_state</c>.
    /// </summary>
    public Task<LeadMachineBinding?> GetAsync(Guid humanId, CancellationToken ct = default) =>
        (from b in db.LeadMachineBindings.AsNoTracking()
         join m in db.Machines.AsNoTracking() on b.MachineId equals m.Id
         where b.HumanId == humanId && !b.Revoked
         select new LeadMachineBinding(b.MachineId, m.Name, b.BoundAt))
        .FirstOrDefaultAsync(ct);

    private Task<Guid?> LiveHolderOfAsync(Guid machineId, CancellationToken ct) =>
        db.LeadMachineBindings.AsNoTracking()
            .Where(b => b.MachineId == machineId && !b.Revoked)
            .Select(b => (Guid?)b.HumanId)
            .FirstOrDefaultAsync(ct);
}
