using Landbridge.ControlPlane;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Landbridge.Hub;

/// <summary>Deletes <c>hub_queue</c> rows older than <see cref="HubOptions.Retain"/>.</summary>
public sealed class HubRetention(
    IDbContextFactory<LandbridgeDbContext> dbFactory,
    IOptions<HubOptions> options,
    TimeProvider clock,
    ILogger<HubRetention> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = options.Value;
        using var timer = new PeriodicTimer(opts.SweepInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var cutoff = clock.GetUtcNow() - opts.Retain;
                await using var db = await dbFactory.CreateDbContextAsync(stoppingToken);
                var n = await db.HubQueue.Where(q => q.CreatedAt < cutoff).ExecuteDeleteAsync(stoppingToken);
                if (n > 0)
                    logger.LogInformation("hub_queue dropped {Count} rows older than {Cutoff}", n, cutoff);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "hub_queue retention sweep failed");
            }
        }
    }
}
