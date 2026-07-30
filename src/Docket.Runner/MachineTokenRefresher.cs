namespace Docket.Runner;

/// <summary>
/// Keeps docketd's short-lived machine access token fresh (spec §5, §13). It
/// owns the current credentials, hands the control-plane channel the CURRENT
/// access token on every (re)connect through <see cref="CurrentAccessToken"/>
/// (the <c>Func&lt;string&gt;</c> seam), and re-mints the access token two ways:
///
/// <list type="bullet">
/// <item><b>Proactively</b>, on a <see cref="TimeProvider"/> timer, at ~50% of
///   the access token's remaining lifetime — so a long-running daemon never
///   reaches an expiry it did not see coming.</item>
/// <item><b>Reactively</b>, via <see cref="RefreshOnceAsync"/>, which the channel
///   invokes when a reconnect is rejected 401 — the token expired or was revoked
///   out from under a live daemon, and the next dial must carry a new one.</item>
/// </list>
///
/// Each successful refresh is persisted atomically (§13) via the injected
/// <c>persist</c> delegate. This type is used only in file-credential mode; the
/// env-token dev loop never constructs it and so never refreshes (unchanged
/// behaviour).
/// </summary>
internal sealed class MachineTokenRefresher : IAsyncDisposable
{
    private readonly Func<string, CancellationToken, Task<RefreshResponse?>> _refresh;
    private readonly Action<MachineCredentialFile> _persist;
    private readonly TimeProvider _clock;
    private readonly Action<string>? _log;

    // Floor on the wait between ticks so a persistently-failing refresh (a
    // revoked machine whose access token has already expired) backs off instead
    // of hot-looping. The 401-on-connect hook is the real recovery path.
    private readonly TimeSpan _retryFloor;

    private readonly object _gate = new();
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private MachineCredentialFile _current;
    private Task? _loop;

    public MachineTokenRefresher(
        MachineCredentialFile initial,
        Func<string, CancellationToken, Task<RefreshResponse?>> refresh,
        Action<MachineCredentialFile> persist,
        TimeProvider clock,
        Action<string>? log = null,
        TimeSpan? retryFloor = null)
    {
        _current = initial;
        _refresh = refresh;
        _persist = persist;
        _clock = clock;
        _log = log;
        _retryFloor = retryFloor ?? TimeSpan.FromSeconds(30);
    }

    /// <summary>
    /// The access token to present on the next (re)connect. Read fresh each dial
    /// so a refresh that lands between connection attempts is picked up
    /// immediately (§13). This is the seam the channel's token provider calls.
    /// </summary>
    public string CurrentAccessToken
    {
        get { lock (_gate) return _current.AccessToken; }
    }

    /// <summary>Starts the proactive half-life refresh loop.</summary>
    public void Start() => _loop = Task.Run(() => LoopAsync(_cts.Token));

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(NextDelay(), _clock, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            await RefreshOnceAsync(ct);
        }
    }

    /// <summary>
    /// The wait until the next proactive refresh: half of the access token's
    /// remaining lifetime, floored so an already-expired token retries on a
    /// bounded cadence rather than spinning.
    /// </summary>
    private TimeSpan NextDelay()
    {
        DateTimeOffset expiry;
        lock (_gate) expiry = _current.AccessExpiresAt;
        var remaining = expiry - _clock.GetUtcNow();
        // Half of the remaining lifetime; the floor guards only the already-expired
        // case so a failing refresh backs off instead of spinning at zero delay.
        return remaining <= TimeSpan.Zero ? _retryFloor : remaining / 2;
    }

    /// <summary>
    /// Mints a fresh access token from the current refresh token, swaps it in,
    /// and persists (§13). Serialized against the timer loop so the proactive and
    /// reactive (401-hook) paths never refresh concurrently. Returns false — never
    /// throws — when the plane refuses (dead refresh / revoked machine) or the
    /// call fails, so the caller (timer or channel) simply treats it as a missed
    /// tick and the connect loop keeps retrying.
    /// </summary>
    public async Task<bool> RefreshOnceAsync(CancellationToken ct)
    {
        try
        {
            await _refreshLock.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        try
        {
            string refreshToken;
            lock (_gate) refreshToken = _current.RefreshToken;

            RefreshResponse? refreshed;
            try
            {
                refreshed = await _refresh(refreshToken, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _log?.Invoke($"machine token refresh failed: {e.Message}");
                return false;
            }

            if (refreshed is null)
            {
                _log?.Invoke("machine token refresh refused by the control plane (dead refresh or revoked machine)");
                return false;
            }

            MachineCredentialFile updated;
            lock (_gate)
            {
                _current = _current with
                {
                    AccessToken = refreshed.AccessToken,
                    AccessExpiresAt = refreshed.AccessExpiresAt,
                };
                updated = _current;
            }

            try
            {
                _persist(updated);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The new token is live in memory and will be presented on the next
                // dial; a failed persist only costs us the token across a restart.
                _log?.Invoke($"persisting refreshed credentials failed: {e.Message}");
            }

            _log?.Invoke("machine access token refreshed");
            return true;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        if (_loop is not null)
        {
            try { await _loop; }
            catch (OperationCanceledException) { }
        }
        _cts.Dispose();
        _refreshLock.Dispose();
    }
}
