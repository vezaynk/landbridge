using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;

namespace Landbridge.Preview;

/// <summary>
/// Holds the wildcard-cert PEM that terminates TLS on <c>*.{Domain}</c> (spec §8.4)
/// and hot-reloads it when the files change, so an ACME renewal is served to new
/// handshakes <b>without a restart</b> (previously the PEM was loaded once at
/// startup and a renew-hook restarted the service — see <c>docs/PREVIEW-TLS.md</c>).
///
/// <para><b>How new handshakes pick up a renewal:</b> the server reads <see cref="Current"/>
/// at the start of each handshake (via <c>SslServerAuthenticationOptions.ServerCertificateSelectionCallback</c>),
/// so the atomic swap of that one reference is all it takes — handshakes started
/// before the swap keep the old cert, handshakes after it get the new one, and
/// established (already-handshaken) connections are untouched.</para>
///
/// <para><b>Fail-safe:</b> renewal tools write the cert and key as two separate,
/// non-atomic files, so a file-change event can fire while only one of the pair is
/// new. A reload only swaps when <em>both</em> files load <em>and</em> the key
/// matches the cert (<see cref="PreviewCertificate.LoadFromFiles"/> enforces the
/// match). Any failure — a half-written file, a stale/fresh mismatch, garbage —
/// keeps the current cert and logs loudly; the next write fires another reload and
/// the pair settles regardless of which file lands first. TLS is never dropped
/// because a reload failed.</para>
///
/// <para><b>Debounce:</b> a single change often produces a flurry of file-system
/// events; a short debounce (<see cref="PreviewOptions.CertReloadDebounce"/>)
/// coalesces them into one reload attempt. The debounce is only a churn/latency
/// knob — correctness rests entirely on the load-both-and-match check above, not on
/// the timing.</para>
///
/// <para><b>Retiring the old cert:</b> the selection callback hands out
/// <see cref="Current"/> at the start of a handshake and <c>AuthenticateAsServerAsync</c>
/// uses its private key synchronously while the handshake runs; once the handshake
/// completes the session keys are established and the <see cref="X509Certificate2"/>
/// is no longer touched. The only window in which a just-swapped-out cert can still
/// be in use is an in-flight handshake that read it moments before the swap. Rather
/// than track individual handshakes, we simply defer disposing a retired cert past
/// any plausible handshake duration (<see cref="RetireDelay"/>) — the simplest
/// correct choice, and cheap because renewals are ~monthly so at most a couple of
/// certs are ever retiring at once.</para>
/// </summary>
public sealed class PreviewCertificateProvider : IDisposable
{
    /// <summary>
    /// How long a swapped-out cert is kept alive before disposal — comfortably
    /// longer than any TLS handshake, so no in-flight handshake that grabbed it can
    /// still be using its private key when it is freed.
    /// </summary>
    private static readonly TimeSpan RetireDelay = TimeSpan.FromSeconds(60);

    private readonly string _certPath;
    private readonly string _keyPath;
    private readonly TimeSpan _debounce;
    private readonly ILogger _logger;

    private readonly object _gate = new();
    private readonly object _reloadGate = new();
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly List<Retiring> _retiring = [];
    private readonly Timer _debounceTimer;
    private volatile X509Certificate2 _current;
    private bool _disposed;

    private PreviewCertificateProvider(string certPath, string keyPath, X509Certificate2 initial, TimeSpan debounce, ILogger logger)
    {
        _certPath = Path.GetFullPath(certPath);
        _keyPath = Path.GetFullPath(keyPath);
        _debounce = debounce;
        _logger = logger;
        _current = initial;
        _debounceTimer = new Timer(_ => Reload(), state: null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Loads the configured cert and starts watching its files, or returns null when
    /// no cert is configured — in which case the frontend runs in plaintext and no
    /// watching happens (that mode is untouched by hot-reload). The initial load
    /// throws on a misconfigured or unreadable pair, exactly as before.
    /// </summary>
    public static PreviewCertificateProvider? Create(PreviewOptions options, ILogger logger)
    {
        X509Certificate2? initial = PreviewCertificate.Load(options);
        if (initial is null)
            return null;

        var provider = new PreviewCertificateProvider(
            options.CertPemPath!, options.CertKeyPemPath!, initial, options.CertReloadDebounce, logger);
        provider.StartWatching();
        return provider;
    }

    /// <summary>The cert new TLS handshakes are served off right now (atomically swapped on reload).</summary>
    internal X509Certificate2 Current => _current;

    private void StartWatching()
    {
        // One watcher per distinct directory (the cert and key usually share one).
        // We match events by full path rather than a filename filter so a rename-into-
        // place (write-to-temp-then-rename, which some renewers do) still triggers.
        foreach (var dir in new[] { Path.GetDirectoryName(_certPath), Path.GetDirectoryName(_keyPath) }
                     .Where(d => !string.IsNullOrEmpty(d))
                     .Distinct(StringComparer.Ordinal))
        {
            var watcher = new FileSystemWatcher(dir!)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime,
                IncludeSubdirectories = false,
            };
            watcher.Changed += OnChanged;
            watcher.Created += OnChanged;
            watcher.Renamed += OnRenamed;
            // A dropped/overflowed watch: re-sync by attempting a reload.
            watcher.Error += (_, _) => ScheduleReload();
            watcher.EnableRaisingEvents = true;
            _watchers.Add(watcher);
        }
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        if (Matches(e.FullPath))
            ScheduleReload();
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        if (Matches(e.FullPath) || Matches(e.OldFullPath))
            ScheduleReload();
    }

    private bool Matches(string path)
    {
        var full = Path.GetFullPath(path);
        return string.Equals(full, _certPath, PathComparison) || string.Equals(full, _keyPath, PathComparison);
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private void ScheduleReload()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _debounceTimer.Change(_debounce, Timeout.InfiniteTimeSpan);
        }
    }

    // Reloads are serialized on _reloadGate so overlapping triggers (a debounced
    // watcher event racing another, or a test's direct reload) can't both read the
    // same _current and each retire it — which would double-free one cert and leak
    // the other. Handshakes never take this lock: they read _current (volatile) only.
    private void Reload()
    {
        lock (_reloadGate)
        {
            X509Certificate2 fresh;
            try
            {
                fresh = PreviewCertificate.LoadFromFiles(_certPath, _keyPath);
            }
            catch (Exception e)
            {
                // FAIL-SAFE: a half-written file, a stale-cert/fresh-key (or vice-versa)
                // mismatch caught mid-renewal, or outright garbage. Keep serving the
                // current cert; the write that completes the pair fires another reload.
                _logger.LogError(
                    e, "preview TLS: reloading the certificate from {CertPath} / {KeyPath} failed; keeping the current one",
                    _certPath, _keyPath);
                return;
            }

            X509Certificate2 old;
            lock (_gate)
            {
                if (_disposed)
                {
                    fresh.Dispose();
                    return;
                }
                old = _current;
                // A spurious event (e.g. a metadata touch that left the bytes unchanged):
                // don't churn handshakes onto an identical cert — drop the reloaded dup.
                if (string.Equals(old.Thumbprint, fresh.Thumbprint, StringComparison.Ordinal))
                {
                    fresh.Dispose();
                    return;
                }
                _current = fresh;
            }

            _logger.LogInformation(
                "preview TLS: reloaded certificate (subject {Subject}, thumbprint {Thumbprint}, valid until {NotAfter:u}); new handshakes serve it, existing connections are unaffected",
                fresh.Subject, fresh.Thumbprint, fresh.NotAfter.ToUniversalTime());
            Retire(old);
        }
    }

    private void Retire(X509Certificate2 old)
    {
        var entry = new Retiring(old);
        entry.Timer = new Timer(_ =>
        {
            lock (_gate)
                _retiring.Remove(entry);
            entry.Timer!.Dispose();
            old.Dispose();
        }, state: null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        lock (_gate)
        {
            if (_disposed)
            {
                // Racing Dispose(): free immediately rather than leak past teardown.
                entry.Timer.Dispose();
                old.Dispose();
                return;
            }
            _retiring.Add(entry);
        }
        entry.Timer.Change(RetireDelay, Timeout.InfiniteTimeSpan);
    }

    /// <summary>A swapped-out cert waiting out its retire window before disposal.</summary>
    private sealed class Retiring(X509Certificate2 cert)
    {
        public X509Certificate2 Cert { get; } = cert;
        public Timer? Timer { get; set; }
    }

    /// <summary>Test-only: run a reload synchronously, bypassing the file watcher and debounce.</summary>
    internal void ReloadForTest() => Reload();

    public void Dispose()
    {
        List<Retiring> retiring;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            retiring = [.. _retiring];
            _retiring.Clear();
        }

        foreach (var watcher in _watchers)
            watcher.Dispose();
        _watchers.Clear();
        _debounceTimer.Dispose();

        // Free any certs still in their retire window, then the live one.
        foreach (var entry in retiring)
        {
            entry.Timer?.Dispose();
            entry.Cert.Dispose();
        }
        _current.Dispose();
    }
}
