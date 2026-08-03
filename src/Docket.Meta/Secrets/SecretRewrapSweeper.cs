using Docket.Meta.Data;
using Microsoft.EntityFrameworkCore;

namespace Docket.Meta.Secrets;

/// <summary>
/// Re-seals every retained secret under the CURRENT primary key at startup (task
/// #79). One component covers both jobs that need it:
///
/// <list type="bullet">
/// <item>Rows written before encryption existed hold plaintext. The protector reads
/// those through unchanged, and this sweep converts them, so the store moves forward
/// without a hand-run data migration.</item>
/// <item>After a key rotation, rows still sealed under the retired key are rewrapped
/// under the new primary — which is what lets the operator actually DROP the old key
/// from config afterwards instead of carrying it forever.</item>
/// </list>
///
/// <para>The rewrap is unconditional rather than "only rows that need it": a value
/// converter deliberately hides which key sealed a row, and reaching around it for the
/// raw ciphertext would mean provider-specific SQL for no real gain. Writing always
/// seals under the primary key, so loading a row and forcing its UPDATE is a complete
/// rewrap. Change tracking compares the DECRYPTED value, which is unchanged, so each
/// property is marked modified explicitly — otherwise EF would issue no UPDATE at all.
/// A control plane holds tens of instances, so the cost is a rounding error.</para>
///
/// <para>Destroyed tombstones are skipped — destroy already shredded their secret
/// columns, and re-sealing a blank would only obscure that the shred happened.</para>
/// </summary>
public sealed class SecretRewrapSweeper(IServiceScopeFactory scopes, ILogger<SecretRewrapSweeper> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the store (and any startup migration) settle first, matching the
        // provisioning reconciler's own preamble.
        try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); }
        catch (OperationCanceledException) { return; }

        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MetaDbContext>();
            var n = await SweepAsync(db, stoppingToken);
            if (n > 0)
                log.LogInformation("rewrapped secrets for {Count} row(s) under key {Key}",
                    n, db.Protector.PrimaryFingerprint);
        }
        catch (Exception ex)
        {
            // A failed sweep is not fatal: reads still work as long as the sealing key
            // is configured, so the panel stays up and an operator can retry.
            log.LogWarning(ex, "secret rewrap sweep did not complete; secrets remain readable under their existing keys");
        }
    }

    /// <summary>
    /// Re-seals every encrypted column under the primary key: each live instance's
    /// retained secrets AND each live host's mTLS client key. Both must be covered or a
    /// rotation could not finish — a host key left under the retired key would keep that
    /// key permanently required. Separated from the hosted-service shell so tests and a
    /// future operator-facing "rotate now" action can drive it directly. Returns the
    /// number of rows rewrapped.
    /// </summary>
    public static async Task<int> SweepAsync(MetaDbContext db, CancellationToken ct)
    {
        var instances = await db.Instances
            .Where(i => i.State != InstanceState.Destroyed)
            .ToListAsync(ct);

        foreach (var row in instances)
        {
            db.Entry(row).Property(x => x.DbPassword).IsModified = true;
            db.Entry(row).Property(x => x.RelayBearer).IsModified = true;
        }

        // Only hosts that actually carry a key: the local unix socket has none, and
        // marking a null property modified would write a pointless UPDATE.
        var hosts = await db.Hosts
            .Where(h => h.RemovedAt == null && h.TlsClientKeyPem != null)
            .ToListAsync(ct);

        foreach (var host in hosts)
            db.Entry(host).Property(x => x.TlsClientKeyPem).IsModified = true;

        var total = instances.Count + hosts.Count;
        if (total > 0)
            await db.SaveChangesAsync(ct);

        return total;
    }
}
