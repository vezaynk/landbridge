using Docket.Meta.Data;
using Microsoft.EntityFrameworkCore;

namespace Docket.Meta.Provisioning;

/// <summary>
/// Resumes provisioning that a meta restart interrupted (design note §2). On
/// startup it finds every instance still <c>provisioning</c> or <c>failed</c> — a
/// meta process that died mid-saga — and re-enters <see cref="InstanceProvisioner"/>,
/// which picks up from the first not-Done checkpoint. Terminal states (ready,
/// suspended, destroyed) are left alone. This is what makes "a meta restart
/// mid-provision must be able to continue" real rather than aspirational.
/// </summary>
public sealed class InstanceReconciler(IServiceScopeFactory scopes, ILogger<InstanceReconciler> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // A brief settle so the store (and any dev-loop migration) is up before the sweep.
        try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
        catch (OperationCanceledException) { return; }

        List<Guid> resumable;
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MetaDbContext>();
            resumable = await db.Instances
                .Where(i => i.State == InstanceState.Provisioning || i.State == InstanceState.Failed)
                .Select(i => i.Id)
                .ToListAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "reconciler: could not read instances to resume; skipping");
            return;
        }

        if (resumable.Count == 0)
            return;

        log.LogInformation("reconciler: resuming {Count} interrupted instance(s)", resumable.Count);
        foreach (var id in resumable)
        {
            if (stoppingToken.IsCancellationRequested)
                return;
            try
            {
                using var scope = scopes.CreateScope();
                var provisioner = scope.ServiceProvider.GetRequiredService<InstanceProvisioner>();
                await provisioner.ProvisionAsync(id, stoppingToken);
            }
            catch (Exception ex)
            {
                // Left failed(step); an operator retry re-enters the same resume path.
                log.LogWarning(ex, "reconciler: instance {Id} did not converge; left failed", id);
            }
        }
    }
}
