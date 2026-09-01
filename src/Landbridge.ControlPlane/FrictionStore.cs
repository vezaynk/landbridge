using Landbridge.Core;

namespace Landbridge.ControlPlane;

/// <summary>
/// Persists <c>report_friction</c> rows. Append-only: the plane stores the
/// message and never interprets it. Size and emptiness are refused at the tool,
/// not here — this is the write.
/// </summary>
public sealed class FrictionStore(LandbridgeDbContext db, TimeProvider clock)
{
    /// <summary>Same 16 KiB UTF-8 cap as every other in-band prose field.</summary>
    public const int MaxMessageBytes = ReportResult.MaxReportBytes;

    public async Task RecordAsync(
        string role, Guid teamId, Guid? sessionId, Guid? humanId, string message, CancellationToken ct)
    {
        db.FrictionReports.Add(new FrictionReportRow
        {
            At = clock.GetUtcNow(),
            Role = role,
            TeamId = teamId,
            SessionId = sessionId,
            HumanId = humanId,
            Message = message,
        });
        await db.SaveChangesAsync(ct);
    }
}
