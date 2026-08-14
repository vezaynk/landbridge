using Docket.Meta.Data;
using Microsoft.EntityFrameworkCore;

namespace Docket.Meta.Provisioning;

/// <summary>
/// Finishes lifecycle operations nothing is driving (design note §2). Every
/// <see cref="InstanceStates.InFlight"/> state means an operation was running against that
/// row; if no operation in this process holds it (<see cref="OperationTracker"/>), then the
/// process that did is gone — killed, restarted, or stopped mid-saga — and the row will
/// stay that way until something re-enters it. This sweep is that something, and it runs
/// on a timer rather than once at startup, because the interruptions that matter are not
/// only restarts: a readiness timeout inside an upgrade abandons a row just as thoroughly,
/// and no operator action is guaranteed to follow.
///
/// <para><b>What it re-enters.</b> The three "wants to be ready" cases — an interrupted
/// provision, resume, or upgrade — are all the same saga, so all three go through
/// <see cref="InstanceProvisioner.ContinueInterruptedAsync"/>, which reads the intent
/// already recorded on the row (the new image tag, the invalidated checkpoints) and resumes
/// from the first not-Done step. That entry point exists for this caller alone: it is the
/// only one permitted to claim a row another operation left in flight, and the tracker check
/// below is what earns it that. <c>suspending</c> and <c>destroying</c> are the two whose
/// intent is not "be ready", so they are finished by their own transitions — never converged
/// upward, which would undo what the operator asked for.</para>
///
/// <para><b><c>failed</c> is swept too, with a backoff.</b> A stall is what a timeout or a
/// transient registry error leaves behind, and those deserve a retry without an operator
/// present; a tag that will never pull deserves not to be retried every sweep. So a failed
/// Instance is re-entered only once its last step activity is <see cref="FailedRetryAfter"/>
/// old, which spaces retries out without a retry counter on the row.</para>
///
/// <para>Sequential on purpose: one instance is taken over at a time, awaited to completion,
/// and only then the next — a sweep that fanned out could have several sagas contending for
/// the same host's Docker daemon and port range. The tracker mark is held across each one,
/// so an operator action arriving mid-heal is refused rather than interleaved.</para>
/// </summary>
public sealed class InstanceReconciler(
    IServiceScopeFactory scopes,
    OperationTracker tracker,
    TimeProvider clock,
    ILogger<InstanceReconciler> log)
    : BackgroundService
{
    /// <summary>A brief settle so the store (and any dev-loop migration) is up before the first sweep.</summary>
    internal static readonly TimeSpan StartupSettle = TimeSpan.FromSeconds(2);

    /// <summary>How often the sweep runs.</summary>
    internal static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    /// <summary>How stale a <c>failed</c> Instance's last step activity must be before it is retried.</summary>
    internal static readonly TimeSpan FailedRetryAfter = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(StartupSettle, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            await SweepAsync(stoppingToken);
            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// One pass: take over every Instance that needs it and drive it to its intended end
    /// state. Returns how many were taken over. Never throws — a store that is briefly
    /// unreachable costs a sweep, not the sweeper.
    /// </summary>
    internal async Task<int> SweepAsync(CancellationToken ct)
    {
        List<Candidate> candidates;
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MetaDbContext>();
            candidates = await db.Instances
                .Where(i => i.State == InstanceState.Failed || InstanceStates.InFlight.Contains(i.State))
                .Select(i => new Candidate(
                    i.Id, i.Name, i.State, i.Steps.Max(s => (DateTimeOffset?)s.UpdatedAt)))
                .ToListAsync(ct);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "reconciler: could not read instances to reconcile; skipping this sweep");
            return 0;
        }

        var taken = 0;
        foreach (var c in candidates)
        {
            if (ct.IsCancellationRequested)
                return taken;
            if (c.State is InstanceState.Failed && !RetryDue(c))
                continue;
            // An operation running here is making progress; only an abandoned row is ours.
            if (tracker.Busy(c.Id) is { } verb)
            {
                log.LogDebug("reconciler: instance {Name} left to the {Verb} already running", c.Name, verb);
                continue;
            }
            if (!tracker.TryMark(c.Id, VerbFor(c.State)))
                continue;

            taken++;
            log.LogInformation("reconciler: taking over instance {Name}, left {State}", c.Name, c.State);
            try
            {
                using var scope = scopes.CreateScope();
                var provisioner = scope.ServiceProvider.GetRequiredService<InstanceProvisioner>();
                await FinishAsync(provisioner, c, ct);
            }
            catch (Exception ex)
            {
                // Left failed(step); the next sweep (after the backoff) or an operator
                // retry re-enters the same path.
                log.LogWarning(ex, "reconciler: instance {Name} did not converge", c.Name);
            }
            finally
            {
                tracker.Clear(c.Id);
            }
        }
        return taken;
    }

    /// <summary>
    /// Drives one abandoned Instance to the end state its recorded intent asks for — up to
    /// <c>ready</c> for the saga's three entry points, down to <c>suspended</c>/<c>destroyed</c>
    /// for the two transitions that were heading there.
    /// </summary>
    private static Task FinishAsync(InstanceProvisioner provisioner, Candidate c, CancellationToken ct) =>
        c.State switch
        {
            InstanceState.Suspending => provisioner.SuspendAsync(c.Id, ct),
            InstanceState.Destroying => provisioner.DestroyAsync(c.Id, ct),
            _ => provisioner.ContinueInterruptedAsync(c.Id, ct),
        };

    private static string VerbFor(InstanceState state) => state switch
    {
        InstanceState.Suspending => "suspending",
        InstanceState.Destroying => "destroying",
        _ => "provisioning",
    };

    /// <summary>
    /// Whether a stalled Instance has waited out <see cref="FailedRetryAfter"/>. An Instance
    /// with no steps at all (a create whose provision never started) is due immediately —
    /// there is no activity to be recent.
    /// </summary>
    private bool RetryDue(Candidate c) =>
        c.LastStepAt is not { } last || clock.GetUtcNow() - last >= FailedRetryAfter;

    private sealed record Candidate(Guid Id, string Name, InstanceState State, DateTimeOffset? LastStepAt);
}
