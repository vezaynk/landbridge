using System.Threading.RateLimiting;

namespace Landbridge.ControlPlane.Auth;

/// <summary>
/// Per-client-IP cap on operator passphrase attempts (§5). Replaces the fixed
/// delay on a wrong guess: 10 tries per minute, then 429. Successful and failed
/// POSTs both count — a guessing loop is the thing this bounds.
/// </summary>
public sealed class OperatorAttemptLimiter : IDisposable
{
    public const int PermitsPerWindow = 10;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly PartitionedRateLimiter<string> _inner = PartitionedRateLimiter.Create<string, string>(
        key => RateLimitPartition.GetFixedWindowLimiter(
            key,
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = PermitsPerWindow,
                Window = Window,
                QueueLimit = 0,
                AutoReplenishment = true,
            }));

    public bool TryAcquire(string? clientKey)
    {
        using var lease = _inner.AttemptAcquire(string.IsNullOrEmpty(clientKey) ? "unknown" : clientKey);
        return lease.IsAcquired;
    }

    public void Dispose() => _inner.Dispose();
}
